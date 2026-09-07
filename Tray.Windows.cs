#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DshTray
{
    /// <summary>Windows tray backend (pure Win32).</summary>
    internal static class Platform
    {
        private const uint WM_NULL = 0x0000;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_CTLCOLOREDIT = 0x0133;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_TIMER = 0x0113;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_MOUSELEAVE = 0x02A3;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_CAPTURECHANGED = 0x0215;
        private const uint WM_TRAYICON = 0x0401;
        private const uint WM_APP_EXIT = 0x8001;
        private const uint WM_APP_STATUS = 0x8003;
        private const uint WM_APP_PROGRESS = 0x8004;
        private const uint WM_APP_UPDATE_AVAILABLE = 0x8005;
        private const uint WM_APP_UPDATE_PROGRESS = 0x8006;
        private const uint WM_APP_UPDATE_DONE = 0x8007;
        private const uint WM_APP_UPDATE_FAILED = 0x8008;

        private const int ID_OPEN = 1001;
        private const int ID_LOG = 1002;
        private const int ID_RESTART = 1003;
        private const int ID_EXIT = 1004;
        private const int ID_PROGRESS = 1005;
        private const int ID_CONSOLE = 1006;
        private const int ID_BRANCH_LATEST = 2001;
        private const int ID_BRANCH_NEXT = 2002;
        private const int ID_BRANCH_ALPHA = 2003;
        private const int ID_PROFILE_FIRST = 3001;
        private const int ID_PROFILE_DELETE = 3998;
        private const int ID_PROFILE_CREATE = 3999;

        private const uint MF_STRING = 0x0000;
        private const uint MF_GRAYED = 0x0001;
        private const uint MF_CHECKED = 0x0008;
        private const uint MF_POPUP = 0x0010;
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

        private const int IDI_APPLICATION = 32512;

        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int IDC_HAND = 32649;
        private const uint TME_LEAVE = 0x00000002;
        private const int TRANSPARENT = 1;
        private const int NULL_PEN = 8;
        private const uint DT_LEFT = 0x0000;
        private const uint DT_RIGHT = 0x0002;
        private const uint DT_VCENTER = 0x0004;
        private const uint DT_WORDBREAK = 0x0010;
        private const uint DT_SINGLELINE = 0x0020;
        private const uint DT_NOPREFIX = 0x0800;
        private const uint DT_END_ELLIPSIS = 0x8000;
        private const uint DT_CALCRECT = 0x0400;
        private const uint SRCCOPY = 0x00CC0020;

        private const string ClassName = "DshTrayWindow";
        private const string IconResourceName = "icon.ico";

        private static Core core;
        private static IntPtr hwnd;
        private static IntPtr progressHwnd;
        private static IntPtr updatePromptHwnd;
        private static IntPtr menuMainHwnd;
        private static IntPtr menuSubHwnd;
        private static IntPtr hIcon;
        private static IntPtr headingFont;
        private static IntPtr bodyFont;
        private static IntPtr logFont;
        private static IntPtr promptTitleFont;
        private static IntPtr promptBodyFont;
        private static string currentProgressStage = "正在准备更新…";
        private static bool progressIsCompleted;
        private static int progressAnimationOffset;
        private static int hoveredProgressButton;
        private static int pressedProgressButton;
        private static int updatePromptResult = -1;
        private static int hoveredPromptButton;
        private static int pressedPromptButton;
        private static IntPtr createProfileHwnd;
        private static IntPtr createProfileEditHwnd;
        private static int createProfileResult = -1;
        private static int hoveredCreateButton;
        private static int pressedCreateButton;
        private static string createProfileError;
        private static IntPtr createEditBrush;
        private static readonly List<string> menuProfiles = new List<string>();
        private const string CreateProfileErrorText = "名称无效：仅允许字母、数字、-、_、.，\n且以字母或数字开头。";

        // ---- Delete-profile dialog state ----
        private static IntPtr deleteProfileHwnd;
        private static readonly List<string> deleteProfileNames = new List<string>();
        private static int deleteProfileSelected = -1;
        private static int deleteProfileHover = -1;
        private static int deleteProfileResult = -1;
        private static int hoveredDeleteButton;
        private static int pressedDeleteButton;
        private static string deleteProfileError;

        // ---- Skinned popup menu state ----
        private sealed class MenuItemData
        {
            public string Text;
            public bool Enabled = true;
            public bool Checked;
            public bool IsSeparator;
            public bool HasSubmenu;
            public List<MenuItemData> Submenu;
            public int CommandId;
        }

        private static readonly List<MenuItemData> menuItems = new List<MenuItemData>();
        private static readonly List<MenuItemData> subMenuItems = new List<MenuItemData>();
        private static readonly List<int> menuItemTops = new List<int>();
        private static readonly List<int> menuItemHeights = new List<int>();
        private static readonly List<int> subItemTops = new List<int>();
        private static readonly List<int> subItemHeights = new List<int>();
        private static int menuContentWidth;
        private static int subMenuContentWidth;
        private static bool menuOpen;
        private static bool subMenuOpen;
        private static int menuHoverIndex = -1;
        private static int subMenuHoverIndex = -1;
        private static int openedSubmenuIndex = -1;
        private static int pendingMenuCommand;
        private static IntPtr menuFont;
        private const int MenuItemHeight = 32;
        private const int MenuSeparatorHeight = 10;
        private const int MenuPadding = 8;
        private static bool useDarkTheme;
        private static readonly object trayLock = new object();
        private static readonly object statusLock = new object();
        private static readonly object progressLock = new object();
        private static readonly List<string> progressLogLines = new List<string>();
        private const int MaxProgressLogLines = 400;
        private static bool pendingProgressNotify;
        private static string pendingStatus;
        private static string currentStatus = "DeepSeek Harness";
        private static string pendingProgressStage;
        private static string pendingProgressDetail;
        private static bool pendingProgressCompleted;
        private static bool pendingProgressStarted;
        private static bool hasUpdateProgress;
        private static bool progressDismissedByUser;
        private static readonly object updateLock = new object();
        private static string pendingUpdateTag;
        private static string pendingUpdateDownloadUrl;
        private static long pendingUpdateReceived;
        private static long pendingUpdateTotal;
        private static string pendingUpdateInstallerPath;
        private static string pendingUpdateError;
        private static int currentProgressPercent = -1;

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
            c.SelfUpdateAvailable = QueueUpdateAvailable;
            c.SelfUpdateProgress = QueueUpdateProgress;
            c.SelfUpdateDownloaded = QueueUpdateDownloaded;
            c.SelfUpdateFailed = QueueUpdateFailed;
            // The Windows menu is rebuilt on every right-click, so it reads
            // DshVersionDisplay directly; the callback only exists for macOS.
            c.DshVersionChanged = delegate (string version) { };

            // Ask about updates BEFORE starting the service: when the user
            // picks "update now", the installer flow relaunches the app and
            // the service must not boot against the old version.
            if (PromptUpdateBeforeStart())
            {
                serviceStarted = false;
                SetUpdateProgressUI(0, "开始下载更新…");
                core.BeginSelfUpdateDownload(pendingUpdateDownloadUrl);
            }
            else
            {
                serviceStarted = true;
                c.Start();
            }

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

            if (hWnd == menuMainHwnd || hWnd == menuSubHwnd)
            {
                return MenuWndProc(hWnd, msg, wParam, lParam);
            }

            if (msg == WM_COMMAND)
            {
                int id = (int)((long)wParam & 0xffff);
                if (id == ID_OPEN) core.OpenBrowser();
                else if (id == ID_CONSOLE) core.OpenConsole();
                else if (id == ID_LOG) core.OpenLog();
                else if (id == ID_PROGRESS)
                {
                    progressDismissedByUser = false;
                    ShowProgressWindow();
                }
                else if (id == ID_RESTART) core.RestartServer();
                else if (id == ID_BRANCH_LATEST) core.SetDshBranch(AppSettings.LatestBranch);
                else if (id == ID_BRANCH_NEXT) core.SetDshBranch(AppSettings.NextBranch);
                else if (id == ID_BRANCH_ALPHA) core.SetDshBranch(AppSettings.AlphaBranch);
                else if (id >= ID_PROFILE_FIRST && id < ID_PROFILE_FIRST + menuProfiles.Count)
                    core.SetDshProfile(menuProfiles[id - ID_PROFILE_FIRST]);
                else if (id == ID_PROFILE_CREATE) CreateProfileFromDialog();
                else if (id == ID_PROFILE_DELETE) DeleteProfileFromDialog();
                else if (id == ID_EXIT) Shutdown();
                return IntPtr.Zero;
            }

            if (msg == WM_APP_EXIT)
            {
                Shutdown();
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

            if (msg == WM_APP_UPDATE_AVAILABLE)
            {
                DrainUpdateAvailable();
                return IntPtr.Zero;
            }

            if (msg == WM_APP_UPDATE_PROGRESS)
            {
                DrainUpdateProgress();
                return IntPtr.Zero;
            }

            if (msg == WM_APP_UPDATE_DONE)
            {
                DrainUpdateDownloaded();
                return IntPtr.Zero;
            }

            if (msg == WM_APP_UPDATE_FAILED)
            {
                DrainUpdateFailed();
                return IntPtr.Zero;
            }

            if (hWnd == updatePromptHwnd && msg == WM_CLOSE)
            {
                // Treat close / ESC as "skip": start the service normally.
                updatePromptResult = 1;
                ShowWindow(updatePromptHwnd, SW_HIDE);
                PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                return IntPtr.Zero;
            }

            if (hWnd == updatePromptHwnd && msg == WM_LBUTTONUP)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                int button = GetPromptButtonAt(x, y);
                int clicked = pressedPromptButton == button ? button : 0;
                pressedPromptButton = 0;
                ReleaseCapture();
                InvalidateRect(updatePromptHwnd, IntPtr.Zero, false);
                if (clicked == 1) updatePromptResult = 0; // update now
                else if (clicked == 2) updatePromptResult = 1; // skip
                if (updatePromptResult != -1)
                {
                    ShowWindow(updatePromptHwnd, SW_HIDE);
                    PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                }
                return IntPtr.Zero;
            }

            if (hWnd == updatePromptHwnd && msg == WM_LBUTTONDOWN)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                pressedPromptButton = GetPromptButtonAt(x, y);
                if (pressedPromptButton != 0) SetCapture(updatePromptHwnd);
                InvalidateRect(updatePromptHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == updatePromptHwnd && msg == WM_MOUSEMOVE)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                int button = GetPromptButtonAt(x, y);
                if (button != hoveredPromptButton)
                {
                    hoveredPromptButton = button;
                    InvalidateRect(updatePromptHwnd, IntPtr.Zero, false);
                }
                if (button != 0) SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND));
                TRACKMOUSEEVENT tracking = new TRACKMOUSEEVENT();
                tracking.cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>();
                tracking.dwFlags = TME_LEAVE;
                tracking.hwndTrack = updatePromptHwnd;
                TrackMouseEvent(ref tracking);
                return IntPtr.Zero;
            }

            if (hWnd == updatePromptHwnd && msg == WM_MOUSELEAVE)
            {
                hoveredPromptButton = 0;
                if (pressedPromptButton == 0) InvalidateRect(updatePromptHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == updatePromptHwnd && msg == WM_PAINT)
            {
                PaintUpdatePrompt();
                return IntPtr.Zero;
            }

            if (hWnd == updatePromptHwnd && msg == WM_ERASEBKGND) return (IntPtr)1;

            if (hWnd == updatePromptHwnd && msg == WM_SETTINGCHANGE)
            {
                RefreshSystemTheme();
                InvalidateRect(updatePromptHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == createProfileHwnd && msg == WM_CLOSE)
            {
                createProfileResult = 1;
                ShowWindow(createProfileHwnd, SW_HIDE);
                return IntPtr.Zero;
            }

            if (hWnd == createProfileHwnd && msg == WM_LBUTTONUP)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                int button = GetCreateButtonAt(x, y);
                int clicked = pressedCreateButton == button ? button : 0;
                pressedCreateButton = 0;
                ReleaseCapture();
                InvalidateRect(createProfileHwnd, IntPtr.Zero, false);
                if (clicked == 1)
                {
                    string name = ReadCreateProfileInput();
                    if (!AppSettings.IsValidProfileName(name))
                    {
                        // Stay open and explain why the name was rejected.
                        createProfileError = CreateProfileErrorText;
                        InvalidateRect(createProfileHwnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    }
                    createProfileResult = 0;
                }
                else if (clicked == 2) createProfileResult = 1;
                if (createProfileResult != -1)
                {
                    ShowWindow(createProfileHwnd, SW_HIDE);
                    PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                }
                return IntPtr.Zero;
            }

            if (hWnd == createProfileHwnd && msg == WM_LBUTTONDOWN)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                pressedCreateButton = GetCreateButtonAt(x, y);
                if (pressedCreateButton != 0) SetCapture(createProfileHwnd);
                InvalidateRect(createProfileHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == createProfileHwnd && msg == WM_MOUSEMOVE)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                int button = GetCreateButtonAt(x, y);
                if (button != hoveredCreateButton)
                {
                    hoveredCreateButton = button;
                    InvalidateRect(createProfileHwnd, IntPtr.Zero, false);
                }
                if (button != 0) SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND));
                TRACKMOUSEEVENT tracking = new TRACKMOUSEEVENT();
                tracking.cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>();
                tracking.dwFlags = TME_LEAVE;
                tracking.hwndTrack = createProfileHwnd;
                TrackMouseEvent(ref tracking);
                return IntPtr.Zero;
            }

            if (hWnd == createProfileHwnd && msg == WM_MOUSELEAVE)
            {
                hoveredCreateButton = 0;
                if (pressedCreateButton == 0) InvalidateRect(createProfileHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == createProfileHwnd && msg == WM_PAINT)
            {
                PaintCreateProfileDialog();
                return IntPtr.Zero;
            }

            if (hWnd == createProfileHwnd && msg == WM_ERASEBKGND) return (IntPtr)1;

            if (hWnd == createProfileHwnd && msg == WM_SETTINGCHANGE)
            {
                RefreshSystemTheme();
                DestroyCreateEditBrush();
                InvalidateRect(createProfileHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == createProfileHwnd && msg == WM_CTLCOLOREDIT)
            {
                SetTextColor(wParam, useDarkTheme ? Rgb(242, 244, 246) : Rgb(24, 29, 35));
                SetBkColor(wParam, (uint)(useDarkTheme ? Rgb(45, 49, 54) : Rgb(255, 255, 255)));
                return EnsureCreateEditBrush();
            }

            if (hWnd == deleteProfileHwnd && msg == WM_CLOSE)
            {
                deleteProfileResult = 1;
                ShowWindow(deleteProfileHwnd, SW_HIDE);
                return IntPtr.Zero;
            }

            if (hWnd == deleteProfileHwnd && msg == WM_KEYDOWN && wParam.ToInt32() == 0x1B)
            {
                deleteProfileResult = 1;
                ShowWindow(deleteProfileHwnd, SW_HIDE);
                return IntPtr.Zero;
            }

            if (hWnd == deleteProfileHwnd && msg == WM_LBUTTONDOWN)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                int row = HitTestDeleteList(x, y);
                if (row >= 0 && row < deleteProfileNames.Count
                    && !string.Equals(deleteProfileNames[row], core.DshProfile, StringComparison.Ordinal))
                {
                    deleteProfileSelected = row;
                    deleteProfileError = null;
                    InvalidateRect(deleteProfileHwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }
                pressedDeleteButton = GetDeleteButtonAt(x, y);
                if (pressedDeleteButton != 0) SetCapture(deleteProfileHwnd);
                InvalidateRect(deleteProfileHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == deleteProfileHwnd && msg == WM_LBUTTONUP)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                int button = GetDeleteButtonAt(x, y);
                int clicked = pressedDeleteButton == button ? button : 0;
                pressedDeleteButton = 0;
                ReleaseCapture();
                InvalidateRect(deleteProfileHwnd, IntPtr.Zero, false);
                if (clicked == 1)
                {
                    if (deleteProfileSelected < 0 || deleteProfileSelected >= deleteProfileNames.Count
                        || string.Equals(deleteProfileNames[deleteProfileSelected], core.DshProfile, StringComparison.Ordinal))
                    {
                        deleteProfileError = "请先选择一个可删除的 Profile。";
                        InvalidateRect(deleteProfileHwnd, IntPtr.Zero, false);
                        return IntPtr.Zero;
                    }
                    deleteProfileResult = 0;
                }
                else if (clicked == 2) deleteProfileResult = 1;
                if (deleteProfileResult != -1)
                {
                    ShowWindow(deleteProfileHwnd, SW_HIDE);
                    PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                }
                return IntPtr.Zero;
            }

            if (hWnd == deleteProfileHwnd && msg == WM_MOUSEMOVE)
            {
                int x = (short)((long)lParam & 0xffff);
                int y = (short)(((long)lParam >> 16) & 0xffff);
                int row = HitTestDeleteList(x, y);
                if (row >= 0 && row < deleteProfileNames.Count
                    && string.Equals(deleteProfileNames[row], core.DshProfile, StringComparison.Ordinal))
                    row = -1; // never hover the profile in use
                if (row != deleteProfileHover)
                {
                    deleteProfileHover = row;
                    InvalidateRect(deleteProfileHwnd, IntPtr.Zero, false);
                }
                int button = GetDeleteButtonAt(x, y);
                if (button != hoveredDeleteButton)
                {
                    hoveredDeleteButton = button;
                    InvalidateRect(deleteProfileHwnd, IntPtr.Zero, false);
                }
                if (button != 0) SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND));
                TRACKMOUSEEVENT tracking = new TRACKMOUSEEVENT();
                tracking.cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>();
                tracking.dwFlags = TME_LEAVE;
                tracking.hwndTrack = deleteProfileHwnd;
                TrackMouseEvent(ref tracking);
                return IntPtr.Zero;
            }

            if (hWnd == deleteProfileHwnd && msg == WM_MOUSELEAVE)
            {
                deleteProfileHover = -1;
                hoveredDeleteButton = 0;
                if (pressedDeleteButton == 0) InvalidateRect(deleteProfileHwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (hWnd == deleteProfileHwnd && msg == WM_PAINT)
            {
                PaintDeleteProfileDialog();
                return IntPtr.Zero;
            }

            if (hWnd == deleteProfileHwnd && msg == WM_ERASEBKGND) return (IntPtr)1;

            if (hWnd == deleteProfileHwnd && msg == WM_SETTINGCHANGE)
            {
                RefreshSystemTheme();
                InvalidateRect(deleteProfileHwnd, IntPtr.Zero, false);
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
            if (menuOpen) return;
            RefreshSystemTheme();

            BuildMenuItems();

            if (menuMainHwnd == IntPtr.Zero) EnsureMenuWindows();
            if (menuMainHwnd == IntPtr.Zero) return;

            // Measure the main menu.
            int width = MeasureMenu(menuItems, menuItemTops, menuItemHeights, true, ref menuContentWidth);
            int height = MenuPadding * 2 + (menuItemTops.Count > 0 ? menuItemTops[menuItemTops.Count - 1] + menuItemHeights[menuItemHeights.Count - 1] : 0);
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            POINT pt;
            GetCursorPos(out pt);
            int x = pt.X;
            int y = pt.Y + 2;
            if (y + height > screenH) y = pt.Y - height - 2;
            if (y < 0) y = 0;
            if (x + width > screenW) x = screenW - width - 2;
            if (x < 0) x = 0;

            MoveWindow(menuMainHwnd, x, y, width, height, false);
            SetRoundedWindowRegion(menuMainHwnd, width, height);

            menuOpen = true;
            subMenuOpen = false;
            openedSubmenuIndex = -1;
            menuHoverIndex = -1;
            subMenuHoverIndex = -1;
            pendingMenuCommand = 0;

            // Foreground handshake FIRST: the process may not own the
            // foreground when the tray icon is clicked, so grab it via the
            // hidden tray window (classic WM_NULL trick). Showing the menu
            // before the handshake would let the handshake deactivate the
            // menu window and instantly close it.
            SetForegroundWindow(hwnd);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            InvalidateRect(menuMainHwnd, IntPtr.Zero, false);
            ShowWindow(menuMainHwnd, SW_SHOW);
            SetForegroundWindow(menuMainHwnd);
            SetCapture(menuMainHwnd);

            MSG msg;
            while (menuOpen && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            CloseMenu();
            if (pendingMenuCommand != 0)
                PostMessage(hwnd, WM_COMMAND, (IntPtr)pendingMenuCommand, IntPtr.Zero);
        }

        /// <summary>Build the skinned menu content from current core state.</summary>
        private static void BuildMenuItems()
        {
            menuItems.Clear();

            menuItems.Add(new MenuItemData { Text = currentStatus, Enabled = false });
            menuItems.Add(new MenuItemData { Text = "版本 " + SelfUpdater.GetCurrentVersion(), Enabled = false });
            menuItems.Add(new MenuItemData { Text = core.DshVersionDisplay, Enabled = false });

            MenuItemData branch = new MenuItemData
            {
                Text = "dsh 版本分支",
                HasSubmenu = true,
                Submenu = new List<MenuItemData>
                {
                    new MenuItemData { Text = core.BranchDisplayName(AppSettings.LatestBranch), Checked = core.DshBranch == AppSettings.LatestBranch, CommandId = ID_BRANCH_LATEST },
                    new MenuItemData { Text = core.BranchDisplayName(AppSettings.NextBranch), Checked = core.DshBranch == AppSettings.NextBranch, CommandId = ID_BRANCH_NEXT },
                    new MenuItemData { Text = core.BranchDisplayName(AppSettings.AlphaBranch), Checked = core.DshBranch == AppSettings.AlphaBranch, CommandId = ID_BRANCH_ALPHA }
                }
            };
            menuItems.Add(branch);

            List<string> profiles = core.GetAvailableProfiles();
            menuProfiles.Clear();
            menuProfiles.AddRange(profiles);
            MenuItemData profile = new MenuItemData
            {
                Text = "Profile（当前：" + core.DshProfile + "）",
                HasSubmenu = true,
                Submenu = new List<MenuItemData>()
            };
            for (int i = 0; i < profiles.Count; i++)
            {
                profile.Submenu.Add(new MenuItemData
                {
                    Text = profiles[i],
                    Checked = core.DshProfile == profiles[i],
                    CommandId = ID_PROFILE_FIRST + i
                });
            }
            profile.Submenu.Add(new MenuItemData { IsSeparator = true });
            profile.Submenu.Add(new MenuItemData { Text = "创建 Profile…", CommandId = ID_PROFILE_CREATE });
            profile.Submenu.Add(new MenuItemData { Text = "删除 Profile…", CommandId = ID_PROFILE_DELETE });
            menuItems.Add(profile);

            if (hasUpdateProgress)
                menuItems.Add(new MenuItemData { Text = "显示更新进度", CommandId = ID_PROGRESS });

            menuItems.Add(new MenuItemData { IsSeparator = true });
            menuItems.Add(new MenuItemData { Text = "打开网页", CommandId = ID_OPEN });
            menuItems.Add(new MenuItemData { Text = "启动 dsh 控制台", CommandId = ID_CONSOLE });
            menuItems.Add(new MenuItemData { Text = "查看日志", CommandId = ID_LOG });
            menuItems.Add(new MenuItemData { Text = "重启服务器", CommandId = ID_RESTART });
            menuItems.Add(new MenuItemData { IsSeparator = true });
            menuItems.Add(new MenuItemData { Text = "退出并停止服务", CommandId = ID_EXIT });
        }

        /// <summary>Measure a menu list; fills tops/heights. Returns the window width.</summary>
        private static int MeasureMenu(List<MenuItemData> items, List<int> tops, List<int> heights,
            bool hasArrow, ref int contentWidth)
        {
            tops.Clear();
            heights.Clear();
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr oldFont = SelectObject(dc, menuFont);
            int maxText = 0;
            int y = MenuPadding;
            for (int i = 0; i < items.Count; i++)
            {
                tops.Add(y);
                int h = items[i].IsSeparator ? MenuSeparatorHeight : MenuItemHeight;
                heights.Add(h);
                if (!items[i].IsSeparator)
                {
                    RECT r = new RECT(0, 0, 0, 0);
                    DrawText(dc, items[i].Text ?? string.Empty, -1, ref r,
                        DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
                    int w = r.Right - r.Left;
                    if (w > maxText) maxText = w;
                }
                y += h;
            }
            SelectObject(dc, oldFont);
            DeleteDC(dc);

            int arrowReserve = hasArrow ? 26 : 6;
            contentWidth = maxText + 30 + arrowReserve + MenuPadding * 2 + 2;
            return contentWidth;
        }

        private static void EnsureMenuWindows()
        {
            if (menuMainHwnd != IntPtr.Zero) return;
            IntPtr hInstance = GetModuleHandle(null);
            menuMainHwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName,
                "dsh-tray-menu", WS_POPUP,
                0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (menuMainHwnd == IntPtr.Zero) return;
            menuSubHwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName,
                "dsh-tray-submenu", WS_POPUP,
                0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            menuFont = CreateUiFont(-13, 400, "Microsoft YaHei UI");
        }

        private static void SetRoundedWindowRegion(IntPtr win, int width, int height)
        {
            try
            {
                IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, 8, 8);
                SetWindowRgn(win, region, true);
            }
            catch
            {
            }
        }

        private static void OpenSubmenu(int index)
        {
            MenuItemData item = menuItems[index];
            if (item.Submenu == null) return;

            subMenuItems.Clear();
            subMenuItems.AddRange(item.Submenu);
            int width = MeasureMenu(subMenuItems, subItemTops, subItemHeights, false, ref subMenuContentWidth);
            int height = MenuPadding * 2 + (subItemTops.Count > 0 ? subItemTops[subItemTops.Count - 1] + subItemHeights[subItemHeights.Count - 1] : 0);

            RECT wr;
            GetWindowRect(menuMainHwnd, out wr);
            int x = wr.Right - 2;
            int y = wr.Top + menuItemTops[index];
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            if (x + width > screenW) x = wr.Left - width + 2;
            if (x < 0) x = 0;
            if (y + height > screenH) y = screenH - height;
            if (y < 0) y = 0;

            MoveWindow(menuSubHwnd, x, y, width, height, false);
            SetRoundedWindowRegion(menuSubHwnd, width, height);
            subMenuOpen = true;
            openedSubmenuIndex = index;
            subMenuHoverIndex = -1;
            InvalidateRect(menuSubHwnd, IntPtr.Zero, false);
            ShowWindow(menuSubHwnd, SW_SHOWNOACTIVATE);
        }

        private static bool PointInWindow(IntPtr win, POINT pt)
        {
            RECT wr;
            GetWindowRect(win, out wr);
            return pt.X >= wr.Left && pt.X < wr.Right && pt.Y >= wr.Top && pt.Y < wr.Bottom;
        }

        private static int HitTestMenu(IntPtr win, List<MenuItemData> items, List<int> tops, List<int> heights, POINT pt)
        {
            RECT wr;
            GetWindowRect(win, out wr);
            if (pt.X < wr.Left || pt.X >= wr.Right || pt.Y < wr.Top || pt.Y >= wr.Bottom) return -1;
            int localY = pt.Y - wr.Top;
            for (int i = 0; i < items.Count; i++)
            {
                if (localY >= tops[i] && localY < tops[i] + heights[i])
                    return items[i].IsSeparator ? -1 : i;
            }
            return -1;
        }

        private static void UpdateMenuHover()
        {
            POINT pt;
            GetCursorPos(out pt);

            if (subMenuOpen && PointInWindow(menuSubHwnd, pt))
            {
                int idx = HitTestMenu(menuSubHwnd, subMenuItems, subItemTops, subItemHeights, pt);
                if (idx != subMenuHoverIndex)
                {
                    subMenuHoverIndex = idx;
                    InvalidateRect(menuSubHwnd, IntPtr.Zero, false);
                }
                return;
            }

            if (!PointInWindow(menuMainHwnd, pt)) return;
            int hover = HitTestMenu(menuMainHwnd, menuItems, menuItemTops, menuItemHeights, pt);
            if (hover != menuHoverIndex)
            {
                menuHoverIndex = hover;
                InvalidateRect(menuMainHwnd, IntPtr.Zero, false);
            }
            // Keep the open submenu while the pointer stays on its parent item (or
            // off the menu), switch/hide it only when another item is hovered.
            if (subMenuOpen && hover >= 0 && hover != openedSubmenuIndex)
            {
                subMenuOpen = false;
                openedSubmenuIndex = -1;
                subMenuHoverIndex = -1;
                ShowWindow(menuSubHwnd, SW_HIDE);
            }
            if (hover >= 0 && menuItems[hover].HasSubmenu && openedSubmenuIndex != hover)
                OpenSubmenu(hover);
        }

        private static void HandleMenuClick()
        {
            POINT pt;
            GetCursorPos(out pt);

            if (subMenuOpen && PointInWindow(menuSubHwnd, pt))
            {
                if (subMenuHoverIndex >= 0)
                    pendingMenuCommand = subMenuItems[subMenuHoverIndex].CommandId;
                menuOpen = false;
                return;
            }

            if (menuHoverIndex >= 0 && PointInWindow(menuMainHwnd, pt))
            {
                MenuItemData item = menuItems[menuHoverIndex];
                if (item.HasSubmenu)
                {
                    OpenSubmenu(menuHoverIndex);
                    return;
                }
                pendingMenuCommand = item.CommandId;
                menuOpen = false;
                return;
            }

            // Clicked outside: dismiss.
            menuOpen = false;
        }

        private static void CloseMenu()
        {
            menuOpen = false;
            subMenuOpen = false;
            openedSubmenuIndex = -1;
            menuHoverIndex = -1;
            subMenuHoverIndex = -1;
            try { ReleaseCapture(); } catch { }
            if (menuMainHwnd != IntPtr.Zero) ShowWindow(menuMainHwnd, SW_HIDE);
            if (menuSubHwnd != IntPtr.Zero) ShowWindow(menuSubHwnd, SW_HIDE);
        }

        private static IntPtr MenuWndProc(IntPtr win, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (win == menuMainHwnd)
            {
                if (msg == WM_ACTIVATE && (wParam.ToInt64() & 0xffff) == 0) { menuOpen = false; return IntPtr.Zero; }
                if (msg == WM_CAPTURECHANGED) { menuOpen = false; return IntPtr.Zero; }
                if (msg == WM_KEYDOWN && wParam.ToInt32() == 0x1B) { menuOpen = false; return IntPtr.Zero; }
                if (msg == WM_MOUSEMOVE) { UpdateMenuHover(); return IntPtr.Zero; }
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
                {
                    POINT pt;
                    GetCursorPos(out pt);
                    if (!PointInWindow(menuMainHwnd, pt)
                        && !(subMenuOpen && PointInWindow(menuSubHwnd, pt)))
                    {
                        menuOpen = false;
                    }
                    return IntPtr.Zero;
                }
                if (msg == WM_LBUTTONUP || msg == WM_RBUTTONUP) { HandleMenuClick(); return IntPtr.Zero; }
                if (msg == WM_PAINT) { PaintMenuWindow(menuMainHwnd); return IntPtr.Zero; }
                if (msg == WM_ERASEBKGND) return (IntPtr)1;
                if (msg == WM_SETTINGCHANGE) { RefreshSystemTheme(); InvalidateRect(menuMainHwnd, IntPtr.Zero, false); return IntPtr.Zero; }
            }
            else if (win == menuSubHwnd)
            {
                if (msg == WM_PAINT) { PaintMenuWindow(menuSubHwnd); return IntPtr.Zero; }
                if (msg == WM_ERASEBKGND) return (IntPtr)1;
            }
            return DefWindowProc(win, msg, wParam, lParam);
        }

        private static void PaintMenuWindow(IntPtr win)
        {
            bool main = win == menuMainHwnd;
            List<MenuItemData> items = main ? menuItems : subMenuItems;
            List<int> tops = main ? menuItemTops : subItemTops;
            List<int> heights = main ? menuItemHeights : subItemHeights;
            int hover = main ? menuHoverIndex : subMenuHoverIndex;

            PAINTSTRUCT paint;
            IntPtr target = BeginPaint(win, out paint);
            if (target == IntPtr.Zero) return;

            RECT client;
            GetClientRect(win, out client);
            IntPtr buffer = CreateCompatibleDC(target);
            IntPtr bitmap = CreateCompatibleBitmap(target, client.Right, client.Bottom);
            IntPtr oldBitmap = SelectObject(buffer, bitmap);

            int background = useDarkTheme ? Rgb(31, 33, 36) : Rgb(250, 251, 252);
            int textColor = useDarkTheme ? Rgb(242, 244, 246) : Rgb(38, 44, 51);
            int dimColor = useDarkTheme ? Rgb(169, 176, 184) : Rgb(100, 108, 118);
            int hoverColor = useDarkTheme ? Rgb(45, 49, 54) : Rgb(232, 235, 238);
            int sepColor = useDarkTheme ? Rgb(55, 60, 66) : Rgb(222, 226, 230);
            int accent = useDarkTheme ? Rgb(45, 169, 151) : Rgb(23, 126, 113);
            FillColor(buffer, client, background);

            for (int i = 0; i < items.Count; i++)
            {
                MenuItemData item = items[i];
                if (item.IsSeparator)
                {
                    RECT line = new RECT(16, tops[i] + heights[i] / 2, client.Right - 16, tops[i] + heights[i] / 2 + 1);
                    FillColor(buffer, line, sepColor);
                    continue;
                }

                RECT row = new RECT(0, tops[i], client.Right, tops[i] + heights[i]);
                if (i == hover && item.Enabled)
                {
                    RECT hl = new RECT(6, row.Top + 3, row.Right - 6, row.Bottom - 3);
                    FillRounded(buffer, hl, 6, hoverColor);
                }
                if (item.Checked)
                {
                    RECT dot = new RECT(16, row.Top + heights[i] / 2 - 3, 22, row.Top + heights[i] / 2 + 3);
                    FillRounded(buffer, dot, 3, accent);
                }

                RECT textRect = new RECT(30, row.Top, row.Right - (item.HasSubmenu ? 34 : 14), row.Bottom);
                DrawLabel(buffer, item.Text ?? string.Empty, textRect, menuFont,
                    item.Enabled ? textColor : dimColor,
                    DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

                if (item.HasSubmenu)
                {
                    RECT arrowRect = new RECT(row.Right - 26, row.Top, row.Right - 10, row.Bottom);
                    DrawLabel(buffer, "›", arrowRect, menuFont, textColor,
                        DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                }
            }
            BitBlt(target, 0, 0, client.Right, client.Bottom, buffer, 0, 0, SRCCOPY);
            SelectObject(buffer, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(buffer);
            EndPaint(win, ref paint);
        }

        private static void QueueNotification(string title, string text)
        {
            AppendProgressLog(text);
            lock (progressLock)
            {
                hasUpdateProgress = true;
                pendingProgressNotify = true;
            }
            // Core can call Notify from npx output / port-watcher threads, so marshal
            // the window update back to the tray window's UI thread.
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_PROGRESS, IntPtr.Zero, IntPtr.Zero);
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

        /// <summary>
        /// Pre-start update gate: check for a newer release synchronously and
        /// show the skinned dialog. Returns true when the user chose
        /// "update now" (the service must NOT start in that case).
        /// </summary>
        private static bool PromptUpdateBeforeStart()
        {
            SelfUpdater.ReleaseInfo info;
            try
            {
                info = SelfUpdater.CheckLatest(15);
            }
            catch
            {
                return false; // no network: just start
            }
            if (info == null || info.Version == null) return false;
            Version current = SelfUpdater.TryParseVersion(SelfUpdater.GetCurrentVersion());
            if (current == null || info.Version <= current) return false;

            lock (updateLock)
            {
                pendingUpdateTag = info.Tag;
                pendingUpdateDownloadUrl = info.DownloadUrl;
            }
            int choice = ShowUpdatePromptDialog(info.Tag, current);
            return choice == 0;
        }

        /// <summary>
        /// Skinned (in-place painted) modal dialog: "update now" or "skip".
        /// Runs its own message loop on the UI thread; the window is reused
        /// (hidden, never destroyed) to avoid a stray WM_QUIT.
        /// </summary>
        private static int ShowUpdatePromptDialog(string latestTag, Version current)
        {
            if (updatePromptHwnd == IntPtr.Zero)
            {
                const int width = 460;
                const int height = 214;
                int x = Math.Max(0, (GetSystemMetrics(SM_CXSCREEN) - width) / 2);
                int y = Math.Max(0, (GetSystemMetrics(SM_CYSCREEN) - height) / 3);
                IntPtr hInstance = GetModuleHandle(null);
                updatePromptHwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName,
                    "DeepSeek Harness 更新", WS_CAPTION | WS_SYSMENU,
                    x, y, width, height, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
                if (updatePromptHwnd == IntPtr.Zero) return 1;
                SendMessage(updatePromptHwnd, 0x0080, (IntPtr)1, hIcon); // WM_SETICON / ICON_BIG
                RefreshSystemTheme();
                promptTitleFont = CreateUiFont(-22, 600, "Microsoft YaHei UI");
                promptBodyFont = CreateUiFont(-13, 400, "Microsoft YaHei UI");
            }

            pendingPromptTag = latestTag;
            pendingPromptCurrentVersion = current.ToString();
            updatePromptResult = -1;
            hoveredPromptButton = 0;
            pressedPromptButton = 0;
            InvalidateRect(updatePromptHwnd, IntPtr.Zero, false);
            ShowWindow(updatePromptHwnd, SW_SHOW);
            SetForegroundWindow(updatePromptHwnd);

            MSG msg;
            while (updatePromptResult == -1 && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            ShowWindow(updatePromptHwnd, SW_HIDE);
            return updatePromptResult;
        }

        private static RECT GetUpdateButtonRect(RECT client)
        {
            return new RECT(client.Right - 144, client.Bottom - 56, client.Right - 24, client.Bottom - 20);
        }

        private static RECT GetSkipButtonRect(RECT client)
        {
            return new RECT(client.Right - 274, client.Bottom - 56, client.Right - 154, client.Bottom - 20);
        }

        private static int GetPromptButtonAt(int x, int y)
        {
            RECT client;
            GetClientRect(updatePromptHwnd, out client);
            if (PointInRect(GetUpdateButtonRect(client), x, y)) return 1;
            if (PointInRect(GetSkipButtonRect(client), x, y)) return 2;
            return 0;
        }

        private static void PaintUpdatePrompt()
        {
            PAINTSTRUCT paint;
            IntPtr target = BeginPaint(updatePromptHwnd, out paint);
            if (target == IntPtr.Zero) return;

            RECT client;
            GetClientRect(updatePromptHwnd, out client);
            IntPtr buffer = CreateCompatibleDC(target);
            IntPtr bitmap = CreateCompatibleBitmap(target, client.Right, client.Bottom);
            IntPtr oldBitmap = SelectObject(buffer, bitmap);

            int background = useDarkTheme ? Rgb(31, 33, 36) : Rgb(250, 251, 252);
            int heading = useDarkTheme ? Rgb(242, 244, 246) : Rgb(24, 29, 35);
            int secondary = useDarkTheme ? Rgb(169, 176, 184) : Rgb(91, 99, 108);
            int accent = useDarkTheme ? Rgb(45, 169, 151) : Rgb(23, 126, 113);
            FillColor(buffer, client, background);

            RECT titleRect = new RECT(24, 24, client.Right - 24, 58);
            DrawLabel(buffer, "发现新版本 " + (pendingPromptTag ?? ""), titleRect, promptTitleFont, heading,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

            RECT bodyRect = new RECT(24, 68, client.Right - 24, client.Bottom - 72);
            DrawLabel(buffer,
                "当前版本 " + (pendingPromptCurrentVersion ?? "0.0.0") + "。\n是否立即下载并更新？\n\n选择“立即更新”会先更新并重启，\n服务在新版本启动时开启；\n选择“稍后启动”则立即启动服务。",
                bodyRect, promptBodyFont, secondary,
                DT_LEFT | DT_WORDBREAK | DT_NOPREFIX | DT_END_ELLIPSIS);

            RECT updateRect = GetUpdateButtonRect(client);
            int updateColor = useDarkTheme
                ? (pressedPromptButton == 1 ? Rgb(25, 126, 112)
                    : hoveredPromptButton == 1 ? Rgb(54, 186, 166) : accent)
                : (pressedPromptButton == 1 ? Rgb(12, 91, 82)
                    : hoveredPromptButton == 1 ? Rgb(15, 110, 99) : accent);
            FillRounded(buffer, updateRect, 6, updateColor);
            DrawLabel(buffer, "立即更新", updateRect, promptBodyFont, Rgb(255, 255, 255),
                DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | 0x0001);

            RECT skipRect = GetSkipButtonRect(client);
            int skipColor = useDarkTheme
                ? (pressedPromptButton == 2 ? Rgb(83, 89, 96)
                    : hoveredPromptButton == 2 ? Rgb(70, 76, 82) : Rgb(55, 60, 66))
                : (pressedPromptButton == 2 ? Rgb(205, 211, 216)
                    : hoveredPromptButton == 2 ? Rgb(218, 223, 227) : Rgb(232, 235, 238));
            FillRounded(buffer, skipRect, 6, skipColor);
            DrawLabel(buffer, "稍后启动", skipRect, promptBodyFont,
                useDarkTheme ? Rgb(242, 244, 246) : Rgb(38, 44, 51),
                DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | 0x0001);

            BitBlt(target, 0, 0, client.Right, client.Bottom, buffer, 0, 0, SRCCOPY);
            SelectObject(buffer, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(buffer);
            EndPaint(updatePromptHwnd, ref paint);
        }

        private static string pendingPromptTag;
        private static string pendingPromptCurrentVersion;

        /// <summary>
        /// Skinned modal dialog prompting for a new profile name. Blocks until
        /// the user clicks 创建/取消 (or presses Enter/Escape in the edit box).
        /// Returns the typed name, or null when cancelled.
        /// </summary>
        private static string ShowCreateProfileDialog()
        {
            if (createProfileHwnd == IntPtr.Zero)
            {
                const int width = 480;
                const int height = 248;
                int x = Math.Max(0, (GetSystemMetrics(SM_CXSCREEN) - width) / 2);
                int y = Math.Max(0, (GetSystemMetrics(SM_CYSCREEN) - height) / 3);
                IntPtr hInstance = GetModuleHandle(null);
                createProfileHwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName,
                    "创建 Profile", WS_CAPTION | WS_SYSMENU,
                    x, y, width, height, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
                if (createProfileHwnd == IntPtr.Zero) return null;
                SendMessage(createProfileHwnd, 0x0080, (IntPtr)1, hIcon); // WM_SETICON / ICON_BIG
                RefreshSystemTheme();
                if (promptTitleFont == IntPtr.Zero) promptTitleFont = CreateUiFont(-22, 600, "Microsoft YaHei UI");
                if (promptBodyFont == IntPtr.Zero) promptBodyFont = CreateUiFont(-13, 400, "Microsoft YaHei UI");
                if (createProfileEditHwnd == IntPtr.Zero)
                {
                    createProfileEditHwnd = CreateWindowEx(0x00000200 /* WS_EX_CLIENTEDGE */, "EDIT", "",
                        0x40000000 /* WS_CHILD */ | 0x10000000 /* WS_VISIBLE */ | 0x00010000 /* WS_TABSTOP */
                        | 0x0080 /* ES_AUTOHSCROLL (0x2000 would be ES_NUMBER!) */,
                        24, 100, width - 48, 34, createProfileHwnd, IntPtr.Zero, hInstance, IntPtr.Zero);
                    if (createProfileEditHwnd != IntPtr.Zero)
                    {
                        SendMessage(createProfileEditHwnd, 0x0030 /* WM_SETFONT */, promptBodyFont, new IntPtr(1));
                        SetWindowText(createProfileEditHwnd, "");
                    }
                }
            }

            createProfileResult = -1;
            createProfileError = null;
            hoveredCreateButton = 0;
            pressedCreateButton = 0;
            if (createProfileEditHwnd != IntPtr.Zero) SetWindowText(createProfileEditHwnd, "");
            InvalidateRect(createProfileHwnd, IntPtr.Zero, false);
            ShowWindow(createProfileHwnd, SW_SHOW);
            SetForegroundWindow(createProfileHwnd);
            SetFocus(createProfileEditHwnd);

            MSG msg;
            while (createProfileResult == -1 && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                // The EDIT control consumes Enter/Escape itself, so confirm the
                // dialog from the modal pump before the key goes to the control.
                if (msg.message == WM_KEYDOWN && msg.hwnd == createProfileEditHwnd)
                {
                    int vk = msg.wParam.ToInt32();
                    if (vk == 0x0D)
                    {
                        string name = ReadCreateProfileInput();
                        if (!AppSettings.IsValidProfileName(name))
                        {
                            // Same inline validation as the 创建 button: stay
                            // open and explain why the name was rejected.
                            createProfileError = CreateProfileErrorText;
                            InvalidateRect(createProfileHwnd, IntPtr.Zero, false);
                            continue;
                        }
                        createProfileResult = 0;
                        break;
                    }
                    if (vk == 0x1B) { createProfileResult = 1; break; }
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            ShowWindow(createProfileHwnd, SW_HIDE);
            if (createProfileResult != 0) return null;
            return ReadCreateProfileInput();
        }

        private static string ReadCreateProfileInput()
        {
            try
            {
                int len = GetWindowTextLength(createProfileEditHwnd);
                if (len <= 0) return string.Empty;
                StringBuilder sb = new StringBuilder(len + 1);
                GetWindowText(createProfileEditHwnd, sb, sb.Capacity);
                return sb.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void CreateProfileFromDialog()
        {
            string name = ShowCreateProfileDialog();
            if (string.IsNullOrEmpty(name)) return;
            string error;
            if (!core.CreateProfile(name, out error))
            {
                MessageBox(hwnd, "创建 Profile 失败：\n" + error, "创建 Profile",
                    0x00000010 /* MB_ICONERROR */ | 0x00000000 /* MB_OK */);
                return;
            }
            core.SetDshProfile(name);
            QueueNotification("DeepSeek Harness", "Profile " + name + " 已创建并切换。");
        }

        /// <summary>Number of profile rows shown in the delete dialog before truncation.</summary>
        private const int DeleteProfileMaxRows = 8;

        private static void DeleteProfileFromDialog()
        {
            string name = ShowDeleteProfileDialog();
            if (string.IsNullOrEmpty(name)) return;

            int answer = MessageBox(hwnd,
                "确定删除 Profile「" + name + "」吗？\n将删除该 Profile 的全部插件与配置，操作不可恢复。",
                "删除 Profile",
                0x00000004 /* MB_YESNO */ | 0x00000030 /* MB_ICONWARNING */);
            if (answer != 6 /* IDYES */) return;

            string error;
            if (!core.DeleteProfile(name, out error))
            {
                MessageBox(hwnd, "删除 Profile 失败：\n" + error, "删除 Profile",
                    0x00000010 /* MB_ICONERROR */ | 0x00000000 /* MB_OK */);
                return;
            }
            QueueNotification("DeepSeek Harness", "Profile " + name + " 已删除。");
        }

        /// <summary>
        /// Skinned modal dialog listing the existing profiles; the one in use
        /// is shown disabled and cannot be chosen. Returns the selected profile
        /// name, or null when cancelled.
        /// </summary>
        private static string ShowDeleteProfileDialog()
        {
            List<string> profiles = core.GetAvailableProfiles();
            int rows = Math.Min(profiles.Count, DeleteProfileMaxRows);

            if (deleteProfileHwnd == IntPtr.Zero)
            {
                const int width = 460;
                int height = 138 + 32 * rows + 64; // title+list+error+buttons
                int x = Math.Max(0, (GetSystemMetrics(SM_CXSCREEN) - width) / 2);
                int y = Math.Max(0, (GetSystemMetrics(SM_CYSCREEN) - height) / 3);
                IntPtr hInstance = GetModuleHandle(null);
                deleteProfileHwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName,
                    "删除 Profile", WS_CAPTION | WS_SYSMENU,
                    x, y, width, height, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
                if (deleteProfileHwnd == IntPtr.Zero) return null;
                SendMessage(deleteProfileHwnd, 0x0080, (IntPtr)1, hIcon); // WM_SETICON / ICON_BIG
                RefreshSystemTheme();
                if (promptTitleFont == IntPtr.Zero) promptTitleFont = CreateUiFont(-22, 600, "Microsoft YaHei UI");
                if (promptBodyFont == IntPtr.Zero) promptBodyFont = CreateUiFont(-13, 400, "Microsoft YaHei UI");
            }

            deleteProfileNames.Clear();
            deleteProfileNames.AddRange(profiles);
            deleteProfileSelected = -1;
            deleteProfileHover = -1;
            deleteProfileResult = -1;
            deleteProfileError = null;
            hoveredDeleteButton = 0;
            pressedDeleteButton = 0;
            InvalidateRect(deleteProfileHwnd, IntPtr.Zero, false);
            ShowWindow(deleteProfileHwnd, SW_SHOW);
            SetForegroundWindow(deleteProfileHwnd);

            MSG msg;
            while (deleteProfileResult == -1 && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            ShowWindow(deleteProfileHwnd, SW_HIDE);
            if (deleteProfileResult != 0 || deleteProfileSelected < 0
                || deleteProfileSelected >= deleteProfileNames.Count) return null;
            return deleteProfileNames[deleteProfileSelected];
        }

        private static RECT GetDeleteListRect()
        {
            RECT client;
            GetClientRect(deleteProfileHwnd, out client);
            return new RECT(24, 82, client.Right - 24, client.Bottom - 84);
        }

        private static int HitTestDeleteList(int x, int y)
        {
            RECT list = GetDeleteListRect();
            const int rowH = 32;
            int row = (y - list.Top) / rowH;
            if (y < list.Top || row < 0 || row >= deleteProfileNames.Count) return -1;
            if (x < list.Left || x >= list.Right) return -1;
            return row;
        }

        private static RECT GetDeleteOkRect(RECT client)
        {
            return new RECT(client.Right - 144, client.Bottom - 56, client.Right - 24, client.Bottom - 20);
        }

        private static RECT GetDeleteCancelRect(RECT client)
        {
            return new RECT(client.Right - 274, client.Bottom - 56, client.Right - 154, client.Bottom - 20);
        }

        private static int GetDeleteButtonAt(int x, int y)
        {
            RECT client;
            GetClientRect(deleteProfileHwnd, out client);
            if (PointInRect(GetDeleteOkRect(client), x, y)) return 1;
            if (PointInRect(GetDeleteCancelRect(client), x, y)) return 2;
            return 0;
        }

        private static void PaintDeleteProfileDialog()
        {
            PAINTSTRUCT paint;
            IntPtr target = BeginPaint(deleteProfileHwnd, out paint);
            if (target == IntPtr.Zero) return;

            RECT client;
            GetClientRect(deleteProfileHwnd, out client);
            IntPtr buffer = CreateCompatibleDC(target);
            IntPtr bitmap = CreateCompatibleBitmap(target, client.Right, client.Bottom);
            IntPtr oldBitmap = SelectObject(buffer, bitmap);

            int background = useDarkTheme ? Rgb(31, 33, 36) : Rgb(250, 251, 252);
            int heading = useDarkTheme ? Rgb(242, 244, 246) : Rgb(24, 29, 35);
            int secondary = useDarkTheme ? Rgb(169, 176, 184) : Rgb(91, 99, 108);
            int dimColor = useDarkTheme ? Rgb(108, 114, 122) : Rgb(150, 156, 163);
            int errorColor = useDarkTheme ? Rgb(240, 122, 122) : Rgb(190, 60, 60);
            int hoverColor = useDarkTheme ? Rgb(45, 49, 54) : Rgb(232, 235, 238);
            int accent = useDarkTheme ? Rgb(45, 169, 151) : Rgb(23, 126, 113);
            int danger = useDarkTheme ? Rgb(200, 72, 72) : Rgb(178, 52, 52);
            int dangerHover = useDarkTheme ? Rgb(220, 88, 88) : Rgb(198, 62, 62);
            int dangerPressed = useDarkTheme ? Rgb(172, 58, 58) : Rgb(148, 40, 40);
            FillColor(buffer, client, background);

            RECT titleRect = new RECT(24, 20, client.Right - 24, 52);
            DrawLabel(buffer, "删除 Profile", titleRect, promptTitleFont, heading,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

            RECT hintRect = new RECT(24, 56, client.Right - 24, 78);
            DrawLabel(buffer, "选择要删除的 Profile（当前使用的不可删除）：", hintRect,
                promptBodyFont, secondary, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

            // Profile rows.
            RECT list = GetDeleteListRect();
            const int rowH = 32;
            for (int i = 0; i < deleteProfileNames.Count && i < DeleteProfileMaxRows; i++)
            {
                RECT row = new RECT(list.Left, list.Top + i * rowH, list.Right, list.Top + (i + 1) * rowH);
                bool inUse = string.Equals(deleteProfileNames[i], core.DshProfile, StringComparison.Ordinal);
                bool hovered = i == deleteProfileHover && !inUse;
                if (hovered || i == deleteProfileSelected)
                {
                    RECT hl = new RECT(row.Left, row.Top + 2, row.Right, row.Bottom - 2);
                    FillRounded(buffer, hl, 6, i == deleteProfileSelected && !inUse
                        ? (useDarkTheme ? Rgb(41, 72, 67) : Rgb(213, 235, 230))
                        : hoverColor);
                }
                if (i == deleteProfileSelected && !inUse)
                {
                    RECT dot = new RECT(row.Left + 8, row.Top + rowH / 2 - 3, row.Left + 14, row.Top + rowH / 2 + 3);
                    FillRounded(buffer, dot, 3, accent);
                }

                RECT textRect = new RECT(row.Left + 24, row.Top, row.Right - 10, row.Bottom);
                DrawLabel(buffer, deleteProfileNames[i], textRect, promptBodyFont,
                    inUse ? dimColor : secondary,
                    DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
                if (inUse)
                {
                    RECT tagRect = new RECT(row.Right - 92, row.Top, row.Right - 10, row.Bottom);
                    DrawLabel(buffer, "使用中", tagRect, promptBodyFont, accent,
                        DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                }
            }

            RECT errorRect = new RECT(24, list.Bottom + 4, client.Right - 24, list.Bottom + 26);
            if (!string.IsNullOrEmpty(deleteProfileError))
            {
                DrawLabel(buffer, deleteProfileError, errorRect, promptBodyFont, errorColor,
                    DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
            }

            RECT okRect = GetDeleteOkRect(client);
            int okColor = pressedDeleteButton == 1 ? dangerPressed
                : hoveredDeleteButton == 1 ? dangerHover : danger;
            FillRounded(buffer, okRect, 6, okColor);
            DrawLabel(buffer, "删除", okRect, promptBodyFont, Rgb(255, 255, 255),
                DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | 0x0001);

            RECT cancelRect = GetDeleteCancelRect(client);
            int cancelColor = useDarkTheme
                ? (pressedDeleteButton == 2 ? Rgb(83, 89, 96)
                    : hoveredDeleteButton == 2 ? Rgb(70, 76, 82) : Rgb(55, 60, 66))
                : (pressedDeleteButton == 2 ? Rgb(205, 211, 216)
                    : hoveredDeleteButton == 2 ? Rgb(218, 223, 227) : Rgb(232, 235, 238));
            FillRounded(buffer, cancelRect, 6, cancelColor);
            DrawLabel(buffer, "取消", cancelRect, promptBodyFont,
                useDarkTheme ? Rgb(242, 244, 246) : Rgb(38, 44, 51),
                DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | 0x0001);

            BitBlt(target, 0, 0, client.Right, client.Bottom, buffer, 0, 0, SRCCOPY);
            SelectObject(buffer, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(buffer);
            EndPaint(deleteProfileHwnd, ref paint);
        }

        private static RECT GetCreateOkRect(RECT client)
        {
            return new RECT(client.Right - 144, client.Bottom - 56, client.Right - 24, client.Bottom - 20);
        }

        private static RECT GetCreateCancelRect(RECT client)
        {
            return new RECT(client.Right - 274, client.Bottom - 56, client.Right - 154, client.Bottom - 20);
        }

        private static int GetCreateButtonAt(int x, int y)
        {
            RECT client;
            GetClientRect(createProfileHwnd, out client);
            if (PointInRect(GetCreateOkRect(client), x, y)) return 1;
            if (PointInRect(GetCreateCancelRect(client), x, y)) return 2;
            return 0;
        }

        private static void PaintCreateProfileDialog()
        {
            PAINTSTRUCT paint;
            IntPtr target = BeginPaint(createProfileHwnd, out paint);
            if (target == IntPtr.Zero) return;

            RECT client;
            GetClientRect(createProfileHwnd, out client);
            IntPtr buffer = CreateCompatibleDC(target);
            IntPtr bitmap = CreateCompatibleBitmap(target, client.Right, client.Bottom);
            IntPtr oldBitmap = SelectObject(buffer, bitmap);

            int background = useDarkTheme ? Rgb(31, 33, 36) : Rgb(250, 251, 252);
            int heading = useDarkTheme ? Rgb(242, 244, 246) : Rgb(24, 29, 35);
            int secondary = useDarkTheme ? Rgb(169, 176, 184) : Rgb(91, 99, 108);
            int errorColor = useDarkTheme ? Rgb(240, 122, 122) : Rgb(190, 60, 60);
            int accent = useDarkTheme ? Rgb(45, 169, 151) : Rgb(23, 126, 113);
            FillColor(buffer, client, background);

            RECT titleRect = new RECT(24, 20, client.Right - 24, 52);
            DrawLabel(buffer, "创建 Profile", titleRect, promptTitleFont, heading,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

            RECT bodyRect = new RECT(24, 58, client.Right - 24, 100);
            DrawLabel(buffer,
                "新 profile 将作为独立的 DSH 配置运行 web 服务。\n名称仅允许字母、数字、-、_、.，且以字母或数字开头。",
                bodyRect, promptBodyFont, secondary,
                DT_LEFT | DT_WORDBREAK | DT_NOPREFIX | DT_END_ELLIPSIS);

            // Edit-box frame; the EDIT child window paints itself on top.
            RECT editRect = new RECT(24, 100, client.Right - 24, 134);
            FillRounded(buffer, editRect, 6, useDarkTheme ? Rgb(55, 60, 66) : Rgb(222, 226, 230));

            if (!string.IsNullOrEmpty(createProfileError))
            {
                RECT errorRect = new RECT(24, 136, client.Right - 24, 170);
                DrawLabel(buffer, createProfileError, errorRect, promptBodyFont, errorColor,
                    DT_LEFT | DT_WORDBREAK | DT_NOPREFIX);
            }

            RECT okRect = GetCreateOkRect(client);
            int okColor = useDarkTheme
                ? (pressedCreateButton == 1 ? Rgb(25, 126, 112)
                    : hoveredCreateButton == 1 ? Rgb(54, 186, 166) : accent)
                : (pressedCreateButton == 1 ? Rgb(12, 91, 82)
                    : hoveredCreateButton == 1 ? Rgb(15, 110, 99) : accent);
            FillRounded(buffer, okRect, 6, okColor);
            DrawLabel(buffer, "创建", okRect, promptBodyFont, Rgb(255, 255, 255),
                DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | 0x0001);

            RECT cancelRect = GetCreateCancelRect(client);
            int cancelColor = useDarkTheme
                ? (pressedCreateButton == 2 ? Rgb(83, 89, 96)
                    : hoveredCreateButton == 2 ? Rgb(70, 76, 82) : Rgb(55, 60, 66))
                : (pressedCreateButton == 2 ? Rgb(205, 211, 216)
                    : hoveredCreateButton == 2 ? Rgb(218, 223, 227) : Rgb(232, 235, 238));
            FillRounded(buffer, cancelRect, 6, cancelColor);
            DrawLabel(buffer, "取消", cancelRect, promptBodyFont,
                useDarkTheme ? Rgb(242, 244, 246) : Rgb(38, 44, 51),
                DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | 0x0001);

            BitBlt(target, 0, 0, client.Right, client.Bottom, buffer, 0, 0, SRCCOPY);
            SelectObject(buffer, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(buffer);
            EndPaint(createProfileHwnd, ref paint);
        }

        private static IntPtr EnsureCreateEditBrush()
        {
            if (createEditBrush == IntPtr.Zero)
            {
                int color = useDarkTheme ? Rgb(45, 49, 54) : Rgb(255, 255, 255);
                createEditBrush = CreateSolidBrush(color);
            }
            return createEditBrush;
        }

        private static void DestroyCreateEditBrush()
        {
            if (createEditBrush != IntPtr.Zero)
            {
                DeleteObject(createEditBrush);
                createEditBrush = IntPtr.Zero;
            }
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

        private static void QueueUpdateAvailable(string tag, string downloadUrl, string releaseUrl)
        {
            lock (updateLock)
            {
                pendingUpdateTag = tag;
                pendingUpdateDownloadUrl = downloadUrl;
            }
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_UPDATE_AVAILABLE, IntPtr.Zero, IntPtr.Zero);
        }

        private static void DrainUpdateAvailable()
        {
            string tag, downloadUrl;
            lock (updateLock)
            {
                tag = pendingUpdateTag;
                downloadUrl = pendingUpdateDownloadUrl;
            }
            if (string.IsNullOrEmpty(downloadUrl)) return;

            int result = MessageBox(hwnd,
                "发现新版本 " + tag + "（当前 " + SelfUpdater.GetCurrentVersion() + "）。\n是否下载并自动更新？",
                "DeepSeek Harness 更新",
                0x00000004 /* MB_YESNO */ | 0x00000020 /* MB_ICONQUESTION */);
            if (result == 6 /* IDYES */)
            {
                SetUpdateProgressUI(0, "准备下载…");
                core.BeginSelfUpdateDownload(downloadUrl);
            }
        }

        private static void QueueUpdateProgress(long received, long total)
        {
            lock (updateLock)
            {
                pendingUpdateReceived = received;
                pendingUpdateTotal = total;
            }
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_UPDATE_PROGRESS, IntPtr.Zero, IntPtr.Zero);
        }

        private static void DrainUpdateProgress()
        {
            long received, total;
            lock (updateLock)
            {
                received = pendingUpdateReceived;
                total = pendingUpdateTotal;
            }
            int percent = total > 0 ? (int)(received * 100 / total) : -1;
            string detail = total > 0
                ? SelfUpdater.FormatBytes(received) + " / " + SelfUpdater.FormatBytes(total)
                : SelfUpdater.FormatBytes(received);
            SetUpdateProgressUI(percent, detail);
        }

        private static void SetUpdateProgressUI(int percent, string detail)
        {
            EnsureProgressWindow();
            if (progressHwnd == IntPtr.Zero) return;
            currentProgressStage = "正在下载更新…";
            currentProgressPercent = percent;
            progressDismissedByUser = false;
            progressIsCompleted = false;
            AppendProgressLog("下载进度：" + detail);
            InvalidateRect(progressHwnd, IntPtr.Zero, false);
            ShowProgressWindow();
        }

        private static void QueueUpdateDownloaded(string installerPath)
        {
            lock (updateLock)
            {
                pendingUpdateInstallerPath = installerPath;
            }
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_UPDATE_DONE, IntPtr.Zero, IntPtr.Zero);
        }

        private static void DrainUpdateDownloaded()
        {
            string installerPath;
            lock (updateLock)
            {
                installerPath = pendingUpdateInstallerPath;
            }
            if (string.IsNullOrEmpty(installerPath)) return;

            if (progressHwnd != IntPtr.Zero)
            {
                currentProgressStage = "已下载，正在安装并重启…";
                currentProgressPercent = 100;
                progressIsCompleted = false;
                AppendProgressLog("即将静默安装并重新启动。");
                InvalidateRect(progressHwnd, IntPtr.Zero, false);
                ShowProgressWindow();
            }

            LaunchWindowsInstaller(installerPath);
            Shutdown();
        }

        private static void QueueUpdateFailed(string reason)
        {
            lock (updateLock)
            {
                pendingUpdateError = reason;
            }
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_UPDATE_FAILED, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>True once the dsh web service has been started (or the
        /// user skipped the update); false while an update was chosen and the
        /// service is deliberately kept stopped.</summary>
        private static bool serviceStarted;

        private static void DrainUpdateFailed()
        {
            string reason;
            lock (updateLock)
            {
                reason = pendingUpdateError;
            }
            if (progressHwnd != IntPtr.Zero)
            {
                currentProgressStage = "更新失败";
                currentProgressPercent = -1;
                progressIsCompleted = false;
                AppendProgressLog("更新失败：" + (string.IsNullOrEmpty(reason) ? "未知错误" : reason));
                InvalidateRect(progressHwnd, IntPtr.Zero, false);
            }
            QueueNotification("DeepSeek Harness", "自动更新失败：" + (string.IsNullOrEmpty(reason) ? "未知错误" : reason));

            // The service was held back for the update; start it now instead
            // of leaving the tray without a service.
            if (!serviceStarted)
            {
                serviceStarted = true;
                core.Start();
            }
        }

        private static void LaunchWindowsInstaller(string installerPath)
        {
            try
            {
                string installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "DeepSeek Harness Tray");
                string targetExe = Path.Combine(installDir, "dsh-tray.exe");
                string batPath = Path.Combine(Path.GetDirectoryName(installerPath), "apply-update.cmd");

                string bat =
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    "tasklist /fi \"IMAGENAME eq dsh-tray.exe\" 2>nul | findstr /i /c:\"dsh-tray.exe\" >nul\r\n" +
                    "if %errorlevel%==0 (\r\n" +
                    "  timeout /t 1 /nobreak >nul\r\n" +
                    "  goto wait\r\n" +
                    ")\r\n" +
                    "\"" + installerPath + "\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART\r\n" +
                    "start \"\" \"" + targetExe + "\"\r\n" +
                    "del \"%~f0\"\r\n";
                File.WriteAllText(batPath, bat, Encoding.ASCII);

                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + batPath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch
            {
            }
        }

        private static void DrainProgress()
        {
            string stage;
            string detail;
            bool completed;
            bool started;
            bool notify;
            lock (progressLock)
            {
                stage = pendingProgressStage;
                pendingProgressStage = null;
                detail = pendingProgressDetail;
                pendingProgressDetail = null;
                completed = pendingProgressCompleted;
                started = pendingProgressStarted;
                pendingProgressCompleted = false;
                pendingProgressStarted = false;
                notify = pendingProgressNotify;
                pendingProgressNotify = false;
            }

            if (!string.IsNullOrWhiteSpace(detail)) AppendProgressLog(detail);

            EnsureProgressWindow();
            if (progressHwnd == IntPtr.Zero) return;
            if (started)
            {
                progressDismissedByUser = false;
                progressIsCompleted = false;
                currentProgressPercent = -1;
            }
            if (stage != null) currentProgressStage = stage;

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
            else if (notify && !progressIsCompleted && stage == null && !started)
            {
                // A standalone notification (no in-flight progress stage): show
                // the window briefly, then hide it again automatically.
                KillTimer(progressHwnd, (UIntPtr)2);
                SetTimer(progressHwnd, (UIntPtr)1, 6000, IntPtr.Zero);
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

        private static void AppendProgressLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string line = text.Trim();
            if (line.Length > 220) line = line.Substring(0, 217) + "...";
            bool downloadLine = line.StartsWith("下载进度", StringComparison.Ordinal);
            lock (progressLock)
            {
                if (progressLogLines.Count > 0)
                {
                    string last = progressLogLines[progressLogLines.Count - 1];
                    bool lastDownload = last.StartsWith("下载进度", StringComparison.Ordinal);
                    // Download progress is a single rolling line: keep updating it
                    // in place instead of flooding the log with one entry per tick.
                    if (downloadLine && lastDownload)
                    {
                        if (last != line) progressLogLines[progressLogLines.Count - 1] = line;
                        return;
                    }
                    if (last == line) return;
                }
                progressLogLines.Add(line);
                if (progressLogLines.Count > MaxProgressLogLines)
                    progressLogLines.RemoveRange(0, progressLogLines.Count - MaxProgressLogLines);
            }
        }

        private static void DrawLogLines(IntPtr dc, RECT rect, IntPtr font, int color)
        {
            const int lineHeight = 18;
            int maxLines = Math.Max(1, (rect.Bottom - rect.Top) / lineHeight);

            string[] lines;
            lock (progressLock)
            {
                int start = Math.Max(0, progressLogLines.Count - maxLines);
                int count = progressLogLines.Count - start;
                lines = new string[count];
                for (int i = 0; i < count; i++) lines[i] = progressLogLines[start + i];
            }

            IntPtr oldFont = SelectObject(dc, font);
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, color);
            int y = rect.Bottom - lineHeight * lines.Length;
            for (int i = 0; i < lines.Length; i++)
            {
                RECT lineRect = new RECT(rect.Left, y, rect.Right, y + lineHeight);
                DrawText(dc, lines[i], -1, ref lineRect,
                    DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
                y += lineHeight;
            }
            SelectObject(dc, oldFont);
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
            int trackColor = useDarkTheme ? Rgb(55, 60, 66) : Rgb(222, 226, 230);
            int accent = useDarkTheme ? Rgb(45, 169, 151) : Rgb(23, 126, 113);
            FillColor(buffer, client, background);

            RECT stageRect = new RECT(24, 22, client.Right - 24, 52);
            DrawLabel(buffer, currentProgressStage, stageRect, headingFont, heading,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

            RECT track = new RECT(24, 68, client.Right - 24, 76);
            FillRounded(buffer, track, 6, trackColor);
            if (currentProgressPercent >= 0)
            {
                int trackWidth = track.Right - track.Left;
                int filledWidth = (int)((long)trackWidth * currentProgressPercent / 100);
                if (filledWidth > 0)
                {
                    RECT filled = new RECT(track.Left, track.Top, track.Left + filledWidth, track.Bottom);
                    FillRounded(buffer, filled, 6, accent);
                }
            }
            else if (progressIsCompleted)
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
            DrawLabel(buffer, "启动日志", logTitle, bodyFont, secondary,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            RECT logRect = new RECT(24, 116, client.Right - 24, client.Bottom - 70);
            DrawLogLines(buffer, logRect, logFont, logText);

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
        private static void Cleanup()
        {
            if (progressHwnd != IntPtr.Zero)
            {
                DestroyWindow(progressHwnd);
                progressHwnd = IntPtr.Zero;
            }
            if (updatePromptHwnd != IntPtr.Zero)
            {
                DestroyWindow(updatePromptHwnd);
                updatePromptHwnd = IntPtr.Zero;
            }
            if (createProfileHwnd != IntPtr.Zero)
            {
                DestroyWindow(createProfileHwnd);
                createProfileHwnd = IntPtr.Zero;
                createProfileEditHwnd = IntPtr.Zero;
            }
            if (deleteProfileHwnd != IntPtr.Zero)
            {
                DestroyWindow(deleteProfileHwnd);
                deleteProfileHwnd = IntPtr.Zero;
            }
            DestroyCreateEditBrush();
            if (menuMainHwnd != IntPtr.Zero)
            {
                DestroyWindow(menuMainHwnd);
                menuMainHwnd = IntPtr.Zero;
            }
            if (menuSubHwnd != IntPtr.Zero)
            {
                DestroyWindow(menuSubHwnd);
                menuSubHwnd = IntPtr.Zero;
            }
            if (menuFont != IntPtr.Zero) { DeleteObject(menuFont); menuFont = IntPtr.Zero; }
            if (headingFont != IntPtr.Zero) { DeleteObject(headingFont); headingFont = IntPtr.Zero; }
            if (bodyFont != IntPtr.Zero) { DeleteObject(bodyFont); bodyFont = IntPtr.Zero; }
            if (logFont != IntPtr.Zero) { DeleteObject(logFont); logFont = IntPtr.Zero; }
            if (promptTitleFont != IntPtr.Zero) { DeleteObject(promptTitleFont); promptTitleFont = IntPtr.Zero; }
            if (promptBodyFont != IntPtr.Zero) { DeleteObject(promptBodyFont); promptBodyFont = IntPtr.Zero; }
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
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

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

        [DllImport("gdi32.dll")]
        private static extern uint SetBkColor(IntPtr hdc, uint color);

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    }
}
#endif
