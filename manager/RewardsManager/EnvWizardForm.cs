using System;
using System.Drawing;
using System.IO;
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
        private readonly FlowLayoutPanel statusPanel;
        private readonly RichTextBox txtOut;
        private readonly Button btnInstallDeps, btnInstallNode, btnEnter;
        private readonly bool _standalone;
        private readonly ToolTip toolTip = new ToolTip();
        private bool _busy;

        public EnvWizardForm(bool standalone = false)
        {
            _standalone = standalone;
            Text = "环境初始化";
            Width = 660;
            Height = 540;
            MinimumSize = new Size(600, 450);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormClosing += (_, e) => { if (_busy) e.Cancel = true; };

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            statusPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(10, 10, 10, 0) };

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, Margin = new Padding(10, 6, 10, 10) };
            btnInstallDeps = new Button { Text = "安装依赖并构建", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 10, 0) };
            btnInstallNode = new Button { Text = "安装/修复 Node", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 10, 0) };
            btnEnter = new Button { Text = standalone ? "进入主界面" : "返回主界面", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Visible = !standalone };
            btnInstallDeps.Click += (_, _) => _ = DoInstallDeps();
            btnInstallNode.Click += (_, _) => _ = DoInstallNode();
            btnEnter.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
            btnRow.Controls.Add(btnInstallDeps);
            btnRow.Controls.Add(btnInstallNode);
            btnRow.Controls.Add(btnEnter);

            txtOut = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9F), Margin = new Padding(10, 0, 10, 0), BackColor = Color.White };

            root.Controls.Add(statusPanel, 0, 0);
            root.Controls.Add(txtOut, 0, 1);
            root.Controls.Add(btnRow, 0, 2);
            Controls.Add(root);

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

            string nodeText = node.ok
                ? node.version + " ✓"
                : (string.IsNullOrEmpty(node.version) ? "未安装" : node.version + "（版本过低，需 ≥24）");
            AddStatus("Node.js (需 ≥24):", nodeText, node.ok);
            AddStatus("依赖 node_modules:", hasModules ? "已安装" : "缺失（需安装）", hasModules);
            AddStatus("构建产物 dist:", hasDist ? "已生成" : "缺失（需构建）", hasDist);
            AddStatus("浏览器内核 (patchright chromium):", hasBrowser ? "已安装" : (hasModules ? "缺失（需下载）" : "依赖依赖安装后检测"), hasBrowser);
            AddStatus("配置文件 config.json:", hasConfig ? "已存在" : "将由模板自动生成", hasConfig);

            bool allOk = node.ok && hasModules && hasDist && hasBrowser && hasConfig;
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
            if (_standalone && allOk)
            {
                DialogResult = DialogResult.OK;
                BeginInvoke(new Action(Close));
            }
        }

        private void AddStatus(string label, string value, bool ok)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 240, Margin = new Padding(0, 4, 0, 0) });
            row.Controls.Add(new Label { Text = value, AutoSize = true, ForeColor = ok ? Color.DarkGreen : Color.DarkRed, Margin = new Padding(0, 4, 0, 0) });
            statusPanel.Controls.Add(row);
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
            _busy = true; btnInstallDeps.Enabled = false; btnInstallNode.Enabled = false;
            txtOut.Clear();
            try
            {
                int code = await EnvCheck.InstallDepsAsync(AppendOut);
                AppendOut(code == 0 ? "=== 依赖安装与构建完成 ===" : "=== 安装/构建失败，请查看上方输出 ===");
            }
            catch (Exception ex) { AppendOut("异常: " + ex.Message); }
            finally { _busy = false; RefreshStatus(); }
        }

        private async Task DoInstallNode()
        {
            if (_busy) return;
            if (MessageBox.Show("将尝试使用 winget 自动安装 Node.js（当前版本需 ≥24）。\n若系统无 winget 则会提示手动安装。继续？",
                "安装 Node.js", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _busy = true; btnInstallNode.Enabled = false; btnInstallDeps.Enabled = false;
            txtOut.Clear();
            try
            {
                bool ok = await EnvCheck.InstallNodeAsync(AppendOut);
                if (ok) AppendOut("安装完成。请重启本程序以应用新的 Node.js，再继续安装依赖。");
            }
            catch (Exception ex) { AppendOut("异常: " + ex.Message); }
            finally { _busy = false; RefreshStatus(); }
        }
    }
}
