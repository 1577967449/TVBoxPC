using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TVBoxPC.Core
{
    /// <summary>
    /// HLS(m3u8) 解析器 —— 对标浏览器插件「猫抓(cat-catch)」的 m3u8 解析器能力：
    ///
    ///   · master playlist → 列出**全部** variant（带宽 / 分辨率 / 名称），供用户挑画质，
    ///     而不是像旧实现那样无脑取最高带宽；
    ///   · media playlist → 分片表（序号 / 时长 / 密钥 / 字节范围）；
    ///   · <c>#EXT-X-BYTERANGE</c>：同一个 .ts 文件被按字节切成多段（猫抓支持，之前完全没处理，
    ///     碰到这种流会把整个文件当一段下，产物里塞满重复数据）；
    ///   · <c>#EXT-X-KEY</c>：AES-128 与随流变更的密钥；也保留原始 METHOD 名便于给用户看；
    ///   · <c>#EXT-X-MAP</c>：fMP4 初始化段；
    ///   · 统计**总时长**（所有 #EXTINF 求和）、分片数、是否加密、建议扩展名；
    ///   · 保留**原始 m3u8 文本**（猫抓那个「原始 m3u8」按钮，排障第一把钥匙）；
    ///   · **按时间换算分片区间**（猫抓支持「只下某一段」，用于超长视频或预览）。
    ///
    /// ★ 绝对/相对地址一律交给标准 URI 组合解析，别用 `Uri.TryCreate(uri, UriKind.Absolute)` 判绝对：
    ///   真实样本里同一条 m3u8 会混着 `0000000.ts`（相对）和 `/video/adjump/time/….ts`（以 / 开头的绝对路径），
    ///   后者被 `UriKind.Absolute` 判成 false → 被当成相对路径拼到目录后面 → 多一层目录 → 404。
    /// </summary>
    public static class HlsParser
    {
        // ===================== 数据模型 =====================
        public enum KeyMethod { None, Aes128, Other }

        /// <summary>master playlist 里的一个码率档（画质）。</summary>
        public class Variant
        {
            public string Url { get; set; } = "";
            public long Bandwidth { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string? Name { get; set; }
            public string? Codecs { get; set; }
            public string? ResolutionText { get; set; }

            /// <summary>给下拉框看的一行中文标签。</summary>
            public string Label
            {
                get
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(Name)) parts.Add(Name!);
                    if (Width > 0 && Height > 0) parts.Add($"{Width}×{Height}");
                    else if (!string.IsNullOrEmpty(ResolutionText)) parts.Add(ResolutionText!);
                    if (Bandwidth > 0) parts.Add($"{Bandwidth / 1000.0 / 1000.0:F2} Mbps");
                    if (parts.Count == 0) parts.Add("未知码率");
                    return string.Join(" · ", parts);
                }
            }
        }

        /// <summary>一个分片的密钥（可能中途更换，所以绑在分片上而不是全局一份）。</summary>
        public class KeySpec
        {
            public KeyMethod Method { get; set; }
            /// <summary>已补全的密钥地址。手填密钥时为空。</summary>
            public string? Uri { get; set; }
            public byte[]? Iv { get; set; }
            public string? RawMethod { get; set; }
            /// <summary>用户在手填框里给的密钥字节（优先于 Uri 下载）。</summary>
            public byte[]? ManualKey { get; set; }
        }

        /// <summary>一个分片。</summary>
        public class Segment
        {
            public string Url { get; set; } = "";
            public int Seq { get; set; }
            public double Duration { get; set; }
            public KeySpec? Key { get; set; }
            /// <summary>EXT-X-BYTERANGE 的起始字节（用于 Range 请求头）。</summary>
            public long ByteRangeStart { get; set; } = -1;
            /// <summary>EXT-X-BYTERANGE 的长度。 -1 表示整段下载。</summary>
            public long ByteRangeLength { get; set; } = -1;
            public bool HasByteRange => ByteRangeStart >= 0 && ByteRangeLength > 0;
            public string RangeHeader => HasByteRange ? $"bytes={ByteRangeStart}-{ByteRangeStart + ByteRangeLength - 1}" : "";
            public double TimeStart { get; set; }
        }

        /// <summary>解析结果。</summary>
        public class Info
        {
            public bool IsMaster { get; set; }
            public List<Variant> Variants { get; set; } = new();
            public List<Segment> Segments { get; set; } = new();
            public string? MapUri { get; set; }
            public double TotalDuration { get; set; }
            public long MediaSequence { get; set; }
            /// <summary>这份 playlist 的最终地址（跟随跳转后），用于补全相对路径。</summary>
            public string BaseUrl { get; set; } = "";
            /// <summary>原始 m3u8 全文（「原始 m3u8」查看 / 保存到产物目录）。</summary>
            public string RawText { get; set; } = "";
            /// <summary>建议的产物扩展名。</summary>
            public string Ext { get; set; } = ".ts";
            public int ByteRangeSegments { get; set; }

            public bool Encrypted => Segments.Any(s => s.Key != null && s.Key.Method == KeyMethod.Aes128);
            public bool HasUnsupportedKey => Segments.Any(s => s.Key != null && s.Key.Method == KeyMethod.Other);
            public bool HasByteRange => ByteRangeSegments > 0;

            /// <summary>是否全是 fMP4（.m4s/.mp4）分片 —— 直接拼起来就是 mp4。</summary>
            public bool IsFmp4 => Ext == ".mp4";
        }

        // ===================== 入口 =====================

        /// <summary>判断这个地址像不像 m3u8（查扩展名，也兼容把 .m3u8 藏在 query 里的情况）。</summary>
        public static bool LooksLikeM3u8(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var noQuery = url.Split('?')[0].ToLowerInvariant();
            return noQuery.EndsWith(".m3u8") || noQuery.EndsWith(".m3u") || url.ToLowerInvariant().Contains(".m3u8");
        }

        /// <summary>
        /// 解析一份 m3u8 文本。
        /// <paramref name="baseUrl"/> 是这份文本的**最终**地址（跟随 302 之后），相对分片按它补全。
        /// </summary>
        public static Info Parse(string text, string baseUrl)
        {
            var info = new Info { BaseUrl = baseUrl, RawText = text ?? "" };
            if (string.IsNullOrWhiteSpace(text))
            {
                info.Ext = ".ts";
                return info;
            }

            info.IsMaster = text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);
            if (info.IsMaster)
            {
                info.Variants = ParseVariants(text, baseUrl);
                info.Ext = ".ts";
                return info;
            }

            ParseMedia(text, baseUrl, info);
            return info;
        }

        /// <summary>解析 master playlist 的全部码率档（按带宽从高到低排序，界面里第一项就是最高画质）。</summary>
        public static List<Variant> ParseVariants(string playlist, string baseUrl)
        {
            var list = new List<Variant>();
            var lines = playlist.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;

                var v = new Variant();
                var bw = Attr(line, "BANDWIDTH") ?? Attr(line, "AVERAGE-BANDWIDTH");
                if (long.TryParse(bw, out var b)) v.Bandwidth = b;
                v.Name = Attr(line, "NAME");
                v.Codecs = Attr(line, "CODECS");
                v.ResolutionText = Attr(line, "RESOLUTION");
                if (!string.IsNullOrEmpty(v.ResolutionText))
                {
                    var m = Regex.Match(v.ResolutionText!, @"(\d+)\s*[x×]\s*(\d+)");
                    if (m.Success)
                    {
                        int.TryParse(m.Groups[1].Value, out var w);
                        int.TryParse(m.Groups[2].Value, out var h);
                        v.Width = w; v.Height = h;
                    }
                }

                // 紧跟着的第一行非空、非注释行就是 URI 模板
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var u = lines[j].Trim();
                    if (u.Length == 0) continue;
                    if (u.StartsWith("#")) break;
                    v.Url = Resolve(baseUrl, u);
                    break;
                }
                if (!string.IsNullOrEmpty(v.Url)) list.Add(v);
            }
            return list.OrderByDescending(x => x.Bandwidth).ToList();
        }

        private static void ParseMedia(string playlist, string baseUrl, Info info)
        {
            var lines = playlist.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            KeySpec? current = null;
            double pendingDuration = 0;
            double clock = 0;
            long nextByteStart = 0;      // EXT-X-BYTERANGE 省略 @offset 时接着上一段的末尾
            (long start, long len)? pendingRange = null;   // 出现在分片行之前，临时带过去
            var extCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int seq = (int)info.MediaSequence;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.OrdinalIgnoreCase))
                {
                    var v = Val(line);
                    if (long.TryParse(v, out var s)) { info.MediaSequence = s; seq = (int)s; }
                    continue;
                }
                if (line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
                {
                    var u = Attr(line, "URI");
                    if (!string.IsNullOrEmpty(u)) info.MapUri = Resolve(baseUrl, u!);
                    continue;
                }
                if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
                {
                    current = ParseKey(line, baseUrl);
                    continue;
                }
                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    var v = Val(line);
                    var comma = v.IndexOf(',');
                    if (comma >= 0) v = v[..comma];
                    if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
                        pendingDuration = d;
                    continue;
                }
                if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.OrdinalIgnoreCase))
                {
                    // 语法：<n>[@<o>]；n = 长度，o = 起始偏移（省略即接上一段末尾）
                    var v = Val(line);
                    var at = v.IndexOf('@');
                    long len = 0, start = nextByteStart;
                    if (at >= 0)
                    {
                        long.TryParse(v[..at], out len);
                        long.TryParse(v[(at + 1)..], out start);
                    }
                    else long.TryParse(v, out len);

                    if (len > 0)
                    {
                        pendingRange = (start, len);
                        nextByteStart = start + len;
                        info.ByteRangeSegments++;
                    }
                    continue;
                }
                if (line.StartsWith("#")) continue;

                var seg = new Segment
                {
                    Url = Resolve(baseUrl, line),
                    Key = current,
                    Seq = seq++,
                    Duration = pendingDuration,
                    TimeStart = clock,
                };
                if (pendingRange.HasValue)
                {
                    seg.ByteRangeStart = pendingRange.Value.start;
                    seg.ByteRangeLength = pendingRange.Value.len;
                    pendingRange = null;
                }
                clock += pendingDuration;
                pendingDuration = 0;
                info.Segments.Add(seg);

                var e = System.IO.Path.GetExtension(line.Split('?')[0]);
                if (e.Length is > 0 and < 6) extCount[e] = extCount.GetValueOrDefault(e) + 1;
            }

            info.TotalDuration = info.Segments.Sum(s => s.Duration);
            info.Ext = PickExt(extCount);
        }

        private static KeySpec? ParseKey(string line, string baseUrl)
        {
            var method = (Attr(line, "METHOD") ?? "").Trim();
            if (method.Length == 0 || method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                return null;

            var spec = new KeySpec { RawMethod = method };
            if (method.Equals("AES-128", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("AES128", StringComparison.OrdinalIgnoreCase))
                spec.Method = KeyMethod.Aes128;
            else
            {
                // SAMPLE-AES 之类本实现解不了：标记出来，让界面把话说清楚，而不是默默下出坏文件
                spec.Method = KeyMethod.Other;
                return spec;
            }

            var ku = Attr(line, "URI");
            if (!string.IsNullOrEmpty(ku)) spec.Uri = Resolve(baseUrl, ku!);
            var ivs = Attr(line, "IV");
            if (!string.IsNullOrEmpty(ivs)) spec.Iv = ParseHex(ivs!);
            return spec;
        }

        /// <summary>解析 0x 开头的十六进制串（IV）。</summary>
        public static byte[]? ParseHex(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            s = s.Replace("-", "").Replace(" ", "");
            if (s.Length == 0 || s.Length % 2 != 0) return null;
            try
            {
                var r = new byte[s.Length / 2];
                for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
                return r;
            }
            catch { return null; }
        }

        /// <summary>
        /// 手工密钥解析：既支持「32 位十六进制」，也支持 base64（猫抓就是这两种一起收），
        /// 还容错带空格的写法。
        /// </summary>
        public static byte[]? ParseManualKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var s = input.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];

            // 纯十六进制（允许空格/短横线分隔）
            var compact = s.Replace(" ", "").Replace("-", "").Replace(":", "");
            if (compact.Length == 32 && compact.All(Uri.IsHexDigit))
            {
                var r = new byte[16];
                for (int i = 0; i < 16; i++) r[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
                return r;
            }
            // base64
            try
            {
                var b = Convert.FromBase64String(s);
                if (b.Length == 16) return b;
            }
            catch { }
            return null;
        }

        /// <summary>取 #EXT-X-KEY / #EXT-X-STREAM-INF 这类行里 `NAME=value` 的值（支持带引号）。</summary>
        public static string? Attr(string line, string name)
        {
            var idx = line.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var start = idx + name.Length + 1;
            if (start >= line.Length) return null;
            if (line[start] == '"')
            {
                var end = line.IndexOf('"', start + 1);
                return end < 0 ? null : line.Substring(start + 1, end - start - 1);
            }
            var e = line.IndexOf(',', start);
            return e < 0 ? line.Substring(start).Trim() : line.Substring(start, e - start).Trim();
        }

        /// <summary>取「冒号之后」的值（#EXTINF:10.0, / #EXT-X-MEDIA-SEQUENCE:0 / #EXT-X-BYTERANGE:1000@0）。</summary>
        private static string Val(string line)
        {
            var c = line.IndexOf(':');
            return c < 0 ? "" : line[(c + 1)..].Trim();
        }

        /// <summary>
        /// 绝对/相对地址统一用标准 URI 组合解析。
        /// 以 `/` 开头、`//host/x`、带 `?query`、纯相对名 —— 全都能正确处理。
        /// </summary>
        public static string Resolve(string baseUrl, string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return baseUrl;
            uri = uri.Trim();
            if (Uri.TryCreate(uri, UriKind.Absolute, out var abs) &&
                (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                return abs.ToString();
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) &&
                Uri.TryCreate(b, uri, out var combined))
                return combined.ToString();
            return uri;
        }

        private static string PickExt(Dictionary<string, int> extCount)
        {
            if (extCount.Count == 0) return ".ts";
            var top = extCount.OrderByDescending(kv => kv.Value).First().Key.ToLowerInvariant();
            return top switch
            {
                ".m4s" or ".mp4" or ".m4v" or ".cmfv" => ".mp4",
                ".aac" or ".m4a" => ".aac",
                ".mp3" => ".mp3",
                _ => ".ts",
            };
        }

        // ===================== 区间选择（猫抓的「只下选中的分片/某时间段」） =====================

        /// <summary>
        /// 按**分片序号区间**取子集（1 基，闭区间）。规则（都有断言覆盖）：
        ///   · 0 表示"不限"（from=1 / to=最后一段）；
        ///   · 两个数都落在有效范围内但写反了 → 容错交换（8~3 当 3~8）；
        ///   · 只超上界 → 夹到最后一段（1~9999 当 1~N）；
        ///   · 只超下界（起始比总段数还大）→ **空集**，让上层明确告诉用户"这个范围里没有分片"，
        ///     而不是悄悄给他最后一段（那种"我明明填了 20~99 结果下了一段"最难查）。
        /// </summary>
        public static List<Segment> Slice(List<Segment> segs, int fromIndex1Based, int toIndex1Based)
        {
            if (segs.Count == 0) return segs;
            int count = segs.Count;
            int from = fromIndex1Based <= 0 ? 1 : fromIndex1Based;
            int to = toIndex1Based <= 0 ? count : toIndex1Based;

            if (from > to && from <= count && to >= 1) (from, to) = (to, from);

            from = Math.Max(1, from);
            to = Math.Min(count, to);
            if (from > to) return new List<Segment>();
            return segs.Where(s => s.Seq >= from - 1 && s.Seq <= to - 1).ToList();
        }

        /// <summary>按**时间区间**（秒）取子集：与区间有重叠的分片都算（和猫抓一致，避免留半段）。</summary>
        public static List<Segment> SliceByTime(List<Segment> segs, double fromSec, double toSec)
        {
            if (segs.Count == 0) return segs;
            if (toSec <= 0) toSec = segs[^1].TimeStart + segs[^1].Duration;
            if (toSec < fromSec) (fromSec, toSec) = (toSec, fromSec);
            return segs.Where(s => s.TimeStart + s.Duration > fromSec && s.TimeStart < toSec).ToList();
        }

        /// <summary>把秒格式化成 mm:ss / h:mm:ss。</summary>
        public static string FormatDuration(double seconds)
        {
            if (seconds <= 0 || double.IsNaN(seconds)) return "--:--";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        /// <summary>给用户看的一句话摘要（总时长 · 分片数 · 画质 · 加密）。</summary>
        public static string Describe(Info info)
        {
            if (info.IsMaster)
                return $"这是**多码率**清单，含 {info.Variants.Count} 个画质档。选一档后即可查看分片。";

            var parts = new List<string>
            {
                $"总时长 {FormatDuration(info.TotalDuration)}",
                $"{info.Segments.Count} 个分片",
            };
            if (info.HasByteRange) parts.Add($"{info.ByteRangeSegments} 段是字节范围切片（EXT-X-BYTERANGE）");
            if (info.Encrypted) parts.Add("AES-128 加密（自动解密）");
            if (info.HasUnsupportedKey) parts.Add("有 SAMPLE-AES 等本程序解不了的加密方式");
            if (!string.IsNullOrEmpty(info.MapUri)) parts.Add("含 fMP4 初始化段");
            return string.Join(" · ", parts);
        }
    }
}
