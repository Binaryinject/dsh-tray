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

        /// <summary>Invoked (on the platform UI thread) when a "stop" command is received.</summary>
        public Action OnShutdownRequest;

        /// <summary>Invoked to surface a notification (title, text) on server exit.</summary>
        public Action<string, string> Notify;

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
            if (autoOpen) StartPortWatcher();
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

        private void StartServer()
        {
            ProcessStartInfo psi = new ProcessStartInfo();
#if WINDOWS
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c npx --yes @deepseek-ai/dsh web --port " + port;
#else
            psi.FileName = "/bin/sh";
            psi.Arguments = "-c \"npx --yes @deepseek-ai/dsh web --port " + port + "\"";
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

        private void StartCommandListener()
        {
            Thread t = new Thread(delegate()
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
            Action<string, string> cb = Notify;
            if (cb != null) cb("DeepSeek Harness", "服务已退出，详情见日志。");
        }

        private void StopServer()
        {
            if (server == null) return;
            try
            {
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
