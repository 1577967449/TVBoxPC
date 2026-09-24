using System;
using System.Collections.Generic;
using System.Text;

namespace TVBoxPC.Core
{
    /// <summary>播放地址的性质。</summary>
    public enum MediaKind
    {
        /// <summary>可直接交给播放器（http/https/rtsp/rtmp/ftp/file…）。</summary>
        Direct,
        /// <summary>网页地址（.html 之类），要靠「解析接口」才能拿到真实流 —— 本程序不解。</summary>
        Page,
        /// <summary>网盘/磁力/推送等协议，播放器都放不了。</summary>
        Unsupported,
    }

    /// <summary>一条播放地址解析后的结果。</summary>
    public sealed class MediaTarget
    {
        /// <summary>真正的媒体地址（已剥掉 | 后面的头参数）。</summary>
        public string Url = "";
        /// <summary>随地址一起带来的请求头（TVBox 约定：url|User-Agent=x&Referer=y）。</summary>
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public MediaKind Kind = MediaKind.Direct;
        /// <summary>给用户看的原因说明（Kind != Direct 时必有）。</summary>
        public string? Note;

        public string? Referer => Headers.TryGetValue("Referer", out var v) ? v : null;
        public string? UserAgent => Headers.TryGetValue("User-Agent", out var v) ? v : null;
        public bool HasHeaders => Headers.Count > 0;
    }

    /// <summary>
    /// 解析 TVBox 的播放地址串。**不要**直接把原始串丢给 `new Uri(...)`：
    ///   1) 很多源返回 `http://xx/index.m3u8|User-Agent=Mozilla/5.0&amp;Referer=http://yy/`
    ///      —— `new Uri` 会**原样保留那个竖线**，VLC/PotPlayer 于是去请求一个含 `|` 的路径，必然失败；
    ///   2) `magnet:` `ed2k:` `push://` 这类协议 `new Uri` 不会报错，但播放器放不了，
    ///      之前是静默黑屏，用户完全不知道发生了什么；
    ///   3) 少数源给相对路径（`/api/x.m3u8`），需要按站点基址补全。
    /// 本类把上面三种情况都显式区分出来。
    /// </summary>
    public static class MediaUrl
    {
        private static readonly HashSet<string> DirectSchemes = new(StringComparer.OrdinalIgnoreCase)
        { "http", "https", "rtsp", "rtmps", "rtmp", "rtp", "udp", "ftp", "file", "mms", "srt", "rtp" };

        private static readonly HashSet<string> UnsupportedSchemes = new(StringComparer.OrdinalIgnoreCase)
        { "magnet", "ed2k", "thunder", "push", "ppc", "pikpak", "weiyun", "quark", "uc", "ali", "alist", "xunlei" };

        /// <summary>解析一条播放地址。baseUrl 用于补全相对路径（一般是站点主页）。</summary>
        public static MediaTarget Parse(string? raw, string? baseUrl = null)
        {
            var t = new MediaTarget();
            var s = (raw ?? "").Trim();
            if (s.Length == 0) { t.Kind = MediaKind.Page; t.Note = "这条选集没有地址。"; return t; }

            // ---- 1) 剥掉 | 后面的请求头 ----
            var bar = s.IndexOf('|');
            if (bar > 0)
            {
                t.Url = s[..bar].Trim();
                ParseHeaders(s[(bar + 1)..], t.Headers);
            }
            else
            {
                // 少数源用 `;` 代替 `|`。不能无脑按 `;` 切 —— URL 里合法出现 `;`
                // （如 `;jsessionid=xxx`）会被误切。只在「分号后面确实跟着一个头名」时才认。
                var semi = FindHeaderSeparator(s);
                if (semi > 0)
                {
                    t.Url = s[..semi].Trim();
                    ParseHeaders(s[(semi + 1)..], t.Headers);
                }
                else t.Url = s;
            }

            // ---- 2) 判断协议 ----
            var colon = t.Url.IndexOf(':');
            string scheme = colon > 0 ? t.Url[..colon] : "";

            if (scheme.Length == 0 || t.Url.StartsWith("/") || t.Url.StartsWith("./"))
            {
                // 相对路径：能补就补，补不了当网页处理
                if (!string.IsNullOrEmpty(baseUrl) && Uri.TryCreate(new Uri(EnsureSlash(baseUrl)), t.Url.TrimStart('.', '/'), out var abs))
                {
                    t.Url = abs.ToString();
                    t.Kind = MediaKind.Direct;
                }
                else
                {
                    t.Kind = MediaKind.Page;
                    t.Note = "这是相对路径，且配置里没有可用的站点基址，无法拼成完整地址。";
                }
                return t;
            }

            if (UnsupportedSchemes.Contains(scheme))
            {
                t.Kind = MediaKind.Unsupported;
                t.Note = $"这是 {scheme}:// 协议（网盘/磁力/推送），播放器无法直接播放。";
                return t;
            }

            if (!DirectSchemes.Contains(scheme))
            {
                t.Kind = MediaKind.Page;
                t.Note = $"未知协议 {scheme}://，无法当作媒体流播放。";
                return t;
            }

            // http(s) 里也可能其实是网页（需要解析的源）
            var noQuery = t.Url.Split('?')[0].ToLowerInvariant();
            if (noQuery.EndsWith(".html") || noQuery.EndsWith(".htm") || noQuery.EndsWith(".php") || noQuery.EndsWith("/"))
            {
                t.Kind = MediaKind.Page;
                t.Note = "这看起来是一个网页地址（不是媒体流文件），需要「解析接口」才能拿到真实播放地址。";
                return t;
            }

            t.Kind = MediaKind.Direct;
            return t;
        }

