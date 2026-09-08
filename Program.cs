using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            return CheckLatest(30);
        }

        /// <summary>Query the latest stable release with a custom timeout (used for the pre-start prompt).</summary>
        internal static ReleaseInfo CheckLatest(int timeoutSeconds)
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
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

    /// <summary>Persistent user preferences (currently: the dsh dist-tag branch).</summary>
    internal static class AppSettings
    {
        internal const string LatestBranch = "latest";
        internal const string NextBranch = "next";
        internal const string AlphaBranch = "alpha";

        private static string SettingsDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "dsh-tray");
            }
        }

        private static string BranchFile { get { return Path.Combine(SettingsDir, "branch.txt"); } }

        internal static bool IsKnownBranch(string branch)
        {
            return branch == LatestBranch || branch == NextBranch || branch == AlphaBranch;
        }

        /// <summary>Read the persisted branch; defaults to "next" (historic behavior).</summary>
        internal static string GetBranch()
        {
            try
            {
                if (File.Exists(BranchFile))
                {
                    string b = File.ReadAllText(BranchFile).Trim();
                    if (IsKnownBranch(b)) return b;
                }
            }
            catch
            {
            }
            return NextBranch;
        }

        internal static void SetBranch(string branch)
        {
            if (!IsKnownBranch(branch)) return;
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(BranchFile, branch);
            }
            catch
            {
            }
        }

        private static string ProfileFile { get { return Path.Combine(SettingsDir, "profile.txt"); } }

        internal const string DefaultProfile = "web";

        /// <summary>
        /// Profile-name validation aligned with dsh's resolver (rejects "/",
        /// "\", ".", "..", the reserved node_modules/desktop names) plus a strict
        /// ASCII charset starting with a letter or digit — the name is embedded
        /// in a shell command line, so no shell metacharacters are allowed.
        /// </summary>
        internal static bool IsValidProfileName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
            char first = name[0];
            if (!char.IsAsciiLetterOrDigit(first)) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.') return false;
            }
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return false;
            if (name == "." || name == "..") return false;
            if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(name, "desktop", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>Read the persisted profile; defaults to the built-in "web".</summary>
        internal static string GetProfile()
        {
            try
            {
                if (File.Exists(ProfileFile))
                {
                    string p = File.ReadAllText(ProfileFile).Trim();
                    if (IsValidProfileName(p)) return p;
                }
            }
            catch
            {
            }
            return DefaultProfile;
        }

        internal static void SetProfile(string profile)
        {
            if (!IsValidProfileName(profile)) return;
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(ProfileFile, profile);
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
        private string dshBranch;
        private string dshProfile;
        private string dshVersionLatest;   // null = querying, "" = unknown, otherwise concrete version
        private string dshVersionNext;
        private string dshVersionAlpha;

        /// <summary>DSH's Chrome Web Store app id, used for PWA launch on Windows and macOS.</summary>
        private const string ChromeDshAppId = "hgiemfgfjhalibdoboikeiepnnjapnpc";

        /// <summary>Invoked (on a background thread) when a newer release is available: (latestTag, downloadUrl, releaseUrl).</summary>
        public Action<string, string, string> SelfUpdateAvailable;

        /// <summary>Invoked (on a background thread) with download progress: (received, total).</summary>
        public Action<long, long> SelfUpdateProgress;

        /// <summary>Invoked (on a background thread) after the installer finished downloading: (installerPath).</summary>
        public Action<string> SelfUpdateDownloaded;

        /// <summary>Invoked (on a background thread) when the update check/download fails: (reason).</summary>
        public Action<string> SelfUpdateFailed;

        /// <summary>Invoked (on a background thread) once the concrete dsh version is known: (version, empty string when unknown).</summary>
        public Action<string> DshVersionChanged;

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

        /// <summary>Absolute path of the server log (used by the log viewer).</summary>
        public string ServerLogPath { get { return logPath; } }

        /// <summary>The currently selected dsh dist-tag branch (latest / next / alpha).</summary>
        public string DshBranch { get { return dshBranch; } }

        /// <summary>The currently selected dsh profile (web or a custom one).</summary>
        public string DshProfile { get { return dshProfile; } }

        /// <summary>Absolute path of $DSH_HOME/profiles (DSH_HOME defaults to ~/.dsh).</summary>
        internal static string ProfilesDirectory
        {
            get
            {
                string root = Environment.GetEnvironmentVariable("DSH_HOME");
                if (string.IsNullOrWhiteSpace(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                else
                    root = root.Trim();
                return Path.Combine(root, "profiles");
            }
        }

        /// <summary>Absolute path of a single profile's directory.</summary>
        private static string ProfileDirectory(string name)
        {
            return Path.Combine(ProfilesDirectory, name);
        }

        /// <summary>
        /// All present, switchable profiles: names of profile directories that
        /// carry a package.json, excluding reserved names (node_modules,
        /// desktop). Sorted ordinally.
        /// </summary>
        public List<string> GetAvailableProfiles()
        {
            List<string> result = new List<string>();
            try
            {
                string root = ProfilesDirectory;
                if (!Directory.Exists(root)) return result;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (!AppSettings.IsValidProfileName(name)) continue;
                    if (!File.Exists(Path.Combine(dir, "package.json"))) continue;
                    result.Add(name);
                }
            }
            catch
            {
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// Switch the dsh profile, persist the choice, and restart the service
        /// on the new profile. Matches the branch-switch behavior.
        /// </summary>
        public void SetDshProfile(string profile)
        {
            if (!AppSettings.IsValidProfileName(profile) || profile == dshProfile) return;
            dshProfile = profile;
            AppSettings.SetProfile(profile);
            Log("[profile] switching to " + profile);
            RestartServer();
        }

        /// <summary>
        /// Create a web-style profile (dsh-base + dsh-web-app bundles, live
        /// patch reload) under $DSH_HOME/profiles/&lt;name&gt;. The written
        /// files mirror what `dsh plugin` init would create — manifest, empty
        /// user patch layer, pnpm workspace — so plugin management and future
        /// boots work without pnpm being installed first. Returns false with an
        /// error message on failure.
        /// </summary>
        public bool CreateProfile(string name, out string error)
        {
            error = null;
            if (!AppSettings.IsValidProfileName(name))
            {
                error = "名称无效：仅允许字母、数字、-、_、.，且以字母或数字开头（desktop 与 node_modules 为保留名）。";
                return false;
            }
            try
            {
                string dir = ProfileDirectory(name);
                Directory.CreateDirectory(dir);

                string manifest = Path.Combine(dir, "package.json");
                if (!File.Exists(manifest))
                {
                    // Literal JSON (same shape `dsh plugin` init writes): name
                    // is charset-validated, so no escaping is needed.
                    string manifestJson =
                        "{\n" +
                        "  \"name\": \"dsh-profile-" + name + "\",\n" +
                        "  \"private\": true,\n" +
                        "  \"dependencies\": {},\n" +
                        "  \"dsh\": {\n" +
                        "    \"profile\": {\n" +
                        "      \"bundles\": [\n" +
                        "        \"@deepseek-ai/dsh-base\",\n" +
                        "        \"@deepseek-ai/dsh-web-app\"\n" +
                        "      ],\n" +
                        "      \"patchReload\": \"live\"\n" +
                        "    }\n" +
                        "  }\n" +
                        "}\n";
                    File.WriteAllText(manifest, manifestJson, new UTF8Encoding(false));
                }

                string patchFile = Path.Combine(dir, "cordis.patch.yml");
                if (!File.Exists(patchFile))
                {
                    File.WriteAllText(patchFile,
                        "# Your patch layer for this dsh profile, applied after every bundle layer:\n" +
                        "# a top-level YAML array of loader patch entries (id-targeted config\n" +
                        "# overrides, disables, and insert lists; `!!js` expressions allowed).\n" +
                        "[]\n",
                        new UTF8Encoding(false));
                }

                string workspaceFile = Path.Combine(dir, "pnpm-workspace.yaml");
                if (!File.Exists(workspaceFile))
                {
                    File.WriteAllText(workspaceFile,
                        "packages:\n" +
                        "  - .\n\n" +
                        "nodeLinker: hoisted\n" +
                        "autoInstallPeers: false\n",
                        new UTF8Encoding(false));
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Delete a profile directory (with its plugins and configuration).
        /// The profile currently in use cannot be deleted — switch to another
        /// profile first. Deleting the built-in "web" is allowed, because dsh
        /// re-initializes it from its template on the next boot. Returns false
        /// with an error message on failure.
        /// </summary>
        public bool DeleteProfile(string name, out string error)
        {
            error = null;
            if (!AppSettings.IsValidProfileName(name))
            {
                error = "名称无效。";
                return false;
            }
            string dir = ProfileDirectory(name);
            if (!Directory.Exists(dir))
            {
                error = "Profile \"" + name + "\" 不存在。";
                return false;
            }
            if (string.Equals(name, dshProfile, StringComparison.Ordinal))
            {
                error = "当前正在使用的 Profile 不能删除，请先切换到其他 Profile。";
                return false;
            }
            try
            {
                Directory.Delete(dir, true);
                Log("[profile] deleted " + name);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        public string DshVersionDisplay
        {
            get
            {
                string branch = dshBranch;
                string v = GetDshVersion(branch);
                if (v == null) return "dsh 版本查询中… · " + branch;
                if (v.Length == 0) return "dsh · " + branch;
                return "dsh " + v + " · " + branch;
            }
        }

        /// <summary>Concrete version for a branch (null = querying, "" = unknown).</summary>
        private string GetDshVersion(string branch)
        {
            if (branch == AppSettings.LatestBranch) return dshVersionLatest;
            if (branch == AppSettings.NextBranch) return dshVersionNext;
            if (branch == AppSettings.AlphaBranch) return dshVersionAlpha;
            return "";
        }

        /// <summary>Branch menu-item label with its version, e.g. "next（最新）· 0.1.2-rc.1".</summary>
        public string BranchDisplayName(string branch)
        {
            string desc =
                branch == AppSettings.LatestBranch ? "latest（稳定）" :
                branch == AppSettings.NextBranch ? "next（最新）" :
                branch == AppSettings.AlphaBranch ? "alpha（预览）" : branch;
            string v = GetDshVersion(branch);
            if (v == null || v.Length == 0) return desc;
            return desc + " · " + v;
        }

        /// <summary>npm package spec for the selected branch; "latest" uses the bare package name.</summary>
        private string PackageSpec
        {
            get { return "@deepseek-ai/dsh" + (dshBranch == AppSettings.LatestBranch ? "" : "@" + dshBranch); }
        }

        /// <summary>Switch the dsh branch, persist it, and restart the service on the new branch.</summary>
        public void SetDshBranch(string branch)
        {
            if (!AppSettings.IsKnownBranch(branch) || branch == dshBranch) return;
            dshBranch = branch;
            AppSettings.SetBranch(branch);
            Log("[branch] switching to " + branch);
            RestartServer();
            QueryDshVersionAsync();
        }

        /// <summary>Resolve the concrete dsh versions for all three branches via `npm view`.</summary>
        public void QueryDshVersionAsync()
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    dshVersionLatest = RunNpmViewVersion(AppSettings.LatestBranch) ?? "";
                    dshVersionNext = RunNpmViewVersion(AppSettings.NextBranch) ?? "";
                    dshVersionAlpha = RunNpmViewVersion(AppSettings.AlphaBranch) ?? "";
                }
                catch
                {
                    if (dshVersionLatest == null) dshVersionLatest = "";
                    if (dshVersionNext == null) dshVersionNext = "";
                    if (dshVersionAlpha == null) dshVersionAlpha = "";
                }
                Action<string> cb = DshVersionChanged;
                if (cb != null) cb(dshBranch);
            });
            t.IsBackground = true;
            t.Start();
        }

        private string RunNpmViewVersion(string branch)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
#if WINDOWS
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c npm view @deepseek-ai/dsh@" + branch + " version";
#else
            psi.FileName = "npm";
            psi.Arguments = "view @deepseek-ai/dsh@" + branch + " version";
#endif
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd(); // drain stderr to avoid any pipe deadlock
                if (!p.WaitForExit(30000)) return null;
                if (p.ExitCode != 0) return null;
                if (string.IsNullOrWhiteSpace(output)) return null;
                string first = output.Trim();
                int newline = first.IndexOfAny(new[] { '\r', '\n' });
                if (newline >= 0) first = first.Substring(0, newline);
                first = first.Trim();
                return first.Length == 0 ? null : first;
            }
        }

        public Core(int port, bool autoOpen)
        {
            this.port = port;
            this.autoOpen = autoOpen;
            this.logPath = Path.Combine(Path.GetTempPath(), "dsh-tray-server.log");
            this.latestLogPath = Path.Combine(Path.GetTempPath(), "dsh-tray-server-latest.log");
            this.dshBranch = AppSettings.GetBranch();
            this.dshProfile = AppSettings.GetProfile();
            // A saved profile may have been removed by hand; fall back to the
            // built-in web profile (dsh re-initializes it from its template).
            if (!Directory.Exists(ProfileDirectory(dshProfile)))
            {
                Log("[profile] saved profile '" + dshProfile + "' is missing; falling back to " + AppSettings.DefaultProfile);
                dshProfile = AppSettings.DefaultProfile;
                AppSettings.SetProfile(dshProfile);
            }
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
            QueryDshVersionAsync();
        }

        /// <summary>
        /// Kick off a background check for a newer GitHub release. Used by the
        /// macOS backend; the Windows backend asks about updates synchronously
        /// BEFORE starting the service (see Platform.PromptUpdateBeforeStart),
        /// so an in-place update does not boot the old service first.
        /// </summary>
        public void CheckSelfUpdateAsync()
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
            // Close DSH Chrome App windows on exit too, matching the restart
            // and branch-switch paths.
            CloseDshChromeApps();
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
                // macOS: launch the installed DSH PWA via Chrome's bundle id;
                // fall back to the default browser when it is not installed.
                if (!TryOpenMacChromeApp())
                {
                    Process.Start(new ProcessStartInfo("open", Url) { UseShellExecute = false });
                    NotifyUser("DSH App 尚未安装，请在 Chrome 地址栏点击“安装应用”。");
                }
#endif
            }
            catch
            {
            }
        }

#if WINDOWS
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

#if MACOS
        private string MacChromeAppBundleId
        {
            get { return "com.google.Chrome.app." + ChromeDshAppId; }
        }

        private bool TryOpenMacChromeApp()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("/usr/bin/open");
                psi.UseShellExecute = false;
                psi.ArgumentList.Add("-b");
                psi.ArgumentList.Add(MacChromeAppBundleId);
                Process p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(10000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private void CloseDshChromeApps()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("/usr/bin/osascript");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("tell application id \"" + MacChromeAppBundleId + "\" to quit");
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
            }
            catch
            {
            }
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
                // 不再使用 notepad（由 Windows 平台层的自绘日志查看器接管）。
#else
                Process.Start(new ProcessStartInfo("open", latestLogPath) { UseShellExecute = false });
#endif
            }
            catch
            {
            }
        }

        /// <summary>
        /// Streaming tail read of the server log: the last <paramref name="maxLines"/>
        /// lines, scanned from EOF backwards in blocks, so arbitrarily large logs
        /// stay fast and memory-bounded. Returns lines in file order.
        /// </summary>
        public static List<string> ReadLogTail(string path, int maxLines)
        {
            List<string> result = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;

                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long length = stream.Length;
                    if (length == 0) return result;

                    const int blockSize = 64 * 1024;
                    byte[] buffer = new byte[blockSize];
                    StringBuilder sb = new StringBuilder();
                    long pos = length;
                    int newlineCount = 0;

                    while (pos > 0)
                    {
                        int toRead = (int)Math.Min(blockSize, pos);
                        pos -= toRead;
                        stream.Seek(pos, SeekOrigin.Begin);
                        int read = stream.Read(buffer, 0, toRead);
                        if (read <= 0) break;

                        string chunk = Encoding.UTF8.GetString(buffer, 0, read);
                        sb.Insert(0, chunk);
                        for (int i = 0; i < chunk.Length; i++)
                            if (chunk[i] == '\n') newlineCount++;

                        // Enough lines (a margin avoids splitting mid-line).
                        if (newlineCount >= maxLines + 8) break;
                    }

                    string text = sb.ToString();
                    string[] all = text.Split('\n');
                    int count = all.Length;
                    // A trailing '\n' yields one empty tail element; that is the
                    // "nothing after" case and should be dropped. A file without
                    // trailing newline keeps its (possibly incomplete) last line.
                    bool endsWithNewline = text.Length > 0 && text[text.Length - 1] == '\n';
                    if (endsWithNewline) count--;

                    int start = Math.Max(0, count - maxLines);
                    for (int i = start; i < count; i++)
                    {
                        string line = all[i];
                        if (line.EndsWith("\r")) line = line.Substring(0, line.Length - 1);
                        result.Add(line);
                    }
                }
            }
            catch
            {
            }
            return result;
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
                    "function dsh { & npx.cmd --yes " + PackageSpec + " @args };" +
                    "Write-Host '';" +
                    "Write-Host '  dsh plugin --profile " + dshProfile + " add <package>     install a plugin';" +
                    "Write-Host '  dsh plugin --profile " + dshProfile + " remove <package>  remove a plugin';" +
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
                    "& npx.cmd --yes " + PackageSpec + " --help;" +
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
                    "echo 'dsh plugin --profile " + dshProfile + " add <package>  - install a plugin' && " +
                    "echo 'dsh --profile headless \"task\"           - run a one-shot task' && " +
                    "echo 'Note: the tray already runs the web service on port " + port + ".' && " +
                    "npx --yes " + PackageSpec + " --help";
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

            Log("[restart] restart requested");
            UpdateStatus("DeepSeek Harness — 服务正在重启…");

            // The DSH Chrome app keeps the old server alive from the user's
            // point of view (stale websocket/banners), so close those windows
            // before tearing the service down.
            CloseDshChromeApps();
            Log("[restart] stopping server");
            StopServer();
            KillPortOwner();
            WaitForPortClosed(5000);
            StartServer();
            StartPortWatcher();
            Log("[restart] server restarted");
        }

