using System.IO;
using System.Text.Json;

namespace MultiplePagesBrowser.Models
{
    /// <summary>
    /// 保存/恢复上次关闭时各格子的 URL 和布局。
    /// 文件：%LocalAppData%\MultiplePagesBrowser\last_session.json
    /// </summary>
    public class SessionData
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiplePagesBrowser", "last_session.json");

        /// <summary>关闭时的布局索引（对应 MainViewModel.LayoutPresets）</summary>
        public int LayoutIndex { get; set; } = 2;

        /// <summary>各格子 URL（按格子序号排列）</summary>
        public List<string> Urls { get; set; } = new();

        public static SessionData Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<SessionData>(json) ?? new SessionData();
                }
            }
            catch { }
            return new SessionData();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this,
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public bool HasData => Urls.Any(u => u != "about:blank" && !string.IsNullOrEmpty(u));
    }
}
