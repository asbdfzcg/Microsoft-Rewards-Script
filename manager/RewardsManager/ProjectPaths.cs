using System;
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
            // package.json 是项目固有文件（开发/发布包都带），用它定位根目录，
            // 不再要求 config.json（用户首次运行才生成）。
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "package.json")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            // 回退：exe 位于 autorun/ 时取其父目录
            return Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
                   ?? AppContext.BaseDirectory;
        }
    }
}
