using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using System.Diagnostics;

namespace RewardsManager
{
    public class MainForm : Form, IMessageFilter
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SendMessage(IntPtr hWnd, int wMsg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_COMPOSITED = 0x02000000;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOACTIVATE = 0x0010;

        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };

        // 日志页
        private ListBox lstLogs;
        private RichTextBox txtLogView;
        private TableLayoutPanel logSplit;
        private TableLayoutPanel logLayout;
        private Label lblTodayPoints;    // 今日获得
        private Label lblCurrentPoints;  // 当前积分
        private ComboBox cmbAccount;     // 选择账号
        private FlowLayoutPanel logLeftFlow;  // 工具栏左侧按钮组(刷新/打开目录/清理日志)，用于账号框右对齐
        private TableLayoutPanel statRowPanel; // 顶部状态条(账号/今日获得/当前积分)，用于对齐刷新

        // 配置页
        private CheckBox chkHeadless, chkDailySet, chkMorePromotions, chkPunchCards, chkDesktopSearch,
            chkMobileSearch, chkDailyCheckIn, chkReadToEarn, chkStreakProtection, chkNtfyEnabled;
        private TextBox txtNtfyTopic, txtNtfyUrl;
        private FlowLayoutPanel envFlow;
        private Panel configScrollPanel;
        private TableLayoutPanel configRoot;
        private readonly List<EnvEntry> envEntries = new List<EnvEntry>();
        private bool _precreating;          // 启动期预渲染配置页时为 true，跳过 SelectedIndexChanged 的刷新逻辑
        private bool _configPrecreated;     // 配置页句柄已创建过，避免重复预创建/闪烁

        // 自动化页
        private StatusGroupBox grpStatus;
        private Label lblTaskDetail, lblTaskTriggers;
        private TextBox txtRunTime;
        private CheckBox chkSilentWindow, chkNotify;
        private GroupBox grpSettingsAutomation;
        private FlowLayoutPanel vflowSettings;

        // 更新页
        private Label lblCurrentVer, lblLatestVer, lblPublished;
        private TextBox lblUpdateState;
        private RichTextBox txtChangelog;
        private List<(int start, int end, string url)> _changelogUrls = new List<(int, int, string)>();
        private bool _cursorOnUrl;
        private Point _changelogDownPos = Point.Empty;
        private bool _changelogDownOnUrl;
        private Button btnUpdate, btnSkip;
        private const string ProjectRepoUrl = "https://github.com/asbdfzcg/Microsoft-Rewards-Script";


        public MainForm(int initialTab = 0, int setGap = -1, bool verify = false, bool verifySwitch = false)
        {
            Text = "Microsoft Rewards Script 管理程序";
            Width = 1000;
            Height = 720;
            MinimumSize = new Size(1000, 720);
            // 不用 CenterScreen：DPI 缩放下 CenterScreen 会先用缩放前尺寸算中心点，缩放后窗口变大造成一次位置跳变（视觉闪一下）。
            // 改为 Manual，在 Shown 的 BeginInvoke 里用已缩放的正确尺寸计算居中位置，一次性设置，无跳变。
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-100000, -100000); // 先移出屏幕，避免首帧在错误位置闪现
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            verifyMode = verify;
            verifySwitchMode = verifySwitch;
            if (verifyMode || verifySwitchMode) { this.ShowInTaskbar = false; }

            // 注意：不要加 ControlStyles.AllPaintingInWmPaint。该样式会抑制 WM_ERASEBKGND，
            // 导致窗体/内容区在重绘时不清空背景——切到「配置编辑」这种重页（数十个控件、绘制跨多帧）
            // 时，未画完的区域会残留上一页的旧像素，表现为“背景透明、文字与文本框不同步出现”的半透明重影。
            // 只保留 OptimizedDoubleBuffer（WS_EX_COMPOSITED 双缓冲）即可消除闪烁，且不影响背景擦除。
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            ResizeRedraw = true;
            tabs.SelectedIndexChanged += (_, _) =>
            {
                if (_precreating) return;                 // 启动期预渲染配置页时不走此逻辑
                // 切页时先让 TabControl 整体失效：恢复 WM_ERASEBKGND 后，内容区会被立即擦成新页的
                // 实心背景，旧页像素当场清除，不会在重页多帧绘制期间透出；随后同步推进整页重绘。
                tabs.Invalidate(true);
                tabs.Update();
            };
            SetDoubleBuffered(tabs);
            SetDoubleBuffered(this);
            // 注：logSplit 在 BuildLogsTab 中创建，双缓冲在 BuildLogsTab 末尾设置。
            // 启用 TabControl 原生双缓冲(TCS_EX_DOUBLEBUFFER)，进一步消除切页时的普通闪烁
            tabs.HandleCreated += (_, _) =>
            {
                const int TCM_SETEXTENDEDSTYLE = 0x2000 + 0x0033; // 0x2033
                const int TCS_EX_DOUBLEBUFFER = 0x0004;
                SendMessage(tabs.Handle, TCM_SETEXTENDEDSTYLE, (IntPtr)TCS_EX_DOUBLEBUFFER, (IntPtr)TCS_EX_DOUBLEBUFFER);
            };
            Resize += (_, _) =>
            {
                if (WindowState == FormWindowState.Normal && tabs.SelectedTab != null)
                {
                    this.PerformLayout();
                    tabs.PerformLayout();
                    tabs.SelectedTab.PerformLayout();
                    tabs.SelectedTab.Refresh();
                    foreach (Control c in tabs.SelectedTab.Controls) c.PerformLayout();
                    FixAutomationGroupHeight();
                }
            };

            tabs.TabPages.Add(BuildLogsTab());
            tabs.TabPages.Add(BuildConfigTab());
            tabs.TabPages.Add(BuildAutomationTab());
            tabs.TabPages.Add(BuildUpdateTab());
            Controls.Add(tabs);

            if (initialTab >= 0 && initialTab < tabs.TabPages.Count)
                tabs.SelectedIndex = initialTab;

