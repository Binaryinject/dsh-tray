using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Net.Http;
using System.Reflection;
using System.Text;
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

    /// <summary>
    /// Self-update: query the latest GitHub release, compare versions, and download
    /// the platform installer. The platform layer owns the UI (prompt/progress) and
    /// the actual silent install + relaunch.
    /// </summary>
    internal static class SelfUpdater
    {
        private const string RepoOwner = "Binaryinject";
        private const string RepoName = "dsh-tray";
        private const string ApiUrl = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

        internal sealed class ReleaseInfo
        {
            public string Tag;
            public Version Version;
            public string DownloadUrl;
            public string HtmlUrl;
        }

        /// <summary>Installer asset name for the current platform.</summary>
        internal static string GetInstallerFileName()
        {
#if WINDOWS
            return "dsh-tray-setup-win-x64.exe";
#else
            return "dsh-tray-osx-arm64.dmg";
#endif
        }

        /// <summary>Current app version, read from the assembly informational version.</summary>
        internal static string GetCurrentVersion()
        {
            try
            {
                var attr = typeof(SelfUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                string v = attr != null ? attr.InformationalVersion : null;
                if (!string.IsNullOrEmpty(v))
                {
                    int plus = v.IndexOf('+');
                    if (plus >= 0) v = v.Substring(0, plus);
                    if (TryParseVersion(v) != null) return v;
                }
            }
            catch
            {
            }
            try
            {
                Version av = typeof(SelfUpdater).Assembly.GetName().Version;
                if (av != null) return av.Major + "." + av.Minor + "." + av.Build;
            }
            catch
            {
            }
            return "0.0.0";
        }

        internal static Version TryParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();
            if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V')) v = v.Substring(1);
            Version parsed;
            if (Version.TryParse(v, out parsed)) return parsed;
            return null;
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            return (mb / 1024.0).ToString("0.0") + " GB";
        }

        /// <summary>Query the latest stable release. Returns null on error or no usable asset.</summary>
        internal static ReleaseInfo CheckLatest()
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-tray");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using (HttpResponseMessage resp = client.GetAsync(ApiUrl).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return ParseRelease(json);
                }
            }
        }

        /// <summary>Download a file, reporting (received, total) progress (-1 total when unknown).</summary>
        internal static void DownloadFile(string url, string destPath, Action<long, long> progress)
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-tray");
                using (HttpResponseMessage resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength.HasValue ? resp.Content.Headers.ContentLength.Value : -1;
                    using (Stream stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (FileStream file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        long received = 0;
                        int n;
                        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            file.Write(buffer, 0, n);
                            received += n;
                            if (progress != null) progress(received, total);
                        }
                    }
                }
            }
        }

        private static ReleaseInfo ParseRelease(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            ReleaseInfo info = new ReleaseInfo();
            info.Tag = ExtractStringField(json, "tag_name");
            info.HtmlUrl = ExtractStringField(json, "html_url");
            info.DownloadUrl = ExtractAssetUrl(json, GetInstallerFileName());
            info.Version = TryParseVersion(info.Tag);
            if (info.DownloadUrl == null || info.Version == null) return null;
            return info;
        }

        private static string ExtractStringField(string json, string field)
        {
            int keyIdx = json.IndexOf("\"" + field + "\"", StringComparison.Ordinal);
            if (keyIdx < 0) return null;
            int colon = json.IndexOf(':', keyIdx);
            if (colon < 0) return null;
            int valueQuote = json.IndexOf('"', colon + 1);
            if (valueQuote < 0) return null;
            int endQuote = json.IndexOf('"', valueQuote + 1);
            if (endQuote < 0) return null;
            return json.Substring(valueQuote + 1, endQuote - valueQuote - 1);
        }

        private static string ExtractAssetUrl(string json, string assetName)
        {
            int nameIdx = json.IndexOf("\"" + assetName + "\"", StringComparison.Ordinal);
            if (nameIdx < 0) return null;
            int urlKey = json.IndexOf("browser_download_url", nameIdx, StringComparison.Ordinal);
            if (urlKey < 0) return null;
            int colon = json.IndexOf(':', urlKey);
            if (colon < 0) return null;
            int valueQuote = json.IndexOf('"', colon + 1);
            if (valueQuote < 0) return null;
            int endQuote = json.IndexOf('"', valueQuote + 1);
            if (endQuote < 0) return null;
            return json.Substring(valueQuote + 1, endQuote - valueQuote - 1);
        }
    }

    /// <summary>Shared, platform-independent launcher logic.</summary>
    internal sealed class Core
    {
        private readonly int port;
        private readonly bool autoOpen;
        private readonly string logPath;
        private readonly string latestLogPath;
        private readonly object logLock = new object();
        private Process server;
        private StreamWriter logWriter;
        private volatile bool shuttingDown;
        private readonly object downloadNoticeLock = new object();
        private volatile bool downloadInstallNoticeShown;
        private volatile bool installRequired;
        private int downloadStepCount;
        private DateTime lastNpmActivityUtc;
        private string updateProgressStage;
        private int portWatcherGeneration;
        private volatile bool webReady;
        private string webLaunchUrl;

        /// <summary>Invoked (on a background thread) when a newer release is available: (latestTag, downloadUrl, releaseUrl).</summary>
        public Action<string, string, string> SelfUpdateAvailable;

        /// <summary>Invoked (on a background thread) with download progress: (received, total).</summary>
        public Action<long, long> SelfUpdateProgress;

        /// <summary>Invoked (on a background thread) after the installer finished downloading: (installerPath).</summary>
        public Action<string> SelfUpdateDownloaded;

        /// <summary>Invoked (on a background thread) when the update check/download fails: (reason).</summary>
        public Action<string> SelfUpdateFailed;

        /// <summary>Invoked (on the platform UI thread) when a "stop" command is received.</summary>
        public Action OnShutdownRequest;

        /// <summary>Invoked to surface a notification (title, text). May be called from background threads.</summary>
        public Action<string, string> Notify;

        /// <summary>Invoked to update a persistent status display (e.g. tray tooltip). May be called from background threads.</summary>
        public Action<string> StatusChanged;

        /// <summary>Invoked with the current update stage and latest output line.</summary>
        public Action<string, string> UpdateProgressChanged;

        /// <summary>Invoked once when a new dependency check/update begins.</summary>
        public Action UpdateProgressStarted;

        /// <summary>Invoked when an installation/update has completed and the service is ready.</summary>
        public Action UpdateProgressCompleted;

        public bool ShuttingDown { get { return shuttingDown; } }
        public string Url { get { return "http://127.0.0.1:" + port; } }

        public Core(int port, bool autoOpen)
        {
            this.port = port;
            this.autoOpen = autoOpen;
            this.logPath = Path.Combine(Path.GetTempPath(), "dsh-tray-server.log");
            this.latestLogPath = Path.Combine(Path.GetTempPath(), "dsh-tray-server-latest.log");
        }

        public void Start()
        {
            if (PortIsOpen())
            {
                KillPortOwner();
                WaitForPortClosed(5000);
            }
            StartServer();
            StartPortWatcher();
            StartCommandListener();
            StartSelfUpdateCheck();
        }

        /// <summary>Kick off a background check for a newer GitHub release.</summary>
        private void StartSelfUpdateCheck()
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    SelfUpdater.ReleaseInfo info = SelfUpdater.CheckLatest();
                    if (info == null || info.Version == null) return;
                    Version current = SelfUpdater.TryParseVersion(SelfUpdater.GetCurrentVersion());
                    if (current == null || info.Version <= current) return;
                    Action<string, string, string> cb = SelfUpdateAvailable;
                    if (cb != null) cb(info.Tag, info.DownloadUrl, info.HtmlUrl);
                }
                catch
                {
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>Download the update installer (called by the platform layer after the user accepts).</summary>
        public void BeginSelfUpdateDownload(string downloadUrl)
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    string dir = Path.Combine(Path.GetTempPath(), "dsh-tray-update");
                    Directory.CreateDirectory(dir);
                    string dest = Path.Combine(dir, SelfUpdater.GetInstallerFileName());
                    SelfUpdater.DownloadFile(downloadUrl, dest, delegate (long received, long total)
                    {
                        Action<long, long> cb = SelfUpdateProgress;
                        if (cb != null) cb(received, total);
                    });
                    Action<string> done = SelfUpdateDownloaded;
                    if (done != null) done(dest);
                }
                catch (Exception ex)
                {
                    Action<string> fail = SelfUpdateFailed;
                    if (fail != null) fail(ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
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
                // Launch the installed DSH PWA through Chrome's proxy entry
                // point. The dsh web process itself is started with --no-open.
                if (!TryOpenChromeApp())
                {
                    // Chrome does not expose a reliable unattended PWA
                    // installer on Windows. Open the installable page once so
                    // the user can click Chrome's "Install app" button.
                    Process.Start(new ProcessStartInfo(webLaunchUrl ?? Url) { UseShellExecute = true });
                    NotifyUser("Chrome DSH App 尚未安装，请在地址栏点击“安装应用”。");
                }
#else
                Process.Start(new ProcessStartInfo("open", Url) { UseShellExecute = false });
#endif
            }
            catch
            {
            }
        }

