using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 原生实现的「套娃」蜘蛛（对应 TVBox 的 csp_XPath / csp_XPathMac / csp_XPathFilter /
    /// csp_XPathMacFilter / csp_XPathEgg）。不依赖任何 JAR / Java 运行时，纯 C# 按规则抓取。
    ///
    /// 规则来自站点配置的 ext 字段（可以是规则 JSON 的网络地址，也可以直接内联 JSON 串），
    /// 结构：
    /// {
    ///   "homeUrl": "https://site.com",
    ///   "header": { "User-Agent": "..." },
    ///   "cateManual": { "电影": "1", "剧集": "2" },
    ///   "list":     { "region": ["",""], "url": "https://.../{cateId}/index{catePg}.html",
    ///                 "vod_id": [...], "vod_name": [...], "vod_pic": [...], "vod_remarks": [...] },
    ///   "detail":   { "region": ["",""], "url": "https://.../{vid}.html",
    ///                 "vod_name": [...], "vod_pic": [...], "vod_actor": [...], "vod_content": [...] },
    ///   "playlist": { "region": ["",""], "sort": 0, "vod_play_from": ["线路一"],
    ///                 "vod_play_url": [...], "vod_play_url_title": [...] },
    ///   "search":   { "url": "https://.../search.php?wd={wd}&page={pg}", ... : 同 list 规则 }
    /// }
    /// </summary>
    public class XPathSpider : ISiteClient
    {
        private readonly JsonNode _cfg;
        private readonly HttpClient _http;
        private readonly string? _homeUrl;

        public string Key { get; }
        public string Name { get; }
        public bool Available => true;
        public string? UnavailableReason => null;

        private XPathSpider(string key, string name, JsonNode cfg, Dictionary<string, string> headers, string? homeUrl)
        {
            Key = key; Name = name; _cfg = cfg; _homeUrl = homeUrl;
            _http = HttpFactory.Create(25);
            foreach (var h in headers) _http.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
            if (!headers.ContainsKey("User-Agent"))
                _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }

        /// <summary>构建蜘蛛；返回 null 表示 ext 无法解析。</summary>
        public static async Task<XPathSpider?> Create(string key, string name, string ext)
        {
            JsonNode? cfg = null;
            try
            {
                string json = ext;
                if (ext.Contains("://") && !ext.TrimStart().StartsWith("{"))
                {
                    using var hc = HttpFactory.Create(25);
                    json = await hc.GetStringAsync(ext);
                }
                cfg = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip
                });
            }
            catch { return null; }
            if (cfg is not JsonObject) return null;

            var headers = new Dictionary<string, string>();
            if (cfg["header"] is JsonObject ho)
                foreach (var kv in ho) if (kv.Value != null) headers[kv.Key] = kv.Value.ToString();

            return new XPathSpider(key, name, cfg, headers, cfg["homeUrl"]?.ToString());
        }

        // ===== 分类 =====
        public Task<List<Category>> GetCategories()
        {
            var list = new List<Category>();
            if (_cfg["cateManual"] is JsonObject cm)
                foreach (var kv in cm) list.Add(new Category { Name = kv.Key, Id = kv.Value?.ToString() ?? "" });
            if (list.Count == 0 && _cfg["class"] is JsonArray cls)
                foreach (var c in cls)
                    list.Add(new Category { Id = c?["type_id"]?.ToString() ?? "", Name = c?["type_name"]?.ToString() ?? "" });
            return Task.FromResult(list);
        }

        // ===== 列表 =====
        public async Task<List<VodItem>> GetList(string? categoryId, int page)
        {
            var ln = _cfg["list"];
            var tpl = ln?["url"]?.ToString();
            if (string.IsNullOrEmpty(tpl)) return new List<VodItem>();
            var url = Resolve(HtmlRules.ApplyTemplate(tpl!, categoryId, page));
            var html = await GetHtml(url);
            return ExtractList(html, ln, categoryId);
        }

        private List<VodItem> ExtractList(string html, JsonNode? section, string? categoryId)
        {
            var items = new List<VodItem>();
            if (section == null) return items;

            var body = HtmlRules.CutRegion(html, section["region"]);
            if (string.IsNullOrEmpty(body)) return items;

            var idRule = section["vod_id"];
            var spans = HtmlRules.ExtractAllSpans(body, idRule);
            if (spans.Count == 0)
            {
                // 兜底：无 vod_id 规则时按 vod_name 定位
                var nameSpans = HtmlRules.ExtractAllSpans(body, section["vod_name"]);
                foreach (var (s, e, val) in nameSpans)
                    items.Add(new VodItem { Id = val, Name = HtmlRules.StripHtml(val) });
                return items;
            }

            for (int i = 0; i < spans.Count; i++)
            {
                var start = spans[i].start;
                var end = i + 1 < spans.Count ? spans[i + 1].start : body.Length;
                var seg = body.Substring(start, Math.Max(0, end - start));

                var id = spans[i].value;
                if (string.IsNullOrEmpty(id)) continue;

                items.Add(new VodItem
                {
                    Id = id,
                    Name = HtmlRules.StripHtml(HtmlRules.Extract(seg, section["vod_name"])),
                    Pic = Resolve(HtmlRules.Extract(seg, section["vod_pic"])),
                    Remarks = HtmlRules.StripHtml(HtmlRules.Extract(seg, section["vod_remarks"]))
                });
            }
            return items;
        }

        // ===== 详情 =====
        public async Task<VodDetail?> GetDetail(string id)
        {
            var dn = _cfg["detail"];
            var tpl = dn?["url"]?.ToString();
            if (string.IsNullOrEmpty(tpl)) return null;
            var url = Resolve(HtmlRules.ApplyTemplate(tpl!, null, 1, null, id));
            var html = await GetHtml(url);
            var body = HtmlRules.CutRegion(html, dn?["region"]);
            if (string.IsNullOrEmpty(body)) body = html;

            var d = new VodDetail
            {
                Id = id,
                Name = HtmlRules.StripHtml(HtmlRules.Extract(body, dn?["vod_name"])),
                Pic = Resolve(HtmlRules.Extract(body, dn?["vod_pic"])),
                Actor = HtmlRules.StripHtml(HtmlRules.Extract(body, dn?["vod_actor"])),
                Director = HtmlRules.StripHtml(HtmlRules.Extract(body, dn?["vod_director"])),
                Area = HtmlRules.StripHtml(HtmlRules.Extract(body, dn?["vod_area"])),
                Year = HtmlRules.StripHtml(HtmlRules.Extract(body, dn?["vod_year"])),
                TypeName = HtmlRules.StripHtml(HtmlRules.Extract(body, dn?["vod_type"])),
                Remarks = HtmlRules.StripHtml(HtmlRules.Extract(body, dn?["vod_remarks"])),
                Content = HtmlRules.StripHtml(HtmlRules.Extract(body, dn?["vod_content"]))
            };
            if (string.IsNullOrEmpty(d.Name)) d.Name = id;

            // 播放列表：优先独立的 playlist 段，其次 detail 段内联
            var pn = _cfg["playlist"] ?? dn;
            BuildPlayLines(d, html, pn, url);
            return d;
        }

        private void BuildPlayLines(VodDetail d, string pageHtml, JsonNode? pn, string pageUrl)
        {
            if (pn == null) return;
            var body = HtmlRules.CutRegion(pageHtml, pn["region"]);
            if (string.IsNullOrEmpty(body)) body = pageHtml;

            var urls = HtmlRules.ExtractAll(body, pn["vod_play_url"]);
            if (urls.Count == 0) return;

            // 剧集名（可选）
            var titles = HtmlRules.ExtractAll(body, pn["vod_play_url_title"]);

            // 线路名：字符串取单条；数组按能否整除均分，否则并成一条
            string singleName = "线路1";
            var lineNames = new List<string>();
            var fromNode = pn["vod_play_from"];
            if (fromNode is JsonArray fa)
            {
                foreach (var x in fa) lineNames.Add(x?.ToString() ?? "");
            }
            else if (fromNode != null)
            {
                singleName = fromNode.ToString();
            }

            bool sortDesc = (pn["sort"]?.ToString() == "1");

            void Fill(PlayLine line, List<string> segUrls, int offset)
            {
                for (int k = 0; k < segUrls.Count; k++)
                {
                    var u = ResolveSeg(segUrls[k], pageUrl);
                    if (string.IsNullOrEmpty(u)) continue;
                    var nm = (titles.Count == urls.Count)
                        ? HtmlRules.StripHtml(titles[offset + k])
                        : $"第{offset + k + 1}集";
                    if (string.IsNullOrEmpty(nm)) nm = $"第{offset + k + 1}集";
                    line.Episodes.Add(new Episode { Name = nm, Url = u });
                }
                if (sortDesc) line.Episodes.Reverse();
            }

            if (lineNames.Count > 1 && urls.Count % lineNames.Count == 0)
            {
                int per = urls.Count / lineNames.Count;
                for (int i = 0; i < lineNames.Count; i++)
                {
                    var line = new PlayLine { Name = string.IsNullOrEmpty(lineNames[i]) ? $"线路{i + 1}" : lineNames[i] };
                    Fill(line, urls.GetRange(i * per, per), i * per);
                    if (line.Episodes.Count > 0) d.Lines.Add(line);
                }
            }
            else
            {
                var line = new PlayLine { Name = lineNames.Count > 0 && !string.IsNullOrEmpty(lineNames[0]) ? lineNames[0] : singleName };
                Fill(line, urls, 0);
                if (line.Episodes.Count > 0) d.Lines.Add(line);
            }
        }

        // ===== 搜索 =====
        public bool Searchable
        {
            get
            {
                var s = _cfg["search"]?["url"]?.ToString();
                var ln = _cfg["list"];
                var t = s ?? ln?["searchUrl"]?.ToString() ?? ln?["searchurl"]?.ToString();
                return !string.IsNullOrEmpty(t);
            }
        }

        public async Task<List<VodItem>> Search(string keyword, int page)
        {
            var sn = _cfg["search"];
            var tpl = sn?["url"]?.ToString();
            var listNode = _cfg["list"];
            tpl ??= listNode?["searchUrl"]?.ToString() ?? listNode?["searchurl"]?.ToString();
            if (string.IsNullOrEmpty(tpl)) return new List<VodItem>();
            var url = Resolve(HtmlRules.ApplyTemplate(tpl!, null, page, keyword));
            var html = await GetHtml(url);
            // 搜索段若有自己的规则优先用它，否则回落到 list 规则
            var section = sn ?? listNode;
            return ExtractList(html, section, null);
        }

        // ===== 工具 =====
        private async Task<string> GetHtml(string url)
        {
            try { return await _http.GetStringAsync(url); }
            catch { return ""; }
        }

        private string Resolve(string? url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (url!.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
            if (!string.IsNullOrEmpty(_homeUrl))
            {
                try { return new Uri(new Uri(_homeUrl!), url).ToString(); } catch { }
            }
            return url;
        }

        private static string ResolveSeg(string seg, string pageUrl)
        {
            if (string.IsNullOrEmpty(seg)) return "";
            seg = seg.Trim();
            if (seg.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return seg;
            try { return new Uri(new Uri(pageUrl), seg).ToString(); } catch { return seg; }
        }
    }
}
