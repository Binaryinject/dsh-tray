using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

// dsh-tray — tray launcher for the DeepSeek Harness web UI.
// Pure Win32 (no WinForms/System.Drawing), so it compiles with NativeAOT into a
// fully self-contained native .exe with no .NET runtime dependency.
//
//   dsh-tray                 port 3080, auto-open browser
//   dsh-tray --port 8080     custom port
//   dsh-tray --no-open       start server, do not open browser
//   dsh-tray --stop          tell a running instance to exit gracefully
internal sealed class TrayApp
{
    private const uint WM_NULL = 0x0000;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_TRAYICON = 0x0401;   // WM_USER + 1
    private const uint WM_APP_EXIT = 0x8001;   // WM_APP + 1

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

    private const int SW_SHOWNORMAL = 1;
    private const int IDI_APPLICATION = 32512;

    private const string MutexName = @"Local\DeepSeekHarnessTrayApp";
    private const string ReopenEventName = @"Local\DeepSeekHarnessTrayApp.Reopen";
    private const string ExitEventName = @"Local\DeepSeekHarnessTrayApp.Exit";
    private const string ClassName = "DeepSeekHarnessTrayWindow";
    private const string IconResourceName = "icon.ico";

    private readonly int port;
    private readonly bool autoOpen;
    private readonly string logPath;

    private Process server;
    private StreamWriter logWriter;
    private IntPtr hwnd;
    private IntPtr hIcon;
    private volatile bool shuttingDown;
    private readonly object trayLock = new object();
    private static readonly object logLock = new object();

    private static TrayApp current;

    private TrayApp(int port, bool autoOpen)
    {
        this.port = port;
        this.autoOpen = autoOpen;
        this.logPath = Path.Combine(Path.GetTempPath(), "dsh-tray-server.log");
    }

    private static int Main(string[] args)
    {
        int port = 3080;
        bool autoOpen = true;
        bool stop = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--port" && i + 1 < args.Length)
            {
                int p;
                if (int.TryParse(args[i + 1], out p)) { port = p; i++; }
            }
            else if (a == "--no-open")
            {
                autoOpen = false;
            }
            else if (a == "--stop")
            {
                stop = true;
            }
        }

        if (stop)
        {
            Signal(ExitEventName);
            return 0;
        }