            Load += (_, _) =>
            {
                RefreshLogs();
                LoadAccountList();
                RefreshPointsSummary();
                LoadConfig();
                LoadEnv();
                if (configScrollPanel != null)
                {
                    configScrollPanel.HorizontalScroll.Maximum = 0;
                    configScrollPanel.HorizontalScroll.Visible = false;
                    configScrollPanel.AutoScroll = true;
                    configScrollPanel.PerformLayout();
                    if (configRoot != null)
                        configScrollPanel.AutoScrollMinSize = new Size(0, configRoot.Height + configScrollPanel.Padding.Vertical);
                }
                // 测试/自检用：--setgap N 设定按钮带上下对称留白(px)（调好间距后此参数可不传）
                bandGap = setGap >= 0 ? Math.Max(0, Math.Min(40, setGap)) : bandGap;
                if (logToolbar != null)
                {
                    logToolbar.Padding = new Padding(8, bandGap, 8, bandGap);
                    logSplit.Margin = Padding.Empty;          // 间隔由 logLayout 的第 1 行提供
                    ((TabPage)tabs.TabPages[0]).Padding = new Padding(0, bandGap, 0, 0);
                    if (logLayout != null && logLayout.RowStyles.Count > 1)
                        logLayout.RowStyles[1].Height = bandGap;
                }
                RefreshUpdateStatus();
                FixAutomationGroupHeight();
                // 以下两项较重（配置页句柄预创建 + 版本对账写文件），延后到首绘完成后，
                // 避免阻塞窗体首次出现；二者都不影响用户立即看到/操作系统。
                this.BeginInvoke(new Action(() =>
                {
                    PrecreateConfigTab();
                    ReconcileLocalVersion();
                }));
                // 计划任务状态查询要冷启动 powershell.exe（CLR 启动 0.5~1.5s），放到后台线程，
                // 不阻塞首绘；其内部已是 async，这里仅触发，不 await。
                RefreshTaskStatus();
            };
            Shown += (_, _) =>
            {
                // 日志页左右宽度由 TableLayoutPanel 的 Percent 列样式自动布局，首帧即正确，无 DPI 缩放问题。
                // 用 BeginInvoke 把"居中定位"推到首绘之后：此时 this.Width/Height 已是 DPI 缩放后的正确值，
                // 用 Screen.WorkingArea 计算居中位置一次性设置，避免 CenterScreen 的位置跳变闪屏。
                this.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var wa = Screen.PrimaryScreen.WorkingArea;
                        int x = Math.Max(wa.Left, (wa.Width - this.Width) / 2 + wa.Left);
                        int y = Math.Max(wa.Top, (wa.Height - this.Height) / 2 + wa.Top);
                        this.Location = new Point(x, y);
                    }
                    catch { }
                    // 账号下拉框右缘对齐「清理日志」按钮右缘（几何坐标法，幂等；
                    // 真正测量在 toolbar 完成布局后由 LayoutCompleted 触发，这里仅兜底）。
                    try { AlignAccountBox(); } catch { }
                }));
                // 注意：不再在此处做“Opacity=0 强制绘制配置页”，也不做多余 PerformLayout/Refresh（避免额外重绘闪屏）。
                // 配置页句柄创建已移至 Load 的 BeginInvoke(PrecreateConfigTab) 在首绘后后台完成。
                // 自检模式：把真实像素几何写入 geometry.txt 后退出（无需肉眼看截图）
                if (verifyMode)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        try { DumpGeometry(Path.Combine(Path.GetTempPath(), "geometry.txt")); } catch { }
                        Application.Exit();
                    }));
                }
                else if (verifySwitchMode)
                {
                    this.BeginInvoke(new Action(() => { _ = RunVerifySwitch(); }));
                }
            };
            Activated += (_, _) =>
            {
                tabs.SelectedTab?.PerformLayout();
                tabs.SelectedTab?.Refresh();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // 注意：不要设置 WS_EX_COMPOSITED。该样式会让 RichTextBox 等子控件的原生滚动条
                // 在鼠标拖动时不同步（拖动滑块不跟随），且对 Panel.AutoScroll 无影响。
                // 切页残影改用 SelectedIndexChanged 整体重绘 + TabControl 双缓冲解决。
                return cp;
            }
        }

        // ============================================================
        //  Tab 1: 运行日志
        // ============================================================
        private TabPage BuildLogsTab()
        {
            var page = new TabPage("运行日志")
            {
                Padding = new Padding(0, bandGap, 0, 0)
            };

            // 垂直布局容器：状态条(自动高) / 工具栏(自动高) / 固定间隔(bandGap) / 日志区(填充)
            // 用显式间隔行保证“按钮上方间距 == 按钮下方间距”，避免 Dock=Fill 控件边距不生效的坑
            var logLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 0: 状态条
            logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 1: 工具栏
            logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, bandGap)); // 2: 间隔
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));      // 3: 日志区

            // 顶部状态条：选择账号(左) / 今日获得 + 当前积分(右对齐，单行)
            // 数据源为最近一次成功运行的 run_*.log（[ACCOUNT-END] 优先，旧版 [运行结束] 回退）
            var statRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty,
                Padding = new Padding(8, bandGap, 8, 0),
                ColumnCount = 3,
                RowCount = 1
            };
            statRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // 0: 账号(标签+下拉)
            statRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));  // 1: 弹簧
            statRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // 2: 今日获得 + 当前积分

            var accountCell = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty
            };
            accountCell.Controls.Add(new Label
            {
                Text = "账号:",
                AutoSize = true,
                Margin = new Padding(0, 0, 4, 0),
                BackColor = SystemColors.Control,
                TextAlign = ContentAlignment.MiddleLeft
            });
            cmbAccount = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei UI", 9F),
                Width = 120,
                Margin = new Padding(0, 0, 0, 0),
                FlatStyle = FlatStyle.System
            };
            cmbAccount.SelectedIndexChanged += (_, _) => RefreshPointsSummary();
            accountCell.Controls.Add(cmbAccount);
            statRow.Controls.Add(accountCell, 0, 0);

            var pointsCell = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty,
                Anchor = AnchorStyles.Right
            };
            pointsCell.Controls.Add(MkStatLabel("今日获得:"));
            lblTodayPoints = MkStatLabel("—");
            pointsCell.Controls.Add(lblTodayPoints);
            pointsCell.Controls.Add(new Label { Width = 28, Margin = Padding.Empty, BackColor = SystemColors.Control });
            pointsCell.Controls.Add(MkStatLabel("当前积分:"));
            lblCurrentPoints = MkStatLabel("—");
            pointsCell.Controls.Add(lblCurrentPoints);
            statRow.Controls.Add(pointsCell, 2, 0);
            statRowPanel = statRow;

            var toolbar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, bandGap, 8, bandGap),
                Margin = Padding.Empty,
                ColumnCount = 3,
                RowCount = 1
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var leftFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty
            };
            var btnRefresh = MkButton("刷新", (_, _) => RefreshLogs());
            var btnOpenDir = MkButton("打开日志目录", (_, _) =>
            {
                Directory.CreateDirectory(ProjectPaths.LogsDir);
                System.Diagnostics.Process.Start("explorer.exe", ProjectPaths.LogsDir);
            });
            var btnCleanLogs = MkButton("清理日志", (_, _) => CleanLogs());
            leftFlow.Controls.Add(btnRefresh);
            leftFlow.Controls.Add(btnOpenDir);
            leftFlow.Controls.Add(btnCleanLogs);
            logLeftFlow = leftFlow;
            // leftFlow(AutoSize) 尺寸在按钮布局完成后由 SizeChanged 定稿；此时再对齐账号框，
            // 避免 Shown 的 BeginInvoke 测量过早导致宽度停在初始值。AlignAccountBox 幂等。
            logLeftFlow.SizeChanged += (_, _) => { try { AlignAccountBox(); } catch { } };

            var rightFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty
            };
            var btnRun = MkButton("运行", (_, _) => RunManual());
            var btnStop = MkButton("停止", (_, _) => StopManual());
            rightFlow.Controls.Add(btnRun);
            rightFlow.Controls.Add(btnStop);

            toolbar.Controls.Add(leftFlow, 0, 0);
            toolbar.Controls.Add(rightFlow, 2, 0);
            logToolbar = toolbar;
            btnRefreshLogs = btnRefresh;

            // 用 TableLayoutPanel 两列（Percent）替代 SplitContainer，避免 AutoScaleMode=Dpi 下
            // SplitterDistance 被缩放引擎按未定宽度算错导致左侧瞬间变窄/闪烁。
            logSplit = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = SystemColors.Control
            };
            logSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32f));   // 左：日志列表
            logSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 4f));   // 中：分割条
            logSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68f));   // 右：日志内容
            logSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            SetDoubleBuffered(logSplit);
            lstLogs = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                IntegralHeight = false
            };
            txtLogView = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.ForcedBoth,
                DetectUrls = false,
                AutoWordSelection = false,
                ShowSelectionMargin = false,
                BackColor = Color.White
            };
            var splitterBar = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = SystemColors.ControlDark
            };
            lstLogs.SelectedIndexChanged += (_, _) => LoadSelectedLog();
            logSplit.Controls.Add(lstLogs, 0, 0);
            logSplit.Controls.Add(splitterBar, 1, 0);
            logSplit.Controls.Add(txtLogView, 2, 0);

            this.logLayout = logLayout;
            logLayout.Controls.Add(statRow, 0, 0);
            logLayout.Controls.Add(toolbar, 0, 1);
            // 第 2 行为空的固定间隔行(bandGap)，提供“按钮下方间距”
            logLayout.Controls.Add(logSplit, 0, 3);
            page.Controls.Add(logLayout);
            return page;
        }

        private void RefreshLogs()
        {
            lstLogs.Items.Clear();
            if (!Directory.Exists(ProjectPaths.LogsDir)) return;
            var files = new DirectoryInfo(ProjectPaths.LogsDir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime).ToArray();
            foreach (var f in files)
                lstLogs.Items.Add($"{f.Name}  ({f.Length / 1024.0:0.0} KB, {f.LastWriteTime:MM-dd HH:mm})");
            if (lstLogs.Items.Count > 0) lstLogs.SelectedIndex = 0;
            RefreshPointsSummary();
        }

        // 从 .env 解析所有 ACCOUNT_N_EMAIL，取 email 本地部分（@ 之前）作为账号标签填入下拉框。
        // 标签与运行日志 [ACCOUNT-END] 消息内联的账号邮箱保持一致，便于按账号过滤积分。
        private void LoadAccountList()
        {
            if (cmbAccount == null) return;
            var accounts = new List<string>();
            try
            {
                string envPath = ProjectPaths.EnvFile;
                if (File.Exists(envPath))
                {
                    foreach (var raw in File.ReadAllLines(envPath))
                    {
                        var lineStr = raw.Trim();
                        var m = System.Text.RegularExpressions.Regex.Match(lineStr, @"^ACCOUNT_([1-9]\d*)_EMAIL\s*=\s*(.+)$");
                        if (!m.Success) continue;
                        string email = m.Groups[2].Value.Trim().Trim('"', '\'');
                        if (string.IsNullOrEmpty(email)) continue;
                        // 下拉显示完整邮箱；日志里的账号标识是 email 本地部分（@ 之前），
                        // 故匹配时用本地部分，显示用完整 email。
                        if (!accounts.Contains(email, StringComparer.OrdinalIgnoreCase))
                            accounts.Add(email);
                    }
                }
            }
            catch { }
            cmbAccount.Items.Clear();
            if (accounts.Count == 0)
                cmbAccount.Items.Add("(无账号)");
            else
                foreach (var a in accounts) cmbAccount.Items.Add(a);
            cmbAccount.SelectedIndex = 0;
        }

        // 从最近一次成功运行的 run_*.log 解析「今日获得 / 当前积分」。
        // 兼容两种日志格式：
        //  - 旧版(v4.2.x)：行含 "[运行结束]"，形如 "获得积分=(\d+) 当前余额=(\d+)"
        //  - 新版(v4.3.0+)：行含 "[ACCOUNT-END]"（按账号）或 "[RUN-END]"（汇总），
        //    形如 "pointsGained=(\d+) ... currentBalance=(\d+)"，账号邮箱内联在消息里。
        // 仅取与当前下拉框选中账号匹配的行（邮箱本地部分或完整邮箱）。
        private void RefreshPointsSummary()
        {
            if (lblTodayPoints == null || lblCurrentPoints == null) return;
            string selEmail = cmbAccount != null && cmbAccount.SelectedItem != null
                ? cmbAccount.SelectedItem.ToString() : null;
            // 选中的账号本地部分（@ 之前），用于匹配
            string selAccount = null;
            if (selEmail != null)
            {
                int at = selEmail.IndexOf('@');
                selAccount = at > 0 ? selEmail.Substring(0, at) : selEmail;
            }
            int today = -1, cur = -1;
            try
            {
                if (Directory.Exists(ProjectPaths.LogsDir))
                {
                    var runLogs = new DirectoryInfo(ProjectPaths.LogsDir).GetFiles("run_*.log")
                        .OrderByDescending(f => f.Name).ToArray();   // 文件名含时间戳，天然有序
                    foreach (var f in runLogs)
                    {
                        string line = null;
                        var lines = File.ReadAllLines(f.FullName);
                        for (int i = lines.Length - 1; i >= 0; i--)
                        {
                            // 优先新格式（按账号归集，能精确匹配选中账号）
                            if (lines[i].Contains("[ACCOUNT-END]")) { line = lines[i]; break; }
                        }
                        if (line == null)
                        {
                            // 回退：旧格式整轮汇总行
                            for (int i = lines.Length - 1; i >= 0; i--)
                            {
                                if (lines[i].Contains("[运行结束]")) { line = lines[i]; break; }
                            }
                        }
                        if (line == null) continue;

                        // 账号过滤：旧格式取第二个中括号内容；新格式从消息提取 "Completed account: <email>"
                        if (selAccount != null)
                        {
                            bool match = false;
                            if (line.Contains("[ACCOUNT-END]"))
                            {
                                var mEmail = System.Text.RegularExpressions.Regex.Match(line, @"Completed account:\s*([^\s|]+)");
                                if (mEmail.Success)
                                {
                                    string acc = mEmail.Groups[1].Value;
                                    int at = acc.IndexOf('@');
                                    string local = at > 0 ? acc.Substring(0, at) : acc;
                                    match = string.Equals(local, selAccount, StringComparison.OrdinalIgnoreCase);
                                }
                            }
                            else
                            {
                                var mAcc = System.Text.RegularExpressions.Regex.Match(line, @"^\[[^\]]*\] \[([^\]]*)\]");
                                match = mAcc.Success && string.Equals(mAcc.Groups[1].Value, selAccount, StringComparison.OrdinalIgnoreCase);
                            }
                            if (!match) continue;   // 非当前选中账号，跳过
                        }

                        bool parsed = false;
                        var mT = System.Text.RegularExpressions.Regex.Match(line, @"pointsGained=(\d+)");
                        var mB = System.Text.RegularExpressions.Regex.Match(line, @"currentBalance=(\d+)");
                        if (mT.Success && mB.Success)
                        {
                            int t = int.Parse(mT.Groups[1].Value);
                            int b = int.Parse(mB.Groups[1].Value);
                            cur = b;
                            if (t > 0) { today = t; }
                            else if (today < 0) { today = t; }
                            parsed = true;
                        }
                        else
                        {
                            // 旧格式回退
                            var mTo = System.Text.RegularExpressions.Regex.Match(line, @"获得积分=(\d+)");
                            var mBo = System.Text.RegularExpressions.Regex.Match(line, @"当前余额=(\d+)");
                            if (mTo.Success && mBo.Success)
                            {
                                int t = int.Parse(mTo.Groups[1].Value);
                                int b = int.Parse(mBo.Groups[1].Value);
                                cur = b;
                                if (t > 0) { today = t; }
                                else if (today < 0) { today = t; }
                                parsed = true;
                            }
                        }
                        // 命中选中账号且成功解析后，取该日志文件内该账号最新的一条即可停止（文件已按时间倒序）
                        if (parsed && selAccount != null) break;
                    }
                }
            }
            catch { }
            // 配色与计划任务状态「Ready」一致：正常=DarkGreen，0/非法=DarkRed
            SetColoredValue(lblTodayPoints, today, today > 0);
            SetColoredValue(lblCurrentPoints, cur, cur > 0);
        }

        // 配置页首次绘制较重（.env 每行一个 CheckBox+Label+TextBox，加上十几个自定义 CheckBox
        // 的句柄创建与布局）。在窗体已可见后异步创建全部子控件句柄并布局一次，把成本移出启动关键路径；
        // 真正的“首次像素绘制”在 Shown 中以 Opacity=0 不可见方式强制完成（见 Shown 处理）。
        private void PrecreateConfigTab()
        {
            if (_configPrecreated || _precreating) return;
            // 用户已经手动切到配置页（或被切到），说明已经绘制过，无需再后台预创建，避免切回闪烁
            if (tabs.SelectedIndex == 1) { _configPrecreated = true; return; }
            try
            {
                _precreating = true;
                var sw = Stopwatch.StartNew();
                tabs.SelectedIndex = 1;
                tabs.TabPages[1].PerformLayout();
                configScrollPanel?.CreateControl();           // 递归创建全部子控件句柄
                configScrollPanel?.PerformLayout();
                sw.Stop();
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "precreate.txt"), $"precreate_ms={sw.ElapsedMilliseconds}\n"); } catch { }
                // 预创建完成后切回原页（务必切回，否则用户会看到停在配置页）
                if (tabs.SelectedIndex == 1) tabs.SelectedIndex = 0;
            }
            catch { }
            finally { _precreating = false; _configPrecreated = true; }
        }

        // 按窗口宽度的固定比例设置日志页 SplitContainer 的左侧宽度（约 1/3，上下限保护）。
        private void LoadSelectedLog()
        {
            if (lstLogs.SelectedItem == null) return;
            var name = lstLogs.SelectedItem.ToString().Split(' ')[0];
            var path = Path.Combine(ProjectPaths.LogsDir, name);
            if (File.Exists(path))
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    txtLogView.Text = sr.ReadToEnd();
                }
                catch (Exception ex) { txtLogView.Text = "读取失败: " + ex.Message; }
            }
        }

        private void CleanLogs()
        {            if (!Directory.Exists(ProjectPaths.LogsDir)) return;
            var files = Directory.GetFiles(ProjectPaths.LogsDir, "*.log");
            if (files.Length == 0)
            {
                MessageBox.Show("没有可清理的日志文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show($"确定要删除 logs 目录下的 {files.Length} 个日志文件吗？", "确认清理", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                foreach (var f in files) File.Delete(f);
                RefreshLogs();
                txtLogView.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show("清理失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RunManual()
        {
            var bat = Path.Combine(ProjectPaths.AutorunDir, "run-manual.bat");
            if (!File.Exists(bat))
            {
                MessageBox.Show("找不到 run-manual.bat", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true
            });
        }

        private void StopManual()
        {
            try
            {
                int killed = 0;
                // 结束 node.exe 中运行 dist\index.js 的进程
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='node.exe'"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        var cmd = obj["CommandLine"]?.ToString() ?? "";
                        if (cmd.Contains("dist\\index.js") || cmd.Contains("dist/index.js"))
                        {
                            try
                            {
                                var pid = Convert.ToInt32(obj["ProcessId"]);
                                System.Diagnostics.Process.GetProcessById(pid).Kill();
                                killed++;
                            }
                            catch { }
                        }
                    }
                }
                // 结束运行 run-rewards.ps1 的 powershell 进程
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe' OR Name='pwsh.exe'"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        var cmd = obj["CommandLine"]?.ToString() ?? "";
                        if (cmd.Contains("run-rewards.ps1"))
                        {
                            try
                            {
                                var pid = Convert.ToInt32(obj["ProcessId"]);
                                System.Diagnostics.Process.GetProcessById(pid).Kill();
                                killed++;
                            }
                            catch { }
                        }
                    }
                }
                // 清理锁文件
                var lockFile = Path.Combine(ProjectPaths.AutorunDir, ".run-lock");
                try { if (File.Exists(lockFile)) File.Delete(lockFile); } catch { }
                MessageBox.Show(killed > 0 ? $"已停止 {killed} 个相关进程。" : "没有正在运行的手动任务。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("停止失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ============================================================
        //  Tab 2: 配置编辑
        // ============================================================
        private TabPage BuildConfigTab()
        {
            var page = new TabPage("配置编辑")
            {
                Padding = new Padding(0, 3, 0, 0),
                // 显式实心背景：配合窗体恢复 WM_ERASEBKGND，确保切到本页时内容区先被擦成实心灰，
                // 重页多帧绘制期间绝不透出上一页（根治“背景透明、文字与文本框不同步”的半透明重影）。
                BackColor = SystemColors.Control
            };
            configScrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(10),
                AutoScrollMinSize = new Size(0, 1),
                // 显式不透明背景：避免切页首次重绘期间透明区域漏出上一页的旧像素（ghost）
                BackColor = SystemColors.Control
            };
            // 不对此 AutoScroll Panel 开双缓冲：TabControl 切页时它会保留旧帧缓冲，
            // 与 Label 的透明背景叠加产生 ghost/重影。configRoot 的双缓冲保留即可。

            configRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                // 显式不透明背景：透明容器在重页多帧绘制时会漏出上一页旧像素（ghost/重影）
                BackColor = SystemColors.Control
            };
            configRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            configRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            SetDoubleBuffered(configRoot);

            // --- config.json 常用项 ---
            var grpConfig = new GroupBox
            {
                Text = "config.json 常用配置",
                Dock = DockStyle.Top,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10),
                Padding = new Padding(12, 8, 12, 12),
                BackColor = SystemColors.Control
            };

            var checkFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 8),
                BackColor = SystemColors.Control
            };
            chkHeadless = MkCheck("无头模式 (headless)");
            chkStreakProtection = MkCheck("连击保护 (ensureStreakProtection)");
            chkDailySet = MkCheck("每日任务集 (doDailySet)");
            chkMorePromotions = MkCheck("更多推广 (doMorePromotions)");
            chkPunchCards = MkCheck("打卡任务 (doPunchCards)");
            chkDesktopSearch = MkCheck("桌面端搜索 (doDesktopSearch)");
            chkMobileSearch = MkCheck("移动端搜索 (doMobileSearch)");
            chkDailyCheckIn = MkCheck("每日签到 (doDailyCheckIn)");
            chkReadToEarn = MkCheck("阅读赚积分 (doReadToEarn)");
            chkNtfyEnabled = MkCheck("启用 ntfy 推送");
            checkFlow.Controls.Add(chkHeadless);
            checkFlow.Controls.Add(chkStreakProtection);
            checkFlow.Controls.Add(chkDailySet);
            checkFlow.Controls.Add(chkMorePromotions);
            checkFlow.Controls.Add(chkPunchCards);
            checkFlow.Controls.Add(chkDesktopSearch);
            checkFlow.Controls.Add(chkMobileSearch);
            checkFlow.Controls.Add(chkDailyCheckIn);
            checkFlow.Controls.Add(chkReadToEarn);
            checkFlow.Controls.Add(chkNtfyEnabled);

            var ntfyRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 8),
                BackColor = SystemColors.Control
            };
            ntfyRow.Controls.Add(new Label { Text = "ntfy 主题:", AutoSize = true, Margin = new Padding(0, 6, 4, 0), BackColor = SystemColors.Control });
            txtNtfyTopic = new TextBox { Width = 220, Margin = new Padding(0, 3, 20, 0) };
            ntfyRow.Controls.Add(txtNtfyTopic);
            ntfyRow.Controls.Add(new Label { Text = "ntfy 地址:", AutoSize = true, Margin = new Padding(0, 6, 4, 0), BackColor = SystemColors.Control });
            txtNtfyUrl = new TextBox { Width = 300, Margin = new Padding(0, 3, 0, 0) };
            ntfyRow.Controls.Add(txtNtfyUrl);

            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 0),
                BackColor = SystemColors.Control
            };
            btnRow.Controls.Add(MkButton("保存 config.json", (_, _) => SaveConfig()));
            btnRow.Controls.Add(MkButton("编辑原始 JSON", (_, _) => System.Diagnostics.Process.Start("notepad.exe", ProjectPaths.ConfigFile)));

            grpConfig.Controls.Add(btnRow);
            grpConfig.Controls.Add(ntfyRow);
            grpConfig.Controls.Add(checkFlow);

            // --- .env 账号配置（单行输入框，避免长文本被裁切）---
            var grpEnv = new GroupBox
            {
                Text = ".env 账号配置（含密码，注意保密）",
                Dock = DockStyle.Top,
                AutoSize = true,
                Margin = new Padding(0),
                Padding = new Padding(12, 8, 12, 12),
                BackColor = SystemColors.Control
            };

            envFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Margin = Padding.Empty,
                BackColor = SystemColors.Control
            };

            var envBtnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 8, 0, 0),
                BackColor = SystemColors.Control
            };
            envBtnRow.Controls.Add(MkButton("保存 .env", (_, _) => SaveEnv()));
            envBtnRow.Controls.Add(MkButton("从模板创建 .env", (_, _) => CreateEnvFromExample()));

            grpEnv.Controls.Add(envBtnRow);
            grpEnv.Controls.Add(envFlow);

            configRoot.Controls.Add(grpConfig, 0, 0);
            configRoot.Controls.Add(grpEnv, 0, 1);
            configScrollPanel.Controls.Add(configRoot);
            page.Controls.Add(configScrollPanel);
            return page;
        }

        private static CheckBox MkCheck(string text)
        {
            return new CheckBox
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(6, 6, 28, 6),
                // 显式不透明背景：透明 CheckBox 在重页多帧绘制时会漏出上一页旧像素（ghost/重影）
                BackColor = SystemColors.Control
            };
        }

        private static Button MkButton(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(0, 0, 12, 0)
            };
            b.Click += onClick;
            return b;
        }

        private static Label MkStatLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Margin = Padding.Empty,
                BackColor = SystemColors.Control,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        // 数字彩色：正常(DarkGreen) / 0 或非法(DarkRed)，与计划任务状态「Ready」配色一致
        private static void SetColoredValue(Label lbl, int value, bool ok)
        {
            if (value < 0) { lbl.Text = "—"; lbl.ForeColor = Color.DarkRed; }
            else { lbl.Text = value.ToString("N0"); lbl.ForeColor = ok ? Color.DarkGreen : Color.DarkRed; }
        }

        // 更新日志纯文本美化：统一字号（控件 Font 9pt Consolas，不加粗）。
        // 仅做 Markdown 去噪点（# 标题前缀、**加粗** 星号、--- 分隔线）+ 列表标记(?/- )换 •（保留缩进）+
        // 链接([文字](url)) 转为「文字 url」保留裸 URL，由 RichTextBox.DetectUrls（.Text 模式）自动变蓝可点击。
        // 连续空行压缩为最多一个空行，避免间距过疏。
        private static string ChangelogToPlain(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            var outLines = new System.Collections.Generic.List<string>();
            var re = new System.Text.RegularExpressions.Regex(@"\[([^\]]+)\]\(([^)]+)\)");
            bool prevBlank = false;
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                // 跳过 --- 分隔线（纯装饰）
                if (line.Replace(" ", "").Replace("-", "").Length == 0 && line.Contains("-")) continue;
                // 标题：去掉 # 前缀
                if (line.Length > 0 && line[0] == '#')
                {
                    int i = 0; while (i < line.Length && line[i] == '#') i++;
                    var title = line.Substring(i).Trim();
                    if (title.Length == 0) continue;
                    line = title;
                }
                // 列表项：? 或 - 开头 → •（保留前导空格缩进）
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)[?-]\s+(.*)$");
                if (m.Success)
                {
                    line = m.Groups[1].Value + "• " + StripBold(m.Groups[2].Value);
                }
                else
                {
                    line = StripBold(line);
                }
                // 链接：[文字](url) → 文字 url（保留裸 URL 供 DetectUrls 识别）
                line = re.Replace(line, (mm) => mm.Groups[1].Value + " " + mm.Groups[2].Value);
                bool isBlank = line.Trim().Length == 0;
                if (isBlank)
                {
                    if (prevBlank) continue; // 压缩连续空行
                    prevBlank = true;
                }
                else
                {
                    prevBlank = false;
                }
                outLines.Add(line);
            }
            return string.Join("\n", outLines);
        }

        private static string StripBold(string s)
        {
            return System.Text.RegularExpressions.Regex.Replace(s, @"\*\*(.+?)\*\*", "$1");
        }

        // 更新日志链接：自己扫描裸 URL，染蓝+下划线，并记录区间供点击命中。
        // 不依赖 RichTextBox.DetectUrls（在自定义内容/ReadOnly 下不可靠）。
        private static readonly System.Text.RegularExpressions.Regex UrlRe =
            new System.Text.RegularExpressions.Regex(@"https?://[^\s，。、）)]+");

        private void HighlightChangelogUrls()
        {
            _changelogUrls.Clear();
            var text = txtChangelog.Text;
            if (string.IsNullOrEmpty(text)) return;
            foreach (System.Text.RegularExpressions.Match m in UrlRe.Matches(text))
            {
                _changelogUrls.Add((m.Index, m.Index + m.Length, m.Value));
            }
            if (_changelogUrls.Count == 0) return;
            // 临时解除只读以设置颜色，结束后恢复
            bool ro = txtChangelog.ReadOnly;
            txtChangelog.ReadOnly = false;
            try
            {
                foreach (var u in _changelogUrls)
                {
                    txtChangelog.SelectionStart = u.start;
                    txtChangelog.SelectionLength = u.end - u.start;
                    txtChangelog.SelectionColor = Color.Blue;
                    txtChangelog.SelectionFont = new Font(txtChangelog.Font, FontStyle.Underline);
                }
                txtChangelog.SelectionStart = 0;
                txtChangelog.SelectionLength = 0;
            }
            finally
            {
                txtChangelog.ReadOnly = ro;
            }
        }

        private void TxtChangelog_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int idx = txtChangelog.GetCharIndexFromPosition(e.Location);
            var hit = _changelogUrls.FirstOrDefault(u => idx >= u.start && idx < u.end);
            _changelogDownPos = e.Location;
            _changelogDownOnUrl = hit.url != null;
            if (hit.url != null)
            {
                // 阻止默认文本选择，直接打开浏览器
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = hit.url,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        private void TxtChangelog_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // 纯单击（几乎没移动）且不在链接上：让日志框失焦，停止插入符（|）持续闪烁。
            // 拖拽选中（发生移动）则保持焦点，便于 Ctrl+C 复制。
            if (!_changelogDownOnUrl && _changelogDownPos != Point.Empty)
            {
                int dx = Math.Abs(e.Location.X - _changelogDownPos.X);
                int dy = Math.Abs(e.Location.Y - _changelogDownPos.Y);
                if (dx <= 2 && dy <= 2)
                {
                    // 把焦点移到父容器，使 RichTextBox 失去焦点，插入符停止闪烁
                    txtChangelog.Parent?.Focus();
                }
            }
            _changelogDownPos = Point.Empty;
        }

        private void TxtChangelog_MouseMove(object sender, MouseEventArgs e)
        {
            int idx = txtChangelog.GetCharIndexFromPosition(e.Location);
            _cursorOnUrl = _changelogUrls.Any(u => idx >= u.start && idx < u.end);
        }

        // 通过 Application 级消息过滤器拦截 WM_SETCURSOR，彻底压制 RichTextBox 内部强制的 IBeam（| 形）光标。
        // 比子类化 + 重写 WndProc 更安全（不碰控件内部 WndProc，避免在消息路径中重入崩溃）。
        // 仅在 MouseMove 中计算链接命中状态并缓存到 _cursorOnUrl，过滤器只做只读 HWND 比较 + 设光标。
        private const int WM_SETCURSOR = 0x0020;
        bool IMessageFilter.PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_SETCURSOR && txtChangelog != null && txtChangelog.IsHandleCreated && m.HWnd == txtChangelog.Handle)
            {
                Cursor.Current = _cursorOnUrl ? Cursors.Hand : Cursors.Arrow;
                return true; // 已处理，阻止 RichTextBox 默认设 IBeam
            }
            return false;
        }

        // 账号下拉框右缘对齐「清理日志」按钮右缘：几何坐标法（相对 logLayout 坐标系，两边左 padding 抵消）。
        // 必须在 toolbar 布局完成后调用（LayoutCompleted 或 Shown 兜底），否则 logLeftFlow 宽度未定导致不生效。
        private void AlignAccountBox()
        {
            if (cmbAccount == null || logLeftFlow == null) return;
            if (!(cmbAccount.Parent is FlowLayoutPanel ac) || ac.Controls.Count == 0) return;
            if (!logLeftFlow.IsHandleCreated || logLeftFlow.Width <= 0) return; // 布局未完成，下次 SizeChanged 再试
            if (!ac.IsHandleCreated) return;
            if (logLeftFlow.Controls.Count == 0) return;
            // 直接取「清理日志」按钮(最后一项)的真实右缘做基准（比 leftFlow 整体宽度边缘精确）
            var cleanBtn = logLeftFlow.Controls[logLeftFlow.Controls.Count - 1];
            int cleanRightX = cleanBtn.PointToScreen(new Point(cleanBtn.Width, 0)).X;
            int cellLeftX = ac.PointToScreen(Point.Empty).X;
            int labelW = ac.Controls[0].PreferredSize.Width; // 账号标签文本宽
            int target = cleanRightX - cellLeftX - labelW - 4; // 4 = 标签右 margin
            if (target > 80) cmbAccount.Width = target;
        }

        private static void SetDoubleBuffered(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.SetValue(c, true);
            }
            catch { }
        }

        /// <summary>
        /// 动态开关窗体级 WS_EX_COMPOSITED。开启后整窗的绘制走 DWM 离屏缓冲并一次性合成，
        /// 切页时不会出现「旧页残留帧」（重影/ghost）。仅应在切页那一瞬开启，平时关闭，
        /// 以免持续开启影响 RichTextBox 等子控件原生滚动条的拖动同步。
        /// </summary>
        private void EnableComposited(bool enable)
        {
            try
            {
                int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
                if (enable) ex |= WS_EX_COMPOSITED; else ex &= ~WS_EX_COMPOSITED;
                SetWindowLong(this.Handle, GWL_EXSTYLE, ex);
                SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
            }
            catch { }
        }

        private static void RefreshAll(Control c)
        {
            if (c == null) return;
            c.Refresh();
            foreach (Control child in c.Controls) RefreshAll(child);
        }

        // ===== 日志页布局自检（--verify，无 UI 调试框）=====
        // 间距对称由 bandGap 常量在构造期一次性定死；本段仅把真实像素几何写入文本文件，
        // 供自动化客观确认“按钮上/下间距相等”，无需肉眼看截图（本环境无法解析图片）。
        private TableLayoutPanel logToolbar;
        private Button btnRefreshLogs;
        private int bandGap = 5;          // 按钮带上/下每侧留白(px)，上下对称
        private readonly bool verifyMode;
        private readonly bool verifySwitchMode;

        private void DumpGeometry(string path)
        {
            // 注意：工具栏现在嵌套在 logLayout(TableLayoutPanel) 内，
            // 所以 btnTop / logTop 都要加上 logLayout.Top 偏移到 TabPage 坐标系。
            int layoutTop = logLayout != null ? logLayout.Top : 0;
            int btnTop = btnRefreshLogs.Top + btnRefreshLogs.Parent.Top + logToolbar.Top + layoutTop;
            int btnBottom = btnTop + btnRefreshLogs.Height;
            int logTop = logSplit.Top + layoutTop;
            int topGap = btnTop;                 // tab 行底部 -> 按钮顶部
            int bottomGap = logTop - btnBottom;  // 按钮底部 -> 内容区顶部
            var sb = new StringBuilder();
            sb.AppendLine($"# verify {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"bandGap={bandGap}");
            sb.AppendLine($"page.Padding.Top={((TabPage)tabs.TabPages[0]).Padding.Top}");
            sb.AppendLine($"logLayout.Top={layoutTop}");
            sb.AppendLine($"toolbar.Top={logToolbar.Top} toolbar.Height={logToolbar.Height} toolbar.Padding=(T{logToolbar.Padding.Top},B{logToolbar.Padding.Bottom})");
            sb.AppendLine($"btn.Top={btnRefreshLogs.Top} btn.Parent.Top={btnRefreshLogs.Parent.Top} btn.Height={btnRefreshLogs.Height}");
            sb.AppendLine($"logSplit.Top={logSplit.Top} logSplit.Margin.Top={logSplit.Margin.Top}");
            sb.AppendLine($"TOP_GAP={topGap} BOTTOM_GAP={bottomGap} EQUAL={(topGap == bottomGap)}");
            try { File.WriteAllText(path, sb.ToString()); } catch { }
        }

        // ===== 切页自检（--verify-switch，无 UI）=====
        // 依次切换到每个分页，跑一遍 SelectedIndexChanged 的“隐藏/显示 TabControl”残影修复路径，
        // 把每次切换是否成功、是否抛异常写到 switchlog.txt（无需肉眼看截图即可确认切页逻辑稳定）。
        private async System.Threading.Tasks.Task RunVerifySwitch()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# verify-switch {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "precreate.txt"), ""); } catch { }
            for (int t = 0; t < tabs.TabPages.Count; t++)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    tabs.SelectedIndex = t;
                    Application.DoEvents();                       // 让 SelectedIndexChanged 的刷新与绘制跑完
                    System.Threading.Thread.Sleep(30);
                    Application.DoEvents();
                    sw.Stop();
                    sb.AppendLine($"tab={t} name={tabs.SelectedTab?.Text} visible={tabs.Visible} top={tabs.SelectedTab?.Top} blocked_ms={sw.ElapsedMilliseconds}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"tab={t} EXCEPTION {ex.GetType().Name}: {ex.Message}");
                }
            }
            tabs.SelectedIndex = 0;
            await System.Threading.Tasks.Task.Delay(250);
            try { DumpGeometry(Path.Combine(Path.GetTempPath(), "geometry.txt")); } catch { }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "switchlog.txt"), sb.ToString()); } catch { }
            Application.Exit();
        }

        /// <summary>在 FlowLayoutPanel 中放置可自动换行的说明文字</summary>
        private static Label AddWrappedHint(FlowLayoutPanel panel, string text)
        {
            var lbl = new Label
            {
                Text = text,
                AutoSize = true,
                UseCompatibleTextRendering = true,
                Margin = Padding.Empty
            };
            EventHandler handler = (_, _) =>
            {
                int maxW = Math.Max(50, panel.ClientSize.Width - panel.Padding.Horizontal - lbl.Margin.Horizontal);
                lbl.MaximumSize = new Size(maxW, 0);
            };
            panel.Resize += handler;
            panel.HandleCreated += (_, _) => handler(panel, EventArgs.Empty);
            panel.Controls.Add(lbl);
            return lbl;
        }

        private void LoadConfig()
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(ProjectPaths.ConfigFile));
                chkHeadless.Checked = root?["headless"]?.GetValue<bool>() ?? false;
                chkStreakProtection.Checked = root?["ensureStreakProtection"]?.GetValue<bool>() ?? false;
                var w = root?["workers"];
                chkDailySet.Checked = w?["doDailySet"]?.GetValue<bool>() ?? false;
                chkMorePromotions.Checked = w?["doMorePromotions"]?.GetValue<bool>() ?? false;
                chkPunchCards.Checked = w?["doPunchCards"]?.GetValue<bool>() ?? false;
                chkDesktopSearch.Checked = w?["doDesktopSearch"]?.GetValue<bool>() ?? false;
                chkMobileSearch.Checked = w?["doMobileSearch"]?.GetValue<bool>() ?? false;
                chkDailyCheckIn.Checked = w?["doDailyCheckIn"]?.GetValue<bool>() ?? false;
                chkReadToEarn.Checked = w?["doReadToEarn"]?.GetValue<bool>() ?? false;
                var ntfy = root?["webhook"]?["ntfy"];
                chkNtfyEnabled.Checked = ntfy?["enabled"]?.GetValue<bool>() ?? false;
                txtNtfyTopic.Text = ntfy?["topic"]?.GetValue<string>() ?? "";
                txtNtfyUrl.Text = ntfy?["url"]?.GetValue<string>() ?? "";
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取 config.json 失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveConfig()
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(ProjectPaths.ConfigFile)).AsObject();
                root["headless"] = chkHeadless.Checked;
                root["ensureStreakProtection"] = chkStreakProtection.Checked;
                var w = root["workers"]?.AsObject() ?? new JsonObject();
                w["doDailySet"] = chkDailySet.Checked;
                w["doMorePromotions"] = chkMorePromotions.Checked;
                w["doPunchCards"] = chkPunchCards.Checked;
                w["doDesktopSearch"] = chkDesktopSearch.Checked;
                w["doMobileSearch"] = chkMobileSearch.Checked;
                w["doDailyCheckIn"] = chkDailyCheckIn.Checked;
                w["doReadToEarn"] = chkReadToEarn.Checked;
                root["workers"] = w;
                var webhook = root["webhook"]?.AsObject() ?? new JsonObject();
                var ntfy = webhook["ntfy"]?.AsObject() ?? new JsonObject();
                ntfy["enabled"] = chkNtfyEnabled.Checked;
                ntfy["topic"] = txtNtfyTopic.Text.Trim();
                ntfy["url"] = txtNtfyUrl.Text.Trim();
                webhook["ntfy"] = ntfy;
                root["webhook"] = webhook;

                File.WriteAllText(ProjectPaths.ConfigFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                LoadConfig();
                RefreshLogs();
                MessageBox.Show("config.json 已保存", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>从主界面手动打开环境向导</summary>
        private void ShowEnvWizard()
        {
            try
            {
                new EnvWizardForm(standalone: false).ShowDialog(this);
            }
            catch { }
        }

        private void LoadEnv()
        {
            envFlow.Controls.Clear();
            envEntries.Clear();
            try
            {
                var lines = File.Exists(ProjectPaths.EnvFile)
                    ? File.ReadAllText(ProjectPaths.EnvFile).Replace("\r\n", "\n").Split('\n')
                    : Array.Empty<string>();
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    bool enabled = true;
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("#"))
                    {
                        var body = trimmed.Substring(1).TrimStart();
                        // 纯说明注释不展示
                        if (body.IndexOf('=') < 0) continue;
                        enabled = false;
                        line = body;
                    }
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1);
                    bool secret = key.IndexOf("PASSWORD", StringComparison.OrdinalIgnoreCase) >= 0
                                || key.IndexOf("TOTP", StringComparison.OrdinalIgnoreCase) >= 0
                                || key.IndexOf("SECRET", StringComparison.OrdinalIgnoreCase) >= 0
                                || key.IndexOf("TOKEN", StringComparison.OrdinalIgnoreCase) >= 0;
                    envFlow.Controls.Add(BuildEnvRow(key, val, secret, enabled));
                }
                if (envFlow.Controls.Count == 0)
                    envFlow.Controls.Add(new Label { Text = "（.env 为空或不存在，可点击「从模板创建 .env」）", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 4, 0, 4), BackColor = SystemColors.Control });
            }
            catch (Exception ex)
            {
                envFlow.Controls.Add(new Label { Text = "读取失败: " + ex.Message, AutoSize = true, ForeColor = Color.DarkRed, BackColor = SystemColors.Control });
            }
        }

        /// <summary>构建一行「复选框 + 中文标签 + 单行输入框」</summary>
        private FlowLayoutPanel BuildEnvRow(string key, string value, bool secret, bool enabled)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Height = 28,
                Margin = new Padding(0, 4, 0, 4),
                BackColor = SystemColors.Control
            };
            var chk = new CheckBox
            {
                Checked = enabled,
                AutoSize = true,
                Margin = new Padding(2, 6, 6, 0),
                BackColor = SystemColors.Control
            };
            var lbl = new Label
            {
                Text = GetEnvLabelChinese(key),
                AutoSize = true,
                Height = 24,
                Margin = new Padding(0, 4, 12, 0),
                BackColor = SystemColors.Control
            };
            var txt = new TextBox
            {
                Width = 420,
                Margin = new Padding(0, 2, 0, 0),
                Text = value
            };
            if (secret) txt.PasswordChar = '*';
            row.Controls.Add(chk);
            row.Controls.Add(lbl);
            row.Controls.Add(txt);
            envEntries.Add(new EnvEntry { Key = key, Secret = secret, Check = chk, Box = txt });
            return row;
        }

        private static string GetEnvLabelChinese(string key)
        {
            if (key.StartsWith("ACCOUNT_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = key.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out int n))
                {
                    string suffix = string.Join("_", parts, 2, parts.Length - 2).ToUpperInvariant();
                    string cn = suffix switch
                    {
                        "EMAIL" => "邮箱",
                        "PASSWORD" => "密码",
                        "TOTP_SECRET" => "TOTP 密钥",
                        "RECOVERY_EMAIL" => "恢复邮箱",
                        "GEO_LOCALE" => "地区",
                        "LANG_CODE" => "语言代码",
                        "PROXY_HTTP" => "使用 HTTP 代理",
                        "PROXY_URL" => "代理地址",
                        "PROXY_PORT" => "代理端口",
                        "PROXY_USERNAME" => "代理用户名",
                        "PROXY_PASSWORD" => "代理密码",
                        "SAVE_FINGERPRINT_MOBILE" => "保存移动端指纹",
                        "SAVE_FINGERPRINT_DESKTOP" => "保存桌面端指纹",
                        _ => suffix
                    };
                    return $"账号 {n} {cn}";
                }
            }
            return key.ToUpperInvariant() switch
            {
                "API_TOKEN" => "API 令牌",
                _ => key
            };
        }

        private void SaveEnv()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var e in envEntries)
                {
                    string prefix = e.Check.Checked ? "" : "#";
                    sb.AppendLine($"{prefix}{e.Key}={e.Box.Text}");
                }
                File.WriteAllText(ProjectPaths.EnvFile, sb.ToString(), new UTF8Encoding(false));
                LoadEnv();
                RefreshLogs();
                MessageBox.Show(".env 已保存", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>从 .env.example 模板创建 .env，便于用户直接填写账号</summary>
        private void CreateEnvFromExample()
        {
            try
            {
                if (File.Exists(ProjectPaths.EnvFile))
                {
                    MessageBox.Show(".env 已存在，无需创建。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var example = Path.Combine(ProjectPaths.Root, ".env.example");
                if (!File.Exists(example))
                {
                    MessageBox.Show("模板 .env.example 不存在，无法创建。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                File.Copy(example, ProjectPaths.EnvFile, false);
                LoadEnv();
                MessageBox.Show("已从模板创建 .env，请填写你的邮箱和密码。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("创建失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ============================================================
        //  Tab 3: 自动化设置
        // ============================================================
        private TabPage BuildAutomationTab()
        {
            var page = new TabPage("自动化设置")
            {
                Padding = new Padding(0, 3, 0, 0)
            };
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };

            // --- 状态组 ---
            grpStatus = new StatusGroupBox
            {
                Text = "",   // 标题由 OnPaint 自定义绘制（左侧默认色 + 右侧状态值着色）
                Dock = DockStyle.Top,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10),
                Padding = new Padding(12, 8, 12, 12)
            };

            lblTaskDetail = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = SystemColors.ControlText,
                Margin = new Padding(0, 4, 0, 4),
                UseCompatibleTextRendering = true
            };
            lblTaskTriggers = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = SystemColors.ControlText,
                Margin = new Padding(0, 4, 0, 0),
                UseCompatibleTextRendering = true
            };

            grpStatus.Controls.Add(lblTaskTriggers);
            grpStatus.Controls.Add(lblTaskDetail);

            // --- 设置组 ---
            grpSettingsAutomation = new GroupBox
            {
                Text = "计划任务设置",
                Dock = DockStyle.Top,
                AutoSize = false,
                Margin = new Padding(0),
                Padding = new Padding(12, 8, 12, 12)
            };

            var opsRow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 8)
            };
            opsRow.Controls.Add(MkButton("注册/重建计划任务", (_, _) =>
            {
                try { WriteAutomationSettings(); } catch { }
                ProcessHelper.RunElevated($"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(ProjectPaths.AutorunDir, "setup-task.ps1")}\"");
                MessageBox.Show("已请求管理员权限创建任务，完成后点击「刷新状态」查看。", "提示");
            }));
            opsRow.Controls.Add(MkButton("注销计划任务", (_, _) =>
            {
                if (MessageBox.Show("确定要注销计划任务 MicrosoftRewardsScript 吗？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    ProcessHelper.RunElevated("-NoProfile -Command \"Unregister-ScheduledTask -TaskName 'MicrosoftRewardsScript' -Confirm:$false\"");
                }
            }));
            opsRow.Controls.Add(MkButton("立即手动运行", (_, _) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.Combine(ProjectPaths.AutorunDir, "run-manual.bat"),
                    UseShellExecute = true
                });
            }));
            opsRow.Controls.Add(MkButton("运行诊断", (_, _) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.Combine(ProjectPaths.AutorunDir, "diagnose.bat"),
                    UseShellExecute = true
                });
            }));
            opsRow.Controls.Add(MkButton("刷新状态", (_, _) => RefreshTaskStatus()));
            opsRow.Controls.Add(MkButton("环境设置", (_, _) => ShowEnvWizard()));

            var timeRow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 4, 0, 8)
            };
            timeRow.Controls.Add(new Label { Text = "每日运行时间 (HH:mm):", AutoSize = true, Margin = new Padding(0, 8, 4, 0), UseCompatibleTextRendering = true });
            txtRunTime = new TextBox { Text = "07:00", Width = 80, Margin = new Padding(0, 5, 12, 0) };
            timeRow.Controls.Add(txtRunTime);
            timeRow.Controls.Add(MkButton("应用时间（需管理员）", (_, _) => ApplyRunTime()));

            // --- 运行外观与通知设置 ---
            var appearanceRow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0, 6, 0, 4)
            };
            chkSilentWindow = new CheckBox
            {
                Text = "静默窗口（勾选=静默隐藏，半选=最小化，未选=正常窗口）",
                AutoSize = true,
                ThreeState = true,
                Margin = new Padding(0, 2, 0, 2),
                UseCompatibleTextRendering = true
            };
            chkNotify = new CheckBox
            {
                Text = "Windows 通知（勾选=启动+完成都通知，半选=仅完成通知，未选=不通知）",
                AutoSize = true,
                ThreeState = true,
                Margin = new Padding(0, 2, 0, 2),
                UseCompatibleTextRendering = true
            };
            appearanceRow.Controls.Add(chkSilentWindow);
            appearanceRow.Controls.Add(chkNotify);
            // 保存按钮：用 MouseDown 而不是 Click，避免 FlowLayoutPanel 自动布局在鼠标按下/抬起之间
            // 重算位置导致 Click 事件丢失，从而需要点击两次才生效。
            var btnSaveAppearance = new Button
            {
                Text = "保存外观/通知设置",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 3, 8, 3),
                Margin = new Padding(0, 6, 12, 0)
            };
            btnSaveAppearance.MouseDown += (_, _) => SaveAutomationSettings();
            appearanceRow.Controls.Add(btnSaveAppearance);

            // 用一个垂直 FlowLayoutPanel 包住所有行，作为 GroupBox 的唯一子控件。
            // GroupBox 的 AutoSize 对「多个 Dock=Top 子控件」高度求和会算错，
            // 改为只放一个自动高度的容器即可正确计算。
            vflowSettings = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = Padding.Empty
            };
            vflowSettings.Controls.Add(appearanceRow);
            vflowSettings.Controls.Add(timeRow);
            vflowSettings.Controls.Add(opsRow);

            grpSettingsAutomation.Controls.Add(vflowSettings);

            LoadAutomationSettings();

            panel.Controls.Add(grpSettingsAutomation);
            panel.Controls.Add(grpStatus);
            page.Controls.Add(panel);
            return page;
        }

        // GroupBox 的 AutoSize 对停靠子控件高度求和会算错，导致底部按钮被截断。
        // 改为手动按内容实际高度设置 GroupBox 高度（vflow 的 AutoSize 可正确计算自身高度）。
        private void FixAutomationGroupHeight()
        {
            if (grpSettingsAutomation == null || vflowSettings == null) return;
            grpSettingsAutomation.PerformLayout();
            int header = grpSettingsAutomation.DisplayRectangle.Y; // 标题栏高度
            if (header <= 0) header = grpSettingsAutomation.Font.Height + 8;
            int h = vflowSettings.Height + grpSettingsAutomation.Padding.Top + grpSettingsAutomation.Padding.Bottom + header;
            if (h > grpSettingsAutomation.Height || grpSettingsAutomation.Height - h > 4)
            {
                grpSettingsAutomation.Height = h;
            }
        }

        private async void RefreshTaskStatus()
        {
            grpStatus.StatusValue = "刷新中...";
            grpStatus.StatusValueColor = SystemColors.ControlText;
            lblTaskDetail.Text = "正在查询计划任务状态...";
            lblTaskTriggers.Text = "";
            // 注意：查询计划任务要冷启动 powershell.exe（CLR 启动约 0.5~1.5s）。本方法已是 async，
            // 直接 await 后台线程上的查询即可，不要在主线程 DoEvents 等待——否则会阻塞首绘。
            // 这里用 Task.Run 把 powershell 进程创建与等待放到线程池，UI 在等待期间可正常绘制/响应。
            var (code, output) = await System.Threading.Tasks.Task.Run(() =>
            {
                string ps = @"$t = Get-ScheduledTask -TaskName 'MicrosoftRewardsScript' -ErrorAction SilentlyContinue; " +
                            @"if ($t) { $i = $t | Get-ScheduledTaskInfo; " +
                            @"[PSCustomObject]@{ State = $t.State.ToString(); " +
                            @"LastRun = $i.LastRunTime.ToString('yyyy-MM-dd HH:mm'); " +
                            @"NextRun = $i.NextRunTime.ToString('yyyy-MM-dd HH:mm'); " +
                            @"LastResult = $i.LastTaskResult; " +
                            @"Triggers = (($t.Triggers | ForEach-Object { $_.CimClass.CimClassName }) -join ' | '); " +
                            @"Action = ($t.Actions[0].Execute + ' ' + $t.Actions[0].Arguments) } | ConvertTo-Json -Compress }";
                return ProcessHelper.Run("powershell.exe", "-NoProfile -Command \"" + ps + "\"");
            });
            if (code == 0 && !string.IsNullOrWhiteSpace(output) && output.TrimStart().StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(output);
                    var r = doc.RootElement;
                    string state = r.GetProperty("State").GetString();
                    string lastRun = r.GetProperty("LastRun").GetString() ?? "";
                    if (lastRun.StartsWith("1999") || lastRun.StartsWith("1900")) lastRun = "从未运行";
                    grpStatus.StatusValue = state;
                    grpStatus.StatusValueColor = state == "Ready" || state == "Running" ? Color.DarkGreen : Color.DarkRed;
                    lblTaskDetail.ForeColor = SystemColors.ControlText;
                    lblTaskTriggers.ForeColor = SystemColors.ControlText;
                    lblTaskDetail.Text = $"上次运行: {lastRun}    下次运行: {r.GetProperty("NextRun").GetString()}    上次退出码: {r.GetProperty("LastResult")}";
                    lblTaskTriggers.Text = "触发器: " + PrettifyTriggers(r.GetProperty("Triggers").GetString() ?? "");
                    var action = r.GetProperty("Action").GetString() ?? "";
                    if (!action.Contains(ProjectPaths.AutorunDir))
                    {
                        grpStatus.StatusValue = state + "（警告: 指向旧路径）";
                        grpStatus.StatusValueColor = Color.DarkOrange;
                        lblTaskDetail.ForeColor = SystemColors.ControlText;
                        lblTaskTriggers.ForeColor = SystemColors.ControlText;
                    }
                    return;
                }
                catch { }
            }
            grpStatus.StatusValue = "不存在";
            grpStatus.StatusValueColor = Color.DarkRed;
            lblTaskDetail.ForeColor = SystemColors.ControlText;
            lblTaskTriggers.ForeColor = SystemColors.ControlText;
            lblTaskDetail.Text = "计划任务不存在，请点击「注册/重建计划任务」创建。";
            lblTaskTriggers.Text = "";
        }

        private static string PrettifyTriggers(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            return raw
                .Replace("MSFT_TaskDailyTrigger", "每天定时")
                .Replace("MSFT_TaskLogonTrigger", "用户登录时")
                .Replace("MSFT_TaskTimeTrigger", "一次性定时")
                .Replace("MSFT_TaskBootTrigger", "系统启动时");
        }

        private void ApplyRunTime()
        {
            var time = txtRunTime.Text.Trim();
            if (!TimeSpan.TryParse(time, out _))
            {
                MessageBox.Show("时间格式不正确，请输入 HH:mm（例如 07:30）", "提示");
                return;
            }
            var ps = "$t = Get-ScheduledTask -TaskName 'MicrosoftRewardsScript' -ErrorAction Stop; " +
                     "$daily = New-ScheduledTaskTrigger -Daily -At '" + time + "'; " +
                     "$logon = New-ScheduledTaskTrigger -AtLogOn; " +
                     "Set-ScheduledTask -TaskName 'MicrosoftRewardsScript' -Trigger @($daily, $logon)";
            ProcessHelper.RunElevated("-NoProfile -Command \"" + ps.Replace("\"", "\\\"") + "\"");
            MessageBox.Show("已请求管理员权限修改运行时间，完成后点击「刷新状态」查看。", "提示");
        }

        // ---------- 自动化外观/通知设置（持久化到 autorun/automation-settings.json） ----------
        private static string WindowModeFromCheckState(CheckState cs) =>
            cs == CheckState.Checked ? "silent" : cs == CheckState.Indeterminate ? "minimized" : "normal";
        private static CheckState WindowModeToCheckState(string m) =>
            m == "silent" ? CheckState.Checked : m == "minimized" ? CheckState.Indeterminate : CheckState.Unchecked;
        private static string NotifyModeFromCheckState(CheckState cs) =>
            cs == CheckState.Checked ? "both" : cs == CheckState.Indeterminate ? "complete" : "none";
        private static CheckState NotifyModeToCheckState(string m) =>
            m == "both" ? CheckState.Checked : m == "complete" ? CheckState.Indeterminate : CheckState.Unchecked;

        private void WriteAutomationSettings()
        {
            var obj = new Dictionary<string, string>
            {
                ["windowMode"] = WindowModeFromCheckState(chkSilentWindow.CheckState),
                ["notifyMode"] = NotifyModeFromCheckState(chkNotify.CheckState)
            };
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProjectPaths.AutomationSettingsFile, json, new UTF8Encoding(false));
        }

        private void SaveAutomationSettings()
        {
            try
            {
                WriteAutomationSettings();

                // 若计划任务已存在，自动重建它以应用新的窗口模式，无需用户再点第二下。
                bool taskExists = false;
                try
                {
                    var (code, output) = ProcessHelper.Run("powershell.exe",
                        "-NoProfile -Command \"if (Get-ScheduledTask -TaskName 'MicrosoftRewardsScript' -ErrorAction SilentlyContinue) { 'EXISTS' }\"");
                    taskExists = code == 0 && output.Contains("EXISTS");
                }
                catch { }

                if (taskExists)
                {
                    var setupScript = Path.Combine(ProjectPaths.AutorunDir, "setup-task.ps1");
                    ProcessHelper.RunElevated($"-NoProfile -ExecutionPolicy Bypass -File \"{setupScript}\"");
                    MessageBox.Show(
                        "自动化设置已保存，并已请求管理员权限更新计划任务。\n“静默窗口”模式将立即生效；“Windows 通知”下次运行自动生效。",
                        "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(
                        "自动化设置已保存。\n计划任务尚未注册，“静默窗口”需点击「注册/重建计划任务」后生效；“Windows 通知”下次运行自动生效。",
                        "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadAutomationSettings()
        {
            try
            {
                if (File.Exists(ProjectPaths.AutomationSettingsFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(ProjectPaths.AutomationSettingsFile));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("windowMode", out var wm) && wm.ValueKind == JsonValueKind.String)
                        chkSilentWindow.CheckState = WindowModeToCheckState(wm.GetString());
                    if (r.TryGetProperty("notifyMode", out var nm) && nm.ValueKind == JsonValueKind.String)
                        chkNotify.CheckState = NotifyModeToCheckState(nm.GetString());
                }
                else
                {
                    // 默认：静默窗口（与现有行为一致）+ 不通知
                    chkSilentWindow.CheckState = CheckState.Checked;
                    chkNotify.CheckState = CheckState.Unchecked;
                }
            }
            catch { }
        }

        // ============================================================
        //  Tab 4: 版本更新
        // ============================================================
        private TabPage BuildUpdateTab()
        {
            var page = new TabPage("版本更新")
            {
                Padding = new Padding(0, 3, 0, 0)
            };
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                BackColor = SystemColors.Control
            };
            SetDoubleBuffered(panel);
            panel.Resize += (_, _) => panel.Invalidate(true);

            lblUpdateState = new TextBox
            {
                Dock = DockStyle.Top,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Margin = new Padding(4, 4, 4, 8)
            };

            var verRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(4, 0, 4, 8)
            };
            lblCurrentVer = new Label { AutoSize = true, Margin = new Padding(0, 3, 60, 0), UseCompatibleTextRendering = true };
            lblLatestVer = new Label { AutoSize = true, Margin = new Padding(0, 3, 60, 0), UseCompatibleTextRendering = true };
            lblPublished = new Label { AutoSize = true, Margin = new Padding(0, 3, 0, 0), UseCompatibleTextRendering = true };
            verRow.Controls.Add(lblCurrentVer);
            verRow.Controls.Add(lblLatestVer);
            verRow.Controls.Add(lblPublished);

            var grpLog = new GroupBox
            {
                Text = "更新日志",
                Dock = DockStyle.Fill,
                MinimumSize = new Size(0, 200),
                Margin = new Padding(0, 0, 0, 10),
                Padding = new Padding(12, 8, 12, 12)
            };
            txtChangelog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.ForcedBoth,
                DetectUrls = false,
                AutoWordSelection = false,
                ShowSelectionMargin = false,
                BackColor = Color.White,
                Cursor = Cursors.Arrow
            };
            txtChangelog.MouseDown += TxtChangelog_MouseDown;
            txtChangelog.MouseMove += TxtChangelog_MouseMove;
            txtChangelog.MouseUp += TxtChangelog_MouseUp;
            grpLog.Controls.Add(txtChangelog);
            Application.AddMessageFilter(this);

            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 0, 0, 8)
            };
            btnUpdate = MkButton("立即更新", async (_, _) => await DoUpdate());
            btnSkip = MkButton("跳过此版本", (_, _) => SkipVersion());
            var btnRecheck = MkButton("重新检查更新", (_, _) => RecheckUpdates());
            var btnHome = MkButton("项目主页", (_, _) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ProjectRepoUrl,
                    UseShellExecute = true
                });
            });
            btnRow.Controls.Add(btnUpdate);
            btnRow.Controls.Add(btnSkip);
            btnRow.Controls.Add(btnRecheck);
            btnRow.Controls.Add(btnHome);

            panel.Controls.Add(grpLog);
            panel.Controls.Add(btnRow);
            panel.Controls.Add(verRow);
            panel.Controls.Add(lblUpdateState);
            page.Controls.Add(panel);
            return page;
        }

        private void RefreshUpdateStatus()
        {
            if (!File.Exists(ProjectPaths.UpdateStatusFile))
            {
                lblUpdateState.Text = "尚无更新检查记录（脚本运行后自动生成，或点击「重新检查更新」）";
                lblUpdateState.ForeColor = Color.Black;
                lblCurrentVer.Text = lblLatestVer.Text = lblPublished.Text = "";
                txtChangelog.Text = "";
                btnUpdate.Enabled = btnSkip.Enabled = false;
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ProjectPaths.UpdateStatusFile));
                var r = doc.RootElement;
                string current = r.GetProperty("currentVersion").GetString() ?? "?";
                string latest = r.GetProperty("latestVersion").ValueKind == JsonValueKind.String ? r.GetProperty("latestVersion").GetString() : "未知";
                bool available = r.GetProperty("updateAvailable").GetBoolean();
                string error = r.GetProperty("error").ValueKind == JsonValueKind.String ? r.GetProperty("error").GetString() : null;
                string published = r.GetProperty("publishedAt").ValueKind == JsonValueKind.String ? r.GetProperty("publishedAt").GetString() : "";
                string changelog = r.GetProperty("changelog").ValueKind == JsonValueKind.String ? r.GetProperty("changelog").GetString() : "";

                lblCurrentVer.Text = $"当前版本: {current}";
                lblLatestVer.Text = $"最新版本: {latest}";
                string publishedDate = "未知";
                if (!string.IsNullOrEmpty(published) && DateTime.TryParse(published, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                    publishedDate = dt.ToString("yyyy-MM-dd");
                else if (!string.IsNullOrEmpty(published))
                    publishedDate = published.Split('T')[0];
                lblPublished.Text = $"发布时间: {publishedDate}";
                try { txtChangelog.Text = ChangelogToPlain(changelog); HighlightChangelogUrls(); }
                catch { txtChangelog.Text = changelog ?? ""; _changelogUrls.Clear(); }

                if (error != null)
                {
                    lblUpdateState.Text = "检查更新时出错: " + error;
                    lblUpdateState.ForeColor = Color.DarkOrange;
                }
                else if (available)
                {
                    lblUpdateState.Text = $"发现新版本 v{latest}！";
                    lblUpdateState.ForeColor = Color.DarkRed;
                }
                else
                {
                    lblUpdateState.Text = "当前已是最新版本";
                    lblUpdateState.ForeColor = Color.DarkGreen;
                }
                btnUpdate.Enabled = btnSkip.Enabled = available;
            }
            catch (Exception ex)
            {
                lblUpdateState.Text = "读取更新状态失败: " + ex.Message;
                lblUpdateState.ForeColor = Color.DarkOrange;
            }
        }

        private async void RecheckUpdates()
        {
            lblUpdateState.Text = "正在检查更新...";
            lblUpdateState.ForeColor = Color.Black;
            btnUpdate.Enabled = btnSkip.Enabled = false;

            var node = EnvCheck.FindNodePath();
            var result = await System.Threading.Tasks.Task.Run(() =>
                ProcessHelper.Run(node,
                    "-e \"require('./dist/util/UpdateChecker').checkForUpdates().then(()=>process.exit(0))\"",
                    ProjectPaths.Root, 30000));

            RefreshUpdateStatus();

            if (result.exitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(result.output) ? "" : "\n\n" + result.output.Trim();
                MessageBox.Show($"检查更新失败（退出码 {result.exitCode}）。{detail}", "检查失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (!File.Exists(ProjectPaths.UpdateStatusFile))
            {
                MessageBox.Show("检查完成，但没有生成更新状态文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private async System.Threading.Tasks.Task DoUpdate()
        {
            // 读取更新状态里的最新版本与发布页地址
            string latest = null, releaseUrl = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ProjectPaths.UpdateStatusFile));
                var root = doc.RootElement;
                if (root.TryGetProperty("latestVersion", out var lv)) latest = lv.GetString();
                if (root.TryGetProperty("releaseUrl", out var ru)) releaseUrl = ru.GetString();
            }
            catch { }

            var cur = ReadCurrentVersion();
            if (string.IsNullOrEmpty(latest) || latest == cur)
            {
                MessageBox.Show("当前已是最新版本，无需更新。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(
                    $"将自动从 GitHub 下载 v{latest} 并覆盖安装，期间脚本无法运行。\n" +
                    "你的 .env、config.json、node_modules 与日志将被保留。继续？",
                    "确认更新",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes) return;

            var downloadUrl = BuildDownloadUrl(releaseUrl, latest);
            var win = new OutputWindow("正在更新 Microsoft Rewards Script...");
            win.Show(this);

            if (string.IsNullOrEmpty(downloadUrl))
            {
                win.AppendSafe("[错误] 无法构造下载地址，请手动前往 GitHub Release 下载。");
                Process.Start(new ProcessStartInfo { FileName = ProjectRepoUrl + "/releases", UseShellExecute = true });
                MessageBox.Show("无法自动更新，已为你打开发布页面。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 1. 下载
            var tmpZip = Path.Combine(Path.GetTempPath(), $"mrs-update-{latest}.zip");
            try
            {
                if (File.Exists(tmpZip)) File.Delete(tmpZip);
                await DownloadFileAsync(downloadUrl, tmpZip, msg => win.AppendSafe(msg));
                win.AppendSafe("下载完成，正在解压…");
            }
            catch (Exception ex)
            {
                var detail = DescribeNetworkError(ex);
                var hint = "常见原因：网络不稳定、DNS 污染、TLS 版本受限或 GitHub 访问受阻。" +
                           "可尝试切换网络/代理，或点击下方「项目主页」手动下载 zip 解压覆盖。";
                win.AppendSafe($"[错误] 下载失败：{detail}");
                win.AppendSafe($"[提示] {hint}");
                MessageBox.Show($"下载失败：{detail}\n\n{hint}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 2. 解压
            string sourceDir;
            var extractDir = Path.Combine(Path.GetTempPath(), $"mrs-update-{latest}");
            try
            {
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(tmpZip, extractDir);
                // 兼容压缩包内层带顶层文件夹的情况
                sourceDir = extractDir;
                if (!File.Exists(Path.Combine(extractDir, "package.json")))
                {
                    var top = Directory.GetDirectories(extractDir);
                    if (top.Length == 1) sourceDir = top[0];
                }
            }
            catch (Exception ex)
            {
                win.AppendSafe("[错误] 解压失败：" + ex.Message);
                MessageBox.Show("解压失败：" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 3. 生成更新脚本并启动（等待本进程退出后覆盖安装并重启）
            var updaterPath = Path.Combine(Path.GetTempPath(), "mrs-updater.ps1");
            File.WriteAllText(updaterPath, BuildUpdaterScript(), new UTF8Encoding(false));

            // 单文件发布时 Process.MainModule.FileName 会指向临时解压目录，
            // 必须用 Application.ExecutablePath 才能拿到用户实际双击的入口 exe。
            var selfExe = Application.ExecutablePath;
            var pid = Process.GetCurrentProcess().Id;
            var node = EnvCheck.FindNodePath();
            // node 为空时不传 -Node 参数（PowerShell 的 "-Node \"\"" 会被丢弃并导致参数绑定失败）；
            // 脚本侧 $Node 已有 = '' 默认值兜底。
            var nodeArg = string.IsNullOrEmpty(node) ? "" : $"-Node \"{node}\" ";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =                             $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{updaterPath}\" " +
                            $"-Target \"{ProjectPaths.Root}\" -Source \"{sourceDir}\" -ParentPid {pid} " +
                            $"-Self \"{selfExe}\" " + nodeArg,
                UseShellExecute = true,
                CreateNoWindow = true
            };
            try
            {
                Process.Start(psi);
                var logPath = Path.Combine(ProjectPaths.Root, "autorun", "update-log.txt");
                win.AppendSafe("更新程序已启动，本程序即将退出以完成安装。");
                win.AppendSafe($"如安装后未自动重启，请查看日志：{logPath}");
                await System.Threading.Tasks.Task.Delay(1000);
                // 删除已下载的临时压缩包（解压目录留给更新脚本清理）
                try { File.Delete(tmpZip); } catch { }
                // 必须用 Environment.Exit 强制终止进程，确保 updater 能及时检测到
                // 本进程已退出；Application.Exit() 在 async 上下文里不一定真正退出。
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                win.AppendSafe("[错误] 无法启动更新程序：" + ex.Message);
                MessageBox.Show("无法启动更新程序：" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>从 package.json 读取当前版本号</summary>
        private string ReadCurrentVersion()
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ProjectPaths.PackageJson));
                return doc.RootElement.GetProperty("version").GetString() ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 启动自检：把 update-status.json 的 currentVersion 与真实的 package.json 版本对齐。
        /// 更新脚本即使某一环节失败（如未能改写 currentVersion），重启后界面也能立即显示正确版本，
        /// 无需再手动点一次「检查更新」。
        /// </summary>
        private void ReconcileLocalVersion()
        {
            try
            {
                if (!File.Exists(ProjectPaths.UpdateStatusFile)) return;
                var current = ReadCurrentVersion();
                if (string.IsNullOrEmpty(current)) return;
                var json = File.ReadAllText(ProjectPaths.UpdateStatusFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("currentVersion", out var cv) || cv.GetString() != current)
                {
                    // 保留其它字段，仅修正 currentVersion
                    var dict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json)
                               ?? new System.Collections.Generic.Dictionary<string, object>();
                    dict["currentVersion"] = current;
                    File.WriteAllText(ProjectPaths.UpdateStatusFile,
                        System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                        new System.Text.UTF8Encoding(false));
                }
            }
            catch { /* 自检失败不阻断启动 */ }
        }

        /// <summary>根据发布页地址和版本号构造压缩包下载直链</summary>
        private string BuildDownloadUrl(string releaseUrl, string latest)
        {
            if (string.IsNullOrEmpty(releaseUrl) || string.IsNullOrEmpty(latest)) return null;
            var dl = releaseUrl.Replace("/releases/tag/", "/releases/download/");
            var asset = $"Microsoft-Rewards-Script-portable-v{latest}.zip";
            return dl.TrimEnd('/') + "/" + asset;
        }

        /// <summary>带进度的文件下载（显式 TLS1.2/1.3 + 自动重试）</summary>
        private async System.Threading.Tasks.Task DownloadFileAsync(string url, string dest, Action<string> onProgress)
        {
            const int maxRetries = 3;
            Exception lastEx = null;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var handler = new SocketsHttpHandler
                    {
                        SslOptions = new SslClientAuthenticationOptions
                        {
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                        },
                        ConnectTimeout = TimeSpan.FromSeconds(30),
                        PooledConnectionLifetime = TimeSpan.FromMinutes(1)
                    };

                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
                    client.DefaultRequestHeaders.Add("User-Agent", "RewardsManager");
                    onProgress($"正在连接… (第 {attempt} 次尝试)");
                    using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? -1L;
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var fs = File.Create(dest);
                    var buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, n);
                        read += n;
                        if (total > 0) onProgress($"下载中… {(int)(read * 100 / total)}%");
                    }
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (attempt < maxRetries)
                    {
                        onProgress($"连接失败：{DescribeNetworkError(ex)}，{2 * attempt} 秒后重试…");
                        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                    }
                }
            }

            throw lastEx;
        }

        /// <summary>把网络异常转换成更友好的提示</summary>
        private string DescribeNetworkError(Exception ex)
        {
            if (ex == null) return "未知错误";
            var msg = ex.InnerException?.Message ?? ex.Message;
            if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("handshake", StringComparison.OrdinalIgnoreCase))
                return "TLS/SSL 握手失败";
            if (msg.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("could not resolve", StringComparison.OrdinalIgnoreCase))
                return "DNS 解析失败";
            if (msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return "连接超时";
            if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase))
                return "连接被拒绝";
            return msg;
        }

                        /// <summary>生成更新脚本（PowerShell）：等待主进程退出 → 覆盖安装（保留用户数据）→ 重新构建 → 重启</summary>
        private string BuildUpdaterScript()
        {
                                                return string.Join("\r\n", new[]
            {
                "param([string]$Target, [string]$Source, [int]$ParentPid, [string]$Self, [string]$Node = '')",
                "$ErrorActionPreference = 'Continue'",
                "$log = Join-Path $Target 'autorun/update-log.txt'",
                "function Log($m){ try { Add-Content -Path $log -Value \"$(Get-Date -Format 'HH:mm:ss') $m\" } catch {} }",
                "# 任何致命错误都写入日志，避免静默崩溃无法诊断",
                "trap { Log (\"FATAL: \" + $_.Exception.Message + ' @L' + $_.InvocationInfo.ScriptLineNumber); exit 1 }",
                "Log \"Updater started.\"",
                "Log \"Target=$Target\"",
                "Log \"Source=$Source\"",
                "Log \"Self=$Self\"",
                "Log \"Node=$Node\"",
                "# 等待主进程退出（最多等 30 秒，超时则强制继续，避免主进程假死导致卡住）",
                "try { Wait-Process -Id $ParentPid -Timeout 30 -ErrorAction SilentlyContinue }",
                "catch { Log (\"Wait process warning: \" + $_.Exception.Message) }",
                "Log 'Main process exited (or timeout reached).'",
                "Log 'Main process exited.'",
                "# 校验关键路径",
                "if (-not (Test-Path $Source)) { Log \"ERROR: Source directory not found.\"; exit 1 }",
                "if (-not (Test-Path $Target)) { Log \"ERROR: Target directory not found.\"; exit 1 }",
                "if (-not (Test-Path $Self)) { Log \"ERROR: Self executable not found.\"; exit 1 }",
                "# 覆盖安装（保留用户数据）",
                "$excludedDirs = @('node_modules', 'logs', '.git', 'data')",
                "$excludedFiles = @('.env', 'config.json', 'update-status.json', 'update-skipped.json')",
                "$args1 = @($Source, $Target, '/E', '/R:2', '/W:2', '/NP', '/NFL', '/NDL')",
                "foreach ($d in $excludedDirs) { $args1 += '/XD'; $args1 += $d }",
                "foreach ($f in $excludedFiles) { $args1 += '/XF'; $args1 += $f }",
                "Log ('Robocopy ' + ($args1 -join ' '))",
                "& robocopy.exe @args1",
                "$rc = $LASTEXITCODE",
                "Log (\"Robocopy exit: \" + $rc)",
                "if ($rc -ge 8) { Log \"ERROR: Robocopy reported a failure.\"; exit 1 }",
                "# 重新构建 dist（依赖 + 构建，失败不阻断 EXE 更新）",
                "$nodeDir = if ($Node) { Split-Path $Node -Parent } else { '' }",
                "$npm = if ($nodeDir -and (Test-Path (Join-Path $nodeDir 'npm.cmd'))) { Join-Path $nodeDir 'npm.cmd' } else { 'npm.cmd' }",
                "if ($nodeDir) { $env:PATH = $nodeDir + ';' + $env:PATH }",
                "$env:PLAYWRIGHT_BROWSERS_PATH = '0'",
                "Log \"npm install... (npm=$npm)\"",
                "& cmd.exe /c \"chcp 65001 >nul & `\"$npm`\" install\" 2>&1 | ForEach-Object { Log $_ }",
                "$npmRc = $LASTEXITCODE",
                "Log (\"npm install exit: \" + $npmRc)",
                "Log 'npm run build...'",
                "& cmd.exe /c \"chcp 65001 >nul & `\"$npm`\" run build\" 2>&1 | ForEach-Object { Log $_ }",
                "$buildRc = $LASTEXITCODE",
                "Log (\"npm run build exit: \" + $buildRc)",
                "Log 'npm install/build finished (build failure is non-fatal for the EXE update).'",
                "# 刷新 update-status.json 的当前版本号（避免更新后仍显示旧版本）",
                "try {",
                "  $pkg = Get-Content (Join-Path $Target 'package.json') -Raw | ConvertFrom-Json",
                "  $newVer = $pkg.version",
                "  $usPath = Join-Path $Target 'autorun/update-status.json'",
                "  if (Test-Path $usPath) {",
                "    $us = Get-Content $usPath -Raw | ConvertFrom-Json",
                "    $us.currentVersion = $newVer",
                "    $us.updateAvailable = $false",
                "    $us | ConvertTo-Json -Depth 10 | Set-Content -Path $usPath -Encoding UTF8",
                "    Log (\"Updated update-status currentVersion to \" + $newVer)",
                "  }",
                "} catch { Log (\"WARN: failed to update update-status.json: \" + $_.Exception.Message) }",
                "# 重启程序",
                "Log (\"Restarting \" + $Self)",
                "$workDir = Split-Path $Self",
                "try { Start-Process -FilePath $Self -WorkingDirectory $workDir }",
                "catch { Log (\"ERROR: Failed to restart: \" + $_.Exception.Message) }",
                "Log 'Done.'"
            });
        }

        private void SkipVersion()
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ProjectPaths.UpdateStatusFile));
                var latest = doc.RootElement.GetProperty("latestVersion").GetString();
                var json = JsonSerializer.Serialize(new { skippedVersion = latest, skippedAt = DateTime.Now.ToString("o") }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ProjectPaths.UpdateSkippedFile, json, new UTF8Encoding(false));
                RefreshUpdateStatus();
                MessageBox.Show($"已跳过版本 v{latest}，该版本将不再提醒。", "提示");
            }
            catch (Exception ex)
            {
                MessageBox.Show("操作失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /// <summary>.env 解析后的单行条目，用于「配置编辑」页的逐行输入框</summary>
    internal class EnvEntry
    {
        public string Key;
        public bool Secret;
        public CheckBox Check;
        public TextBox Box;
    }

    /// <summary>标题用「左侧默认色 + 右侧状态值着色」自绘的 GroupBox，例如「计划任务状态: Ready」</summary>
    internal class StatusGroupBox : GroupBox
    {
        public string StatusValue { get; set; } = "";
        public Color StatusValueColor { get; set; } = SystemColors.ControlText;
        private const string TitleLeft = "计划任务状态:";

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); // 原生边框; Text 为空时不画标题, 整条上边框
            var font = Font;
            var leftSize = TextRenderer.MeasureText(e.Graphics, TitleLeft, font);
            int x = 9, y = 1;
            int valW = 0;
            if (!string.IsNullOrEmpty(StatusValue))
                valW = TextRenderer.MeasureText(e.Graphics, StatusValue, font).Width;
            int totalW = leftSize.Width + valW;
            // 用背景色擦掉标题位置的边框线，形成缺口
            using (var brush = new SolidBrush(BackColor))
                e.Graphics.FillRectangle(brush, x - 3, y - 2, totalW + 6, leftSize.Height + 4);
            TextRenderer.DrawText(e.Graphics, TitleLeft, font, new Point(x, y),
                ForeColor, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
            if (!string.IsNullOrEmpty(StatusValue))
                TextRenderer.DrawText(e.Graphics, StatusValue, font, new Point(x + leftSize.Width, y),
                    StatusValueColor, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>移除 WS_EX_COMPOSITED 的 RichTextBox，避免窗体级双缓冲导致滚动条拖动不同步</summary>
    internal class PlainRichTextBox : RichTextBox
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle &= ~0x02000000; // 移除 WS_EX_COMPOSITED
                return cp;
            }
        }
    }
}
