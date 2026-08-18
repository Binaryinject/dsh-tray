using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Threading;

namespace DshTray
{
    internal static class Program
    {
        internal const string MutexName = "dsh-tray";
        internal const string PipeName = "dsh-tray";

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
                SendCommand("stop");
                return 0;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Already running: ask it to reopen the browser instead of
                    // spawning a second server that would collide on the port.
                    SendCommand("reopen");
                    return 0;
                }

                Core core = new Core(port, autoOpen);
                return Platform.Run(core);
            }
        }

        internal static void SendCommand(string command)
        {
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(1200);
                    using (StreamWriter writer = new StreamWriter(client))
                    {
                        writer.WriteLine(command);
                        writer.Flush();
                    }
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>Shared, platform-independent launcher logic.</summary>
    internal sealed class Core
    {
        private readonly int port;
        private readonly bool autoOpen;
        private readonly string logPath;
        private Process server;
        private StreamWriter logWriter;
        private volatile bool shuttingDown;
        private readonly object downloadNoticeLock = new object();
        private volatile bool downloadInstallNoticeShown;
        private int downloadStepCount;
        private int portWatcherGeneration;

        private const int SlowStartNoticeSeconds = 15;
        private const int DownloadProgressNoticeSeconds = 30;
        private const int LongStartNoticeSeconds = 120;
        private const int LongStartNoticeIntervalSeconds = 120;

        /// <summary>Invoked (on the platform UI thread) when a "stop" command is received.</summary>
        public Action OnShutdownRequest;

        /// <summary>Invoked to surface a notification (title, text). May be called from background threads.</summary>
        public Action<string, string> Notify;

        /// <summary>Invoked to update a persistent status display (e.g. tray tooltip). May be called from background threads.</summary>
        public Action<string> StatusChanged;

        public bool ShuttingDown { get { return shuttingDown; } }
        public string Url { get { return "http://127.0.0.1:" + port; } }

        public Core(int port, bool autoOpen)
        {
            this.port = port;
            this.autoOpen = autoOpen;
            this.logPath = Path.Combine(Path.GetTempPath(), "dsh-tray-server.log");
        }

        public void Start()
        {
            StartServer();
            StartPortWatcher();
            StartCommandListener();
        }

        public void Shutdown()
        {
            if (shuttingDown) return;
            shuttingDown = true;
            StopServer();
        }

        public void OpenBrowser()
        {
            try
            {
#if WINDOWS
                Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
#else
                Process.Start(new ProcessStartInfo("open", Url) { UseShellExecute = false });
#endif
            }
            catch
            {
            }
        }

        public void OpenLog()
        {
            try
            {
                if (!File.Exists(logPath)) File.WriteAllText(logPath, "");
#if WINDOWS
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
#else
                Process.Start(new ProcessStartInfo("open", logPath) { UseShellExecute = false });
#endif
            }
            catch
            {
            }
        }

        public void RestartServer()
        {
            if (shuttingDown) return;

            Action<string, string> cb = Notify;
            if (cb != null) cb("DeepSeek Harness", "服务正在重启…");
            UpdateStatus("DeepSeek Harness — 服务正在重启…");

            StopServer();
            StartServer();
            StartPortWatcher();
        }

        private void StartServer()
        {
            downloadInstallNoticeShown = false;
            downloadStepCount = 0;
            UpdateStatus("DeepSeek Harness — 服务正在启动…");

            ProcessStartInfo psi = new ProcessStartInfo();
#if WINDOWS
            psi.FileName = "cmd.exe";
            // --loglevel http makes npm emit fetch/cache-miss lines even when
            // stderr is redirected, so the tray can report downloads.
            psi.Arguments = "/c npx --yes --loglevel http @deepseek-ai/dsh web --port " + port;
#else
            psi.FileName = "/bin/sh";
            psi.Arguments = "-c \"npx --yes --loglevel http @deepseek-ai/dsh web --port " + port + "\"";
#endif
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

            server.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { Log(e.Data); };
            server.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { Log(e.Data); };

            server.Start();
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
        }

        private void Log(string line)
        {
            if (line == null) return;

            CheckServerOutput(line);
            TrackDownloadProgress(line);
            if (logWriter == null) return;

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

        /// <summary>
        /// npx prints a warning when @deepseek-ai/dsh is not cached (first run)
        /// or needs to be fetched again. Surface that immediately, otherwise the
        /// user only sees a silent, multi-minute startup.
        /// </summary>
        private void CheckServerOutput(string line)
        {
            lock (downloadNoticeLock)
            {
                if (downloadInstallNoticeShown) return;

                // npm < 11 prints "will be installed" when npx has to fetch a package;
                // npm >= 11 stays quiet unless loglevel is http, then cache misses are visible.
                bool olderNpmInstallNotice = line.IndexOf("will be installed", StringComparison.OrdinalIgnoreCase) >= 0;
                bool fetchWithCacheMiss = line.IndexOf("npm http fetch GET", StringComparison.OrdinalIgnoreCase) >= 0
                    && line.IndexOf("cache miss", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!olderNpmInstallNotice && !fetchWithCacheMiss) return;

                downloadInstallNoticeShown = true;
                downloadStepCount = 0;
            }

            NotifyUser("检测到 dsh 需要安装/更新，正在后台下载，请稍候…");
            UpdateStatus("DeepSeek Harness — 正在下载/更新 dsh…");
        }

        /// <summary>Counts fetched tarballs after a download/update has been detected.</summary>
        private void TrackDownloadProgress(string line)
        {
            if (!downloadInstallNoticeShown) return;
            if (line.IndexOf("npm http fetch GET", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (line.IndexOf(".tgz", StringComparison.OrdinalIgnoreCase) < 0) return;

            int count = Interlocked.Increment(ref downloadStepCount);
            UpdateStatus("DeepSeek Harness — 正在下载/更新 dsh：已获取 " + count + " 个包");
        }

        private void NotifyUser(string text)
        {
            Action<string, string> cb = Notify;
            if (cb != null) cb("DeepSeek Harness", text);
        }

        private void UpdateStatus(string text)
        {
            Action<string> cb = StatusChanged;
            if (cb != null) cb(text);
        }

        private void StartPortWatcher()
        {
            int generation = Interlocked.Increment(ref portWatcherGeneration);

            Thread t = new Thread(delegate ()
            {
                DateTime started = DateTime.UtcNow;
                bool slowStartNoticeShown = false;
                double lastDownloadProgressNotice = -1;
                double lastLongStartNotice = -1;

                while (true)
                {
                    if (shuttingDown || generation != portWatcherGeneration) return;
                    if (PortIsOpen())
                    {
                        if (shuttingDown || generation != portWatcherGeneration) return;
                        if (autoOpen) OpenBrowser();
                        else if (downloadInstallNoticeShown)
                        {
                            int downloaded = Volatile.Read(ref downloadStepCount);
                            NotifyUser("dsh 下载/更新完成，服务已就绪（共获取 " + downloaded + " 个包）。");
                        }
                        else
                        {
                            NotifyUser("服务已就绪。");
                        }
                        UpdateStatus("DeepSeek Harness");
                        return;
                    }

                    // npx failed fast: OnServerExited has already told the user;
                    // do not keep polling and later fire a misleading timeout notice.
                    Process s = server;
                    if (s != null && s.HasExited) return;

                    double elapsed = (DateTime.UtcNow - started).TotalSeconds;
                    if (!slowStartNoticeShown && elapsed >= SlowStartNoticeSeconds)
                    {
                        slowStartNoticeShown = true;
                        NotifyUser("服务仍在启动：dsh 可能正在下载/更新，请稍候…");
                    }
                    else if (downloadInstallNoticeShown
                        && elapsed >= DownloadProgressNoticeSeconds
                        && (lastDownloadProgressNotice < 0 || elapsed - lastDownloadProgressNotice >= DownloadProgressNoticeSeconds))
                    {
                        lastDownloadProgressNotice = elapsed;
                        int downloaded = Volatile.Read(ref downloadStepCount);
                        NotifyUser("正在下载/更新 dsh：已获取 " + downloaded + " 个包，已等待 " + FormatWaitTime(elapsed) + "。请稍候…");
                    }
                    else if (!downloadInstallNoticeShown && elapsed >= LongStartNoticeSeconds
                        && (lastLongStartNotice < 0 || elapsed - lastLongStartNotice >= LongStartNoticeIntervalSeconds))
                    {
                        lastLongStartNotice = elapsed;
                        int minutes = (int)(elapsed / 60);
                        NotifyUser("服务仍在启动（已等待 " + minutes + " 分钟），dsh 可能仍在下载/更新。请耐心等待，可点击『查看日志』了解进度。");
                    }

                    Thread.Sleep(500);
                }
            });
            t.IsBackground = true;
            t.Start();
        }


        private static string FormatWaitTime(double totalSeconds)
        {
            int seconds = (int)totalSeconds;
            if (seconds < 60) return seconds + " 秒";
            return (seconds / 60) + " 分钟";
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

        private void StartCommandListener()
        {
            Thread t = new Thread(delegate ()
            {
                while (!shuttingDown)
                {
                    try
                    {
                        using (NamedPipeServerStream pipe = new NamedPipeServerStream(
                            Program.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                        {
                            pipe.WaitForConnection();
                            using (StreamReader reader = new StreamReader(pipe))
                            {
                                string cmd = reader.ReadLine();
                                if (cmd == "reopen") OpenBrowser();
                                else if (cmd == "stop")
                                {
                                    Action cb = OnShutdownRequest;
                                    if (cb != null) cb();
                                }
                            }
                        }
                    }
                    catch
                    {
                        Thread.Sleep(200);
                    }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OnServerExited(object sender, EventArgs e)
        {
            if (shuttingDown) return;
            UpdateStatus("DeepSeek Harness");
            Action<string, string> cb = Notify;
            if (cb != null) cb("DeepSeek Harness", "服务已退出，详情见日志。");
        }

        private void StopServer()
        {
            if (server == null) return;
            try
            {
                server.Exited -= OnServerExited;
                if (!server.HasExited)
                {
#if WINDOWS
                    Process p = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + server.Id + " /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (p != null) p.WaitForExit(5000);
#else
                    server.Kill(true); // kill the entire process tree
#endif
                }
            }
            catch
            {
            }
            server = null;
        }
    }
}
