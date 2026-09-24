using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 等价于 drpy 里 cheerio 的抽取能力：pdfh（取一个）/ pdfa（取全部）。
    ///
    /// 选择器格式与 drpy 保持一致：
    ///   · `sel&amp;&amp;attr`            —— attr 为 Text（文本）/ Html（内层 HTML）/ OuterHtml / 任意属性名（href、src、data-* …）
    ///   · `&&` 之前的部分按 CSS 后代关系拼接，因此同时兼容 drpy2 的 `a&amp;&amp;Text`
    ///     与 drpy1 的 `.list&amp;&amp;a&amp;&amp;href` 两种写法
    ///   · 多组备选用 `||` 分隔，取第一个非空结果
    ///   · 未写 attr 时默认取文本
    /// </summary>
    public static class HtmlSelect
    {
        private static readonly HtmlParser Parser = new(new HtmlParserOptions
        {
            IsScripting = false
        });

        /// <summary>解析 HTML 文档（容错：html 为 null / 空 / 非标准片段都不抛异常）。</summary>
        public static IDocument Parse(string? html)
        {
            try { return Parser.ParseDocument(html ?? string.Empty); }
            catch { return Parser.ParseDocument(string.Empty); }
        }

        // ================= pdfh =================

        /// <summary>在整篇 HTML 上取第一个匹配项的属性/文本。</summary>
        public static string Pdfh(string? html, string? selector)
            => Pdfh(Parse(html).DocumentElement, selector);

        /// <summary>在某个元素（循环里的 it）上取属性/文本。</summary>
        public static string Pdfh(IElement? ctx, string? selector)
        {
            if (ctx == null || string.IsNullOrWhiteSpace(selector)) return "";

            foreach (var alt in SplitAlternatives(selector))
            {
                var (css, attr) = SplitSelector(alt);
                IElement? el;
                if (string.IsNullOrEmpty(css)) el = ctx;          // 纯属性写法："Text"
                else el = SafeQuery(ctx, css);
                if (el == null) continue;

                var val = Extract(el, attr);
                if (!string.IsNullOrEmpty(val)) return val;
            }
            return "";
        }

        // ================= pdfa =================

        /// <summary>在整篇 HTML 上取全部匹配项。</summary>
        public static List<IElement> Pdfa(string? html, string? selector)
            => Pdfa(Parse(html).DocumentElement, selector);

        /// <summary>在某个元素范围内取全部匹配项。</summary>
        public static List<IElement> Pdfa(IElement? ctx, string? selector)
        {
            var list = new List<IElement>();
            if (ctx == null || string.IsNullOrWhiteSpace(selector)) return list;

            foreach (var alt in SplitAlternatives(selector))
            {
                var (css, _) = SplitSelector(alt);
                if (string.IsNullOrEmpty(css)) { list.Add(ctx); return list; }
                try
                {
                    var nodes = ctx.QuerySelectorAll(css);
                    if (nodes.Length > 0)
                    {
                        list.AddRange(nodes);
                        return list;
                    }
                }
                catch { /* 选择器非法则尝试下一个备选 */ }
            }
            return list;
        }

        /// <summary>pdfh + 相对地址转绝对地址（drpy 的 pd）。</summary>
        public static string Pd(string? html, string? selector, string? baseUrl)
            => UrlJoin(baseUrl, Pdfh(html, selector));

        /// <summary>pdfh + 相对地址转绝对地址（元素级）。</summary>
        public static string Pd(IElement? ctx, string? selector, string? baseUrl)
            => UrlJoin(baseUrl, Pdfh(ctx, selector));

        // ================= 内部实现 =================

        private static IElement? SafeQuery(IElement ctx, string css)
        {
            try { return ctx.QuerySelector(css); }
            catch { return null; }
        }

        /// <summary>按 `||` 拆分备选（drpy 的 `a||b` 语义）。</summary>
        private static IEnumerable<string> SplitAlternatives(string selector)
        {
            foreach (var part in selector.Split("||", StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim();
                if (t.Length > 0) yield return t;
            }
        }

        /// <summary>
        /// 把一个 drpy 选择器拆成 (CSS 选择器, 取值方式)。
        /// 规则：若含 `&amp;&amp;`，最后一段是取值方式，其余拼成 CSS 后代选择器；
        ///       否则整串都是 CSS，取值方式默认 Text。
        /// </summary>
        private static (string css, string attr) SplitSelector(string raw)
        {
            var s = raw.Trim();
            var parts = s.Split("&&", StringSplitOptions.None);

            // 无 && ：若整串看似"纯取值"（Text/Html/属性名且不像选择器），按取值处理
            if (parts.Length == 1)
            {
                if (IsBareAttrToken(s)) return ("", s);
                return (s, "Text");
            }

            var attr = parts[^1].Trim();
            var css = string.Join(" ", parts.Take(parts.Length - 1).Select(p => p.Trim())
                                     .Where(p => p.Length > 0));
            if (string.IsNullOrEmpty(css)) return ("", attr);
            return (css, attr);
        }

        /// <summary>判断整串是否只是一个取值标记（而非 CSS 选择器）。</summary>
        private static bool IsBareAttrToken(string s)
        {
            if (s.Length == 0) return false;
            if (s.Equals("Text", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("Html", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("OuterHtml", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("ownText", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("TextNodes", StringComparison.OrdinalIgnoreCase)) return true;
            // CSS 里不可能出现 . # [ ] > : 空白 ，其余裸词才可能是属性名（如 data-src）
            return !s.Any(ch => ch == '.' || ch == '#' || ch == '[' || ch == ']'
                                || ch == '>' || ch == ':' || ch == ' ' || ch == '+'
                                || ch == '~' || ch == '*');
        }

        /// <summary>按取值方式从元素取出内容。</summary>
        private static string Extract(IElement el, string attr)
        {
            switch (attr)
            {
                case "Text":
                case "text":
                    return el.TextContent?.Trim() ?? "";
                case "Html":
                case "html":
                    return el.InnerHtml ?? "";
                case "OuterHtml":
                case "outerHtml":
                    return el.OuterHtml ?? "";
                case "ownText":
                    // 只取直接文本子节点（不含后代文本）
                    return string.Concat(el.ChildNodes.OfType<IText>().Select(t => t.Data)).Trim();
                case "TextNodes":
                    return string.Join(" ", el.ChildNodes.OfType<IText>().Select(t => t.Data?.Trim()));
                default:
                    var v = el.GetAttribute(attr);
                    return v ?? "";
            }
        }

        /// <summary>去掉 HTML 标签并还原实体，得到纯文本。</summary>
        public static string StripTags(string? html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var t = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
            t = System.Net.WebUtility.HtmlDecode(t);
            return t.Trim();
        }

        /// <summary>相对地址转绝对地址（与 drpy 的 urljoin 一致）。</summary>
        public static string UrlJoin(string? baseUrl, string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            url = url.Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.StartsWith("data:") || url.StartsWith("magnet:") ||
                url.StartsWith("push:") || url.StartsWith("proxy:")) return url;

            if (string.IsNullOrWhiteSpace(baseUrl)) return url;
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) &&
                Uri.TryCreate(b, url, out var abs)) return abs.ToString();
            return url;
        }
    }
}
