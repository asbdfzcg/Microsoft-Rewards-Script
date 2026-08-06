using System;
using System.IO;
using System.Windows.Forms;

namespace RewardsManager
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
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
            Application.Run(new MainForm(initialTab, setGap, verify, verifySwitch));
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
