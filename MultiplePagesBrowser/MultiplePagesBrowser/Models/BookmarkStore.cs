using System.IO;
using System.Text.Json;

namespace MultiplePagesBrowser.Models
{
    /// <summary>
    /// 全局共享书签存储（所有格子共用同一份书签列表）。
    /// 书签文件：%LocalAppData%\MultiplePagesBrowser\bookmarks.json
    /// </summary>
    public static class BookmarkStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiplePagesBrowser", "bookmarks.json");

        private static List<Bookmark> _items = new();

        public static IReadOnlyList<Bookmark> Items => _items;

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    _items = JsonSerializer.Deserialize<List<Bookmark>>(json) ?? new();
                    return;
                }
            }
            catch { }
            _items = new();
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(_items,
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static bool Contains(string url) =>
            _items.Any(b => b.Url == url);

        /// <summary>添加书签；若已存在同 URL 则忽略</summary>
        public static void Add(string url, string title)
        {
            if (Contains(url)) return;
            _items.Insert(0, new Bookmark { Url = url, Title = title, AddedAt = DateTime.Now });
            Save();
        }

        public static void Remove(string url)
        {
            _items.RemoveAll(b => b.Url == url);
            Save();
        }

        public static void Clear()
        {
            _items.Clear();
            Save();
        }
    }
}
