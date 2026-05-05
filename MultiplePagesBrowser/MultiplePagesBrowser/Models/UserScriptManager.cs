using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiplePagesBrowser.Models
{
    /// <summary>
    /// 用户脚本管理器（类 Tampermonkey 轻量实现）。
    ///
    /// 脚本目录：%LocalAppData%\MultiplePagesBrowser\UserScripts\
    ///   每个 .js 文件 = 一个用户脚本
    ///
    /// 启用状态存储在：%LocalAppData%\MultiplePagesBrowser\userscripts_config.json
    ///   （不修改 .js 文件本身，保持脚本内容纯净）
    ///
    /// 脚本头部支持如下元数据注释（// @key value）：
    ///   // @name        脚本名称
    ///   // @version     版本
    ///   // @description 描述
    ///   // @match       https://example.com/*   （可多行，支持 * 通配符）
    ///   // @include     同 @match
    ///   // @exclude     排除的 URL
    ///   // @run-at      document_start | document_end（默认 document_end）
    /// </summary>
    public static class UserScriptManager
    {
        public static readonly string ScriptsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiplePagesBrowser", "UserScripts");

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiplePagesBrowser", "userscripts_config.json");

        private static readonly Regex MetaPattern =
            new(@"//\s*@(\w[\w-]*)\s+(.+)", RegexOptions.Multiline);

        // 内存中的启用状态：key = 文件名（不含路径），value = enabled
        private static Dictionary<string, bool> _enabledMap = new();

        // ── 配置读写 ──────────────────────────────────────────────

        public static void LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return;
            try
            {
                var json = File.ReadAllText(ConfigPath);
                _enabledMap = JsonSerializer.Deserialize<Dictionary<string, bool>>(json)
                              ?? new Dictionary<string, bool>();
            }
            catch { _enabledMap = new(); }
        }

        public static void SaveConfig()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(_enabledMap,
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        public static bool IsEnabled(string fileName)
            // 默认启用
            => !_enabledMap.TryGetValue(fileName, out bool v) || v;

        public static void SetEnabled(string fileName, bool enabled)
        {
            _enabledMap[fileName] = enabled;
            SaveConfig();
        }

        // ── 脚本查询 ──────────────────────────────────────────────

        /// <summary>加载所有已安装脚本（含启用状态）</summary>
        public static List<UserScript> GetAllScripts()
        {
            LoadConfig();
            var list = new List<UserScript>();
            if (!Directory.Exists(ScriptsRoot)) return list;

            foreach (var file in Directory.EnumerateFiles(ScriptsRoot, "*.js"))
            {
                try
                {
                    var script = ParseScript(file);
                    if (script != null) list.Add(script);
                }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// 返回应注入到指定 URL 的所有脚本代码。
        /// runAt: "document_start" 或 "document_end"
        /// </summary>
        public static List<UserScript> GetMatchingScripts(string url, string runAt = "document_end")
        {
            return GetAllScripts()
                .Where(s => s.Enabled &&
                            s.RunAt == runAt &&
                            s.MatchesUrl(url))
                .ToList();
        }

        /// <summary>解析 .js 文件为 UserScript 对象</summary>
        public static UserScript? ParseScript(string filePath)
        {
            string source = File.ReadAllText(filePath);
            string fileName = Path.GetFileName(filePath);
            var script = new UserScript
            {
                FilePath = filePath,
                FileName = fileName,
                Name     = Path.GetFileNameWithoutExtension(filePath),
                Source   = source,
                // 启用状态从配置文件读取，不依赖脚本内容
                Enabled  = IsEnabled(fileName)
            };

            foreach (Match m in MetaPattern.Matches(source))
            {
                string key = m.Groups[1].Value.ToLowerInvariant();
                string val = m.Groups[2].Value.Trim();
                switch (key)
                {
                    case "name":        script.Name        = val; break;
                    case "version":     script.Version     = val; break;
                    case "description": script.Description = val; break;
                    case "match":
                    case "include":     script.MatchPatterns.Add(val); break;
                    case "exclude":     script.ExcludePatterns.Add(val); break;
                    case "run-at":      script.RunAt = val.Replace("-", "_"); break;
                    // @enabled 仍可作为默认值（仅首次，之后以配置文件为准）
                    case "enabled":
                        if (!_enabledMap.ContainsKey(fileName))
                            script.Enabled = val.ToLowerInvariant() != "false";
                        break;
                }
            }
            return script;
        }

        /// <summary>打开脚本目录（供用户手动放入脚本）</summary>
        public static void OpenScriptsFolder()
        {
            Directory.CreateDirectory(ScriptsRoot);
            System.Diagnostics.Process.Start("explorer.exe", ScriptsRoot);
        }

        /// <summary>写入一个示例脚本（首次使用时生成）</summary>
        public static void CreateExampleScripts()
        {
            Directory.CreateDirectory(ScriptsRoot);

            string hideAds = Path.Combine(ScriptsRoot, "HideAds.js");
            if (!File.Exists(hideAds))
                File.WriteAllText(hideAds, """
// ==UserScript==
// @name        Hide Ads（示例）
// @version     1.0
// @description 隐藏常见广告容器（仅作演示）
// @match       *://*/*
// @run-at      document_end
// ==/UserScript==

(function () {
  const selectors = [
    '[class*="ad-"]','[class*="-ad"]','[id*="ad-banner"]',
    '[class*="advertisement"]','ins.adsbygoogle'
  ];
  const style = document.createElement('style');
  style.textContent = selectors.join(',') + '{ display:none!important; }';
  document.head.appendChild(style);
})();
""");

            string darkMode = Path.Combine(ScriptsRoot, "DarkMode.js");
            if (!File.Exists(darkMode))
            {
                File.WriteAllText(darkMode, """
// ==UserScript==
// @name        Force Dark Mode（示例）
// @version     1.0
// @description 强制所有网页使用暗色调
// @match       *://*/*
// @run-at      document_start
// ==/UserScript==

(function () {
  const s = document.createElement('style');
  s.textContent = 'html{filter:invert(1) hue-rotate(180deg)!important}'+
                  'img,video,canvas,picture{filter:invert(1) hue-rotate(180deg)!important}';
  document.documentElement.appendChild(s);
})();
""");
                // 示例脚本默认禁用
                SetEnabled(Path.GetFileName(darkMode), false);
            }
        }
    }

    public class UserScript
    {
        public string FilePath    { get; set; } = string.Empty;
        public string FileName    { get; set; } = string.Empty;
        public string Name        { get; set; } = string.Empty;
        public string Version     { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Source      { get; set; } = string.Empty;
        public string RunAt       { get; set; } = "document_end";
        public bool   Enabled     { get; set; } = true;
        public List<string> MatchPatterns   { get; } = new();
        public List<string> ExcludePatterns { get; } = new();

        /// <summary>判断当前脚本是否匹配给定 URL</summary>
        public bool MatchesUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url == "about:blank") return false;

            // 有排除规则时先检查排除
            if (ExcludePatterns.Any(p => GlobMatch(p, url))) return false;

            // 无 match/include 规则 → 匹配全部
            if (MatchPatterns.Count == 0) return true;

            return MatchPatterns.Any(p => GlobMatch(p, url));
        }

        /// <summary>将 @match/@include 通配符转成正则进行匹配</summary>
        private static bool GlobMatch(string pattern, string url)
        {
            // 特殊模式
            if (pattern == "*" || pattern == "*://*/*") return true;

            // 转义特殊字符，然后将 * 替换为 .*
            string regex = "^" +
                Regex.Escape(pattern)
                     .Replace(@"\*\://", @"[a-z]+://")  // *:// → 任意协议
                     .Replace(@"\*", @"[^/]*")           // 单段 *
                     .Replace(@"\*\*", @".*")            // ** → 跨路径通配
                + "$";
            return Regex.IsMatch(url, regex, RegexOptions.IgnoreCase);
        }
    }
}
