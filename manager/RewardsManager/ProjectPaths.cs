using System;
using System.Diagnostics;
using System.IO;

namespace RewardsManager
{
    /// <summary>定位项目根目录（向上查找含 package.json 的目录；发布包/便携场景无 config.json 也能定位）</summary>
    internal static class ProjectPaths
    {
        public static string Root { get; } = FindRoot();
        public static string AutorunDir => Path.Combine(Root, "autorun");
        public static string LogsDir => Path.Combine(AutorunDir, "logs");
        public static string ConfigFile => Path.Combine(Root, "config.json");
        public static string EnvFile => Path.Combine(Root, ".env");
        public static string PackageJson => Path.Combine(Root, "package.json");
        public static string UpdateStatusFile => Path.Combine(AutorunDir, "update-status.json");
        public static string UpdateSkippedFile => Path.Combine(AutorunDir, "update-skipped.json");

        private static string FindRoot()
        {
            // 用进程主模块路径作为起点（单文件发布时 AppContext.BaseDirectory 是临时解压目录，不可靠）。
            // 从 exe 所在目录向上查找 package.json，即可兼容开发、发布包、便携运行等各种场景。
            string exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
                exePath = AppContext.BaseDirectory;

            var startDir = Path.GetDirectoryName(exePath);
            var dir = string.IsNullOrEmpty(startDir) ? null : new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "package.json")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            // 回退：exe 位于 autorun/ 时取其父目录
            return Directory.GetParent(exePath.TrimEnd(Path.DirectorySeparatorChar))?.FullName
                   ?? Path.GetDirectoryName(exePath)
                   ?? AppContext.BaseDirectory;
        }
    }
}