        bool createdNew;
        using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
        {
            if (!createdNew)
            {
                Signal(ReopenEventName);
                return 0;
            }

            current = new TrayApp(port, autoOpen);
            return current.Run();
        }
    }

    private static void Signal(string name)
    {
        try
        {
            using (EventWaitHandle h = EventWaitHandle.OpenExisting(name))
            {
                h.Set();
            }
        }
        catch
        {
        }
    }

    private int Run()
    {
        if (!CreateWindowAndTray()) return 1;

        StartServer();
        if (autoOpen) StartPortWatcher();
        StartSignalListener();

        ShowBalloon("DeepSeek Harness",
            autoOpen ? "服务正在启动，浏览器将自动打开。" : "服务正在启动。",
            NIIF_INFO);

        AppLog("entering message loop");
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        AppLog("message loop exited");

        Cleanup();
        AppLog("cleanup done");
        Environment.Exit(0);
        return 0;
    }

    private bool CreateWindowAndTray()
    {
        IntPtr hInstance = GetModuleHandle(null);

        WNDCLASSEX wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>();
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcHandler);
        wc.hInstance = hInstance;
        wc.lpszClassName = ClassName;

        if (RegisterClassEx(ref wc) == 0) return false;

        hwnd = CreateWindowEx(0, ClassName, "DeepSeek Harness Tray", 0,
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
        if (current == null) return DefWindowProc(hWnd, msg, wParam, lParam);

        if (msg == WM_TRAYICON)
        {
            uint m = (uint)lParam.ToInt64();
            if (m == WM_RBUTTONUP) current.ShowMenu();
            else if (m == WM_LBUTTONDBLCLK) current.OpenBrowser();
            return IntPtr.Zero;
        }

        if (msg == WM_COMMAND)
        {
            int id = (int)((long)wParam & 0xffff);
            if (id == ID_OPEN) current.OpenBrowser();
            else if (id == ID_LOG) current.OpenLog();
            else if (id == ID_EXIT) current.Shutdown();
            return IntPtr.Zero;
        }

        if (msg == WM_APP_EXIT)
        {
            current.Shutdown();
            return IntPtr.Zero;
        }

        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
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

    private void AppLog(string msg)
    {
        try
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  [app] " + msg;
            if (logWriter != null)
            {
                lock (logWriter)
                {
                    logWriter.WriteLine(line);
                    logWriter.Flush();
                }
            }
            else
            {
                lock (logLock)
                {
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
            }
        }
        catch
        {
        }
    }

    private void StartServer()
    {
        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = "cmd.exe";
        psi.Arguments = "/c npx --yes @deepseek-ai/dsh web --port " + port;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        server = new Process();
        server.StartInfo = psi;
        server.EnableRaisingEvents = true;
        server.Exited += OnServerExited;

        try
        {
            logWriter = new StreamWriter(logPath, true);
        }
        catch
        {
            logWriter = null;
        }

        server.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { Log(e.Data); };
        server.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { Log(e.Data); };

        server.Start();
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
    }

    private void Log(string line)
    {
        if (line == null || logWriter == null) return;
        try
        {
            lock (logWriter)
            {
                logWriter.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line);
                logWriter.Flush();
            }
        }
        catch
        {
        }
    }

    private void StartPortWatcher()
    {
        Thread t = new Thread(delegate()
        {
            for (int i = 0; i < 240; i++)
            {
                if (shuttingDown) return;
                if (PortIsOpen())
                {
                    OpenBrowser();
                    return;
                }
                Thread.Sleep(500);
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private bool PortIsOpen()
    {
        TcpClient client = new TcpClient();
        try
        {
            IAsyncResult r = client.BeginConnect("127.0.0.1", port, null, null);
            if (r.AsyncWaitHandle.WaitOne(500))
            {
                client.EndConnect(r);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            client.Close();
        }
    }

    private void OpenBrowser()
    {
        try
        {
            ShellExecute(IntPtr.Zero, "open", "http://127.0.0.1:" + port, null, null, SW_SHOWNORMAL);
        }
        catch
        {
        }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(logPath)) File.WriteAllText(logPath, "");
            ShellExecute(IntPtr.Zero, "open", logPath, null, null, SW_SHOWNORMAL);
        }
        catch
        {
        }
    }

    private void StartSignalListener()
    {
        EventWaitHandle reopen = null;
        EventWaitHandle exit = null;
        try { reopen = new EventWaitHandle(false, EventResetMode.AutoReset, ReopenEventName); } catch { }
        try { exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName); } catch { }

        List<WaitHandle> handles = new List<WaitHandle>();
        if (reopen != null) handles.Add(reopen);
        if (exit != null) handles.Add(exit);
        if (handles.Count == 0) return;

        WaitHandle[] arr = handles.ToArray();

        Thread t = new Thread(delegate()
        {
            while (!shuttingDown)
            {
                int idx = WaitHandle.WaitAny(arr, 500);
                if (idx == WaitHandle.WaitTimeout) continue;
                if (arr[idx] == reopen) OpenBrowser();
                else if (arr[idx] == exit)
                {
                    AppLog("exit signal received");
                    PostMessage(hwnd, WM_APP_EXIT, IntPtr.Zero, IntPtr.Zero);
                }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    private void ShowBalloon(string title, string text, uint flags)
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

    private void OnServerExited(object sender, EventArgs e)
    {
        if (shuttingDown) return;
        ShowBalloon("DeepSeek Harness", "服务已退出，详情见日志。", NIIF_WARNING);
    }

    private void Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        AppLog("shutdown begin");

        try { StopServer(); } catch { }
        AppLog("stopserver done");

        try
        {
            NOTIFYICONDATA nid = new NOTIFYICONDATA();
            nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
            nid.hWnd = hwnd;
            nid.uID = 1;
            lock (trayLock)
            {
                Shell_NotifyIcon(NIM_DELETE, ref nid);
            }
        }
        catch { }
        AppLog("nif_delete done");

        PostQuitMessage(0);
        AppLog("postquit posted");
    }

    private void StopServer()
    {
        if (server == null) return;
        try
        {
            if (!server.HasExited)
            {
                Process p = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + server.Id + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p != null) p.WaitForExit(5000);
            }
        }
        catch
        {
        }
        server = null;
    }

    private void Cleanup()
    {
        StopServer();
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
        if (logWriter != null)
        {
            try { logWriter.Dispose(); } catch { }
            logWriter = null;
        }
    }

    private static IntPtr LoadEmbeddedIcon()
    {
        try
        {
            using (Stream s = typeof(TrayApp).Assembly.GetManifestResourceStream(IconResourceName))
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
                ushort count = BitConverter.ToUInt16(all, 4);
                if (count < 1) return IntPtr.Zero;

                uint bytesInRes = BitConverter.ToUInt32(all, 6 + 8);
                uint imageOffset = BitConverter.ToUInt32(all, 6 + 12);
                if (imageOffset + bytesInRes > all.Length) return IntPtr.Zero;

                byte[] data = new byte[bytesInRes];
                Array.Copy(all, (int)imageOffset, data, 0, (int)bytesInRes);
                return CreateIconFromResource(data, bytesInRes, true, 0x00030000);
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);
}
