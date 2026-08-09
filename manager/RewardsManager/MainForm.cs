using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using System.Diagnostics;

namespace RewardsManager
{
    public class MainForm : Form
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
        private SplitContainer logSplit;
        private TableLayoutPanel logLayout;

        // 配置页
        private CheckBox chkHeadless, chkDailySet, chkMorePromotions, chkPunchCards, chkDesktopSearch,
            chkMobileSearch, chkDailyCheckIn, chkReadToEarn, chkStreakProtection, chkNtfyEnabled;
        private TextBox txtNtfyTopic, txtNtfyUrl;
        private FlowLayoutPanel envFlow;
        private Panel configScrollPanel;
        private TableLayoutPanel configRoot;
        private readonly List<EnvEntry> envEntries = new List<EnvEntry>();
        private bool _precreating;   // 启动期预渲染配置页时为 true，跳过 SelectedIndexChanged 的刷新逻辑

        // 自动化页
        private StatusGroupBox grpStatus;
        private Label lblTaskDetail, lblTaskTriggers;
        private TextBox txtRunTime;

        // 更新页
        private Label lblCurrentVer, lblLatestVer, lblPublished;
        private TextBox lblUpdateState;
        private RichTextBox txtChangelog;
        private Button btnUpdate, btnSkip;
        private const string ProjectRepoUrl = "https://github.com/asbdfzcg/Microsoft-Rewards-Script";


