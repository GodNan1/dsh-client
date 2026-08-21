// DSHClient.cs — DSH Web 一键客户端
// 编译：C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /codepage:65001
//       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
//       /r:System.Web.Extensions.dll /win32icon:deepseek.ico /out:DSHClient.exe DSHClient.cs
// 命令行参数：
//   （无）      启动主窗口，服务未运行则自动启动并（可选）自动打开浏览器
//   --tray     直接隐藏到系统托盘（不自动启动服务）
//   --stop     停止服务后退出（供「DSH 停止服务」快捷方式使用）
//   --selftest 自检：加载配置/检查 node/检测端口，结果写入 selftest.txt 后退出
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace DSHClient
{
    internal static class Program
    {
        internal static EventWaitHandle ActivateEvent;

        [STAThread]
        private static void Main(string[] args)
        {
            bool trayMode = false;
            bool stopMode = false;
            foreach (string a in args)
            {
                if (a == "--tray") trayMode = true;
                else if (a == "--stop") stopMode = true;
                else if (a == "--selftest") { Selftest.Run(); return; }
            }

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            Config cfg = Config.Load(appDir);

            if (stopMode)
            {
                App app = new App(cfg);
                if (app.Stop())
                {
                    MessageBox.Show("DSH Web 已停止。", "DSH 客户端");
                }
                else if (!NetUtil.PortOpen(cfg.port))
                {
                    MessageBox.Show("DSH Web 当前没有运行。", "DSH 客户端");
                }
                else
                {
                    MessageBox.Show("未能停止 DSH Web，请检查进程或日志。", "DSH 客户端",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            bool createdNew;
            Mutex m = new Mutex(true, "DSHClient-SingleInstance", out createdNew);
            ActivateEvent = CreateActivateEvent();
            if (!createdNew)
            {
                if (trayMode) return;
                if (!SignalActivate())
                {
                    MessageBox.Show("DSH 客户端已经在运行，请从系统托盘打开主窗口。", "DSH 客户端");
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm f = new MainForm(cfg, trayMode);
            if (trayMode)
            {
                Application.Run(); // 不显示主窗口，仅托盘常驻
            }
            else
            {
                Application.Run(f);
            }
            try { m.ReleaseMutex(); } catch { }
        }

        private static EventWaitHandle CreateActivateEvent()
        {
            try
            {
                bool created;
                return new EventWaitHandle(false, EventResetMode.AutoReset,
                    "DSHClient-Activate", out created);
            }
            catch { return null; }
        }

        // 通知已运行的实例把主窗口调到前台；返回是否成功发出通知
        private static bool SignalActivate()
        {
            try
            {
                EventWaitHandle h = EventWaitHandle.OpenExisting("DSHClient-Activate");
                h.Set();
                h.Dispose();
                return true;
            }
            catch { return false; }
        }
    }

    // 高峰时段（每段 开始-结束 小时，0-23；end<=start 表示跨天）
    internal class PeakWindow
    {
        public int start;
        public int end;

        public PeakWindow() { }
        public PeakWindow(int s, int e) { start = s; end = e; }

        public bool Contains(int hour)
        {
            if (start == end) return false;
            if (start < end) return hour >= start && hour < end;
            return hour >= start || hour < end; // 跨天（如 22-6）
        }

        // 该窗口的下一个边界时刻（切换点）
        public DateTime NextBoundary(DateTime now)
        {
            if (Contains(now.Hour))
            {
                DateTime n = now.Date.AddHours(end);
                if (n <= now) n = n.AddDays(1);
                return n;
            }
            DateTime m = now.Date.AddHours(start);
            if (m <= now) m = m.AddDays(1);
            return m;
        }
    }

    internal class Config
    {
        // 路径支持 %环境变量% 和 ~（用户主目录）；留空则自动检测
        public string checkoutPath = "";
        public string nodePath = "";
        public int port = 3080;
        public bool autoOpenBrowser = true;
        public bool minimizeToTray = true;
        // 高峰期时段（北京时间），其余时间为空闲期
        public List<PeakWindow> peakWindows = new List<PeakWindow>();
        // DeepSeek API Key（留空则读取 DEEPSEEK_API_KEY 环境变量或 DSH 的 .credentials.yaml）
        public string apiKey = "";

        public Config()
        {
            // 默认高峰：北京时间 9:00-12:00、14:00-18:00
            peakWindows.Add(new PeakWindow(9, 12));
            peakWindows.Add(new PeakWindow(14, 18));
        }

        public string Dir = "";
        public string ConfigFile { get { return Path.Combine(Dir, "config.json"); } }
        public string LogDir { get { return Path.Combine(Dir, "logs"); } }
        public string LogFile { get { return Path.Combine(LogDir, "dsh-web.log"); } }
        public string ErrLogFile { get { return Path.Combine(LogDir, "dsh-web.err.log"); } }
        public string Url() { return "http://127.0.0.1:" + port.ToString(); }

        public static Config Load(string dir)
        {
            Config c = new Config();
            c.Dir = dir;
            string f = Path.Combine(dir, "config.json");
            if (File.Exists(f))
            {
                try
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(f, Encoding.UTF8));
                    if (d != null)
                    {
                        string s = GetStr(d, "checkoutPath", null);
                        if (s != null) c.checkoutPath = s;
                        int p = GetInt(d, "port", 0);
                        if (p > 0 && p < 65536) c.port = p;
                        s = GetStr(d, "nodePath", null);
                        if (s != null) c.nodePath = s;
                        c.autoOpenBrowser = GetBool(d, "autoOpenBrowser", c.autoOpenBrowser);
                        c.minimizeToTray = GetBool(d, "minimizeToTray", c.minimizeToTray);
                        // 高峰时段列表（新格式）
                        object wobj = null;
                        if (d.TryGetValue("peakWindows", out wobj) && wobj != null)
                        {
                            object[] arr = wobj as object[];
                            if (arr != null && arr.Length > 0)
                            {
                                List<PeakWindow> list = new List<PeakWindow>();
                                foreach (object o in arr)
                                {
                                    Dictionary<string, object> wd = o as Dictionary<string, object>;
                                    if (wd != null)
                                    {
                                        int ws = GetInt(wd, "start", -1);
                                        int we = GetInt(wd, "end", -1);
                                        if (ws >= 0 && ws <= 23 && we >= 0 && we <= 23)
                                            list.Add(new PeakWindow(ws, we));
                                    }
                                }
                                if (list.Count > 0) c.peakWindows = list;
                            }
                        }
                        else
                        {
                            // 兼容旧格式 peakStartHour/peakEndHour
                            int ls = GetInt(d, "peakStartHour", -1);
                            int le = GetInt(d, "peakEndHour", -1);
                            if (ls >= 0 && ls <= 23 && le >= 0 && le <= 23 && ls != le)
                            {
                                c.peakWindows = new List<PeakWindow>();
                                c.peakWindows.Add(new PeakWindow(ls, le));
                            }
                        }
                        s = GetStr(d, "apiKey", null);
                        if (!string.IsNullOrEmpty(s)) c.apiKey = s;
                    }
                }
                catch { }
            }
            return c;
        }

        public void Save()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["checkoutPath"] = checkoutPath;
            d["nodePath"] = nodePath;
            d["port"] = port;
            d["autoOpenBrowser"] = autoOpenBrowser;
            d["minimizeToTray"] = minimizeToTray;
            List<Dictionary<string, object>> wins = new List<Dictionary<string, object>>();
            foreach (PeakWindow w in peakWindows)
            {
                Dictionary<string, object> wd = new Dictionary<string, object>();
                wd["start"] = w.start;
                wd["end"] = w.end;
                wins.Add(wd);
            }
            d["peakWindows"] = wins;
            d["apiKey"] = apiKey;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(ConfigFile, ser.Serialize(d), new UTF8Encoding(false));
            }
            catch { }
        }

        private static string GetStr(Dictionary<string, object> d, string k, string def)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null) return v.ToString();
            return def;
        }
        private static int GetInt(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (d.TryGetValue(k, out v)) { try { return Convert.ToInt32(v); } catch { } }
            return def;
        }
        private static bool GetBool(Dictionary<string, object> d, string k, bool def)
        {
            object v;
            if (d.TryGetValue(k, out v)) { try { return Convert.ToBoolean(v); } catch { } }
            return def;
        }

        public static string FindNode()
        {
            string[] cands = {
                "D:\\Program Files\\nodejs\\node.exe",
                "C:\\Program Files\\nodejs\\node.exe",
                "C:\\Program Files (x86)\\nodejs\\node.exe"
            };
            foreach (string c in cands) { try { if (File.Exists(c)) return c; } catch { } }
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string dir in path.Split(';'))
                {
                    string d = dir.Trim();
                    if (d.Length == 0) continue;
                    try { string p = Path.Combine(d, "node.exe"); if (File.Exists(p)) return p; } catch { }
                }
            }
            return null;
        }

        // 把 %环境变量% 和 ~（用户主目录）展开为实际路径
        public static string ExpandPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            p = p.Trim();
            if (p == "~")
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (p.StartsWith("~\\") || p.StartsWith("~/"))
                p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    p.Substring(2).TrimStart('\\', '/'));
            int i = p.IndexOf('%');
            int guard = 0;
            while (i >= 0 && guard++ < 16)
            {
                int j = p.IndexOf('%', i + 1);
                if (j < 0) break;
                string var = p.Substring(i + 1, j - i - 1);
                string val = Environment.GetEnvironmentVariable(var);
                if (val == null) break;
                p = p.Substring(0, i) + val + p.Substring(j + 1);
                i = p.IndexOf('%');
            }
            return p;
        }

        // 解析后的实际路径（支持环境变量 / ~ / 留空自动检测）
        public string ResolvedCheckoutPath
        {
            get
            {
                string p = ExpandPath(checkoutPath);
                if (string.IsNullOrEmpty(p)) p = FindCheckout();
                return p;
            }
        }

        public string ResolvedNodePath
        {
            get
            {
                string p = ExpandPath(nodePath);
                if (string.IsNullOrEmpty(p)) p = FindNode();
                return p;
            }
        }

        // 自动检测 DSH 仓库目录（环境变量 DSH_CHECKOUT_PATH 优先，再试常见位置）
        public static string FindCheckout()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("DSH_CHECKOUT_PATH");
                if (!string.IsNullOrEmpty(env) &&
                    File.Exists(Path.Combine(env, "apps\\cli\\src\\bin.ts"))) return env;
                string[] cands = {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "deepseek-harness"),
                    @"C:\deepseek-harness",
                    @"D:\deepseek-harness",
                    @"H:\deepseek-harness"
                };
                foreach (string c in cands)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(c, "apps\\cli\\src\\bin.ts"))) return c;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // ---- 高峰期 / 空闲期（北京时间 UTC+8）----
        public DateTime NowBJ() { return DateTime.UtcNow.AddHours(8); }

        public bool IsPeakAt(int hour)
        {
            foreach (PeakWindow w in peakWindows)
                if (w.Contains(hour)) return true;
            return false;
        }

        public bool IsPeakNow() { return IsPeakAt(NowBJ().Hour); }

        public string CurrentPeriodName() { return IsPeakNow() ? "高峰期" : "空闲期"; }
        public string NextPeriodName() { return IsPeakNow() ? "空闲期" : "高峰期"; }

        // 当前所在高峰窗口（如 "9-12"），空闲期返回 ""
        public string CurrentWindowLabel()
        {
            int hour = NowBJ().Hour;
            foreach (PeakWindow w in peakWindows)
                if (w.Contains(hour)) return w.start + "-" + w.end;
            return "";
        }

        // 最近的下一次时段切换时间
        public DateTime NextTransition()
        {
            DateTime now = NowBJ();
            DateTime best = DateTime.MaxValue;
            foreach (PeakWindow w in peakWindows)
            {
                DateTime b = w.NextBoundary(now);
                if (b < best) best = b;
            }
            return best;
        }

        public string PeakSummary()
        {
            List<string> parts = new List<string>();
            foreach (PeakWindow w in peakWindows) parts.Add(w.start + "-" + w.end);
            return string.Join("、", parts.ToArray());
        }
    }

    internal static class NetUtil
    {
        public static bool PortOpen(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult r = c.BeginConnect("127.0.0.1", port, null, null);
                    if (r.AsyncWaitHandle.WaitOne(800, false))
                    {
                        if (c.Connected) { c.EndConnect(r); return true; }
                    }
                }
            }
            catch { }
            return false;
        }

        public static void OpenBrowser(string url)
        {
            try { Process.Start(url); } catch { }
        }

        public static void KillByPort(int port)
        {
            try
            {
                Process p = new Process();
                p.StartInfo.FileName = "netstat.exe";
                p.StartInfo.Arguments = "-ano";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                foreach (string line in output.Split('\n'))
                {
                    if (line.IndexOf(":" + port.ToString(), StringComparison.Ordinal) >= 0 &&
                        line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 5)
                        {
                            int pid;
                            if (int.TryParse(parts[parts.Length - 1], out pid) && pid != 0)
                            {
                                try { Process.Start("taskkill.exe", "/PID " + pid.ToString() + " /T /F"); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }

    internal static class BalanceChecker
    {
        // 查找 DeepSeek API Key：设置里填的 > 环境变量 > DSH 的 .credentials.yaml
        public static string GetApiKey(Config cfg)
        {
            try
            {
                if (!string.IsNullOrEmpty(cfg.apiKey)) return cfg.apiKey.Trim();
                string k = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
                if (!string.IsNullOrEmpty(k)) return k.Trim();
                string home = Environment.GetEnvironmentVariable("DSH_HOME");
                if (string.IsNullOrEmpty(home))
                    home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                string yaml = Path.Combine(home, ".credentials.yaml");
                if (File.Exists(yaml))
                {
                    foreach (string line in File.ReadAllLines(yaml))
                    {
                        int idx = line.IndexOf(':');
                        if (idx < 0) continue;
                        string key = line.Substring(0, idx).Trim();
                        string val = line.Substring(idx + 1).Trim().Trim('"', '\'');
                        if (key.Equals("deepseek_api_key", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("api_key", StringComparison.OrdinalIgnoreCase))
                            return val;
                    }
                }
            }
            catch { }
            return null;
        }

        // 查询余额，返回如 "¥110.00" 或错误说明
        public static string Query(string apiKey)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.deepseek.com/user/balance");
                req.Method = "GET";
                req.Headers["Authorization"] = "Bearer " + apiKey;
                req.Accept = "application/json";
                req.Timeout = 10000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string json = sr.ReadToEnd();
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                        if (d != null && d.ContainsKey("balance_infos"))
                        {
                            object[] arr = d["balance_infos"] as object[];
                            if (arr != null && arr.Length > 0)
                            {
                                Dictionary<string, object> info = arr[0] as Dictionary<string, object>;
                                if (info != null)
                                {
                                    string currency = info.ContainsKey("currency") ? info["currency"].ToString() : "CNY";
                                    string total = info.ContainsKey("total_balance") ? info["total_balance"].ToString() : "0";
                                    string sym = currency == "CNY" ? "¥" : (currency == "USD" ? "$" : currency + " ");
                                    return sym + total;
                                }
                            }
                        }
                        return "未知响应";
                    }
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                if (r != null && r.StatusCode == HttpStatusCode.Unauthorized) return "API Key 无效";
                return "请求失败";
            }
            catch { return "请求失败"; }
        }
    }

    internal class App
    {
        public Config Config;
        private Process _server;
        private readonly object _logLock = new object();

        public App(Config cfg) { Config = cfg; }

        public bool ServerAlive { get { return _server != null && !_server.HasExited; } }
        public bool JustStarted { get; set; }

        public void Start()
        {
            if (NetUtil.PortOpen(Config.port)) { NetUtil.OpenBrowser(Config.Url()); return; }
            if (ServerAlive) return;

            string checkout = Config.ResolvedCheckoutPath;
            string node = Config.ResolvedNodePath;

            if (string.IsNullOrEmpty(checkout) || !Directory.Exists(checkout))
            {
                MessageBox.Show("找不到 DSH 仓库目录：\n" + (string.IsNullOrEmpty(checkout) ? "(未设置)" : checkout) +
                    "\n\n请在「设置」中填写，或点击「自动检测」。",
                    "DSH 客户端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(node) || !File.Exists(node))
            {
                MessageBox.Show("找不到 node.exe：\n" + (string.IsNullOrEmpty(node) ? "(未设置)" : node) +
                    "\n\n请在「设置」中填写，或点击「自动检测」。",
                    "DSH 客户端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Directory.CreateDirectory(Config.Dir);
                Directory.CreateDirectory(Config.LogDir);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = node;
                psi.Arguments = "--import tsx/esm apps/cli/src/bin.ts web";
                psi.WorkingDirectory = checkout;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                _server = new Process();
                _server.StartInfo = psi;
                _server.OutputDataReceived += OnOut;
                _server.ErrorDataReceived += OnErr;
                if (!_server.Start())
                {
                    _server = null;
                    MessageBox.Show("启动 node 失败，请检查 nodePath 设置。", "DSH 客户端");
                    return;
                }
                _server.BeginOutputReadLine();
                _server.BeginErrorReadLine();
                JustStarted = true;
                WriteLog("[DSH] 正在启动: " + node + " --import tsx/esm apps/cli/src/bin.ts web (port " +
                    Config.port.ToString() + ")");
            }
            catch (Exception ex)
            {
                _server = null;
                MessageBox.Show("启动失败：" + ex.Message, "DSH 客户端",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnOut(object s, DataReceivedEventArgs e)
        {
            if (e.Data != null) WriteLog(e.Data);
        }
        private void OnErr(object s, DataReceivedEventArgs e)
        {
            if (e.Data != null) WriteLog("[err] " + e.Data);
        }

        private void WriteLog(string line)
        {
            try
            {
                lock (_logLock)
                {
                    Directory.CreateDirectory(Config.LogDir);
                    File.AppendAllText(Config.LogFile,
                        DateTime.Now.ToString("HH:mm:ss ") + line + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        // 返回 true 表示服务已停止（或本来就没在运行）
        public bool Stop()
        {
            bool killedMine = false;
            if (ServerAlive)
            {
                try
                {
                    Process k = Process.Start("taskkill.exe", "/PID " + _server.Id.ToString() + " /T /F");
                    if (k != null) k.WaitForExit(5000);
                    if (!_server.HasExited) _server.Kill();
                    killedMine = true;
                }
                catch { }
                _server = null;
                Thread.Sleep(400);
            }
            if (NetUtil.PortOpen(Config.port))
            {
                if (!killedMine)
                {
                    DialogResult r = MessageBox.Show(
                        "端口 " + Config.port.ToString() + " 仍有服务在监听。\n\n是否强制结束占用该端口的进程？",
                        "DSH 客户端", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r == DialogResult.Yes)
                    {
                        NetUtil.KillByPort(Config.port);
                        Thread.Sleep(600);
                    }
                }
                else
                {
                    Thread.Sleep(400);
                }
            }
            return !NetUtil.PortOpen(Config.port);
        }
    }

    internal class MainForm : Form
    {
        private readonly Config _cfg;
        private readonly App _app;
        private bool _quitting;
        private bool _prevUp;
        private bool _openedAfterStart;
        private System.Windows.Forms.Timer _pollTimer;
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private Icon _iconUp;
        private Icon _iconDown;
        private System.Threading.Thread _activateThread;
        private Label _lblStatus;
        private Label _lblPeriod;
        private Label _lblBalance;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnOpen;
        private Button _btnBalance;
        private DateTime _lastNotifiedTransition = DateTime.MinValue;
        private DateTime _lastBalanceRefresh = DateTime.MinValue;
        private bool _balanceLoading;
        private LogForm _logForm;
        private SettingsForm _settingsForm;

        public MainForm(Config cfg, bool trayMode)
        {
            _cfg = cfg;
            _app = new App(cfg);

            BuildUi();
            BuildTray();

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = 1000;
            _pollTimer.Tick += OnPoll;
            _pollTimer.Start();

            _prevUp = NetUtil.PortOpen(cfg.port);
            UpdateUi(_prevUp);

            if (!_prevUp && !trayMode)
            {
                _app.Start(); // 一键启动：自动拉起服务
            }

            // 监听“再次启动”信号：把主窗口调到前台（服务未运行则顺带启动）
            if (Program.ActivateEvent != null)
            {
                _activateThread = new System.Threading.Thread(ActivateLoop);
                _activateThread.IsBackground = true;
                _activateThread.Start();
            }
        }

        private void ActivateLoop()
        {
            while (true)
            {
                try { Program.ActivateEvent.WaitOne(); }
                catch { break; }
                try
                {
                    this.Invoke((Action)delegate
                    {
                        ShowWindow();
                        if (!NetUtil.PortOpen(_cfg.port)) _app.Start();
                    });
                }
                catch { }
            }
        }

        private void BuildUi()
        {
            this.Text = "DSH 客户端";
            this.ClientSize = new Size(392, 206);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Microsoft YaHei UI", 9F);

            _lblStatus = new Label();
            _lblStatus.Location = new Point(14, 14);
            _lblStatus.Size = new Size(364, 24);
            _lblStatus.Text = "●";
            Controls.Add(_lblStatus);

            _lblPeriod = new Label();
            _lblPeriod.Location = new Point(14, 38);
            _lblPeriod.Size = new Size(364, 22);
            _lblPeriod.Text = "●";
            _lblPeriod.ForeColor = Color.DodgerBlue;
            Controls.Add(_lblPeriod);

            _lblBalance = new Label();
            _lblBalance.Location = new Point(14, 62);
            _lblBalance.Size = new Size(318, 24);
            _lblBalance.Text = "余额：查询中…";
            Controls.Add(_lblBalance);

            _btnBalance = new Button();
            _btnBalance.Text = "刷新";
            _btnBalance.Location = new Point(334, 60);
            _btnBalance.Size = new Size(46, 26);
            _btnBalance.Click += delegate { RefreshBalance(); };
            Controls.Add(_btnBalance);

            _btnStart = new Button();
            _btnStart.Text = "启动服务";
            _btnStart.Location = new Point(14, 96);
            _btnStart.Size = new Size(88, 32);
            _btnStart.Click += delegate { _app.Start(); };
            Controls.Add(_btnStart);

            _btnStop = new Button();
            _btnStop.Text = "停止服务";
            _btnStop.Location = new Point(108, 96);
            _btnStop.Size = new Size(88, 32);
            _btnStop.Click += delegate { _app.Stop(); };
            Controls.Add(_btnStop);

            _btnOpen = new Button();
            _btnOpen.Text = "打开网页";
            _btnOpen.Location = new Point(202, 96);
            _btnOpen.Size = new Size(88, 32);
            _btnOpen.Click += delegate
            {
                if (NetUtil.PortOpen(_cfg.port)) NetUtil.OpenBrowser(_cfg.Url());
                else MessageBox.Show("服务未运行，请先点击「启动服务」。", "DSH 客户端");
            };
            Controls.Add(_btnOpen);

            Button bLog = new Button();
            bLog.Text = "查看日志";
            bLog.Location = new Point(14, 140);
            bLog.Size = new Size(88, 32);
            bLog.Click += delegate
            {
                if (_logForm == null || _logForm.IsDisposed) _logForm = new LogForm(_cfg);
                _logForm.Show();
                _logForm.Activate();
            };
            Controls.Add(bLog);

            Button bSet = new Button();
            bSet.Text = "设置";
            bSet.Location = new Point(108, 140);
            bSet.Size = new Size(88, 32);
            bSet.Click += delegate
            {
                if (_settingsForm == null || _settingsForm.IsDisposed) _settingsForm = new SettingsForm(_cfg);
                _settingsForm.ShowDialog(this);
                UpdatePeriod();
            };
            Controls.Add(bSet);

            Button bQuit = new Button();
            bQuit.Text = "退出";
            bQuit.Location = new Point(296, 140);
            bQuit.Size = new Size(82, 32);
            bQuit.Click += delegate { _quitting = true; Application.Exit(); };
            Controls.Add(bQuit);
        }

        private void BuildTray()
        {
            _menu = new ContextMenuStrip();
            ToolStripMenuItem miShow = new ToolStripMenuItem("显示主窗口");
            miShow.Click += delegate { ShowWindow(); };
            ToolStripMenuItem miOpen = new ToolStripMenuItem("打开 DSH Web");
            miOpen.Click += delegate { NetUtil.OpenBrowser(_cfg.Url()); };
            ToolStripMenuItem miStart = new ToolStripMenuItem("启动服务");
            miStart.Click += delegate { _app.Start(); };
            ToolStripMenuItem miStop = new ToolStripMenuItem("停止服务");
            miStop.Click += delegate { _app.Stop(); };
            ToolStripMenuItem miLog = new ToolStripMenuItem("查看日志");
            miLog.Click += delegate
            {
                if (_logForm == null || _logForm.IsDisposed) _logForm = new LogForm(_cfg);
                _logForm.Show();
                _logForm.Activate();
            };
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { _quitting = true; _tray.Visible = false; Application.Exit(); };
            _menu.Items.Add(miShow);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miOpen);
            _menu.Items.Add(miStart);
            _menu.Items.Add(miStop);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miLog);
            _menu.Items.Add(miExit);

            _tray = new NotifyIcon();
            _tray.Icon = GetIcon(_prevUp);
            _tray.Text = "DSH 客户端";
            _tray.ContextMenuStrip = _menu;
            _tray.MouseDoubleClick += OnTrayDoubleClick;
            _tray.Visible = true;
        }

        private void OnTrayDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (NetUtil.PortOpen(_cfg.port)) NetUtil.OpenBrowser(_cfg.Url());
            else _app.Start();
        }

        private void ShowWindow()
        {
            this.Show();
            this.ShowInTaskbar = true;
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        private void OnPoll(object sender, EventArgs e)
        {
            bool up = NetUtil.PortOpen(_cfg.port);
            if (up != _prevUp)
            {
                _prevUp = up;
                _tray.Icon = GetIcon(up);
                if (!up && _app.JustStarted && !_app.ServerAlive)
                {
                    _app.JustStarted = false;
                    _tray.ShowBalloonTip(4000, "DSH 客户端", "服务启动失败，请查看日志。", ToolTipIcon.Error);
                }
            }
            UpdateUi(up);
            UpdatePeriod();
            if ((DateTime.Now - _lastBalanceRefresh).TotalSeconds >= 900 && !_balanceLoading)
            {
                RefreshBalance();
            }
            if (up && _app.JustStarted && _cfg.autoOpenBrowser && !_openedAfterStart)
            {
                _openedAfterStart = true;
                _app.JustStarted = false;
                NetUtil.OpenBrowser(_cfg.Url());
                _tray.ShowBalloonTip(3000, "DSH 客户端", "服务已就绪：" + _cfg.Url(), ToolTipIcon.Info);
            }
        }

        // 高峰期/空闲期显示 + 切换前 5 分钟 Windows 通知（按北京时间）
        private void UpdatePeriod()
        {
            DateTime now = _cfg.NowBJ();
            TimeSpan remain = _cfg.NextTransition() - now;
            string cur = _cfg.CurrentPeriodName();
            string win = _cfg.CurrentWindowLabel();
            string prefix = cur + (win.Length > 0 ? "（" + win + "）" : "");
            _lblPeriod.Text = "● " + prefix + " · 距" + _cfg.NextPeriodName() +
                "（" + _cfg.NextTransition().ToString("HH:mm") + "）还有 " + FormatRemain(remain);
            if (remain.TotalMinutes > 0 && remain.TotalMinutes <= 5 &&
                _lastNotifiedTransition < _cfg.NextTransition().AddMinutes(-6))
            {
                _lastNotifiedTransition = _cfg.NextTransition();
                _tray.ShowBalloonTip(8000, "DSH 时段提醒",
                    "当前" + prefix + "还剩 " + FormatRemain(remain) +
                    "，即将在 " + _cfg.NextTransition().ToString("HH:mm") + " 切换到" + _cfg.NextPeriodName() + "。",
                    ToolTipIcon.Info);
            }
        }

        private static string FormatRemain(TimeSpan t)
        {
            if (t.TotalHours >= 1) return ((int)t.TotalHours).ToString() + "小时" + t.Minutes.ToString() + "分";
            if (t.TotalMinutes >= 1) return t.Minutes.ToString() + "分";
            return "不到1分钟";
        }

        // 查询并显示余额（后台线程，不卡界面）
        private void RefreshBalance()
        {
            if (_balanceLoading) return;
            _balanceLoading = true;
            _lblBalance.Text = "余额：查询中…";
            Thread t = new Thread(delegate()
            {
                string result;
                string key = BalanceChecker.GetApiKey(_cfg);
                if (string.IsNullOrEmpty(key)) result = "未配置 API Key（设置中可填，或自动读取 DSH 凭据）";
                else result = BalanceChecker.Query(key);
                _lastBalanceRefresh = DateTime.Now;
                _balanceLoading = false;
                try { this.Invoke((Action)delegate { _lblBalance.Text = "余额：" + result; }); } catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void UpdateUi(bool up)
        {
            if (up)
            {
                _lblStatus.Text = "●  运行中（" + _cfg.Url() + "）";
                _lblStatus.ForeColor = Color.ForestGreen;
                _tray.Text = "DSH 客户端：运行中";
            }
            else
            {
                _lblStatus.Text = "●  已停止";
                _lblStatus.ForeColor = Color.DimGray;
                _tray.Text = "DSH 客户端：已停止";
            }
            _tray.Icon = GetIcon(up);
            _btnStart.Enabled = !up;
            _btnStop.Enabled = up;
        }

        private Icon GetIcon(bool up)
        {
            if (up)
            {
                if (_iconUp == null) _iconUp = MakeIcon(Color.ForestGreen);
                return _iconUp;
            }
            if (_iconDown == null) _iconDown = MakeIcon(Color.DimGray);
            return _iconDown;
        }

        private static Icon MakeIcon(Color c)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 14, 14);
            }
            IntPtr h = bmp.GetHicon();
            bmp.Dispose();
            return Icon.FromHandle(h);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (this.WindowState == FormWindowState.Minimized && _cfg.minimizeToTray)
            {
                this.ShowInTaskbar = false;
                this.Hide();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_quitting && _cfg.minimizeToTray)
            {
                e.Cancel = true;
                this.ShowInTaskbar = false;
                this.Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer.Dispose(); }
        }
    }

    internal class LogForm : Form
    {
        private readonly Config _cfg;
        private readonly TextBox _box;
        private readonly System.Windows.Forms.Timer _timer;

        public LogForm(Config cfg)
        {
            _cfg = cfg;
            this.Text = "DSH 日志";
            this.ClientSize = new Size(760, 440);
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Both;
            _box.WordWrap = false;
            _box.Dock = DockStyle.Fill;
            _box.Font = new Font("Consolas", 9F);
            Controls.Add(_box);
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 2000;
            _timer.Tick += delegate { RefreshLog(); };
            _timer.Start();
            RefreshLog();
        }

        private void RefreshLog()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                string[] files = new string[] { _cfg.ErrLogFile, _cfg.LogFile };
                foreach (string f in files)
                {
                    if (!File.Exists(f)) continue;
                    FileInfo fi = new FileInfo(f);
                    if (fi.Length <= 0) continue;
                    using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        long start = Math.Max(0, fs.Length - 65536);
                        fs.Seek(start, SeekOrigin.Begin);
                        byte[] buf = new byte[fs.Length - start];
                        int got = fs.Read(buf, 0, buf.Length);
                        sb.Append(Encoding.UTF8.GetString(buf, 0, got));
                    }
                }
                _box.Text = sb.ToString();
                _box.SelectionStart = _box.TextLength;
                _box.ScrollToCaret();
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); }
        }
    }

    internal class SettingsForm : Form
    {
        private readonly Config _cfg;
        private readonly TextBox _tCheckout;
        private readonly TextBox _tNode;
        private readonly TextBox _tPort;
        private readonly TextBox _tPeak;
        private readonly TextBox _tApiKey;
        private readonly CheckBox _cOpen;
        private readonly CheckBox _cTray;

        public SettingsForm(Config cfg)
        {
            _cfg = cfg;
            this.Text = "设置";
            this.ClientSize = new Size(430, 366);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = new Font("Microsoft YaHei UI", 9F);

            Label l1 = new Label(); l1.Text = "DSH 仓库目录（留空=自动检测，支持 %变量% 和 ~）："; l1.Location = new Point(16, 12); l1.AutoSize = true;
            _tCheckout = new TextBox(); _tCheckout.Text = cfg.checkoutPath; _tCheckout.Location = new Point(16, 34); _tCheckout.Size = new Size(300, 23);
            Button bDetectCheckout = new Button(); bDetectCheckout.Text = "自动检测"; bDetectCheckout.Location = new Point(322, 33); bDetectCheckout.Size = new Size(92, 25);
            bDetectCheckout.Click += delegate
            {
                string f = Config.FindCheckout();
                if (f != null) _tCheckout.Text = f;
                else MessageBox.Show("未检测到，请手动填写。", "设置");
            };

            Label l2 = new Label(); l2.Text = "node.exe 路径（留空=自动检测）："; l2.Location = new Point(16, 66); l2.AutoSize = true;
            _tNode = new TextBox(); _tNode.Text = cfg.nodePath; _tNode.Location = new Point(16, 88); _tNode.Size = new Size(300, 23);
            Button bDetectNode = new Button(); bDetectNode.Text = "自动检测"; bDetectNode.Location = new Point(322, 87); bDetectNode.Size = new Size(92, 25);
            bDetectNode.Click += delegate
            {
                string f = Config.FindNode();
                if (f != null) _tNode.Text = f;
                else MessageBox.Show("未检测到，请手动填写。", "设置");
            };

            Label l3 = new Label(); l3.Text = "端口："; l3.Location = new Point(16, 120); l3.AutoSize = true;
            _tPort = new TextBox(); _tPort.Text = cfg.port.ToString(); _tPort.Location = new Point(16, 140); _tPort.Size = new Size(70, 23);

            Label l4 = new Label(); l4.Text = "高峰时段（北京时间，每段 开始-结束，逗号分隔，可跨天）："; l4.Location = new Point(16, 172); l4.AutoSize = true;
            _tPeak = new TextBox(); _tPeak.Text = cfg.PeakSummary(); _tPeak.Location = new Point(16, 194); _tPeak.Size = new Size(220, 23);
            Label l4c = new Label(); l4c.Text = "如 9-12, 14-18（其余为空闲）"; l4c.Location = new Point(244, 196); l4c.AutoSize = true;

            Label l5 = new Label(); l5.Text = "DeepSeek API Key（留空=读取环境变量或 DSH 凭据）："; l5.Location = new Point(16, 224); l5.AutoSize = true;
            _tApiKey = new TextBox(); _tApiKey.Text = cfg.apiKey; _tApiKey.Location = new Point(16, 246); _tApiKey.Size = new Size(398, 23);
            _tApiKey.UseSystemPasswordChar = true;

            _cOpen = new CheckBox(); _cOpen.Text = "服务就绪后自动打开浏览器"; _cOpen.Checked = cfg.autoOpenBrowser; _cOpen.Location = new Point(16, 278); _cOpen.AutoSize = true;
            _cTray = new CheckBox(); _cTray.Text = "最小化时隐藏到系统托盘"; _cTray.Checked = cfg.minimizeToTray; _cTray.Location = new Point(16, 300); _cTray.AutoSize = true;

            Button bOk = new Button(); bOk.Text = "保存"; bOk.DialogResult = DialogResult.OK; bOk.Location = new Point(234, 326); bOk.Size = new Size(90, 30);
            bOk.Click += delegate { SaveAndClose(); };
            Button bCancel = new Button(); bCancel.Text = "取消"; bCancel.DialogResult = DialogResult.Cancel; bCancel.Location = new Point(330, 326); bCancel.Size = new Size(90, 30);

            Controls.Add(l1); Controls.Add(_tCheckout); Controls.Add(bDetectCheckout);
            Controls.Add(l2); Controls.Add(_tNode); Controls.Add(bDetectNode);
            Controls.Add(l3); Controls.Add(_tPort);
            Controls.Add(l4); Controls.Add(_tPeak); Controls.Add(l4c);
            Controls.Add(l5); Controls.Add(_tApiKey);
            Controls.Add(_cOpen); Controls.Add(_cTray);
            Controls.Add(bOk); Controls.Add(bCancel);
            AcceptButton = bOk;
            CancelButton = bCancel;
        }

        private void SaveAndClose()
        {
            int p;
            if (int.TryParse(_tPort.Text.Trim(), out p) && p > 0 && p < 65536) _cfg.port = p;
            _cfg.checkoutPath = _tCheckout.Text.Trim();
            _cfg.nodePath = _tNode.Text.Trim();
            _cfg.autoOpenBrowser = _cOpen.Checked;
            _cfg.minimizeToTray = _cTray.Checked;
            // 高峰时段解析：每段 开始-结束，逗号分隔
            List<PeakWindow> wins = new List<PeakWindow>();
            string[] toks = _tPeak.Text.Split(new char[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
            bool ok = toks.Length > 0;
            foreach (string t in toks)
            {
                string[] ab = t.Trim().Split('-');
                int s, e;
                if (ab.Length == 2 &&
                    int.TryParse(ab[0].Trim(), out s) && int.TryParse(ab[1].Trim(), out e) &&
                    s >= 0 && s <= 23 && e >= 0 && e <= 23 && s != e)
                {
                    wins.Add(new PeakWindow(s, e));
                }
                else { ok = false; break; }
            }
            if (!ok)
            {
                MessageBox.Show("高峰时段格式不对，示例：9-12, 14-18（每段为 开始-结束 小时，逗号分隔）",
                    "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }
            _cfg.peakWindows = wins;
            _cfg.apiKey = _tApiKey.Text.Trim();
            _cfg.Save();
        }
    }

    internal static class Selftest
    {
        public static void Run()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            List<string> lines = new List<string>();
            try
            {
                Config cfg = Config.Load(dir);
                lines.Add("selftest start");
                lines.Add("configFile=" + cfg.ConfigFile + " exists=" + File.Exists(cfg.ConfigFile));
                lines.Add("checkoutPath(原始)=" + cfg.checkoutPath);
                lines.Add("resolvedCheckout=" + (cfg.ResolvedCheckoutPath ?? "(未找到)") + " exists=" +
                    (!string.IsNullOrEmpty(cfg.ResolvedCheckoutPath) && Directory.Exists(cfg.ResolvedCheckoutPath)));
                lines.Add("resolvedNode=" + (cfg.ResolvedNodePath ?? "(未找到)") + " exists=" +
                    (!string.IsNullOrEmpty(cfg.ResolvedNodePath) && File.Exists(cfg.ResolvedNodePath)));
                lines.Add("port=" + cfg.port.ToString() + " open=" + NetUtil.PortOpen(cfg.port));
                lines.Add("url=" + cfg.Url());
                lines.Add("period=" + cfg.CurrentPeriodName() + " windows=" + cfg.PeakSummary() +
                    " nextSwitch=" + cfg.NextTransition().ToString("MM-dd HH:mm") + " (北京时间)");
                string key = BalanceChecker.GetApiKey(cfg);
                lines.Add("apiKey=" + (string.IsNullOrEmpty(key) ? "(无)" : "已配置(长度 " + key.Length.ToString() + ")"));
                lines.Add("RESULT=OK");
            }
            catch (Exception ex)
            {
                lines.Add("RESULT=FAIL");
                lines.Add(ex.ToString());
            }
            try { File.WriteAllLines(Path.Combine(dir, "selftest.txt"), lines.ToArray(), Encoding.UTF8); } catch { }
        }
    }
}
