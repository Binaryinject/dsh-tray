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
        private static bool progressIsCompleted;

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
            NSMenuItem versionMenuItem = new NSMenuItem { Title = "版本 " + SelfUpdater.GetCurrentVersion(), Enabled = false };
            progressMenuItem = MakeItem("显示更新进度", "showProgress:", actions);
            progressMenuItem.Hidden = true;
            menu.AddItem(statusMenuItem);
            menu.AddItem(versionMenuItem);
            menu.AddItem(progressMenuItem);
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(MakeItem("打开网页", "openBrowser:", actions));
            menu.AddItem(MakeItem("启动 dsh 控制台", "openConsole:", actions));
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
                app.BeginInvokeOnMainThread(delegate
                {
                    progressDismissedByUser = false;
                    progressIsCompleted = false;
                });
            };
            c.UpdateProgressChanged = delegate (string stage, string detail)
            {
                app.BeginInvokeOnMainThread(delegate { UpdateProgressWindow(stage, detail); });
            };
            c.UpdateProgressCompleted = delegate
            {
                app.BeginInvokeOnMainThread(CompleteProgressWindow);
            };
            c.SelfUpdateAvailable = delegate (string tag, string downloadUrl, string releaseUrl)
            {
                app.BeginInvokeOnMainThread(delegate { PromptUpdate(tag, downloadUrl); });
            };
            c.SelfUpdateProgress = delegate (long received, long total)
            {
                app.BeginInvokeOnMainThread(delegate { UpdateSelfUpdateProgress(received, total); });
            };
            c.SelfUpdateDownloaded = delegate (string installerPath)
            {
                app.BeginInvokeOnMainThread(delegate { ApplyMacUpdate(installerPath); });
            };
            c.SelfUpdateFailed = delegate (string reason)
            {
                app.BeginInvokeOnMainThread(delegate { ShowNotification("DeepSeek Harness", "自动更新失败：" + reason); });
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

        private static void PromptUpdate(string tag, string downloadUrl)
        {
            NSAlert alert = new NSAlert();
            alert.MessageText = "发现新版本";
            alert.InformativeText = "发现新版本 " + tag + "（当前 " + SelfUpdater.GetCurrentVersion() + "）。\n是否下载并自动更新？";
            alert.AddButton("更新");
            alert.AddButton("取消");
            long result = (long)alert.RunModal();
            if (result == 1000) // NSAlertFirstButtonReturn
            {
                EnsureProgressWindow();
                progressMenuItem.Hidden = false;
                progressIsCompleted = false;
                progressDismissedByUser = false;
                progressBar.StopAnimation(null);
                progressBar.Indeterminate = true;
                progressBar.StartAnimation(null);
                progressStatus.StringValue = "正在下载更新…";
                progressDetail.StringValue = "准备下载…";
                progressPanel.OrderFrontRegardless();
                core.BeginSelfUpdateDownload(downloadUrl);
            }
        }

        private static void UpdateSelfUpdateProgress(long received, long total)
        {
            EnsureProgressWindow();
            progressMenuItem.Hidden = false;
            progressIsCompleted = false;
            progressDismissedByUser = false;
            int percent = total > 0 ? (int)(received * 100 / total) : -1;
            progressBar.StopAnimation(null);
            if (percent >= 0)
            {
                progressBar.Indeterminate = false;
                progressBar.MinValue = 0;
                progressBar.MaxValue = 100;
                progressBar.DoubleValue = percent;
            }
            else
            {
                progressBar.Indeterminate = true;
                progressBar.StartAnimation(null);
            }
            progressStatus.StringValue = "正在下载更新…";
            progressDetail.StringValue = "下载进度：" + SelfUpdater.FormatBytes(received)
                + (total > 0 ? " / " + SelfUpdater.FormatBytes(total) : "");
            if (!progressDismissedByUser) progressPanel.OrderFrontRegardless();
        }

        private static void ApplyMacUpdate(string dmgPath)
        {
            EnsureProgressWindow();
            progressStatus.StringValue = "已下载，正在安装并重启…";
            progressDetail.StringValue = "即将静默安装并重新启动。";
            progressBar.StopAnimation(null);
            progressBar.Indeterminate = false;
            progressBar.MinValue = 0;
            progressBar.MaxValue = 100;
            progressBar.DoubleValue = 100;

            try
            {
                string dir = Path.GetDirectoryName(dmgPath);
                string mountPoint = Path.Combine(dir, "mnt");
                string scriptPath = Path.Combine(dir, "apply-update.sh");
                string script =
                    "#!/bin/bash\n" +
                    "while pgrep -x dsh-tray >/dev/null 2>&1; do sleep 0.5; done\n" +
                    "hdiutil attach \"" + dmgPath + "\" -nobrowse -mountpoint \"" + mountPoint + "\"\n" +
                    "rm -rf /Applications/dsh-tray.app\n" +
                    "ditto \"" + mountPoint + "/dsh-tray.app\" /Applications/dsh-tray.app\n" +
                    "hdiutil detach \"" + mountPoint + "\"\n" +
                    "open /Applications/dsh-tray.app\n";
                File.WriteAllText(scriptPath, script);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "/bin/bash";
                psi.UseShellExecute = false;
                psi.ArgumentList.Add(scriptPath);
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
            }
            catch
            {
            }

            Shutdown();
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
            if (!progressDismissedByUser)
            {
                progressPanel.OrderFrontRegardless();
                if (progressIsCompleted) ScheduleAutoHide();
            }
        }

        private static void CompleteProgressWindow()
        {
            EnsureProgressWindow();
            progressBar.StopAnimation(null);
            progressBar.Indeterminate = false;
            progressBar.MinValue = 0;
            progressBar.MaxValue = 100;
            progressBar.DoubleValue = 100;
            progressStatus.StringValue = "服务已就绪。";
            progressIsCompleted = true;
            if (!progressDismissedByUser)
            {
                progressPanel.OrderFrontRegardless();
                ScheduleAutoHide();
            }
        }

        private static void ScheduleAutoHide()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(1000);
                NSApplication.SharedApplication.BeginInvokeOnMainThread(delegate
                {
                    if (progressPanel == null) return;
                    if (progressDismissedByUser) return;
                    if (!progressIsCompleted) return;
                    progressPanel.OrderOut(null);
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

            [Export("openConsole:")]
            public void OpenConsole(NSObject sender) { core.OpenConsole(); }

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
