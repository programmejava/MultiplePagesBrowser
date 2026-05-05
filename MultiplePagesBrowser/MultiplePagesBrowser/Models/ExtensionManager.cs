using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MultiplePagesBrowser.Models
{
    /// <summary>
    /// Chrome 扩展管理器。
    /// 扩展目录：%LocalAppData%\MultiplePagesBrowser\Extensions\
    ///   每个子文件夹 = 一个解压后的 Chrome 扩展（包含 manifest.json）
    /// 启用状态存储在：%LocalAppData%\MultiplePagesBrowser\extensions_config.json
    /// </summary>
    public static class ExtensionManager
    {
        public static readonly string ExtensionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiplePagesBrowser", "Extensions");

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiplePagesBrowser", "extensions_config.json");

        // 内存中的启用状态表：key = FolderName，value = enabled
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

        public static bool IsEnabled(string folderName)
            // 默认启用（首次出现的扩展视为启用）
            => !_enabledMap.TryGetValue(folderName, out bool v) || v;

        public static void SetEnabled(string folderName, bool enabled)
        {
            _enabledMap[folderName] = enabled;
            SaveConfig();
        }

        // ── 加载扩展到 WebView2 ───────────────────────────────────

        public static async Task LoadExtensionsAsync(CoreWebView2 webView)
        {
            if (!Directory.Exists(ExtensionsRoot)) return;
            LoadConfig();

            foreach (var dir in Directory.EnumerateDirectories(ExtensionsRoot))
            {
                string manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest)) continue;
                if (!IsEnabled(Path.GetFileName(dir))) continue;

                try
                {
                    await webView.Profile.AddBrowserExtensionAsync(dir);
                }
                catch { /* 扩展不兼容或已加载，忽略 */ }
            }
        }

        // ── 查询已安装扩展 ────────────────────────────────────────

        public static List<ExtensionInfo> GetInstalledExtensions()
        {
            LoadConfig();
            var result = new List<ExtensionInfo>();
            if (!Directory.Exists(ExtensionsRoot)) return result;

            foreach (var dir in Directory.EnumerateDirectories(ExtensionsRoot))
            {
                string manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest)) continue;

                string folderName = Path.GetFileName(dir);
                string name = folderName;
                string version = string.Empty;
                string description = string.Empty;

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("name", out var n))
                        name = n.GetString() ?? name;
                    if (doc.RootElement.TryGetProperty("version", out var v))
                        version = v.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("description", out var d))
                        description = d.GetString() ?? string.Empty;
                }
                catch { }

                result.Add(new ExtensionInfo
                {
                    FolderName  = folderName,
                    FolderPath  = dir,
                    Name        = name,
                    Version     = version,
                    Description = description,
                    Enabled     = IsEnabled(folderName)
                });
            }
            return result;
        }

        public static void OpenExtensionsFolder()
        {
            Directory.CreateDirectory(ExtensionsRoot);
            System.Diagnostics.Process.Start("explorer.exe", ExtensionsRoot);
        }
    }

    public class ExtensionInfo
    {
        public string FolderName  { get; set; } = string.Empty;
        public string FolderPath  { get; set; } = string.Empty;
        public string Name        { get; set; } = string.Empty;
        public string Version     { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool   Enabled     { get; set; } = true;
    }
}
