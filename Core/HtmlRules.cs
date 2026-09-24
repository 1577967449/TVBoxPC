using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace TVBoxPC.Core
{
    /// <summary>
    /// TVBox「套娃」蜘蛛（csp_XPath 家族）的抽取原语。
    /// 规则统一为：["前缀", "后缀", 左偏移, 右偏移, 祖先层]，即取前缀与后缀之间的文本；
    /// region 为 ["起始标记","结束标记"] 用于先把页面缩小到目标区块；
    /// 找不到区块时返回空串，交由调用方兜底。
    /// </summary>
    public static class HtmlRules
    {
        /// <summary>把整页 HTML 按 region 缩小到目标区块；region 为空则原样返回。</summary>
        public static string CutRegion(string html, JsonNode? region)
        {
            if (region is not JsonArray r || r.Count < 2) return html;
            var start = r[0]?.ToString() ?? "";
            var end = r[1]?.ToString() ?? "";
            var from = 0;
            if (!string.IsNullOrEmpty(start))
            {
                var i = html.IndexOf(start, StringComparison.Ordinal);
                if (i < 0) return "";
                from = i + start.Length;
            }
            int to = html.Length;
            if (!string.IsNullOrEmpty(end))
            {
                var j = html.IndexOf(end, from, StringComparison.Ordinal);
                if (j >= 0) to = j;
            }
            if (to <= from) return "";
            return html.Substring(from, to - from);
        }

        /// <summary>抽取单个值（取第一个匹配）。规则为常量字符串时直接返回该常量。</summary>
        public static string Extract(string html, JsonNode? rule)
        {
            if (rule == null) return "";
            if (rule is JsonValue v) return v.ToString();
            if (rule is not JsonArray a) return "";

            var pre = a.Count > 0 ? a[0]?.ToString() ?? "" : "";
            var suf = a.Count > 1 ? a[1]?.ToString() ?? "" : "";
            int left = GetInt(a, 2), right = GetInt(a, 3);

            var i = string.IsNullOrEmpty(pre) ? 0 : html.IndexOf(pre, StringComparison.Ordinal);
            if (i < 0) return "";
            var s = i + pre.Length;
            int e;
            if (string.IsNullOrEmpty(suf)) e = html.Length;
            else
            {
                var k = html.IndexOf(suf, s, StringComparison.Ordinal);
                e = k < 0 ? html.Length : k;
            }
            s += left; e += right;
            if (s < 0) s = 0;
            if (e > html.Length) e = html.Length;
            if (e <= s) return "";
            return html.Substring(s, e - s).Trim();
        }

        /// <summary>抽取全部匹配（用于取整个剧集列表）。</summary>
        public static List<string> ExtractAll(string html, JsonNode? rule)
        {
            var list = new List<string>();
            if (rule is not JsonArray a || a.Count < 2) return list;
            var pre = a[0]?.ToString() ?? "";
            var suf = a[1]?.ToString() ?? "";
            if (string.IsNullOrEmpty(pre) || string.IsNullOrEmpty(suf)) return list;
            int left = GetInt(a, 2), right = GetInt(a, 3);

            int pos = 0;
            var guard = 0;
            while (guard++ < 5000)
            {
                var i = html.IndexOf(pre, pos, StringComparison.Ordinal);
                if (i < 0) break;
                var s = i + pre.Length;
                var k = html.IndexOf(suf, s, StringComparison.Ordinal);
                if (k < 0) break;
                var seg = html.Substring(s, k - s);
                var ss = s + left; var ee = k + right;
                if (ss >= 0 && ee <= html.Length && ee > ss) seg = html.Substring(ss, ee - ss);
                list.Add(seg.Trim());
                pos = k + suf.Length;
            }
            return list;
        }

        /// <summary>取匹配位置（起止下标），用于把列表页切成「一项一段」以提取同一项内的其它字段。</summary>
        public static List<(int start, int end, string value)> ExtractAllSpans(string html, JsonNode? rule)
        {
            var list = new List<(int, int, string)>();
            if (rule is not JsonArray a || a.Count < 2) return list;
            var pre = a[0]?.ToString() ?? "";
            var suf = a[1]?.ToString() ?? "";
            if (string.IsNullOrEmpty(pre) || string.IsNullOrEmpty(suf)) return list;

            int pos = 0, guard = 0;
            while (guard++ < 5000)
            {
                var i = html.IndexOf(pre, pos, StringComparison.Ordinal);
                if (i < 0) break;
                var s = i + pre.Length;
                var k = html.IndexOf(suf, s, StringComparison.Ordinal);
                if (k < 0) break;
                var val = html.Substring(s, k - s).Trim();
                list.Add((i, k + suf.Length, val));
                pos = k + suf.Length;
            }
            return list;
        }

        private static int GetInt(JsonArray a, int idx)
        {
            if (a.Count <= idx) return 0;
            try { return a[idx]?.GetValue<int>() ?? 0; } catch { return 0; }
        }

        /// <summary>去掉 HTML 标签与常见实体，得到纯文本。</summary>
        public static string StripHtml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, "<script[\\s\\S]*?</script>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<style[\\s\\S]*?</style>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", "");
            s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
                 .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
            s = Regex.Replace(s, "[ \t\r\n]+", " ");
            return s.Trim();
        }

        /// <summary>URL 模板占位符替换：{cateId} {catePg} {pg} {wd} {vid} {id}。</summary>
        public static string ApplyTemplate(string tpl, string? cateId, int page, string? wd = null, string? vid = null)
        {
            return tpl
                .Replace("{cateId}", cateId ?? "")
                .Replace("{cate}", cateId ?? "")
                .Replace("{catePg}", page.ToString())
                .Replace("{pg}", page.ToString())
                .Replace("{page}", page.ToString())
                .Replace("{wd}", Uri.EscapeDataString(wd ?? ""))
                .Replace("{keyword}", Uri.EscapeDataString(wd ?? ""))
                .Replace("{vid}", vid ?? "")
                .Replace("{id}", vid ?? "");
        }
    }
}
