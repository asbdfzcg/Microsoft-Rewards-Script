using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RewardsManager
{
    /// <summary>命令执行输出窗口（用于更新过程实时显示）</summary>
    public class OutputWindow : Form
    {
        private readonly RichTextBox txtOutput;

        public OutputWindow(string title)
        {
            Text = title;
            Width = 800;
            Height = 520;
            MinimumSize = new Size(640, 400);
            StartPosition = FormStartPosition.CenterParent;
            txtOutput = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.LightGray,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.ForcedBoth
            };
            Controls.Add(txtOutput);
        }

        public async Task<int> RunCommandAsync(string fileName, string arguments, string workDir)
        {
            AppendLine($"> {fileName} {arguments}");
            AppendLine(new string('-', 60));
            int code = await ProcessHelper.RunWithOutputAsync(fileName, arguments, workDir,
                line => Invoke((Action)(() => AppendLine(line))));
            AppendLine(new string('-', 60));
            AppendLine(code == 0 ? "执行完成（退出码 0）" : $"执行失败（退出码 {code}）");
            return code;
        }

        private void AppendLine(string line)
        {
            txtOutput.AppendText(line + Environment.NewLine);
            txtOutput.SelectionStart = txtOutput.TextLength;
            txtOutput.ScrollToCaret();
        }

        /// <summary>供外部异步线程安全追加一行输出</summary>
        public void AppendSafe(string line)
        {
            if (txtOutput.InvokeRequired)
                Invoke((Action)(() => AppendLine(line)));
            else
                AppendLine(line);
        }
    }
}
