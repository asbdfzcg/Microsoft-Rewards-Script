using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RewardsManager
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 注册代码页提供器，否则 Encoding.GetEncoding(936/GBK) 会抛 NotSupportedException
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // 让 patchright/playwright 把浏览器下载到项目内 node_modules/.local-browsers，
            // 避免沙盒/便携环境或不同用户目录导致找不到浏览器。
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", "0");

            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogException("UnhandledException", e.ExceptionObject as Exception);
            Application.ThreadException += (_, e) => LogException("ThreadException", e.Exception);
            ApplicationConfiguration.Initialize();
            int initialTab = 0;
            int setGap = -1;
            bool verify = false;
            bool verifySwitch = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--tab" && i + 1 < args.Length && int.TryParse(args[i + 1], out int t))
                    initialTab = t;
                else if (args[i] == "--setgap" && i + 1 < args.Length && int.TryParse(args[i + 1], out int g))
                    setGap = g;
                else if (args[i] == "--verify")
                    verify = true;
                else if (args[i] == "--verify-switch")
                    verifySwitch = true;
            }

            // 自检/验证模式直接进主界面，不弹环境向导
            if (!verify && !verifySwitch && EnvCheck.NeedsSetup())
            {
                EnsureConfig();
                using var wizard = new EnvWizardForm(standalone: true);
                if (wizard.ShowDialog() != DialogResult.OK)
                    return; // 环境未就绪且用户关闭/取消，直接退出
            }

            Application.Run(new MainForm(initialTab, setGap, verify, verifySwitch));
        }

        /// <summary>若 config.json 不存在，从 config.example.json 复制一份</summary>
        private static void EnsureConfig()
        {
            try
            {
                if (!File.Exists(ProjectPaths.ConfigFile))
                {
                    var example = Path.Combine(ProjectPaths.Root, "config.example.json");
                    if (File.Exists(example))
                        File.Copy(example, ProjectPaths.ConfigFile, false);
                }
            }
            catch { }
        }

        private static void LogException(string kind, Exception ex)
        {
            try
            {
                var dir = Path.Combine("D:\\Users\\asbdf\\Documents\\Microsoft Rewards Script\\.workbuddy", "RewardsManager");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O} {kind}\n{ex}\n");
            }
            catch { }
        }
    }
}
