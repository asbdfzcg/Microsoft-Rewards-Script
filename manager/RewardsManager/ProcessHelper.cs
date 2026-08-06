using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RewardsManager
{
    /// <summary>进程辅助：运行外部命令并捕获输出</summary>
    internal static class ProcessHelper
    {
        /// <summary>
        /// 使用系统 OEM 代码页读取子进程输出。
        /// cmd.exe 的内部消息（如“不是内部或外部命令”）按 OEM 编码输出；
        /// 在中文 Windows 下 OEM=GBK，若按 UTF-8 读取会乱码。
        /// </summary>
        private static Encoding ConsoleEncoding =>
            Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

        /// <summary>同步运行命令，返回 (退出码, 输出)</summary>
        public static (int exitCode, string output) Run(string fileName, string arguments, string workDir = null, int timeoutMs = 30000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workDir ?? ProjectPaths.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };
            try
            {
                using var p = Process.Start(psi);
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(timeoutMs);
                return (p.ExitCode, (stdout + Environment.NewLine + stderr).Trim());
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        /// <summary>以管理员身份运行 PowerShell 脚本/命令（触发 UAC）</summary>
        public static void RunElevated(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true
            };
            Process.Start(psi);
        }

        /// <summary>异步运行命令并实时输出到回调（用于更新窗口）</summary>
        public static async Task<int> RunWithOutputAsync(string fileName, string arguments, string workDir, Action<string> onOutput)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };
            using var p = Process.Start(psi);
            p.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
    }
}