        public MainForm(int initialTab = 0, int setGap = -1, bool verify = false, bool verifySwitch = false)
        {
            Text = "Microsoft Rewards Script 管理程序";
            Width = 1000;
            Height = 720;
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            verifyMode = verify;
            verifySwitchMode = verifySwitch;
            // 启动期先不可见，避免控件预创建/首次绘制时的白屏闪烁；
            // Shown 中完成预渲染后再恢复 Opacity=1
            this.Opacity = 0;
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
                // 配置页首次绘制较重（.env 每行一个 CheckBox+Label+TextBox，加上十几个自定义 CheckBox
                // 的句柄创建与布局）。在窗体尚不可见的 Load 阶段先创建全部子控件句柄并布局一次，
                // 把这部分成本前置到启动期；真正的“首次像素绘制”在 Shown 中以 Opacity=0 不可见方式强制完成。
                try
                {
                    _precreating = true;
                    int cfgPrev = tabs.SelectedIndex;
                    var sw = Stopwatch.StartNew();
                    tabs.SelectedIndex = 1;
                    tabs.TabPages[1].PerformLayout();
                    configScrollPanel?.CreateControl();           // 递归创建全部子控件句柄
                    configScrollPanel?.PerformLayout();
                    sw.Stop();
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "precreate.txt"), $"precreate_ms={sw.ElapsedMilliseconds}\n"); } catch { }
                    tabs.SelectedIndex = cfgPrev;
                }
                catch { }
                finally { _precreating = false; }
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
                RefreshTaskStatus();
                RefreshUpdateStatus();
            };
            Shown += (_, _) =>
            {
                // 句柄创建后再设置分割位置，避免 DPI 缩放生效前被覆盖
                try { logSplit.SplitterDistance = Math.Min(300, logSplit.Width / 3); } catch { }
                tabs.SelectedTab?.PerformLayout();
                tabs.SelectedTab?.Refresh();
                // 强制配置页完成“首次像素绘制”：用 Opacity=0 让这次绘制对用户不可见，
                // 但窗口已 Visible，WM_PAINT 会真正执行。这样之后每次点击切到配置页都是已缓存的重绘，
                // 不再有 ~1s 首绘卡顿与半透明重影。
                try
                {
                    int cfgPrev = tabs.SelectedIndex;
                    tabs.SelectedIndex = 1;
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(20);
                    Application.DoEvents();
                    tabs.SelectedIndex = cfgPrev;
                    Application.DoEvents();
                }
                catch { }
                finally { this.Opacity = 1; }
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

            // 垂直布局容器：工具栏(自动高) / 固定间隔(bandGap) / 日志区(填充)
            // 用显式间隔行保证“按钮上方间距 == 按钮下方间距”，避免 Dock=Fill 控件边距不生效的坑
            var logLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, bandGap));
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

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

            logSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BorderStyle = BorderStyle.None,
                FixedPanel = FixedPanel.Panel1,
                SplitterDistance = 300
            };
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
            lstLogs.SelectedIndexChanged += (_, _) => LoadSelectedLog();
            logSplit.Panel1.Controls.Add(lstLogs);
            logSplit.Panel2.Controls.Add(txtLogView);

            this.logLayout = logLayout;
            logLayout.Controls.Add(toolbar, 0, 0);
            // 第 1 行为空的固定间隔行(bandGap)，提供“按钮下方间距”
            logLayout.Controls.Add(logSplit, 0, 2);
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
        }

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
        {
            if (!Directory.Exists(ProjectPaths.LogsDir)) return;
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
            var grpSettings = new GroupBox
            {
                Text = "计划任务设置",
                Dock = DockStyle.Top,
                AutoSize = true,
                Margin = new Padding(0),
                Padding = new Padding(12, 8, 12, 12)
            };

            var hintFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 0)
            };
            AddWrappedHint(hintFlow,
                "说明:「注册/重建」「应用时间」「注销」会弹出 UAC 授权窗口; 计划任务包含每天定时 + 用户登录两个触发器（开机/登录补跑）。");

            var opsRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 8)
            };
            opsRow.Controls.Add(MkButton("注册/重建计划任务", (_, _) =>
            {
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
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 4, 0, 8)
            };
            timeRow.Controls.Add(new Label { Text = "每日运行时间 (HH:mm):", AutoSize = true, Margin = new Padding(0, 8, 4, 0), UseCompatibleTextRendering = true });
            txtRunTime = new TextBox { Text = "07:00", Width = 80, Margin = new Padding(0, 5, 12, 0) };
            timeRow.Controls.Add(txtRunTime);
            timeRow.Controls.Add(MkButton("应用时间（需管理员）", (_, _) => ApplyRunTime()));

            grpSettings.Controls.Add(hintFlow);
            grpSettings.Controls.Add(opsRow);
            grpSettings.Controls.Add(timeRow);

            panel.Controls.Add(grpSettings);
            panel.Controls.Add(grpStatus);
            page.Controls.Add(panel);
            return page;
        }

        private async void RefreshTaskStatus()
        {
            grpStatus.StatusValue = "刷新中...";
            grpStatus.StatusValueColor = SystemColors.ControlText;
            lblTaskDetail.Text = "正在查询计划任务状态...";
            lblTaskTriggers.Text = "";
            await System.Threading.Tasks.Task.Run(() => System.Threading.Thread.Sleep(50)); // 让 UI 先刷新

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
                BackColor = Color.White
            };
            grpLog.Controls.Add(txtChangelog);

            var btnRow = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 8)
            };
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var leftFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = Padding.Empty
            };
            btnUpdate = MkButton("立即更新", async (_, _) => await DoUpdate());
            btnSkip = MkButton("跳过此版本", (_, _) => SkipVersion());
            var btnRecheck = MkButton("重新检查更新", (_, _) => RecheckUpdates());
            leftFlow.Controls.Add(btnUpdate);
            leftFlow.Controls.Add(btnSkip);
            leftFlow.Controls.Add(btnRecheck);

            var rightFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty
            };
            var btnHome = MkButton("项目主页", (_, _) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ProjectRepoUrl,
                    UseShellExecute = true
                });
            });
            rightFlow.Controls.Add(btnHome);

            btnRow.Controls.Add(leftFlow, 0, 0);
            btnRow.Controls.Add(rightFlow, 2, 0);

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
                txtChangelog.Text = changelog ?? "";

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
            if (MessageBox.Show("更新将执行 git pull + npm install + 重新构建，期间脚本无法运行。继续？", "确认更新",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            var npmCmd = @"D:\Program Files\nodejs\npm.cmd";
            if (!File.Exists(npmCmd)) npmCmd = "npm.cmd";
            var cmd = $"git pull && \"{npmCmd}\" install && \"{npmCmd}\" run build";
            var win = new OutputWindow("正在更新 Microsoft Rewards Script...");
            win.Show(this);
            int code = await win.RunCommandAsync("cmd.exe", "/c " + cmd, ProjectPaths.Root);
            if (code == 0)
            {
                try { if (File.Exists(ProjectPaths.UpdateSkippedFile)) File.Delete(ProjectPaths.UpdateSkippedFile); } catch { }
                RecheckUpdates();
                MessageBox.Show("更新完成！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("更新失败，请查看输出窗口中的错误信息。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
