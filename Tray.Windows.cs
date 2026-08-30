#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DshTray
{
    /// <summary>Windows tray backend (pure Win32).</summary>
    internal static class Platform
    {
        private const uint WM_NULL = 0x0000;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_TIMER = 0x0113;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_MOUSELEAVE = 0x02A3;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_TRAYICON = 0x0401;
        private const uint WM_APP_EXIT = 0x8001;
        private const uint WM_APP_NOTIFY = 0x8002;
        private const uint WM_APP_STATUS = 0x8003;
        private const uint WM_APP_PROGRESS = 0x8004;

        private const int ID_OPEN = 1001;
        private const int ID_LOG = 1002;
        private const int ID_RESTART = 1003;
        private const int ID_EXIT = 1004;
        private const int ID_PROGRESS = 1005;

        private const uint MF_STRING = 0x0000;
        private const uint MF_GRAYED = 0x0001;
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

        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_SYSMENU = 0x00080000;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int IDC_HAND = 32649;
        private const uint TME_LEAVE = 0x00000002;
        private const int TRANSPARENT = 1;
        private const int NULL_PEN = 8;
        private const uint DT_LEFT = 0x0000;
        private const uint DT_VCENTER = 0x0004;
        private const uint DT_WORDBREAK = 0x0010;
        private const uint DT_SINGLELINE = 0x0020;
        private const uint DT_NOPREFIX = 0x0800;
        private const uint DT_END_ELLIPSIS = 0x8000;
        private const uint SRCCOPY = 0x00CC0020;

        private const string ClassName = "DshTrayWindow";
        private const string IconResourceName = "icon.ico";

        private static Core core;
        private static IntPtr hwnd;
        private static IntPtr progressHwnd;
        private static IntPtr hIcon;
        private static IntPtr headingFont;
        private static IntPtr bodyFont;
        private static IntPtr logFont;
        private static string currentProgressStage = "正在准备更新…";
        private static string currentProgressDetail = "等待 npm 输出…";
        private static bool progressIsCompleted;
        private static int progressAnimationOffset;
        private static int hoveredProgressButton;
        private static int pressedProgressButton;
        private static bool useDarkTheme;
        private static readonly object trayLock = new object();
        private static readonly object notificationLock = new object();
        private static readonly object statusLock = new object();
        private static readonly object progressLock = new object();
        private static string pendingStatus;
        private static string currentStatus = "DeepSeek Harness";
        private static string pendingProgressStage;
        private static string pendingProgressDetail;
        private static bool pendingProgressCompleted;
        private static bool pendingProgressStarted;
        private static bool hasUpdateProgress;
        private static bool progressDismissedByUser;
        private static readonly Queue<Tuple<string, string>> pendingNotifications = new Queue<Tuple<string, string>>();

        public static int Run(Core c)
        {
            core = c;
            if (!CreateWindowAndTray()) return 1;

            c.OnShutdownRequest = delegate { PostMessage(hwnd, WM_APP_EXIT, IntPtr.Zero, IntPtr.Zero); };
            c.Notify = QueueNotification;
            c.StatusChanged = QueueStatus;
            c.UpdateProgressStarted = QueueProgressStarted;
            c.UpdateProgressChanged = QueueProgress;
            c.UpdateProgressCompleted = QueueProgressCompleted;

            c.Start();

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
                else if (id == ID_PROGRESS)
                {
                    progressDismissedByUser = false;
                    ShowProgressWindow();
                }
                else if (id == ID_RESTART) core.RestartServer();
                else if (id == ID_EXIT) Shutdown();
                return IntPtr.Zero;
            }

            if (msg == WM_APP_EXIT)
            {
                Shutdown();
                return IntPtr.Zero;
            }

            if (msg == WM_APP_NOTIFY)
            {
                DrainNotifications();
                return IntPtr.Zero;
            }

            if (msg == WM_APP_STATUS)
            {
                DrainStatus();
                return IntPtr.Zero;
            }

            if (msg == WM_APP_PROGRESS)
            {
                DrainProgress();
                return IntPtr.Zero;
            }

            if (hWnd == progressHwnd && msg == WM_CLOSE)
            {
                progressDismissedByUser = true;
                ShowWindow(progressHwnd, SW_HIDE);
                return IntPtr.Zero;
            }

            if (hWnd == progressHwnd && msg == WM_TIMER)
            {
                long timerId = wParam.ToInt64();
                if (timerId == 1)
                {
                    KillTimer(progressHwnd, (UIntPtr)1);
                    ShowWindow(progressHwnd, SW_HIDE);
                }
                else if (timerId == 2 && !progressIsCompleted)
                {
                    progressAnimationOffset = (progressAnimationOffset + 10) % 620;
                    InvalidateRect(progressHwnd, IntPtr.Zero, false);
                }
                return IntPtr.Zero;
            }

            if (hWnd == progressHwnd && msg == WM_LBUTTONUP)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                int button = GetProgressButtonAt(x, y);
                int clicked = pressedProgressButton == button ? button : 0;
                pressedProgressButton = 0;
                ReleaseCapture();
                InvalidateRect(progressHwnd, IntPtr.Zero, false);
                if (clicked == 1) core.OpenLog();
                else if (clicked == 2)
                {
                    progressDismissedByUser = true;
                    ShowWindow(progressHwnd, SW_HIDE);
                }
                return IntPtr.Zero;
            }

            if (hWnd == progressHwnd && msg == WM_LBUTTONDOWN)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                pressedProgressButton = GetProgressButtonAt(x, y);
                if (pressedProgressButton != 0) SetCapture(progressHwnd);
                InvalidateRect(progressHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == progressHwnd && msg == WM_MOUSEMOVE)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                int button = GetProgressButtonAt(x, y);
                if (button != hoveredProgressButton)
                {
                    hoveredProgressButton = button;
                    InvalidateRect(progressHwnd, IntPtr.Zero, false);
                }
                if (button != 0) SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND));
                TRACKMOUSEEVENT tracking = new TRACKMOUSEEVENT();
                tracking.cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>();
                tracking.dwFlags = TME_LEAVE;
                tracking.hwndTrack = progressHwnd;
                TrackMouseEvent(ref tracking);
                return IntPtr.Zero;
            }

            if (hWnd == progressHwnd && msg == WM_MOUSELEAVE)
            {
                hoveredProgressButton = 0;
                if (pressedProgressButton == 0) InvalidateRect(progressHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == progressHwnd && msg == WM_PAINT)
            {
                PaintProgressWindow();
                return IntPtr.Zero;
            }

            if (hWnd == progressHwnd && msg == WM_ERASEBKGND) return (IntPtr)1;

            if (hWnd == progressHwnd && msg == WM_SETTINGCHANGE)
            {
                RefreshSystemTheme();
                InvalidateRect(progressHwnd, IntPtr.Zero, false);
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
            AppendMenu(menu, MF_STRING | MF_GRAYED, 0, currentStatus);
            if (hasUpdateProgress) AppendMenu(menu, MF_STRING, (uint)ID_PROGRESS, "显示更新进度");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, (uint)ID_OPEN, "打开网页");
            AppendMenu(menu, MF_STRING, (uint)ID_LOG, "查看日志");
            AppendMenu(menu, MF_STRING, (uint)ID_RESTART, "重启服务器");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, (uint)ID_EXIT, "退出并停止服务");

            SetForegroundWindow(hwnd);
            POINT pt;
            GetCursorPos(out pt);
            TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_LEFTALIGN, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(menu);
        }

        private static void QueueNotification(string title, string text)
        {
            lock (notificationLock)
            {
                pendingNotifications.Enqueue(Tuple.Create(title, text));
            }

            // Core can call Notify from npx output / port-watcher threads, so marshal
            // the actual Shell_NotifyIcon call back to the tray window's UI thread.
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_NOTIFY, IntPtr.Zero, IntPtr.Zero);
        }

        private static void DrainNotifications()
        {
            while (true)
            {
                Tuple<string, string> n;
                lock (notificationLock)
                {
                    if (pendingNotifications.Count == 0) return;
                    n = pendingNotifications.Dequeue();
                }
                ShowBalloon(n.Item1, n.Item2, NIIF_INFO);
            }
        }


        private static void QueueStatus(string text)
        {
            lock (statusLock)
            {
                pendingStatus = text;
            }

            // Same marshalling pattern as notifications: the tooltip update must
            // run on the thread that owns the tray window.
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_STATUS, IntPtr.Zero, IntPtr.Zero);
        }

        private static void DrainStatus()
        {
            string text;
            lock (statusLock)
            {
                text = pendingStatus;
                pendingStatus = null;
            }
            if (text != null)
            {
                currentStatus = text;
                UpdateTooltip(text);
            }
        }

        private static void QueueProgress(string stage, string detail)
        {
            lock (progressLock)
            {
                pendingProgressStage = stage;
                if (!string.IsNullOrWhiteSpace(detail)) pendingProgressDetail = detail;
                hasUpdateProgress = true;
            }
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_PROGRESS, IntPtr.Zero, IntPtr.Zero);
        }

        private static void QueueProgressStarted()
        {
            lock (progressLock)
            {
                pendingProgressStarted = true;
                hasUpdateProgress = true;
            }
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_PROGRESS, IntPtr.Zero, IntPtr.Zero);
        }

        private static void QueueProgressCompleted()
        {
            lock (progressLock)
            {
                pendingProgressCompleted = true;
            }
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_PROGRESS, IntPtr.Zero, IntPtr.Zero);
        }

        private static void DrainProgress()
        {
            string stage;
            string detail;
            bool completed;
            bool started;
            lock (progressLock)
            {
                stage = pendingProgressStage;
                detail = pendingProgressDetail;
                completed = pendingProgressCompleted;
                started = pendingProgressStarted;
                pendingProgressCompleted = false;
                pendingProgressStarted = false;
            }

            EnsureProgressWindow();
            if (progressHwnd == IntPtr.Zero) return;
            if (started)
            {
                progressDismissedByUser = false;
                progressIsCompleted = false;
            }
            if (stage != null) currentProgressStage = stage;
            if (detail != null) currentProgressDetail = TrimProgressDetail(detail);

            if (completed)
            {
                progressIsCompleted = true;
                KillTimer(progressHwnd, (UIntPtr)2);
                // Service is ready: show for 1 second, then fall back to the tray.
                SetTimer(progressHwnd, (UIntPtr)1, 1000, IntPtr.Zero);
            }
            else if (!progressIsCompleted && stage != null)
            {
                SetTimer(progressHwnd, (UIntPtr)2, 35, IntPtr.Zero);
            }
            InvalidateRect(progressHwnd, IntPtr.Zero, false);
            if (!progressDismissedByUser)
            {
                ShowProgressWindow();
                if (progressIsCompleted)
                {
                    // Log lines arriving after startup re-show the window once;
                    // hide it again after 1 second.
                    SetTimer(progressHwnd, (UIntPtr)1, 1000, IntPtr.Zero);
                }
            }
        }

        private static void EnsureProgressWindow()
        {
            if (progressHwnd != IntPtr.Zero) return;

            const int width = 540;
            const int height = 280;
            int x = Math.Max(0, GetSystemMetrics(SM_CXSCREEN) - width - 24);
            int y = Math.Max(0, GetSystemMetrics(SM_CYSCREEN) - height - 64);
            IntPtr hInstance = GetModuleHandle(null);
            progressHwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName,
                "DeepSeek Harness 更新", WS_CAPTION | WS_SYSMENU,
                x, y, width, height, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (progressHwnd == IntPtr.Zero) return;
            SendMessage(progressHwnd, 0x0080, (IntPtr)1, hIcon); // WM_SETICON / ICON_BIG
            RefreshSystemTheme();
            headingFont = CreateUiFont(-22, 600, "Microsoft YaHei UI");
            bodyFont = CreateUiFont(-14, 400, "Microsoft YaHei UI");
            logFont = CreateUiFont(-13, 400, "Cascadia Mono");
            SetTimer(progressHwnd, (UIntPtr)2, 35, IntPtr.Zero);
        }

        private static void ShowProgressWindow()
        {
            if (progressHwnd == IntPtr.Zero) EnsureProgressWindow();
            if (progressHwnd != IntPtr.Zero) ShowWindow(progressHwnd, SW_SHOWNOACTIVATE);
        }

        private static string TrimProgressDetail(string value)
        {
            string text = value.Trim();
            if (text.Length > 180) text = text.Substring(0, 177) + "...";
            return text;
        }

        private static void PaintProgressWindow()
        {
            PAINTSTRUCT paint;
            IntPtr target = BeginPaint(progressHwnd, out paint);
            if (target == IntPtr.Zero) return;

            RECT client;
            GetClientRect(progressHwnd, out client);
            IntPtr buffer = CreateCompatibleDC(target);
            IntPtr bitmap = CreateCompatibleBitmap(target, client.Right, client.Bottom);
            IntPtr oldBitmap = SelectObject(buffer, bitmap);

            int background = useDarkTheme ? Rgb(31, 33, 36) : Rgb(250, 251, 252);
            int heading = useDarkTheme ? Rgb(242, 244, 246) : Rgb(24, 29, 35);
            int secondary = useDarkTheme ? Rgb(169, 176, 184) : Rgb(91, 99, 108);
            int logText = useDarkTheme ? Rgb(215, 219, 224) : Rgb(51, 58, 66);
            int trackColor = useDarkTheme ? Rgb(67, 72, 78) : Rgb(222, 226, 230);
            int accent = useDarkTheme ? Rgb(45, 169, 151) : Rgb(23, 126, 113);
            FillColor(buffer, client, background);

            RECT stageRect = new RECT(24, 22, client.Right - 24, 52);
            DrawLabel(buffer, currentProgressStage, stageRect, headingFont, heading,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

            RECT track = new RECT(24, 68, client.Right - 24, 74);
            FillRounded(buffer, track, 6, trackColor);
            if (progressIsCompleted)
            {
                FillRounded(buffer, track, 6, accent);
            }
            else
            {
                int trackWidth = track.Right - track.Left;
                int segmentWidth = Math.Max(90, trackWidth / 4);
                int segmentX = track.Left + progressAnimationOffset - segmentWidth;
                RECT segment = new RECT(Math.Max(track.Left, segmentX), track.Top,
                    Math.Min(track.Right, segmentX + segmentWidth), track.Bottom);
                if (segment.Right > segment.Left) FillRounded(buffer, segment, 6, accent);
            }

            RECT logTitle = new RECT(24, 92, client.Right - 24, 114);
            DrawLabel(buffer, "最新日志", logTitle, bodyFont, secondary,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            RECT logRect = new RECT(24, 116, client.Right - 24, client.Bottom - 70);
            DrawLabel(buffer, currentProgressDetail, logRect, logFont, logText,
                DT_LEFT | DT_WORDBREAK | DT_NOPREFIX | DT_END_ELLIPSIS);

            RECT logButton = GetLogButtonRect(client);
            int logButtonColor = useDarkTheme
                ? (pressedProgressButton == 1 ? Rgb(83, 89, 96)
                    : hoveredProgressButton == 1 ? Rgb(70, 76, 82) : Rgb(55, 60, 66))
                : (pressedProgressButton == 1 ? Rgb(205, 211, 216)
                    : hoveredProgressButton == 1 ? Rgb(218, 223, 227) : Rgb(232, 235, 238));
            FillRounded(buffer, logButton, 6, logButtonColor);
            DrawLabel(buffer, "查看日志", logButton, bodyFont,
                useDarkTheme ? Rgb(242, 244, 246) : Rgb(38, 44, 51),
                DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | 0x0001);
            RECT hideButton = GetHideButtonRect(client);
            int hideButtonColor = useDarkTheme
                ? (pressedProgressButton == 2 ? Rgb(25, 126, 112)
                    : hoveredProgressButton == 2 ? Rgb(54, 186, 166) : accent)
                : (pressedProgressButton == 2 ? Rgb(12, 91, 82)
                    : hoveredProgressButton == 2 ? Rgb(15, 110, 99) : accent);
            FillRounded(buffer, hideButton, 6, hideButtonColor);
            DrawLabel(buffer, "后台运行", hideButton, bodyFont, Rgb(255, 255, 255),
                DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | 0x0001);

            BitBlt(target, 0, 0, client.Right, client.Bottom, buffer, 0, 0, SRCCOPY);
            SelectObject(buffer, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(buffer);
            EndPaint(progressHwnd, ref paint);
        }

        private static RECT GetLogButtonRect(RECT client)
        {
            return new RECT(client.Right - 232, client.Bottom - 50, client.Right - 128, client.Bottom - 16);
        }

        private static RECT GetHideButtonRect(RECT client)
        {
            return new RECT(client.Right - 120, client.Bottom - 50, client.Right - 24, client.Bottom - 16);
        }

        private static bool PointInRect(RECT rect, int x, int y)
        {
            return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
        }

        private static int GetProgressButtonAt(int x, int y)
        {
            RECT client;
            GetClientRect(progressHwnd, out client);
            if (PointInRect(GetLogButtonRect(client), x, y)) return 1;
            if (PointInRect(GetHideButtonRect(client), x, y)) return 2;
            return 0;
        }

        private static int Rgb(int red, int green, int blue)
        {
            return red | (green << 8) | (blue << 16);
        }

        private static void RefreshSystemTheme()
        {
            bool dark = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (value is int) dark = (int)value == 0;
                }
            }
            catch
            {
            }

            useDarkTheme = dark;
            if (progressHwnd != IntPtr.Zero)
            {
                int enabled = dark ? 1 : 0;
                DwmSetWindowAttribute(progressHwnd, 20, ref enabled, sizeof(int));
            }
        }

        private static IntPtr CreateUiFont(int height, int weight, string face)
        {
            return CreateFont(height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);
        }

        private static void FillColor(IntPtr dc, RECT rect, int color)
        {
            IntPtr brush = CreateSolidBrush(color);
            FillRect(dc, ref rect, brush);
            DeleteObject(brush);
        }

        private static void FillRounded(IntPtr dc, RECT rect, int radius, int color)
        {
            IntPtr brush = CreateSolidBrush(color);
            IntPtr oldBrush = SelectObject(dc, brush);
            IntPtr oldPen = SelectObject(dc, GetStockObject(NULL_PEN));
            RoundRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
            SelectObject(dc, oldPen);
            SelectObject(dc, oldBrush);
            DeleteObject(brush);
        }

        private static void DrawLabel(IntPtr dc, string text, RECT rect, IntPtr font, int color, uint format)
        {
            IntPtr oldFont = SelectObject(dc, font);
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, color);
            DrawText(dc, text ?? string.Empty, -1, ref rect, format);
            SelectObject(dc, oldFont);
        }

        private static void UpdateTooltip(string text)
        {
            try
            {
                if (text.Length > 127) text = text.Substring(0, 127);

                NOTIFYICONDATA nid = new NOTIFYICONDATA();
                nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
                nid.hWnd = hwnd;
                nid.uID = 1;
                nid.uFlags = NIF_TIP;
                nid.szTip = text;

                lock (trayLock)
                {
                    Shell_NotifyIcon(NIM_MODIFY, ref nid);
                }
            }
            catch
            {
            }
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
            if (progressHwnd != IntPtr.Zero)
            {
                DestroyWindow(progressHwnd);
                progressHwnd = IntPtr.Zero;
            }
            if (headingFont != IntPtr.Zero) { DeleteObject(headingFont); headingFont = IntPtr.Zero; }
            if (bodyFont != IntPtr.Zero) { DeleteObject(bodyFont); bodyFont = IntPtr.Zero; }
            if (logFont != IntPtr.Zero) { DeleteObject(logFont); logFont = IntPtr.Zero; }
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
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public RECT(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr Hdc;
            public int Erase;
            public RECT Paint;
            public int Restore;
            public int IncUpdate;
            public long Reserved1;
            public long Reserved2;
            public long Reserved3;
            public long Reserved4;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
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
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawText(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int fnObject);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int crColor);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        private static extern int SetTextColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation,
            int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
            uint clipPrecision, uint quality, uint pitchAndFamily, string face);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
            IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

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
