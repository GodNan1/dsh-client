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

    internal class Config
    {
        public string checkoutPath = "H:\\DeepseekHarness\\deepseek-harness";
        public string nodePath = "";
        public int port = 3080;
        public bool autoOpenBrowser = true;
        public bool minimizeToTray = true;

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
                        if (!string.IsNullOrEmpty(s)) c.checkoutPath = s;
                        int p = GetInt(d, "port", 0);
                        if (p > 0 && p < 65536) c.port = p;
                        s = GetStr(d, "nodePath", null);
                        if (!string.IsNullOrEmpty(s) && File.Exists(s)) c.nodePath = s;
                        c.autoOpenBrowser = GetBool(d, "autoOpenBrowser", c.autoOpenBrowser);
                        c.minimizeToTray = GetBool(d, "minimizeToTray", c.minimizeToTray);
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(c.nodePath))
            {
                string n = FindNode();
                if (n != null) c.nodePath = n;
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

            if (!Directory.Exists(Config.checkoutPath))
            {
                MessageBox.Show("找不到 DSH 仓库目录：\n" + Config.checkoutPath + "\n\n请在「设置」中修改。",
                    "DSH 客户端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(Config.nodePath))
            {
                MessageBox.Show("找不到 node.exe：\n" + Config.nodePath + "\n\n请在「设置」中修改。",
                    "DSH 客户端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Directory.CreateDirectory(Config.Dir);
                Directory.CreateDirectory(Config.LogDir);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Config.nodePath;
                psi.Arguments = "--import tsx/esm apps/cli/src/bin.ts web";
                psi.WorkingDirectory = Config.checkoutPath;
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
                WriteLog("[DSH] 正在启动: " + Config.nodePath + " --import tsx/esm apps/cli/src/bin.ts web (port " +
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
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnOpen;
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
            this.ClientSize = new Size(392, 168);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Microsoft YaHei UI", 9F);

            _lblStatus = new Label();
            _lblStatus.Location = new Point(14, 16);
            _lblStatus.Size = new Size(364, 26);
            _lblStatus.Text = "●";
            Controls.Add(_lblStatus);

            _btnStart = new Button();
            _btnStart.Text = "启动服务";
            _btnStart.Location = new Point(14, 58);
            _btnStart.Size = new Size(88, 32);
            _btnStart.Click += delegate { _app.Start(); };
            Controls.Add(_btnStart);

            _btnStop = new Button();
            _btnStop.Text = "停止服务";
            _btnStop.Location = new Point(108, 58);
            _btnStop.Size = new Size(88, 32);
            _btnStop.Click += delegate { _app.Stop(); };
            Controls.Add(_btnStop);

            _btnOpen = new Button();
            _btnOpen.Text = "打开网页";
            _btnOpen.Location = new Point(202, 58);
            _btnOpen.Size = new Size(88, 32);
            _btnOpen.Click += delegate
            {
                if (NetUtil.PortOpen(_cfg.port)) NetUtil.OpenBrowser(_cfg.Url());
                else MessageBox.Show("服务未运行，请先点击「启动服务」。", "DSH 客户端");
            };
            Controls.Add(_btnOpen);

            Button bLog = new Button();
            bLog.Text = "查看日志";
            bLog.Location = new Point(14, 104);
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
            bSet.Location = new Point(108, 104);
            bSet.Size = new Size(88, 32);
            bSet.Click += delegate
            {
                if (_settingsForm == null || _settingsForm.IsDisposed) _settingsForm = new SettingsForm(_cfg);
                _settingsForm.ShowDialog(this);
            };
            Controls.Add(bSet);

            Button bQuit = new Button();
            bQuit.Text = "退出";
            bQuit.Location = new Point(296, 104);
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
            if (up && _app.JustStarted && _cfg.autoOpenBrowser && !_openedAfterStart)
            {
                _openedAfterStart = true;
                _app.JustStarted = false;
                NetUtil.OpenBrowser(_cfg.Url());
                _tray.ShowBalloonTip(3000, "DSH 客户端", "服务已就绪：" + _cfg.Url(), ToolTipIcon.Info);
            }
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
        private readonly CheckBox _cOpen;
        private readonly CheckBox _cTray;

        public SettingsForm(Config cfg)
        {
            _cfg = cfg;
            this.Text = "设置";
            this.ClientSize = new Size(430, 258);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = new Font("Microsoft YaHei UI", 9F);

            Label l1 = new Label(); l1.Text = "DSH 仓库目录："; l1.Location = new Point(16, 14); l1.AutoSize = true;
            _tCheckout = new TextBox(); _tCheckout.Text = cfg.checkoutPath; _tCheckout.Location = new Point(16, 34); _tCheckout.Size = new Size(398, 23);
            Label l2 = new Label(); l2.Text = "node.exe 路径："; l2.Location = new Point(16, 66); l2.AutoSize = true;
            _tNode = new TextBox(); _tNode.Text = cfg.nodePath; _tNode.Location = new Point(16, 86); _tNode.Size = new Size(398, 23);
            Label l3 = new Label(); l3.Text = "端口："; l3.Location = new Point(16, 118); l3.AutoSize = true;
            _tPort = new TextBox(); _tPort.Text = cfg.port.ToString(); _tPort.Location = new Point(16, 138); _tPort.Size = new Size(70, 23);
            _cOpen = new CheckBox(); _cOpen.Text = "服务就绪后自动打开浏览器"; _cOpen.Checked = cfg.autoOpenBrowser; _cOpen.Location = new Point(120, 140); _cOpen.AutoSize = true;
            _cTray = new CheckBox(); _cTray.Text = "最小化时隐藏到系统托盘"; _cTray.Checked = cfg.minimizeToTray; _cTray.Location = new Point(16, 172); _cTray.AutoSize = true;

            Button bOk = new Button(); bOk.Text = "保存"; bOk.DialogResult = DialogResult.OK; bOk.Location = new Point(234, 214); bOk.Size = new Size(90, 30);
            bOk.Click += delegate { SaveAndClose(); };
            Button bCancel = new Button(); bCancel.Text = "取消"; bCancel.DialogResult = DialogResult.Cancel; bCancel.Location = new Point(330, 214); bCancel.Size = new Size(90, 30);

            Controls.Add(l1); Controls.Add(_tCheckout);
            Controls.Add(l2); Controls.Add(_tNode);
            Controls.Add(l3); Controls.Add(_tPort);
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
                lines.Add("checkoutPath=" + cfg.checkoutPath + " exists=" + Directory.Exists(cfg.checkoutPath));
                lines.Add("nodePath=" + cfg.nodePath + " exists=" + File.Exists(cfg.nodePath));
                lines.Add("port=" + cfg.port.ToString() + " open=" + NetUtil.PortOpen(cfg.port));
                lines.Add("url=" + cfg.Url());
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
