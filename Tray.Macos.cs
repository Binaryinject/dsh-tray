#if MACOS
using System;
using System.IO;
using System.Reflection;
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
            c.Notify = delegate
            {
                // macOS notification is optional; skipped for now.
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
