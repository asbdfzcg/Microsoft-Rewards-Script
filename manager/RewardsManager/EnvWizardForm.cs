using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RewardsManager
{
    /// <summary>
    /// 首次启动的环境初始化向导：展示 Node/依赖/dist/浏览器/配置状态，
    /// 提供「安装依赖并构建」「安装/修复 Node」一键按钮，全部就绪后进入主界面。
    /// </summary>
    internal class EnvWizardForm : Form
    {
        private readonly TableLayoutPanel statusPanel;
        private readonly FlowLayoutPanel btnRow;
        private readonly Panel panelOut;
        private readonly RichTextBox txtOut;
        private readonly Button btnInstallDeps, btnInstallNode, btnEnter, btnCancel;
        private readonly bool _standalone;
        private readonly ToolTip toolTip = new ToolTip();
        private bool _busy;
        private CancellationTokenSource _cts;

        public EnvWizardForm(bool standalone = false)
        {
            _standalone = standalone;
            Text = "环境初始化";
            Width = 840;
            Height = 640;
            MinimumSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormClosing += (_, e) => { if (_busy && MessageBox.Show("安装正在进行中，确定要取消并退出吗？", "确认取消", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true; };

            // 状态区：顶部，自动根据内容撑高
            statusPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                Margin = new Padding(10, 12, 10, 8),
                Padding = Padding.Empty
            };
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280f));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            // 按钮行：底部，固定高度
            btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = false,
                WrapContents = false,
                Height = 44,
                Margin = new Padding(10, 8, 10, 10)
            };
            btnInstallDeps = new Button { Text = "安装依赖并构建", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 10, 0) };
            btnInstallNode = new Button { Text = "安装/修复 Node", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 10, 0) };
            btnCancel = new Button { Text = "取消", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 10, 0), Visible = false };
            btnEnter = new Button { Text = standalone ? "进入主界面" : "返回主界面", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Visible = !standalone };
            btnInstallDeps.Click += (_, _) => _ = DoInstallDeps();
            btnInstallNode.Click += (_, _) => _ = DoInstallNode();
            btnCancel.Click += (_, _) => _cts?.Cancel();
            btnEnter.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
            btnRow.Controls.Add(btnInstallDeps);
            btnRow.Controls.Add(btnInstallNode);
            btnRow.Controls.Add(btnCancel);
            btnRow.Controls.Add(btnEnter);

            // 输出区：Dock=Fill 占满状态区与按钮行之间的剩余空间
            panelOut = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(10, 0, 10, 0)
            };
            txtOut = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                Margin = Padding.Empty,
                BackColor = Color.White,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.ForcedBoth
            };
            panelOut.Controls.Add(txtOut);

            Controls.Add(panelOut);
            Controls.Add(statusPanel);
            Controls.Add(btnRow);

            RefreshStatus();
        }

        private void AppendOut(string s) => txtOut.AppendText(s + Environment.NewLine);

        private void RefreshStatus()
        {
            statusPanel.Controls.Clear();
            var node = EnvCheck.CheckNode();
            bool hasModules = EnvCheck.HasNodeModules();
            bool hasDist = EnvCheck.HasDist();
            bool hasBrowser = hasModules && EnvCheck.HasBrowser();
            bool hasConfig = EnvCheck.HasConfig();

            string nodeText;
            if (node.ok)
                nodeText = $"{node.version} ✓ ({node.path})";
            else if (string.IsNullOrEmpty(node.version))
                nodeText = "未安装";
            else
                nodeText = $"{node.version}（版本过低，需 ≥24）";
            AddStatus("Node.js (需 ≥24):", nodeText, node.ok);
            AddStatus("依赖 node_modules:", hasModules ? "已安装" : "缺失（需安装）", hasModules);
            AddStatus("构建产物 dist:", hasDist ? "已生成" : "缺失（需构建）", hasDist);
            AddStatus("浏览器内核 (chromium):", hasBrowser ? "已安装" : (hasModules ? "缺失（需下载）" : "依赖安装后检测"), hasBrowser);
            AddStatus("配置文件 config.json:", hasConfig ? "已存在" : "将由模板自动生成", hasConfig);

            bool allOk = node.ok && hasModules && hasDist && hasBrowser && hasConfig;
            if (!_busy)
                btnEnter.Enabled = allOk;
            btnInstallNode.Visible = !node.ok;
            btnInstallNode.Enabled = !_busy && !node.ok;

            // Node 没装好时不能点「安装依赖」
            btnInstallDeps.Enabled = node.ok && !_busy;
            if (!node.ok)
                toolTip.SetToolTip(btnInstallDeps, "请先点击「安装/修复 Node」安装 Node.js（需 ≥24）");
            else
                toolTip.SetToolTip(btnInstallDeps, "执行 npm install + 下载浏览器 + npm run build");

            // 独立模式（启动前拦截）：全部就绪后自动进入主界面
            if (_standalone && allOk && !_busy)
            {
                DialogResult = DialogResult.OK;
                BeginInvoke(new Action(Close));
            }
        }

        private void AddStatus(string label, string value, bool ok)
        {
            int row = statusPanel.RowCount++;
            statusPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lblName = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 2, 6, 2)
            };
            var lblValue = new Label
            {
                Text = value,
                AutoSize = true,
                AutoEllipsis = true,
                ForeColor = ok ? Color.DarkGreen : Color.DarkRed,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                Margin = new Padding(0, 2, 0, 2)
            };
            toolTip.SetToolTip(lblValue, value);
            statusPanel.Controls.Add(lblName, 0, row);
            statusPanel.Controls.Add(lblValue, 1, row);
        }

        private async Task DoInstallDeps()
        {
            if (_busy) return;
            var node = EnvCheck.CheckNode();
            if (!node.ok)
            {
                MessageBox.Show("请先安装 Node.js（需 ≥24）后再安装依赖。", "需要先安装 Node", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _cts = new CancellationTokenSource();
            _busy = true;
            btnInstallDeps.Enabled = false;
            btnInstallNode.Enabled = false;
            btnEnter.Enabled = false;
            btnCancel.Visible = true;
            txtOut.Clear();
            try
            {
                int code = await EnvCheck.InstallDepsAsync(AppendOut, _cts.Token);
                if (_cts.IsCancellationRequested)
                    AppendOut("=== 用户取消 ===");
                else
                    AppendOut(code == 0 ? "=== 依赖安装与构建完成 ===" : "=== 安装/构建失败，请查看上方输出 ===");
            }
            catch (OperationCanceledException) { AppendOut("=== 用户取消 ==="); }
            catch (Exception ex) { AppendOut("异常: " + ex.Message); }
            finally
            {
                _busy = false;
                btnCancel.Visible = false;
                _cts?.Dispose();
                _cts = null;
                RefreshStatus();
            }
        }

        private async Task DoInstallNode()
        {
            if (_busy) return;
            if (MessageBox.Show("将尝试使用 winget 自动安装 Node.js（当前版本需 ≥24）。\n若系统无 winget 则会提示手动安装。继续？",
                "安装 Node.js", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _cts = new CancellationTokenSource();
            _busy = true;
            btnInstallNode.Enabled = false;
            btnInstallDeps.Enabled = false;
            btnEnter.Enabled = false;
            btnCancel.Visible = true;
            txtOut.Clear();
            try
            {
                bool ok = await EnvCheck.InstallNodeAsync(AppendOut);
                if (_cts.IsCancellationRequested)
                    AppendOut("=== 用户取消 ===");
                else if (ok)
                    AppendOut("安装完成。请重启本程序以应用新的 Node.js，再继续安装依赖。");
            }
            catch (OperationCanceledException) { AppendOut("=== 用户取消 ==="); }
            catch (Exception ex) { AppendOut("异常: " + ex.Message); }
            finally
            {
                _busy = false;
                btnCancel.Visible = false;
                _cts?.Dispose();
                _cts = null;
                RefreshStatus();
            }
        }
    }
}
