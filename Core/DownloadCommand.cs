using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 外部工具命令行生成 —— 对标浏览器插件「猫抓(cat-catch)」的「发送到外部工具」能力：
    /// 把当前下载任务翻译成 ffmpeg / N_m3u8DL-RE / aria2c 三条现成命令，一键复制到剪贴板。
    ///
    /// 为什么有价值：内置下载器解不了的流（SAMPLE-AES、DRM、复杂重定向），用户可以把这条命令
    /// 直接丢给自己的工具链；调试源站问题的时候，一条命令也比截图描述清楚得多。
    ///
    /// ★ 注意：这里**只生成文本**，绝不自动执行 —— 免得"复制命令"变成"静默运行未知程序"。
    /// </summary>
    public static class DownloadCommand
    {
        public class Ctx
        {
            public string Url { get; set; } = "";
            public string SaveDir { get; set; } = "";
            public string SaveName { get; set; } = "";
            /// <summary>产物扩展名，含点（.ts / .mp4 / .aac）。</summary>
            public string Ext { get; set; } = ".ts";
            public Dictionary<string, string> Headers { get; set; } = new();
            public int Concurrency { get; set; } = 12;
            /// <summary>外部引擎（N_m3u8DL-RE.exe）路径；没有就写裸文件名。</summary>
            public string? EnginePath { get; set; }
        }

        /// <summary>ffmpeg：直接把 m3u8 拉成一个文件，-c copy 不重编码。</summary>
        public static string Ffmpeg(Ctx c)
        {
            var sb = new StringBuilder();
            sb.Append("ffmpeg -hide_banner");
            if (c.Headers.TryGetValue("User-Agent", out var ua))
                sb.Append($" -user_agent {Quote(ua)}");
            var hdr = HeaderBlock(c, skipUserAgent: true, newline: "\\r\\n");
            if (!string.IsNullOrEmpty(hdr))
                sb.Append($" -headers {Quote(hdr)}");
            sb.Append($" -i {Quote(c.Url)}");
            sb.Append(" -c copy");
            // 裸 TS 转 mp4 时音频需要走 aac_adtstoasc，否则部分播放器无声
            if (c.Ext == ".mp4") sb.Append(" -bsf:a aac_adtstoasc");
            sb.Append(" -y ").Append(Quote(OutPath(c)));
            return sb.ToString();
        }

        /// <summary>N_m3u8DL-RE：功能最全的 HLS/DASH 下载器，本项目 engine/ 目录就是给它准备的。</summary>
        public static string Nm3u8dlRe(Ctx c)
        {
            var exe = string.IsNullOrEmpty(c.EnginePath) ? "N_m3u8DL-RE" : c.EnginePath!;
            var sb = new StringBuilder();
            sb.Append(Quote(exe));
            sb.Append(' ').Append(Quote(c.Url));
            sb.Append(" --save-dir ").Append(Quote(c.SaveDir));
            sb.Append(" --save-name ").Append(Quote(c.SaveName));
            foreach (var kv in c.Headers)
                sb.Append(" --header ").Append(Quote($"{kv.Key}: {kv.Value}"));
            sb.Append($" --thread-count {Math.Clamp(c.Concurrency, 1, 64)}");
            sb.Append(" --auto-select");
            sb.Append(" -M ").Append(Quote(c.Ext == ".mp4" ? "format=mp4" : "format=ts"));
            sb.Append(" --no-log");
            return sb.ToString();
        }

        /// <summary>aria2c：多连接直下普通文件（mp4/flv），猫抓也是把它当外部下载器用的。</summary>
        public static string Aria2(Ctx c)
        {
            var sb = new StringBuilder();
            sb.Append("aria2c");
            sb.Append(" -x ").Append(Math.Clamp(c.Concurrency, 1, 16));
            sb.Append(" -s ").Append(Math.Clamp(c.Concurrency, 1, 16));
            sb.Append(" -d ").Append(Quote(c.SaveDir));
            sb.Append(" -o ").Append(Quote(c.SaveName + c.Ext));
            foreach (var kv in c.Headers)
                sb.Append(" -H ").Append(Quote($"{kv.Key}: {kv.Value}"));
            sb.Append(' ').Append(Quote(c.Url));
            return sb.ToString();
        }

        /// <summary>把三条命令一次给全，界面里以「工具：命令」的列表展示。</summary>
        public static List<(string tool, string cmd)> All(Ctx c) => new()
        {
            ("ffmpeg", Ffmpeg(c)),
            ("N_m3u8DL-RE", Nm3u8dlRe(c)),
            ("aria2c", Aria2(c)),
        };

        private static string OutPath(Ctx c) =>
            System.IO.Path.Combine(c.SaveDir, c.SaveName + c.Ext);

        private static string HeaderBlock(Ctx c, bool skipUserAgent, string newline)
        {
            var parts = new List<string>();
            foreach (var kv in c.Headers)
            {
                if (skipUserAgent && kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add($"{kv.Key}: {kv.Value}");
            }
            return string.Join(newline, parts);
        }

        /// <summary>Windows 命令行里参数一律用双引号包住；内部的双引号转义成 \"。</summary>
        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";
    }
}
