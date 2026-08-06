using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RewardsManager
{
    /// <summary>
    /// 环境检测与一键安装：Node.js / node_modules / dist / patchright 浏览器内核 / config.json
    /// </summary>
    internal static class EnvCheck
    {
        /// <summary>项目内便携 Node 目录（无需安装、无需管理员）</summary>
        public static string ProjectNodeDir => Path.Combine(ProjectPaths.Root, "tools", "node");

        /// <summary>项目内 node.exe 路径</summary>
        public static string ProjectNodeExe => Path.Combine(ProjectNodeDir, "node.exe");

        /// <summary>检测 Node.js（需 ≥24）。返回 (是否满足版本, 版本字符串, 可执行路径)</summary>
        public static (bool ok, string version, string path) CheckNode()
        {
            // 优先使用项目内便携 Node
            if (File.Exists(ProjectNodeExe))
            {
                var r = ProcessHelper.Run(ProjectNodeExe, "--version");
                if (r.exitCode == 0 && TryParseVersion(r.output, out var v, out var raw) && v.Major >= 24)
                    return (true, raw, ProjectNodeExe);
            }

            // 其次使用系统 PATH 里的 Node
            var sys = ProcessHelper.Run("node.exe", "--version");
            string sysRaw = null;
            if (sys.exitCode == 0 && TryParseVersion(sys.output, out var sv, out sysRaw) && sv.Major >= 24)
                return (true, sysRaw, FindNodePath());

            return (false, sysRaw ?? "", "");
        }

        private static bool TryParseVersion(string output, out Version version, out string raw)
        {
            version = null;
            raw = (output ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string v = raw.TrimStart('v');
            return Version.TryParse(v, out version);
        }

        /// <summary>从 PATH 或项目内定位 node.exe 路径（供脚本使用）</summary>
        public static string FindNodePath()
        {
            if (File.Exists(ProjectNodeExe)) return ProjectNodeExe;

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
            var node = FindNodePath();

            // 1. 优先检查项目内（配合 PLAYWRIGHT_BROWSERS_PATH=0，沙盒/便携环境用）
            var r = ProcessHelper.Run(node,
                "-e \"try{process.env.PLAYWRIGHT_BROWSERS_PATH='0';const{chromium}=require('patchright');process.stdout.write(chromium.executablePath())}catch(e){process.stdout.write('')}\"",
                ProjectPaths.Root, 15000);
            if (r.exitCode == 0 && !string.IsNullOrWhiteSpace(r.output))
            {
                var path = r.output.Trim();
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return true;
            }

            // 2. 再检查系统默认位置（本地用户已安装到用户目录/.cache 等）
            r = ProcessHelper.Run(node,
                "-e \"try{const{chromium}=require('patchright');process.stdout.write(chromium.executablePath())}catch(e){process.stdout.write('')}\"",
                ProjectPaths.Root, 15000);
            if (r.exitCode == 0 && !string.IsNullOrWhiteSpace(r.output))
            {
                var path = r.output.Trim();
                return !string.IsNullOrEmpty(path) && File.Exists(path);
            }
            return false;
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
        public static async Task<int> InstallDepsAsync(Action<string> onOutput, CancellationToken cancellationToken = default)
        {
            var node = FindNodePath();
            var nodeDir = Path.GetDirectoryName(node);
            var npm = Path.Combine(nodeDir, "npm.cmd");
            if (!File.Exists(npm)) npm = "npm.cmd";

            // 把 node.exe 所在目录临时加到 PATH 最前，确保 npx/npm 脚本能直接调用 node
            var pathPrepend = Directory.Exists(nodeDir) ? nodeDir : null;

            // 非交互 + 把浏览器下载到项目内，避免沙盒/便携环境丢失
            var extraEnv = new Dictionary<string, string>
            {
                ["CI"] = "true",
                ["npm_config_yes"] = "true",
                ["PLAYWRIGHT_BROWSERS_PATH"] = "0"
            };

            onOutput(">>> npm install");
            int code = await ProcessHelper.RunWithOutputAsync(
                "cmd.exe", $"/c chcp 65001 >nul & \"{npm}\" install", ProjectPaths.Root, onOutput, pathPrepend, useUtf8: true, extraEnv, cancellationToken);
            if (code != 0) return code;

            // 直接用 node 跑 patchright/cli.js，绕过 npm exec 在 Node 24 上可能 spawn .cmd 的兼容问题
            string patchrightCli = Path.Combine(ProjectPaths.Root, "node_modules", "patchright", "cli.js");
            onOutput(">>> 下载浏览器内核（patchright chromium），可能需要 1-5 分钟，请耐心等待...");
            code = await ProcessHelper.RunWithOutputAsync(
                "cmd.exe", $"/c chcp 65001 >nul & \"{node}\" \"{patchrightCli}\" install chromium",
                ProjectPaths.Root, onOutput, pathPrepend, useUtf8: true, extraEnv, cancellationToken);
            if (code != 0) return code;

            onOutput(">>> npm run build");
            code = await ProcessHelper.RunWithOutputAsync(
                "cmd.exe", $"/c chcp 65001 >nul & \"{npm}\" run build", ProjectPaths.Root, onOutput, pathPrepend, useUtf8: true, extraEnv, cancellationToken);
            return code;
        }

        /// <summary>
        /// 尝试自动安装 Node.js（≥24）。优先 winget；沙盒/无 winget 环境则下载便携 zip 解压到 tools/node。
        /// </summary>
        public static async Task<bool> InstallNodeAsync(Action<string> onOutput)
        {
            // 1. 尝试 winget（若可用）
            if (await HasWingetAsync())
            {
                onOutput(">>> 使用 winget 安装 Node.js (current, 需 ≥24) ...");
                int code = await ProcessHelper.RunWithOutputAsync("cmd.exe",
                    "/c winget install --id OpenJS.NodeJS -e --silent --accept-package-agreements --accept-source-agreements",
                    null, onOutput);
                if (code == 0) return true;
                onOutput("winget 安装失败，尝试下载便携版 Node.js ...");
            }
            else
            {
                onOutput(">>> 未检测到 winget，将下载便携版 Node.js（无需管理员）...");
            }

            // 2. 下载便携 zip 到 tools/node
            return await InstallPortableNodeAsync(onOutput);
        }

        private static async Task<bool> HasWingetAsync()
        {
            var r = await Task.Run(() => ProcessHelper.Run("cmd.exe", "/c winget --version", null, 10000));
            return r.exitCode == 0 && !string.IsNullOrWhiteSpace(r.output);
        }

        private static async Task<bool> InstallPortableNodeAsync(Action<string> onOutput)
        {
            try
            {
                string zipUrl = await GetLatestNodeZipUrlAsync();
                if (string.IsNullOrEmpty(zipUrl))
                {
                    onOutput("无法从 nodejs.org 获取最新 Node.js v24 下载地址。请手动安装。");
                    return false;
                }

                onOutput($">>> 下载 {zipUrl}");
                string tempZip = Path.Combine(Path.GetTempPath(), $"node-portable-{Guid.NewGuid()}.zip");
                Directory.CreateDirectory(ProjectNodeDir);

                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var response = await client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? -1;
                    var stream = await response.Content.ReadAsStreamAsync();
                    var buffer = new byte[8192];
                    long read = 0;
                    var lastReport = DateTime.MinValue;
                    int n;
                    while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, n);
                        read += n;
                        if (total > 0 && (DateTime.Now - lastReport).TotalSeconds >= 1.0)
                        {
                            lastReport = DateTime.Now;
                            onOutput($"    已下载 {read / (1024.0 * 1024.0):F2} / {total / (1024.0 * 1024.0):F2} MB");
                        }
                    }
                    if (total > 0)
                        onOutput($"    已下载 {total / (1024.0 * 1024.0):F2} / {total / (1024.0 * 1024.0):F2} MB");
                }

                onOutput($">>> 解压到 {ProjectNodeDir}");
                string extractTemp = Path.Combine(Path.GetTempPath(), $"node-extract-{Guid.NewGuid()}");
                ZipFile.ExtractToDirectory(tempZip, extractTemp);

                // zip 内根目录是 node-vXX.X.X-win-x64，需要把它里面的内容移到 ProjectNodeDir
                var inner = Directory.GetDirectories(extractTemp).FirstOrDefault();
                if (inner != null)
                {
                    foreach (var entry in Directory.GetFileSystemEntries(inner))
                    {
                        string dest = Path.Combine(ProjectNodeDir, Path.GetFileName(entry));
                        if (Directory.Exists(entry))
                        {
                            if (Directory.Exists(dest)) Directory.Delete(dest, true);
                            Directory.Move(entry, dest);
                        }
                        else
                        {
                            if (File.Exists(dest)) File.Delete(dest);
                            File.Move(entry, dest);
                        }
                    }
                }

                Directory.Delete(extractTemp, true);
                File.Delete(tempZip);

                if (File.Exists(ProjectNodeExe))
                {
                    var v = ProcessHelper.Run(ProjectNodeExe, "--version");
                    onOutput($"便携 Node.js 已就绪：{v.output.Trim()} 路径：{ProjectNodeExe}");
                    return true;
                }
                onOutput("解压后未找到 node.exe。请手动安装 Node.js >= 24。");
                return false;
            }
            catch (Exception ex)
            {
                onOutput($"下载/解压失败：{ex.Message}");
                return false;
            }
        }

        private static async Task<string> GetLatestNodeZipUrlAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                string json = await client.GetStringAsync("https://nodejs.org/dist/index.json");
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string ver = item.GetProperty("version").GetString();
                    if (ver.StartsWith("v24.", StringComparison.OrdinalIgnoreCase))
                        return $"https://nodejs.org/dist/{ver}/node-{ver}-win-x64.zip";
                }
            }
            catch { }
            return null;
        }
    }
}
