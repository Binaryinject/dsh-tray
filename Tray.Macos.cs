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
        private static NSMenuItem dshVersionMenuItem;
        private static NSMenuItem progressMenuItem;
        private static NSMenuItem branchLatestItem;
        private static NSMenuItem branchNextItem;
        private static NSMenuItem branchAlphaItem;
        private static NSMenu profileMenu;
        private static NSMenuItem profileParentItem;
        private static readonly System.Collections.Generic.List<string> profileMenuNames = new System.Collections.Generic.List<string>();
        private static NSPanel progressPanel;
        private static NSTextField progressStatus;
        private static NSScrollView progressLogScroll;
        private static NSTextView progressLogView;
        private static NSProgressIndicator progressBar;
        private static bool progressDismissedByUser;
        private static bool progressIsCompleted;
        private static readonly System.Collections.Generic.List<string> progressLogLines = new System.Collections.Generic.List<string>();
        private const int MaxProgressLogLines = 400;

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
            dshVersionMenuItem = new NSMenuItem { Title = core.DshVersionDisplay, Enabled = false };
            progressMenuItem = MakeItem("显示更新进度", "showProgress:", actions);
            progressMenuItem.Hidden = true;
            menu.AddItem(statusMenuItem);
            menu.AddItem(versionMenuItem);
            menu.AddItem(dshVersionMenuItem);

            NSMenu branchMenu = new NSMenu();
            branchLatestItem = MakeBranchItem(AppSettings.LatestBranch, 1);
            branchNextItem = MakeBranchItem(AppSettings.NextBranch, 2);
            branchAlphaItem = MakeBranchItem(AppSettings.AlphaBranch, 3);
            branchMenu.AddItem(branchLatestItem);
            branchMenu.AddItem(branchNextItem);
            branchMenu.AddItem(branchAlphaItem);
            NSMenuItem branchParent = new NSMenuItem { Title = "dsh 版本分支" };
            branchParent.Submenu = branchMenu;
            menu.AddItem(branchParent);

            profileMenu = new NSMenu();
            profileParentItem = new NSMenuItem { Title = "Profile（当前：web）" };
            profileParentItem.Submenu = profileMenu;
            menu.AddItem(profileParentItem);
            RefreshProfileMenu();

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
                app.BeginInvokeOnMainThread(delegate { NotifyToWindow(text); });
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
                app.BeginInvokeOnMainThread(delegate
                {
                    EnsureProgressWindow();
                    progressMenuItem.Hidden = false;
                    AppendProgressLog("更新失败：" + (string.IsNullOrEmpty(reason) ? "未知错误" : reason));
                    if (!progressDismissedByUser) progressPanel.OrderFrontRegardless();
                });
            };
            c.DshVersionChanged = delegate (string version)
            {
                app.BeginInvokeOnMainThread(delegate { RefreshDshMenuTitles(); });
            };

            c.Start();
            // Background update check (the Windows backend asks about updates
            // synchronously before starting the service).
            c.CheckSelfUpdateAsync();

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

        private static NSMenuItem MakeBranchItem(string branch, nint tag)
        {
            NSMenuItem item = MakeItem(core.BranchDisplayName(branch), "setBranch:", actions);
            item.Tag = tag;
            item.State = core.DshBranch == branch ? NSCellStateValue.On : NSCellStateValue.Off;
            return item;
        }

        /// <summary>Rebuild the Profile submenu from the discovered profiles and
        /// refresh its parent title (called after boot, switch, and create).</summary>
        private static void RefreshProfileMenu()
        {
            if (profileMenu == null) return;
            profileMenu.RemoveAllItems();
            profileMenuNames.Clear();

            System.Collections.Generic.List<string> profiles = core.GetAvailableProfiles();
            for (int i = 0; i < profiles.Count; i++)
            {
                profileMenuNames.Add(profiles[i]);
                NSMenuItem item = MakeItem(profiles[i], "setProfile:", actions);
                item.Tag = i;
                item.State = core.DshProfile == profiles[i] ? NSCellStateValue.On : NSCellStateValue.Off;
                profileMenu.AddItem(item);
            }
            profileMenu.AddItem(NSMenuItem.SeparatorItem);
            profileMenu.AddItem(MakeItem("创建 Profile…", "createProfile:", actions));
            profileMenu.AddItem(MakeItem("删除 Profile…", "deleteProfile:", actions));

            if (profileParentItem != null)
                profileParentItem.Title = "Profile（当前：" + core.DshProfile + "）";
        }

        private static void RefreshDshMenuTitles()
        {
            if (dshVersionMenuItem != null) dshVersionMenuItem.Title = core.DshVersionDisplay;
            if (branchLatestItem != null) branchLatestItem.Title = core.BranchDisplayName(AppSettings.LatestBranch);
            if (branchNextItem != null) branchNextItem.Title = core.BranchDisplayName(AppSettings.NextBranch);
            if (branchAlphaItem != null) branchAlphaItem.Title = core.BranchDisplayName(AppSettings.AlphaBranch);
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
                AppendProgressLog("下载进度：准备下载…");
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
            AppendProgressLog("下载进度：" + SelfUpdater.FormatBytes(received)
                + (total > 0 ? " / " + SelfUpdater.FormatBytes(total) : ""));
            if (!progressDismissedByUser) progressPanel.OrderFrontRegardless();
        }

        private static void ApplyMacUpdate(string dmgPath)
        {
            EnsureProgressWindow();
            progressStatus.StringValue = "已下载，正在安装并重启…";
            AppendProgressLog("即将静默安装并重新启动。");
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
                new CGRect(0, 0, 500, 340),
                NSWindowStyle.Titled | NSWindowStyle.Closable,
                NSBackingStore.Buffered,
                false);
            progressPanel.Title = "DeepSeek Harness 更新";
            progressPanel.ReleasedWhenClosed = false;
            progressPanel.FloatingPanel = true;
            progressPanel.HidesOnDeactivate = false;
            progressPanel.WillClose += delegate { progressDismissedByUser = true; };

            progressStatus = CreateLabel(new CGRect(24, 284, 452, 28), "正在准备更新…", 15);
            progressBar = new NSProgressIndicator(new CGRect(24, 256, 452, 16));
            progressBar.Style = NSProgressIndicatorStyle.Bar;
            progressBar.Indeterminate = true;
            progressBar.StartAnimation(null);

            progressLogScroll = new NSScrollView(new CGRect(24, 72, 452, 176));
            progressLogScroll.HasVerticalScroller = true;
            progressLogScroll.BorderType = NSBorderType.BezelBorder;
            progressLogView = new NSTextView(new CGRect(0, 0, 452, 176));
            progressLogView.Editable = false;
            progressLogView.Selectable = true;
            progressLogView.Font = NSFont.SystemFontOfSize(11);
            progressLogView.DrawsBackground = false;
            progressLogScroll.DocumentView = progressLogView;

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
            progressPanel.ContentView.AddSubview(progressLogScroll);
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
            if (!string.IsNullOrWhiteSpace(detail)) AppendProgressLog(detail);
            if (!progressDismissedByUser)
            {
                progressPanel.OrderFrontRegardless();
                if (progressIsCompleted) ScheduleAutoHide(1000);
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
                ScheduleAutoHide(1000);
            }
        }

        private static void ScheduleAutoHide(int delayMs)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(delayMs);
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

        private static void AppendProgressLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string line = text.Trim();
            if (line.Length > 220) line = line.Substring(0, 217) + "...";
            bool downloadLine = line.StartsWith("下载进度", StringComparison.Ordinal);
            if (progressLogLines.Count > 0)
            {
                string last = progressLogLines[progressLogLines.Count - 1];
                bool lastDownload = last.StartsWith("下载进度", StringComparison.Ordinal);
                // Download progress is a single rolling line: keep updating it
                // in place instead of flooding the log with one entry per tick.
                if (downloadLine && lastDownload)
                {
                    if (last != line) progressLogLines[progressLogLines.Count - 1] = line;
                    RefreshLogView();
                    return;
                }
                if (last == line) return;
            }
            progressLogLines.Add(line);
            if (progressLogLines.Count > MaxProgressLogLines)
                progressLogLines.RemoveRange(0, progressLogLines.Count - MaxProgressLogLines);
            RefreshLogView();
        }

        private static void RefreshLogView()
        {
            if (progressLogView == null) return;
            string text = string.Join("\n", progressLogLines);
            progressLogView.Value = text;
            if (text.Length > 0)
                progressLogView.ScrollRangeToVisible(new NSRange((nint)text.Length, 0));
        }

        private static void NotifyToWindow(string text)
        {
            AppendProgressLog(text);
            EnsureProgressWindow();
            progressMenuItem.Hidden = false;
            if (!progressDismissedByUser)
            {
                progressPanel.OrderFrontRegardless();
                if (progressIsCompleted) ScheduleAutoHide(6000);
            }
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

            [Export("setBranch:")]
            public void SetBranch(NSObject sender)
            {
                NSMenuItem item = sender as NSMenuItem;
                if (item == null) return;
                string branch = item.Tag == 1 ? AppSettings.LatestBranch
                    : item.Tag == 2 ? AppSettings.NextBranch
                    : AppSettings.AlphaBranch;
                core.SetDshBranch(branch);
            }

            [Export("setProfile:")]
            public void SetProfile(NSObject sender)
            {
                NSMenuItem item = sender as NSMenuItem;
                if (item == null) return;
                int index = (int)item.Tag;
                if (index < 0 || index >= profileMenuNames.Count) return;
                core.SetDshProfile(profileMenuNames[index]);
                RefreshProfileMenu();
            }

            [Export("createProfile:")]
            public void CreateProfile(NSObject sender)
            {
                NSAlert alert = new NSAlert();
                alert.MessageText = "创建 Profile";
                alert.InformativeText = "输入新 Profile 名称（字母、数字、-、_、.，以字母或数字开头）：";
                NSTextField input = new NSTextField(new CGRect(0, 0, 240, 24));
                alert.AccessoryView = input;
                alert.AddButton("创建");
                alert.AddButton("取消");
                long result = (long)alert.RunModal();
                if (result != 1000 /* NSAlertFirstButtonReturn */) return;

                string name = (input.StringValue ?? "").Trim();
                if (!AppSettings.IsValidProfileName(name))
                {
                    NSAlert invalid = new NSAlert();
                    invalid.MessageText = "名称无效";
                    invalid.InformativeText = "仅允许字母、数字、-、_、.，且以字母或数字开头（desktop 与 node_modules 为保留名）。";
                    invalid.RunModal();
                    return;
                }
                string error;
                if (!core.CreateProfile(name, out error))
                {
                    NSAlert failed = new NSAlert();
                    failed.MessageText = "创建 Profile 失败";
                    failed.InformativeText = error ?? "未知错误";
                    failed.RunModal();
                    return;
                }
                core.SetDshProfile(name);
                RefreshProfileMenu();
                NotifyToWindow("Profile " + name + " 已创建并切换。");
            }

            [Export("deleteProfile:")]
            public void DeleteProfile(NSObject sender)
            {
                System.Collections.Generic.List<string> profiles = core.GetAvailableProfiles();
                if (profiles.Count == 0) return;

                NSAlert alert = new NSAlert();
                alert.MessageText = "删除 Profile";
                alert.InformativeText = "选择要删除的 Profile（当前使用的不可删除）：";
                NSPopUpButton popup = new NSPopUpButton(new CGRect(0, 0, 240, 26), true);
                for (int i = 0; i < profiles.Count; i++)
                {
                    popup.AddItem(profiles[i]);
                    if (string.Equals(profiles[i], core.DshProfile, StringComparison.Ordinal))
                    {
                        // The profile in use is listed for context, but disabled.
                        NSMenuItem item = popup.LastItem;
                        if (item != null) item.Enabled = false;
                    }
                }
                alert.AccessoryView = popup;
                alert.AddButton("删除");
                alert.AddButton("取消");
                long result = (long)alert.RunModal();
                if (result != 1000 || string.Equals(popup.TitleOfSelectedItem ?? "", core.DshProfile, StringComparison.Ordinal))
                    return;

                string name = popup.TitleOfSelectedItem;
                if (string.IsNullOrEmpty(name)) return;

                NSAlert confirm = new NSAlert();
                confirm.MessageText = "确定删除";
                confirm.InformativeText = "确定删除 Profile「" + name + "」吗？\n将删除该 Profile 的全部插件与配置，操作不可恢复。";
                confirm.AddButton("删除");
                confirm.AddButton("取消");
                if ((long)confirm.RunModal() != 1000) return;

                string error;
                if (!core.DeleteProfile(name, out error))
                {
                    NSAlert failed = new NSAlert();
                    failed.MessageText = "删除 Profile 失败";
                    failed.InformativeText = error ?? "未知错误";
                    failed.RunModal();
                    return;
                }
                RefreshProfileMenu();
                NotifyToWindow("Profile " + name + " 已删除。");
            }

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