        private static string EnsureSlash(string url) => url.EndsWith("/") ? url : url + "/";

        /// <summary>头名白名单：`;` 后面只有跟着这些之一，才认定它是「头参数分隔符」。</summary>
        private static readonly string[] HeaderHints =
        {
            "user-agent=", "ua=", "referer=", "referrer=", "cookie=", "origin=",
            "host=", "authorization=", "x-", "accept=",
        };

        /// <summary>找出「分号即头分隔符」的位置；找不到返回 -1。</summary>
        private static int FindHeaderSeparator(string s)
        {
            for (int i = s.IndexOf(';'); i > 0; i = s.IndexOf(';', i + 1))
            {
                var tail = s[(i + 1)..].TrimStart().ToLowerInvariant();
                foreach (var h in HeaderHints)
                    if (tail.StartsWith(h, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>
        /// 解析头参数串。TVBox 生态里写法很杂，实测三种都要吃：
        ///   `User-Agent=xx&amp;Referer=yy`（最常见）
        ///   `User-Agent=xx;Referer=yy`
        ///   `User-Agent: xx`  （冒号式）
        /// 认不出的片段直接忽略，别让它污染 URL。
        /// </summary>
        private static void ParseHeaders(string raw, Dictionary<string, string> into)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var seg in raw.Split(new[] { '&', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var piece = seg.Trim();
                if (piece.Length == 0) continue;

                int eq = piece.IndexOf('=');
                string k, v;
                if (eq > 1) { k = piece[..eq].Trim(); v = piece[(eq + 1)..].Trim(); }
                else
                {
                    int cl = piece.IndexOf(':');
                    if (cl <= 1) continue;
                    k = piece[..cl].Trim(); v = piece[(cl + 1)..].Trim();
                }
                k = k.Trim('"', '\'', ' ');
                v = v.Trim('"', '\'', ' ');
                if (k.Length == 0) continue;
                // 常见的拼写差异统一到标准名
                if (k.Equals("referrer", StringComparison.OrdinalIgnoreCase)) k = "Referer";
                if (k.Equals("ua", StringComparison.OrdinalIgnoreCase)) k = "User-Agent";
                into[k] = v;
            }
        }

        /// <summary>把请求头拼成 VLC 的 media option（VLC 只认这几个）。</summary>
        public static void AddVlcOptions(IList<string> options, MediaTarget t)
        {
            if (t.UserAgent != null) options.Add($":http-user-agent={t.UserAgent}");
            if (t.Referer != null) options.Add($":http-referrer={t.Referer}");
            foreach (var kv in t.Headers)
            {
                if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) continue;
                options.Add($":http-{kv.Key.ToLowerInvariant()}={kv.Value}");
            }
        }

        /// <summary>给用户看的一行摘要（排查用）。</summary>
        public static string Describe(MediaTarget t)
        {
            var sb = new StringBuilder(t.Url);
            if (t.HasHeaders)
            {
                sb.Append("  [头: ");
                bool first = true;
                foreach (var kv in t.Headers)
                {
                    if (!first) sb.Append("; ");
                    sb.Append(kv.Key).Append('=').Append(kv.Value.Length > 40 ? kv.Value[..40] + "…" : kv.Value);
                    first = false;
                }
                sb.Append(']');
            }
            return sb.ToString();
        }
    }
}
