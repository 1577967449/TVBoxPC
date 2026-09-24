using System.IO;
using System.Text.Json;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 应用设置：上次配置、聚合搜索参数、代理、下载设置。
    /// 播放固定走内置 LibVLC（外部播放器支持已移除）。
    /// 保存在程序目录 settings.json。
    /// </summary>
    public class AppSettings
    {
        public string? LastConfig { get; set; }
        /// <summary>聚合搜索并发站点数上限。</summary>
        public int AggLimit { get; set; } = 30;
        /// <summary>聚合搜索里「单个源最多等几秒」，到点就当它没结果（默认 8 秒）。</summary>
        public int AggTimeoutSeconds { get; set; } = 8;
        /// <summary>是否使用系统代理（系统里若有失效代理可关掉改为直连）。</summary>
        public bool UseSystemProxy { get; set; } = true;
        /// <summary>下载保存目录。留空则用「用户目录\Downloads\TVBoxPC」。</summary>
        public string? DownloadDir { get; set; }
        /// <summary>下载并发分段数（默认 12）。网络差可调小。</summary>
        public int DownloadConcurrency { get; set; } = 12;

        /// <summary>解析出实际使用的下载根目录。</summary>
        public string ResolvedDownloadDir =>
            string.IsNullOrWhiteSpace(DownloadDir)
                ? System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                    "Downloads", "TVBoxPC")
                : DownloadDir!.Trim();

        private static readonly string Path_ = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");

        public static AppSettings Load()
        {
            try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new(); }
            catch { return new(); }
        }

        public void Save()
        {
            try { File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }
    }
}
