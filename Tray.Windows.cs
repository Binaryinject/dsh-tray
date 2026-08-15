#if WINDOWS
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DshTray
{
    /// <summary>Windows tray backend (pure Win32).</summary>
    internal static class Platform
    {
        private const uint WM_NULL = 0x0000;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_TRAYICON = 0x0401;
        private const uint WM_APP_EXIT = 0x8001;

        private const int ID_OPEN = 1001;
        private const int ID_LOG = 1002;
        private const int ID_EXIT = 1003;

        private const uint MF_STRING = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_BOTTOMALIGN = 0x0020;
        private const uint TPM_LEFTALIGN = 0x0000;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;

        private const uint NIIF_INFO = 0x00000001;
        private const uint NIIF_WARNING = 0x00000002;

        private const int IDI_APPLICATION = 32512;

        private const string ClassName = "DshTrayWindow";
        private const string IconResourceName = "icon.ico";

        private static Core core;
        private static IntPtr hwnd;
        private static IntPtr hIcon;
        private static readonly object trayLock = new object();

        public static int Run(Core c)
        {
            core = c;
            if (!CreateWindowAndTray()) return 1;

            c.OnShutdownRequest = delegate { PostMessage(hwnd, WM_APP_EXIT, IntPtr.Zero, IntPtr.Zero); };
            c.Notify = delegate(string title, string text) { ShowBalloon(title, text, NIIF_WARNING); };

            c.Start();
            ShowBalloon("DeepSeek Harness", "服务正在启动。", NIIF_INFO);

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            core.Shutdown();
            Cleanup();
            Environment.Exit(0);
            return 0;
        }

        private static bool CreateWindowAndTray()
        {
            IntPtr hInstance = GetModuleHandle(null);

            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>();
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcHandler);
            wc.hInstance = hInstance;
            wc.lpszClassName = ClassName;

            if (RegisterClassEx(ref wc) == 0) return false;

            hwnd = CreateWindowEx(0, ClassName, "dsh-tray", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return false;

            hIcon = LoadEmbeddedIcon();
            if (hIcon == IntPtr.Zero) hIcon = LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));

            NOTIFYICONDATA nid = new NOTIFYICONDATA();
            nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
            nid.hWnd = hwnd;
            nid.uID = 1;
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            nid.uCallbackMessage = WM_TRAYICON;
            nid.hIcon = hIcon;
            nid.szTip = "DeepSeek Harness";

            lock (trayLock)
            {
                Shell_NotifyIcon(NIM_ADD, ref nid);
            }
            return true;
        }

        private static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (core == null) return DefWindowProc(hWnd, msg, wParam, lParam);

            if (msg == WM_TRAYICON)
            {
                uint m = (uint)lParam.ToInt64();
                if (m == WM_RBUTTONUP) ShowMenu();
                else if (m == WM_LBUTTONDBLCLK) core.OpenBrowser();
                return IntPtr.Zero;
            }

            if (msg == WM_COMMAND)
            {
                int id = (int)((long)wParam & 0xffff);
                if (id == ID_OPEN) core.OpenBrowser();
                else if (id == ID_LOG) core.OpenLog();
                else if (id == ID_EXIT) Shutdown();
                return IntPtr.Zero;
            }

            if (msg == WM_APP_EXIT)
            {
                Shutdown();
                return IntPtr.Zero;
            }

            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static void Shutdown()
        {
            core.Shutdown();

            NOTIFYICONDATA nid = new NOTIFYICONDATA();
            nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
            nid.hWnd = hwnd;
            nid.uID = 1;
            lock (trayLock)
            {
                Shell_NotifyIcon(NIM_DELETE, ref nid);
            }

            PostQuitMessage(0);
        }

        private static void ShowMenu()
        {
            IntPtr menu = CreatePopupMenu();
            AppendMenu(menu, MF_STRING, (uint)ID_OPEN, "打开网页");
            AppendMenu(menu, MF_STRING, (uint)ID_LOG, "查看日志");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, (uint)ID_EXIT, "退出并停止服务");

            SetForegroundWindow(hwnd);
            POINT pt;
            GetCursorPos(out pt);
            TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_LEFTALIGN, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(menu);
        }

        private static void ShowBalloon(string title, string text, uint flags)
        {
            try
            {
                NOTIFYICONDATA nid = new NOTIFYICONDATA();
                nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
                nid.hWnd = hwnd;
                nid.uID = 1;
                nid.uFlags = NIF_INFO;
                nid.szInfo = text;
                nid.szInfoTitle = title;
                nid.dwInfoFlags = flags;
                nid.uTimeoutOrVersion = 5000;

                lock (trayLock)
                {
                    Shell_NotifyIcon(NIM_MODIFY, ref nid);
                }
            }
            catch
            {
            }
        }

        private static void Cleanup()
        {
            if (hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
                hIcon = IntPtr.Zero;
            }
            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
            }
        }

        private static IntPtr LoadEmbeddedIcon()
        {
            try
            {
                using (Stream s = typeof(Platform).Assembly.GetManifestResourceStream(IconResourceName))
                {
                    if (s == null) return IntPtr.Zero;

                    byte[] all = new byte[s.Length];
                    int read = 0;
                    while (read < all.Length)
                    {
                        int n = s.Read(all, read, all.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    if (all.Length < 22) return IntPtr.Zero;
                    int count = BitConverter.ToUInt16(all, 4);
                    if (count < 1) return IntPtr.Zero;

                    int bestOffset = -1;
                    uint bestSize = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int eo = 6 + 16 * i;
                        int w = all[eo]; if (w == 0) w = 256;
                        int h = all[eo + 1]; if (h == 0) h = 256;
                        uint size = BitConverter.ToUInt32(all, eo + 8);
                        uint off = BitConverter.ToUInt32(all, eo + 12);
                        if (bestOffset == -1) { bestOffset = (int)off; bestSize = size; }
                        if (w == 32 && h == 32) { bestOffset = (int)off; bestSize = size; break; }
                    }
                    if (bestOffset < 0) return IntPtr.Zero;

                    byte[] data = new byte[bestSize];
                    Array.Copy(all, bestOffset, data, 0, (int)bestSize);
                    return CreateIconFromResource(data, bestSize, true, 0x00030000);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        // ---- P/Invoke ----

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static readonly WndProcDelegate WndProcHandler = WndProcImpl;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconFromResource(byte[] presbits, uint dwResSize, bool fIcon, uint dwVer);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
    }
}
#endif
