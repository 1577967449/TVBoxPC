using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AngleSharp.Dom;

namespace TVBoxPC.Core.Jar.Spiders
{
    /// <summary>
    /// 六V（xb6v.com）磁力站。
    /// 对应原 JAR 里的 com.github.catvod.spider.SixV。
    ///
    /// 原算法（从字节码逐条还原）：
    ///   分类：固定 12 个 slug（dianshiju/guoju、dongzuopian …）
    ///   列表：site + "/" + slug + "/"（第 1 页）
    ///         site + "/" + slug + "/index_" + pg + ".html"（后续页）
    ///         UA 固定 Chrome/80，Referer = site
    ///         取 "#post_container .post_hover"；每条取 [class=zoom] 的 href/title、
    ///         img 的 src、[rel=category tag] 的文本作为备注；pagecount 固定 999
    ///   搜索：POST site + "/e/search/index.php"，帝国CMS 表单（show/tempid/tbname/mid/dopost/submit/keyboard）
    ///   详情：GET site + vod_id，取 "#post_content" 下的 table a[href^=magnet]
    ///   （ext 就是站点地址，如 https://www.xb6v.com/）
    /// </summary>
    public sealed class SixV : JarSpider
    {
        public override string SpiderName => "SixV";

        private const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.163 Safari/537.36";

        private static readonly (string Slug, string Name)[] Cats =
        {
            ("dianshiju/guoju",   "国剧"),
            ("dianshiju/rihanju", "日韩剧"),
            ("dianshiju/oumeiju", "欧美剧"),
            ("xijupian",          "喜剧片"),
            ("dongzuopian",       "动作片"),
            ("aiqingpian",        "爱情片"),
            ("kehuanpian",        "科幻片"),
            ("kongbupian",        "恐怖片"),
            ("juqingpian",        "剧情片"),
            ("zhanzhengpian",     "战争片"),
            ("jilupian",          "纪录片"),
            ("donghuapian",       "动画片"),
        };

        protected override string SiteHomeUrl() => Site;

        private string Site => (Ext ?? "").Trim().TrimEnd('/');

        public override Task<List<Category>> CategoriesAsync()
            => Task.FromResult(Cats.Select(c => Cat(c.Slug, c.Name)).ToList());

        private Dictionary<string, string> Headers() => new()
        {
            ["User-Agent"] = Ua,
            ["Referer"] = Site + "/",
        };

        public override async Task<List<VodItem>> ListAsync(string? tid, int page)
        {
            var slug = string.IsNullOrWhiteSpace(tid) ? Cats[0].Slug : tid!.Trim();
            var url = page <= 1 ? $"{Site}/{slug}/" : $"{Site}/{slug}/index_{page}.html";
            var html = await Fetch(url, Headers());
            // 列表页用同一套选择器
            var items = ParseItems(html, "#post_container .post_hover");
            // 原实现 pagecount 固定 999，这里也保持「永远有下一页」的语义
            return items;
        }

        public override async Task<List<VodItem>> SearchAsync(string wd, int page)
        {
            var body = "show=title&tempid=1&tbname=article&mid=1&dopost=search&submit=search&keyboard="
                       + UrlEncode(wd ?? "");
            var headers = Headers();
            headers["Origin"] = Site;
            var html = await Post(Site + "/e/search/index.php", body, headers);
            return ParseItems(html, "#post_container [class=zoom]");
        }

        /// <summary>原实现里 categoryContent 与 searchContent 用同一段解析逻辑，只是选择器不同。</summary>
        private List<VodItem> ParseItems(string html, string itemSelector)
        {
            var res = new List<VodItem>();
            var doc = HtmlSelect.Parse(html);
            var nodes = Pdfa(doc.DocumentElement, itemSelector);
            bool flat = itemSelector.Contains("[class=zoom]");

            foreach (var it in nodes)
            {
                IElement? a, img;
                if (flat)
                {
                    // 搜索页：每条就是 a[class=zoom] 本身
                    a = it;
                    img = Pdfa(it, "img").FirstOrDefault();
                }
                else
                {
                    a = Pdfa(it, "[class=zoom]").FirstOrDefault();
                    img = Pdfa(it, "img").FirstOrDefault();
                }
                if (a == null) continue;

                var href = Pdfh(a, "href");
                var title = Pdfh(a, "title");
                if (string.IsNullOrWhiteSpace(title)) title = Pdfh(a, "Text");
                title = Clean(title);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)) continue;

