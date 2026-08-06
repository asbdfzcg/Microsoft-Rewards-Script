using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
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
        private static Encoding ConsoleEncoding
        {
            get
            {
                try
                {
                    return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                }
                catch
                {
                    // 代码页不可用（如精简运行时缺 GBK），退回到 UTF-8，保证不崩溃
                    return Encoding.UTF8;
                }
            }
        }

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

        /// <summary>
        /// 异步运行命令并实时输出到回调（用于更新窗口）。
        /// </summary>
        /// <param name="pathPrepend">临时插入 PATH 最前面的目录（例如便携 Node 目录），让子进程能找到同目录下的 exe</param>
        /// <param name="useUtf8">为 true 时先执行 chcp 65001 并改用 UTF-8 读取，适合 npm/node 命令；false 时使用系统 OEM 编码</param>
        /// <param name="extraEnv">需要额外设置的环境变量键值对</param>
        /// <param name="cancellationToken">取消令牌，触发时终止子进程</param>
        public static async Task<int> RunWithOutputAsync(
            string fileName,
            string arguments,
            string workDir,
            Action<string> onOutput,
            string pathPrepend = null,
            bool useUtf8 = false,
            Dictionary<string, string> extraEnv = null,
            CancellationToken cancellationToken = default)
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
                StandardOutputEncoding = useUtf8 ? Encoding.UTF8 : ConsoleEncoding,
                StandardErrorEncoding = useUtf8 ? Encoding.UTF8 : ConsoleEncoding
            };

            if (!string.IsNullOrWhiteSpace(pathPrepend))
            {
                var currentPath = psi.EnvironmentVariables.ContainsKey("PATH")
                    ? psi.EnvironmentVariables["PATH"]
                    : Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.EnvironmentVariables["PATH"] = pathPrepend.TrimEnd(';') + ";" + currentPath;
            }

            if (extraEnv != null)
            {
                foreach (var kv in extraEnv)
                {
                    if (kv.Value == null)
                        psi.EnvironmentVariables.Remove(kv.Key);
                    else
                        psi.EnvironmentVariables[kv.Key] = kv.Value;
                }
            }

            using var p = Process.Start(psi);
            p.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (cancellationToken.CanBeCanceled)
            {
                await using (cancellationToken.Register(() =>
                {
                    try { if (!p.HasExited) p.Kill(true); } catch { }
                }))
                {
                    await p.WaitForExitAsync(cancellationToken);
                }
            }
            else
            {
                await p.WaitForExitAsync();
            }
            return p.ExitCode;
        }
    }
}
