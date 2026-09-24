using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TVBoxPC.Core
{
    /// <summary>播放历史 / 收藏 的一个条目。</summary>
    public class HistoryItem
    {
        public string SiteKey { get; set; } = "";
        public string SiteName { get; set; } = "";
        public string VodId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Pic { get; set; } = "";
        public string EpisodeName { get; set; } = "";
        public string EpisodeUrl { get; set; } = "";
        public DateTime Time { get; set; } = DateTime.Now;

        public string Key => SiteKey + "|" + VodId;
    }

    /// <summary>
    /// 历史与收藏的持久化（history.json 与 favorites.json，放在程序目录）。
    /// 历史按「同一站点同一影片」去重并置顶，最多保留 300 条。
    /// </summary>
    public class HistoryStore
    {
        private static readonly string BaseDir = AppContext.BaseDirectory;
        private static readonly string HistoryPath = Path.Combine(BaseDir, "history.json");
        private static readonly string FavPath = Path.Combine(BaseDir, "favorites.json");

        public List<HistoryItem> History { get; private set; } = new();
        public List<HistoryItem> Favorites { get; private set; } = new();

        private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        public HistoryStore() { Load(); }

        public void Load()
        {
            History = Read(HistoryPath);
            Favorites = Read(FavPath);
        }

        private static List<HistoryItem> Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return new();
                return JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(path)) ?? new();
            }
            catch { return new(); }
        }

        private static void Write(string path, List<HistoryItem> list)
        {
            try { File.WriteAllText(path, JsonSerializer.Serialize(list, Opts)); } catch { }
        }

        public void AddHistory(HistoryItem item)
        {
            History.RemoveAll(x => x.Key == item.Key);
            History.Insert(0, item);
            if (History.Count > 300) History = History.Take(300).ToList();
            Write(HistoryPath, History);
        }

        public bool IsFavorite(string siteKey, string vodId) =>
            Favorites.Any(x => x.SiteKey == siteKey && x.VodId == vodId);

        /// <summary>切换收藏，返回切换后是否已收藏。</summary>
        public bool ToggleFavorite(HistoryItem item)
        {
            var exist = Favorites.FirstOrDefault(x => x.Key == item.Key);
            bool nowFav;
            if (exist != null) { Favorites.Remove(exist); nowFav = false; }
            else { Favorites.Insert(0, item); nowFav = true; }
            Write(FavPath, Favorites);
            return nowFav;
        }

        public void RemoveFavorite(string siteKey, string vodId)
        {
            Favorites.RemoveAll(x => x.SiteKey == siteKey && x.VodId == vodId);
            Write(FavPath, Favorites);
        }

        public void ClearHistory()
        {
            History.Clear();
            Write(HistoryPath, History);
        }
    }
}