#if WINDOWS
        /// <summary>
        /// Close every Chrome window that belongs to the DSH app (all other
        /// Chrome windows stay untouched). Matches chrome.exe browser
        /// processes, one per instance, whose command line carries either the
        /// installed DSH app id (PWA launched via chrome_proxy --app-id) or a
        /// URL on this tray's port --set by "open web page". Regular Chrome
        /// instances (no URL on the command line) never match.
        ///
        /// Two channels, both in-process (no PowerShell/WMI/taskkill
        /// subprocesses, which AV products like Huorong block):
        ///   1. WM_CLOSE to the instance's visible top-level windows -- the
        ///      graceful, AV-friendly way; chrome exits on its own.
        ///   2. TerminateProcess as a fallback if the window did not close.
        /// </summary>
        private void CloseDshChromeApps()
        {
            try
            {
                List<int> pids = FindProcessesByName("chrome.exe");
                List<int> targets = new List<int>();
                List<bool> targetIsApp = new List<bool>();
                for (int i = 0; i < pids.Count; i++)
                {
                    int pid = pids[i];
                    string cmdline = ReadProcessCommandLine(pid);
                    if (string.IsNullOrEmpty(cmdline)) continue;
                    // Sub-processes (renderer/gpu/...) carry --type=; only the
                    // browser process has the app/URL switches we match.
                    if (cmdline.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    bool isDsh =
                        cmdline.IndexOf("--app-id=" + ChromeDshAppId, StringComparison.OrdinalIgnoreCase) >= 0
                        || cmdline.IndexOf("127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase) >= 0
                        || cmdline.IndexOf("localhost:" + port, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isDsh)
                    {
                        targets.Add(pid);
                        targetIsApp.Add(cmdline.IndexOf("--app-id=" + ChromeDshAppId, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                }
                if (targets.Count == 0)
                {
                    Log("[close-app] no DSH chrome instance found");
                    return;
                }
                Log("[close-app] matched " + targets.Count + " DSH chrome instance(s)");

                // Channel 1: graceful close of the DSH windows only. The DSH
                // page may share its Chrome instance with the user's other
                // tabs, so match by window, never by process:
                //   - PWA instance (--app-id): only App windows, i.e. window
                //     titles WITHOUT the " - Google Chrome" tab suffix.
                //   - URL instance: the tab window whose title carries the
                //     DSH name or the service URL.
                for (int i = 0; i < targets.Count; i++)
                {
                    pendingClosePort = port;
                    pendingCloseIsApp = targetIsApp[i];
                    PostCloseToWindows(targets[i]);
                    Log("[close-app] WM_CLOSE sent to instance pid " + targets[i]);
                }
                Thread.Sleep(1500);

                // Channel 2: force-kill a browser process only when no other
                // window remains (empty instance after the DSH window closed).
                for (int i = 0; i < targets.Count; i++)
                {
                    int pid = targets[i];
                    if (!IsProcessAlive(pid)) continue;
                    if (ProcessHasVisibleWindow(pid)) continue;
                    IntPtr handle = OpenProcessWin32(ProcessTerminate, false, (uint)pid);
                    if (handle == IntPtr.Zero) continue;
                    try { TerminateProcess(handle, 1); }
                    finally { CloseHandle(handle); }
                    Log("[close-app] force-killed empty instance pid " + pid);
                }
            }
            catch (Exception ex)
            {
                // Never silent: surface what failed so the close path can be
                // diagnosed from the log.
                Log("[close-app] ERROR: " + ex.ToString().Replace(Environment.NewLine, " | "));
            }
        }

        private const uint WmCloseMsg = 0x0010;
        private static int pendingClosePort;
        private static bool pendingCloseIsApp;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // Keep the delegates alive across the EnumWindows calls.
        private static readonly EnumWindowsProc EnumWindowsHandler = EnumWindowsCallback;
        private static readonly EnumWindowsProc HasWindowHandler = HasWindowCallback;

        private static bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid != (uint)lParam.ToInt64() || !IsWindowVisible(hWnd)) return true;
            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrEmpty(title)) return true;

            // Regular Chrome tab windows always carry this suffix; PWA App
            // windows do not. With the suffix stripped, "DeepSeek Harness"
            // appears in the app title instead of the repo name below it.
            bool tabWindow = title.EndsWith(" - Google Chrome", StringComparison.OrdinalIgnoreCase);
            bool dshTitle =
                title.StartsWith("DeepSeek Harness - ", StringComparison.OrdinalIgnoreCase)
                || title.IndexOf("127.0.0.1:" + pendingClosePort, StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("localhost:" + pendingClosePort, StringComparison.OrdinalIgnoreCase) >= 0;

            bool close = pendingCloseIsApp ? (!tabWindow && dshTitle) : dshTitle;
            if (close) PostMessage(hWnd, WmCloseMsg, IntPtr.Zero, IntPtr.Zero);
            return true;
        }

        private static bool HasWindowCallback(IntPtr hWnd, IntPtr lParam)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == (uint)lParam.ToInt64() && IsWindowVisible(hWnd)) return false; // stop at first match
            return true;
        }

        private static void PostCloseToWindows(int processId)
        {
            EnumWindows(EnumWindowsHandler, new IntPtr(processId));
        }

        private static bool ProcessHasVisibleWindow(int processId)
        {
            int matches = 0;
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == (uint)lParam.ToInt64() && IsWindowVisible(hWnd)) { matches++; return false; }
                return true;
            }, new IntPtr(processId));
            return matches > 0;
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return null;
            StringBuilder sb = new StringBuilder(len + 2);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool IsProcessAlive(int processId)
        {
            IntPtr handle = OpenProcessWin32(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)processId);
            if (handle == IntPtr.Zero)
            {
                // ERROR_INVALID_PARAMETER (87) means the pid is gone;
                // anything else (e.g. access denied) means it still exists.
                return Marshal.GetLastWin32Error() != 87;
            }
            CloseHandle(handle);
            return true;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2;
            public IntPtr Reserved3;
            public IntPtr UniqueProcessId;
            public IntPtr Reserved4;
        }

        private const uint Th32csSnapprocess = 0x2;
        private const int ProcessVmRead = 0x0010;
        private const int ProcessQueryInformation = 0x0400;
        private const int ProcessTerminate = 0x0001;

        private static List<int> FindProcessesByName(string imageName)
        {
            List<int> result = new List<int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
            try
            {
                ProcessEntry32 entry = new ProcessEntry32();
                entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        if (string.Equals(entry.szExeFile, imageName, StringComparison.OrdinalIgnoreCase))
                            result.Add((int)entry.th32ProcessID);
                    }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return result;
        }

        /// <summary>
        /// Read another process's command line via its PEB.
        /// x64 layout only (the tray and its target Chrome are win-x64);
        /// returns null on any failure (access denied, 32-bit process, ...).
        /// </summary>
        private static string ReadProcessCommandLine(int processId)
        {
            IntPtr proc = OpenProcessWin32(ProcessQueryInformation | ProcessVmRead, false, (uint)processId);
            if (proc == IntPtr.Zero || proc == new IntPtr(-1)) return null;
            try
            {
                ProcessBasicInformation pbi;
                int retLen;
                int status = NtQueryInformationProcess(proc, 0, out pbi,
                    Marshal.SizeOf<ProcessBasicInformation>(), out retLen);
                if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

                // PEB -> ProcessParameters (x64: 0x20)
                IntPtr parameters = ReadProcessPointer(proc, pbi.PebBaseAddress, 0x20);
                if (parameters == IntPtr.Zero) return null;

                // RTL_USER_PROCESS_PARAMETERS.CommandLine (x64: offset 0x70):
                // the UNICODE_STRING lives there, so read it by address.
                IntPtr commandLine = parameters + 0x70;

                // UNICODE_STRING: Length (ushort @0), padding (0-1), Buffer (x64: offset 8)
                byte[] raw = new byte[16];
                if (!ReadProcessBytes(proc, commandLine, raw, raw.Length)) return null;
                int length = raw[0] | (raw[1] << 8);
                if (length <= 0) return null;
                IntPtr buffer = new IntPtr(BitConverter.ToInt64(raw, 8));
                byte[] chars = new byte[length];
                if (!ReadProcessBytes(proc, buffer, chars, chars.Length)) return null;
                return Encoding.Unicode.GetString(chars);
            }
            finally
            {
                CloseHandle(proc);
            }
        }

        private static IntPtr ReadProcessPointer(IntPtr proc, IntPtr address, int offset)
        {
            byte[] buf = new byte[IntPtr.Size];
            if (!ReadProcessBytes(proc, address + offset, buf, buf.Length)) return IntPtr.Zero;
            long value = IntPtr.Size == 8 ? BitConverter.ToInt64(buf, 0) : BitConverter.ToInt32(buf, 0);
            return new IntPtr(value);
        }

        private static bool ReadProcessBytes(IntPtr proc, IntPtr address, byte[] buffer, int count)
        {
            if (count == 0) return true;
            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr read;
                if (!ReadProcessMemory(proc, address, pinned.AddrOfPinnedObject(), count, out read)) return false;
                return read.ToInt64() == count;
            }
            finally
            {
                pinned.Free();
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
        private static extern IntPtr OpenProcessWin32(int access, bool inheritHandle, uint processId);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            out ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress,
            IntPtr buffer, int size, out IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);
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
            UpdateStatus("DSH Tray — 服务正在启动…");
            Action started = UpdateProgressStarted;
            if (started != null) started();
            ReportUpdateProgress("服务正在启动…", null);

            ProcessStartInfo psi = new ProcessStartInfo();
