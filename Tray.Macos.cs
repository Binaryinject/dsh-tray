#if MACOS
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace DshTray
{
    /// <summary>macOS tray backend (AppKit menu-bar app).</summary>
    internal static class Platform
    {
        private const string IconResourceName = "icon.png";

        private static Core core;
        private static MenuActions actions; // retained so the menu target is not GC'd
        private static NSMenuItem statusMenuItem;
        private static NSMenuItem progressMenuItem;
        private static NSPanel progressPanel;
        private static NSTextField progressStatus;
        private static NSTextField progressDetail;
        private static NSProgressIndicator progressBar;
        private static bool progressDismissedByUser;

        public static int Run(Core c)
        {
            core = c;

            NSApplication app = NSApplication.SharedApplication;
            app.ActivationPolicy = NSApplicationActivationPolicy.Accessory; // no dock icon

            NSStatusItem statusItem = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Variable);
            NSImage image = LoadImage();
            if (image != null)
            {
                image.Size = new CGSize(18, 18);
                statusItem.Button.Image = image;
            }
            else
            {
                statusItem.Button.Title = "dsh";
            }

            actions = new MenuActions(core);
            NSMenu menu = new NSMenu();
            statusMenuItem = new NSMenuItem { Title = "状态：正在启动…", Enabled = false };
            progressMenuItem = MakeItem("显示更新进度", "showProgress:", actions);
            progressMenuItem.Hidden = true;
            menu.AddItem(statusMenuItem);
            menu.AddItem(progressMenuItem);
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(MakeItem("打开网页", "openBrowser:", actions));
            menu.AddItem(MakeItem("查看日志", "openLog:", actions));
            menu.AddItem(MakeItem("重启服务器", "restartServer:", actions));
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(MakeItem("退出并停止服务", "quit:", actions));
            statusItem.Menu = menu;

            c.OnShutdownRequest = delegate
            {
                app.BeginInvokeOnMainThread(delegate { Shutdown(); });
            };
            c.Notify = delegate(string title, string text)
            {
                ShowNotification(title, text);
            };
            c.StatusChanged = delegate (string status)
            {
                app.BeginInvokeOnMainThread(delegate
                {
                    statusMenuItem.Title = "状态：" + status.Replace("DeepSeek Harness — ", "");
                });
            };
            c.UpdateProgressStarted = delegate
            {
                app.BeginInvokeOnMainThread(delegate { progressDismissedByUser = false; });
            };
            c.UpdateProgressChanged = delegate (string stage, string detail)
            {
                app.BeginInvokeOnMainThread(delegate { UpdateProgressWindow(stage, detail); });
            };
            c.UpdateProgressCompleted = delegate
            {
                app.BeginInvokeOnMainThread(CompleteProgressWindow);
            };

            c.Start();

            app.Run();
            core.Shutdown();
            return 0;
        }

        private static NSMenuItem MakeItem(string title, string action, NSObject target)
        {
            NSMenuItem item = new NSMenuItem(title, new Selector(action), "");
            item.Target = target;
            return item;
        }

        private static void Shutdown()
        {
            core.Shutdown();
            NSApplication.SharedApplication.Terminate(null);
        }

        private static void EnsureProgressWindow()
        {
            if (progressPanel != null) return;

            progressPanel = new NSPanel(
                new CGRect(0, 0, 500, 220),
                NSWindowStyle.Titled | NSWindowStyle.Closable,
                NSBackingStore.Buffered,
                false);
            progressPanel.Title = "DeepSeek Harness 更新";
            progressPanel.ReleasedWhenClosed = false;
            progressPanel.FloatingPanel = true;
            progressPanel.HidesOnDeactivate = false;
            progressPanel.WillClose += delegate { progressDismissedByUser = true; };

            progressStatus = CreateLabel(new CGRect(24, 164, 452, 28), "正在准备更新…", 15);
            progressBar = new NSProgressIndicator(new CGRect(24, 136, 452, 16));
            progressBar.Style = NSProgressIndicatorStyle.Bar;
            progressBar.Indeterminate = true;
            progressBar.StartAnimation(null);
            progressDetail = CreateLabel(new CGRect(24, 76, 452, 48), "等待 npm 输出…", 12);
            progressDetail.LineBreakMode = NSLineBreakMode.TruncatingTail;

            NSButton logButton = new NSButton(new CGRect(276, 24, 96, 32));
            logButton.Title = "查看日志";
            logButton.BezelStyle = NSBezelStyle.Rounded;
            logButton.Activated += delegate { core.OpenLog(); };

            NSButton hideButton = new NSButton(new CGRect(380, 24, 96, 32));
            hideButton.Title = "后台运行";
            hideButton.BezelStyle = NSBezelStyle.Rounded;
            hideButton.Activated += delegate
            {
                progressDismissedByUser = true;
                progressPanel.OrderOut(null);
            };

            progressPanel.ContentView.AddSubview(progressStatus);
            progressPanel.ContentView.AddSubview(progressBar);
            progressPanel.ContentView.AddSubview(progressDetail);
            progressPanel.ContentView.AddSubview(logButton);
            progressPanel.ContentView.AddSubview(hideButton);
            progressPanel.Center();
        }

        private static NSTextField CreateLabel(CGRect frame, string text, nfloat fontSize)
        {
            NSTextField label = new NSTextField(frame);
            label.StringValue = text;
            label.Editable = false;
            label.Selectable = false;
            label.Bordered = false;
            label.DrawsBackground = false;
            label.Font = NSFont.SystemFontOfSize(fontSize);
            return label;
        }

        private static void UpdateProgressWindow(string stage, string detail)
        {
            EnsureProgressWindow();
            progressMenuItem.Hidden = false;
            if (!string.IsNullOrEmpty(stage)) progressStatus.StringValue = stage;
            if (!string.IsNullOrWhiteSpace(detail)) progressDetail.StringValue = "最新日志：" + TrimProgressDetail(detail);
            if (!progressDismissedByUser) progressPanel.OrderFrontRegardless();
        }

        private static void CompleteProgressWindow()
        {
            EnsureProgressWindow();
            progressBar.StopAnimation(null);
            progressBar.Indeterminate = false;
            progressBar.MinValue = 0;
            progressBar.MaxValue = 100;
            progressBar.DoubleValue = 100;
            progressStatus.StringValue = "更新完成，服务已就绪。";
            if (!progressDismissedByUser) progressPanel.OrderFrontRegardless();

            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(2500);
                NSApplication.SharedApplication.BeginInvokeOnMainThread(delegate
                {
                    if (progressPanel != null) progressPanel.OrderOut(null);
                });
            });
        }

        private static void ShowProgressWindow()
        {
            EnsureProgressWindow();
            progressDismissedByUser = false;
            progressPanel.OrderFrontRegardless();
        }

        private static string TrimProgressDetail(string value)
        {
            string text = value.Trim();
            return text.Length > 180 ? text.Substring(0, 177) + "..." : text;
        }

        private static void ShowNotification(string title, string text)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "/usr/bin/osascript";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("display notification " + QuoteForAppleScript(text) + " with title " + QuoteForAppleScript(title));
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
            }
            catch
            {
            }
        }

        private static string QuoteForAppleScript(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static NSImage LoadImage()
        {
            try
            {
                using (Stream s = typeof(Platform).Assembly.GetManifestResourceStream(IconResourceName))
                {
                    if (s == null) return null;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return new NSImage(NSData.FromArray(ms.ToArray()));
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private sealed class MenuActions : NSObject
        {
            private readonly Core core;

            public MenuActions(Core c)
            {
                core = c;
            }

            [Export("openBrowser:")]
            public void OpenBrowser(NSObject sender) { core.OpenBrowser(); }

            [Export("openLog:")]
            public void OpenLog(NSObject sender) { core.OpenLog(); }

            [Export("showProgress:")]
            public void ShowProgress(NSObject sender) { ShowProgressWindow(); }

            [Export("restartServer:")]
            public void RestartServer(NSObject sender) { core.RestartServer(); }

            [Export("quit:")]
            public void Quit(NSObject sender)
            {
                core.Shutdown();
                NSApplication.SharedApplication.Terminate(sender);
            }
        }
    }
}
#endif