#if WINDOWS
        private const string ChromeDshAppId = "hgiemfgfjhalibdoboikeiepnnjapnpc";

        private bool TryOpenChromeApp()
        {
            string profile = FindChromeAppProfile(ChromeDshAppId);
            string proxy = FindChromeProxy();
            if (profile == null || proxy == null) return false;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(proxy)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("--profile-directory=" + profile);
                psi.ArgumentList.Add("--app-id=" + ChromeDshAppId);
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string FindChromeProxy()
        {
            string[] roots = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432")
            };
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.IsNullOrEmpty(roots[i])) continue;
                string path = Path.Combine(roots[i], "Google", "Chrome", "Application", "chrome_proxy.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private string FindChromeAppProfile(string appId)
        {
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data");
            if (!Directory.Exists(userData)) return null;

            string appFolder = "_crx_" + appId;
            string[] profiles = Directory.GetDirectories(userData, "*", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < profiles.Length; i++)
            {
                string profileName = Path.GetFileName(profiles[i]);
                if (!string.Equals(profileName, "Default", StringComparison.OrdinalIgnoreCase)
                    && !profileName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                string installed = Path.Combine(profiles[i], "Web Applications", appFolder);
                if (Directory.Exists(installed)) return profileName;
            }
            return null;
        }

#endif

        public void OpenLog()
        {
            try
            {
                lock (logLock)
                {
                    if (!File.Exists(logPath)) File.WriteAllText(logPath, "");
                    if (logWriter != null) logWriter.Flush();

                    List<string> lineList = new List<string>();
                    using (FileStream stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) lineList.Add(line);
                    }
                    string[] lines = lineList.ToArray();
                    Array.Reverse(lines);
                    File.WriteAllLines(latestLogPath, lines);
                }
#if WINDOWS
                ProcessStartInfo viewer = new ProcessStartInfo("notepad.exe");
                viewer.UseShellExecute = false;
                viewer.ArgumentList.Add(latestLogPath);
                Process.Start(viewer);
#else
                Process.Start(new ProcessStartInfo("open", latestLogPath) { UseShellExecute = false });
#endif
            }
            catch
            {
            }
        }

        /// <summary>
        /// Open a fresh terminal console with the dsh CLI ready — for running
        /// plugin installs (`dsh plugin --profile web add &lt;pkg&gt;`), one-shot
        /// headless tasks, and other dsh commands. The tray keeps managing the
        /// web service, so the console must not run another `dsh web`.
        /// </summary>
        public void OpenConsole()
        {
            try
            {
#if WINDOWS
                // PowerShell -NoExit session with a `dsh` function in session
                // scope: the function survives the -Command script, so the
                // prompt accepts `dsh plugin ...` directly (a doskey macro
                // defined inside a batch file does NOT persist, hence PS).
                string script =
                    "function dsh { & npx.cmd --yes @deepseek-ai/dsh@next @args };" +
                    "Write-Host '';" +
                    "Write-Host '  dsh plugin --profile web add <package>     install a plugin';" +
                    "Write-Host '  dsh plugin --profile web remove <package>  remove a plugin';" +
                    "Write-Host '  dsh --profile headless task                run a one-shot task';" +
                    "Write-Host '';" +
                    "Write-Host '  Note: the tray already runs dsh web on port " + port + ".';" +
                    "Write-Host '  Use the tray menu to restart or stop that service.';" +
                    "Write-Host '';" +
                    "Write-Host '  First time? pnpm is required for plugin management:';" +
                    "Write-Host '    npm install -g pnpm';" +
                    "Write-Host '';" +
                    "Write-Host '  Warming the npx cache with dsh --help ...';" +
                    "Write-Host '';" +
                    "& npx.cmd --yes @deepseek-ai/dsh@next --help;" +
                    "Write-Host '';" +
                    "Write-Host '  The dsh function is ready - type dsh commands, e.g.:';" +
                    "Write-Host '    dsh plugin --profile web add <package>';" +
                    "Write-Host '';";
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoExit");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(script);
                Process.Start(psi);
#else
                string cmd =
                    "cd ~ && " +
                    "echo 'dsh plugin --profile web add <package>  - install a plugin' && " +
                    "echo 'dsh --profile headless \"task\"           - run a one-shot task' && " +
                    "echo 'Note: the tray already runs the web service on port " + port + ".' && " +
                    "npx --yes @deepseek-ai/dsh@next --help";
                ProcessStartInfo psi = new ProcessStartInfo("osascript");
                psi.UseShellExecute = false;
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("tell application \"Terminal\" to do script " + AppleScriptQuote(cmd));
                Process.Start(psi);
#endif
            }
            catch
            {
            }
        }

        private static string AppleScriptQuote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public void RestartServer()
        {
            if (shuttingDown) return;

            UpdateStatus("DeepSeek Harness — 服务正在重启…");

#if WINDOWS
            // The DSH Chrome app keeps the old server alive from the user's
            // point of view (stale websocket/banners), so close those windows
            // before tearing the service down.
            CloseDshChromeApps();
#endif
            StopServer();
            KillPortOwner();
            WaitForPortClosed(5000);
            StartServer();
            StartPortWatcher();
        }

#if WINDOWS
        /// <summary>
        /// Close every Chrome window that belongs to the DSH app (all other
        /// Chrome windows stay untouched). Matches chrome.exe browser
        /// processes, one per instance, whose command line carries either the
        /// installed DSH app id (PWA launched via chrome_proxy --app-id) or a
        /// URL on this tray's port --set by "open web page". Regular Chrome
        /// instances (no URL on the command line) never match.
        /// </summary>
        private void CloseDshChromeApps()
        {
            try
            {
                // Only browser processes (no --type=): killing the browser
                // process tree closes that instance's windows cleanly.
                string ps =
                    "$ids = Get-CimInstance Win32_Process -Filter \"Name='chrome.exe'\" | " +
                    "Where-Object { $_.CommandLine -notmatch '--type=' -and (" +
                    "$_.CommandLine -match '--app-id=" + ChromeDshAppId + "' -or " +
                    "$_.CommandLine -match '127\\.0\\.0\\.1:" + port + "' -or " +
                    "$_.CommandLine -match 'localhost:" + port + "') } | " +
                    "ForEach-Object { $_.ProcessId }; " +
                    "foreach ($id in $ids) { & taskkill /PID $id /T /F 2>$null | Out-Null }";

                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(ps);
                Process p = Process.Start(psi);
                if (p != null) p.WaitForExit(15000);
            }
            catch
            {
            }
        }
#endif

        private void StartServer()
        {
            downloadInstallNoticeShown = false;
            installRequired = false;
            downloadStepCount = 0;
            lastNpmActivityUtc = DateTime.MinValue;
            updateProgressStage = null;
            webReady = false;
            webLaunchUrl = null;
            UpdateStatus("DeepSeek Harness — 服务正在启动…");

            ProcessStartInfo psi = new ProcessStartInfo();
#if WINDOWS
            psi.FileName = "cmd.exe";
            // --loglevel http makes npm emit fetch/cache-miss lines even when
            // stderr is redirected, so the tray can report downloads.
            psi.Arguments = "/c npx --yes --loglevel http @deepseek-ai/dsh@next web --port " + port + " --no-open";
#else
            psi.FileName = "/bin/sh";
            psi.Arguments = "-c \"npx --yes --loglevel http @deepseek-ai/dsh@next web --port " + port + " --no-open\"";
#endif
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            // dsh (Node) writes UTF-8 to stdout/stderr. This is a WinExe with no
            // console, so .NET's default Console.OutputEncoding falls back to the
            // system ANSI code page (GBK on Chinese Windows), which would decode
            // the UTF-8 bytes as mojibake. Pin UTF-8 explicitly on both streams.
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["npm_config_yes"] = "true";

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
            // npx also receives npm_config_yes=true. Closing stdin guarantees that
            // an incompatible npx version cannot leave the hidden process waiting.
            server.StandardInput.Close();
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
        }

        private void Log(string line)
        {
            if (line == null) return;

            CheckServerOutput(line);
            int webMarker = line.IndexOf("dsh web:", StringComparison.OrdinalIgnoreCase);
            if (webMarker >= 0)
            {
                string announcedUrl = line.Substring(webMarker + "dsh web:".Length).Trim();
                if (announcedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || announcedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    webLaunchUrl = announcedUrl;
                webReady = true;
            }
            TrackDownloadProgress(line);
            if (downloadInstallNoticeShown) ReportUpdateProgress(null, line);
            if (logWriter == null) return;

            try
            {
                lock (logLock)
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
        /// Surface npm dependency activity immediately; cache revalidation can
        /// take minutes even when no package update is ultimately required.
        /// </summary>
        private void CheckServerOutput(string line)
        {
            lock (downloadNoticeLock)
            {
                bool installNotice = line.IndexOf("will be installed", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Need to install", StringComparison.OrdinalIgnoreCase) >= 0;
                bool npmFetch = line.IndexOf("npm http fetch GET", StringComparison.OrdinalIgnoreCase) >= 0;
                if (installNotice) installRequired = true;
                if (downloadInstallNoticeShown || (!installNotice && !npmFetch)) return;

                downloadInstallNoticeShown = true;
                downloadStepCount = 0;
                lastNpmActivityUtc = DateTime.UtcNow;
            }

            UpdateStatus("DeepSeek Harness — 正在检查 dsh 依赖…");
            Action started = UpdateProgressStarted;
            if (started != null) started();
            ReportUpdateProgress("正在检查并解析依赖…", line);
        }

        /// <summary>Tracks npm network activity and completed tarball fetches.</summary>
        private void TrackDownloadProgress(string line)
        {
            if (!downloadInstallNoticeShown) return;
            if (line.IndexOf("npm http fetch GET", StringComparison.OrdinalIgnoreCase) < 0) return;

            lock (downloadNoticeLock)
            {
                lastNpmActivityUtc = DateTime.UtcNow;
            }

            if (line.IndexOf(".tgz", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                installRequired = true;
                int count = Interlocked.Increment(ref downloadStepCount);
                UpdateStatus("DeepSeek Harness — 正在下载 dsh：已完成 " + count + " 个软件包");
                ReportUpdateProgress("正在下载软件包（已完成 " + count + " 个）…", null);
            }
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
                int packageCountAtLastPhaseChange = -1;
                bool installingStatusShown = false;

                while (true)
                {
                    if (shuttingDown || generation != portWatcherGeneration) return;
                    // dsh emits this only after the web server and its profile
                    // have finished initializing: dsh web: http://...
                    if (webReady)
                    {
                        if (shuttingDown || generation != portWatcherGeneration) return;
                        if (downloadInstallNoticeShown)
                        {
                            int downloaded = Volatile.Read(ref downloadStepCount);
                            if (installRequired)
                            {
                                NotifyUser("dsh 更新完成，服务已启动（下载了 " + downloaded + " 个软件包）。");
                                ReportUpdateProgress("服务已就绪。", null);
                            }
                            else
                            {
                                NotifyUser("依赖检查完成，服务已启动。");
                                ReportUpdateProgress("服务已就绪。", null);
                            }
                            Action completed = UpdateProgressCompleted;
                            if (completed != null) completed();
                        }
                        else
                        {
                            NotifyUser("服务已启动并就绪。");
                        }
                        if (autoOpen) OpenBrowser();
                        UpdateStatus("DeepSeek Harness");
                        return;
                    }

                    // npx failed fast: OnServerExited has already told the user;
                    // do not keep polling and later fire a misleading timeout notice.
                    Process s = server;
                    if (s != null && s.HasExited) return;

                    if (downloadInstallNoticeShown)
                    {
                        int currentCount = Volatile.Read(ref downloadStepCount);
                        if (currentCount != packageCountAtLastPhaseChange)
                        {
                            packageCountAtLastPhaseChange = currentCount;
                            installingStatusShown = false;
                        }

                        double idleSeconds = GetNpmIdleSeconds();
                        if (!installingStatusShown && currentCount > 0 && idleSeconds >= 10)
                        {
                            installingStatusShown = true;
                            UpdateStatus("DeepSeek Harness — 已下载 " + currentCount + " 个软件包，正在安装并启动…");
                            ReportUpdateProgress("正在安装并启动服务…", null);
                        }
                    }

                    Thread.Sleep(500);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private bool PortIsOpen()
        {
            using (TcpClient client = new TcpClient())
            {
                try
                {
                    IAsyncResult result = client.BeginConnect("127.0.0.1", port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(300)) return false;
                    client.EndConnect(result);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private void WaitForPortClosed(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (PortIsOpen() && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
        }

#if WINDOWS
        private void KillPortOwner()
        {
            try
            {
                Process netstat = new Process();
                netstat.StartInfo = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                netstat.Start();
                string output = netstat.StandardOutput.ReadToEnd();
                netstat.WaitForExit(2000);

                string portSuffix = ":" + port;
                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] fields = lines[i].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 5 || !string.Equals(fields[0], "TCP", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!fields[1].EndsWith(portSuffix, StringComparison.OrdinalIgnoreCase)
                        && !fields[2].EndsWith(portSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    int pid;
                    if (!int.TryParse(fields[fields.Length - 1], out pid) || pid <= 0 || pid == Process.GetCurrentProcess().Id) continue;

                    Process killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (killer != null) killer.WaitForExit(3000);
                }
            }
            catch
            {
            }
        }
#else
        private void KillPortOwner()
        {
        }
#endif

        private double GetNpmIdleSeconds()
        {
            lock (downloadNoticeLock)
            {
                if (lastNpmActivityUtc == DateTime.MinValue) return 0;
                return Math.Max(0, (DateTime.UtcNow - lastNpmActivityUtc).TotalSeconds);
            }
        }

        private void ReportUpdateProgress(string stage, string latestLine)
        {
            if (stage != null) updateProgressStage = stage;
            Action<string, string> cb = UpdateProgressChanged;
            if (cb != null) cb(updateProgressStage ?? "正在准备更新…", latestLine);
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

            lock (logLock)
            {
                if (logWriter != null)
                {
                    logWriter.Dispose();
                    logWriter = null;
                }
            }
        }
    }
}