#if WINDOWS
            psi.FileName = "cmd.exe";
            // --loglevel http makes npm emit fetch/cache-miss lines even when
            // stderr is redirected, so the tray can report downloads.
            // `--profile <name>` is the launcher flag; --port/--no-open belong
            // to the web app and are handed over verbatim.
            psi.Arguments = "/c npx --yes --loglevel http " + PackageSpec + " --profile " + dshProfile + " --port " + port + " --no-open";
#else
            psi.FileName = "/bin/sh";
            psi.Arguments = "-c \"npx --yes --loglevel http " + PackageSpec + " --profile " + dshProfile + " --port " + port + " --no-open\"";
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

            RepairProfileManifestEncoding();
            RepairMissingProfileLinks();

            server.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { Log(e.Data); };
            server.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { Log(e.Data); };

            server.Start();
            // npx also receives npm_config_yes=true. Closing stdin guarantees that
            // an incompatible npx version cannot leave the hidden process waiting.
            server.StandardInput.Close();
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
        }

        /// <summary>
        /// Node's JSON.parse rejects a UTF-8 BOM, while profile package.json
        /// files may be written with one by Windows editors or sync tools.
        /// Remove it before dsh reads the profile manifest so startup is
        /// self-healing and does not require a manual profile reset.
        /// </summary>
        private void RepairProfileManifestEncoding()
        {
            try
            {
                string manifest = Path.Combine(ProfilesDirectory, dshProfile, "package.json");
                if (!File.Exists(manifest)) return;

                byte[] bytes = File.ReadAllBytes(manifest);
                if (bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF) return;

                byte[] withoutBom = new byte[bytes.Length - 3];
                Buffer.BlockCopy(bytes, 3, withoutBom, 0, withoutBom.Length);
                File.WriteAllBytes(manifest, withoutBom);
                Log("[profile] removed UTF-8 BOM from package.json");
            }
            catch (Exception ex)
            {
                Log("[profile] unable to repair package.json encoding: " + ex.Message);
            }
        }

        /// <summary>Restore a published dependency when a stale local link prevents profile boot.</summary>
        private void RepairMissingProfileLinks()
        {
            string profileDir = Path.Combine(ProfilesDirectory, dshProfile);
            string manifest = Path.Combine(profileDir, "package.json");
            if (!File.Exists(manifest)) return;

            try
            {
                JsonNode root = JsonNode.Parse(File.ReadAllText(manifest));
                JsonObject dependencies = root?["dependencies"] as JsonObject;
                if (dependencies == null) return;

                bool changed = false;
                foreach (KeyValuePair<string, JsonNode> entry in dependencies.ToList())
                {
                    string spec = entry.Value?.GetValue<string>();
                    if (string.IsNullOrEmpty(spec) || !spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase)) continue;

                    string target = spec.Substring("link:".Length).Trim();
                    if (Directory.Exists(target) || File.Exists(Path.Combine(target, "package.json"))) continue;

                    if (string.Equals(entry.Key, "dsh-review-checkout", StringComparison.Ordinal))
                    {
                        dependencies[entry.Key] = "0.6.0";
                        RemoveStaleProfileLink(Path.Combine(profileDir, "node_modules", entry.Key));
                        changed = true;
                        Log("[profile] repaired missing local link for dsh-review-checkout; using registry version 0.6.0");
                    }
                }

                if (!changed) return;
                File.WriteAllText(manifest, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
                InstallProfileDependencies(profileDir);
            }
            catch (Exception ex)
            {
                Log("[profile] unable to repair missing dependency links: " + ex.Message);
            }
        }

        private void RemoveStaleProfileLink(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(path, false);
            }
            catch
            {
            }
        }

        private void InstallProfileDependencies(string profileDir)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c pnpm install --reporter=silent")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = profileDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process install = Process.Start(psi))
                {
                    if (install == null) return;
                    install.StandardOutput.ReadToEnd();
                    string error = install.StandardError.ReadToEnd();
                    if (!install.WaitForExit(120000) || install.ExitCode != 0)
                        Log("[profile] dependency reinstall failed: " + error.Trim());
                }
            }
            catch (Exception ex)
            {
                Log("[profile] unable to reinstall dependencies: " + ex.Message);
            }
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

            UpdateStatus("DSH Tray — 正在检查 dsh 依赖…");
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
                UpdateStatus("DSH Tray — 正在下载 dsh：已完成 " + count + " 个软件包");
                ReportUpdateProgress("正在下载软件包（已完成 " + count + " 个）…", null);
            }
        }

        private void NotifyUser(string text)
        {
            Action<string, string> cb = Notify;
            if (cb != null) cb("DSH Tray", text);
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
                            ReportUpdateProgress("服务已就绪。", null);
                            Action completed = UpdateProgressCompleted;
                            if (completed != null) completed();
                        }
                        if (autoOpen) OpenBrowser();
                        UpdateStatus("DSH Tray");
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
                            UpdateStatus("DSH Tray — 已下载 " + currentCount + " 个软件包，正在安装并启动…");
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
            UpdateStatus("DSH Tray");
            Action<string, string> cb = Notify;
            if (cb != null) cb("DSH Tray", "服务已退出，详情见日志。");
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
