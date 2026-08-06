using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RewardsManager
{
    /// <summary>
    /// 环境检测与一键安装：Node.js / node_modules / dist / patchright 浏览器内核 / config.json
    /// </summary>
    internal static class EnvCheck
    {
        /// <summary>检测 Node.js（需 ≥24）。返回 (是否满足版本, 版本字符串, 可执行路径)</summary>
        public static (bool ok, string version, string path) CheckNode()
        {
            var r = ProcessHelper.Run("node.exe", "--version");
            if (r.exitCode != 0 || string.IsNullOrWhiteSpace(r.output))
                return (false, "", "");
            string raw = r.output.Trim();
            string v = raw.TrimStart('v');
            if (!Version.TryParse(v, out var ver))
                return (false, raw, "");
            bool ok = ver.Major >= 24;
            return (ok, raw, FindNodePath());
        }

        /// <summary>从 PATH 定位 node.exe 路径（供脚本使用）</summary>
        public static string FindNodePath()
        {
            var r = ProcessHelper.Run("where.exe", "node.exe");
            if (r.exitCode == 0 && !string.IsNullOrWhiteSpace(r.output))
            {
                foreach (var line in r.output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = line.Trim();
                    if (p.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase)) return p;
                }
            }
            return "node.exe";
        }

        public static bool HasNodeModules()
            => Directory.Exists(Path.Combine(ProjectPaths.Root, "node_modules"));

        public static bool HasDist()
            => File.Exists(Path.Combine(ProjectPaths.Root, "dist", "index.js"));

        /// <summary>检测 patchright 的 Chromium 是否已下载（依赖 node_modules 已安装）</summary>
        public static bool HasBrowser()
        {
            var r = ProcessHelper.Run("node.exe",
                "-e \"try{const{chromium}=require('patchright');process.stdout.write(chromium.executablePath())}catch(e){process.stdout.write('')}\"",
                ProjectPaths.Root, 15000);
            if (r.exitCode != 0 || string.IsNullOrWhiteSpace(r.output)) return false;
            var path = r.output.Trim();
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public static bool HasConfig()
            => File.Exists(ProjectPaths.ConfigFile);

        /// <summary>是否需要进行环境初始化（缺任何一项即返回 true）</summary>
        public static bool NeedsSetup()
        {
            if (!CheckNode().ok) return true;
            if (!HasNodeModules()) return true;
            if (!HasDist()) return true;
            if (HasNodeModules() && !HasBrowser()) return true;
            return false;
        }

        /// <summary>安装依赖 + 下载浏览器内核 + 构建，实时回传输出</summary>
        public static async Task<int> InstallDepsAsync(Action<string> onOutput)
        {
            onOutput(">>> npm install");
            int code = await ProcessHelper.RunWithOutputAsync("cmd.exe", Utf8Cmd("npm install"), ProjectPaths.Root, onOutput);
            if (code != 0) return code;
            onOutput(">>> npx patchright install chromium");
            code = await ProcessHelper.RunWithOutputAsync("cmd.exe", Utf8Cmd("npx patchright install chromium"), ProjectPaths.Root, onOutput);
            if (code != 0) return code;
            onOutput(">>> npm run build");
            code = await ProcessHelper.RunWithOutputAsync("cmd.exe", Utf8Cmd("npm run build"), ProjectPaths.Root, onOutput);
            return code;
        }

        /// <summary>把命令包装成「先切 UTF-8 代码页再执行」，避免中文系统 OEM 编码乱码</summary>
        private static string Utf8Cmd(string command) => $"/c chcp 65001 >nul && {command}";

        /// <summary>尝试用 winget 自动安装 Node.js（current 线，需 ≥24）。失败则提示手动安装。</summary>
        public static async Task<bool> InstallNodeAsync(Action<string> onOutput)
        {
            onOutput(">>> 使用 winget 安装 Node.js (current, 需 ≥24) ...");
            int code = await ProcessHelper.RunWithOutputAsync("cmd.exe",
                Utf8Cmd("winget install --id OpenJS.NodeJS -e --silent --accept-package-agreements --accept-source-agreements"),
                null, onOutput);
            if (code == 0) return true;
            onOutput("winget 安装失败。请手动从 https://nodejs.org 下载安装 Node.js >= 24，安装后重启本程序。");
            return false;
        }
    }
}