                var pic = Pdfh(img, "src");
                if (string.IsNullOrWhiteSpace(pic)) pic = Pdfh(img, "data-src");
                pic = UrlJoin(pic);

                var remarks = "";
                var cat = Pdfa(it, "[rel=category tag]").FirstOrDefault();
                if (cat != null) remarks = Clean(Pdfh(cat, "Text"));

                res.Add(Item(href, title, pic, remarks));
            }
            return res;
        }

        public override async Task<VodDetail?> DetailAsync(string id)
        {
            var url = id.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? id : Site + id;
            var html = await Fetch(url, Headers());
            var doc = HtmlSelect.Parse(html);
            var root = doc.DocumentElement;

            var detail = new VodDetail { Id = id };

            var h1 = Pdfa(root, ".article_container > h1").FirstOrDefault()
                     ?? Pdfa(root, "#post_content h1").FirstOrDefault();
            detail.Name = Clean(h1 != null ? Pdfh(h1, "Text") : Pdfh(root, "title"));

            var img = Pdfa(root, "#post_content img").FirstOrDefault();
            detail.Pic = UrlJoin(Pdfh(img, "src"));

            var contentEl = Pdfa(root, "#post_content").FirstOrDefault();
            var contentHtml = contentEl?.InnerHtml ?? "";

            detail.TypeName = Clean(RegexGroup(contentHtml, "◎类　　?别　(.*?)<br"));
            detail.Year = Clean(RegexGroup(contentHtml, "◎年　　?代　(.*?)<br"));
            detail.Area = Clean(RegexGroup(contentHtml, "◎产　　?地　(.*?)<br"));
            detail.Remarks = Clean(RegexGroup(contentHtml, "◎上映日期　(.*?)<br"));
            detail.Actor = Clean(RegexGroup(contentHtml, "◎演　　员　(.*?)</p>")
                                 ?? RegexGroup(contentHtml, "◎主　　演　(.*?)</p>")
                                 ?? RegexGroup(contentHtml, "◎主　演　(.*?)<br"));
            detail.Director = Clean(RegexGroup(contentHtml, "◎导　　演　(.*?)<br")
                                    ?? RegexGroup(contentHtml, "◎导　演　(.*?)<br"));
            var brief = RegexGroup(contentHtml, "◎简　　介(.*?)<hr")
                        ?? RegexGroup(contentHtml, "◎简　介(.*?)<hr");
            detail.Content = Clean(brief);

            // 磁力：取 #post_content 下所有 table 里的 a[href^=magnet]
            var magnets = new List<Episode>();
            var seen = new HashSet<string>();
            foreach (var a in Pdfa(root, "#post_content a"))
            {
                var href = Pdfh(a, "href");
                if (string.IsNullOrWhiteSpace(href) ||
                    !href.StartsWith("magnet", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(href)) continue;

                var name = MagnetName(href, magnets.Count + 1);
                magnets.Add(Ep(name, href));
            }
            // 兜底：整页里直接抓 magnet 链接（有些模板把 table 放在 post_content 之外）
            if (magnets.Count == 0)
            {
                foreach (var a in Pdfa(root, "a"))
                {
                    var href = Pdfh(a, "href");
                    if (string.IsNullOrWhiteSpace(href) ||
                        !href.StartsWith("magnet", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(href)) continue;
                    magnets.Add(Ep(MagnetName(href, magnets.Count + 1), href));
                }
            }

            if (magnets.Count > 0)
                detail.Lines.Add(new PlayLine { Name = "磁力", Episodes = magnets });

            return detail;
        }

        /// <summary>磁力链接没有可读标题时，从 dn= 参数里取发布名，取不到就用序号。</summary>
        private static string MagnetName(string magnet, int index)
        {
            var m = System.Text.RegularExpressions.Regex.Match(magnet, @"[&?]dn=([^&]+)");
            if (m.Success)
            {
                var n = Clean(UrlDecode(m.Groups[1].Value));
                if (!string.IsNullOrWhiteSpace(n))
                    return n.Length > 80 ? n.Substring(0, 80) : n;
            }
            return "磁力 " + index;
        }

        private static string RegexGroup(string input, string pattern)
        {
            if (string.IsNullOrEmpty(input)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(input, pattern,
                System.Text.RegularExpressions.RegexOptions.Singleline);
            return m.Success && m.Groups.Count > 1 ? Unescape(m.Groups[1].Value) : "";
        }
    }
}
