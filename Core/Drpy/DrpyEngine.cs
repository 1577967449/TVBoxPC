using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Jint;
using Jint.Native;

namespace TVBoxPC.Core.Drpy
{
    /// <summary>播放解析结果（对应 drpy 的 lazy_play 对象）。</summary>
    public sealed class DrpyPlayInfo
    {
        public string Url = "";
        public int Parse = 1;
        public int Jx;
        public string Flag = "";
        public Dictionary<string, object> Extra = new();
    }

    /// <summary>
    /// drpy 规则执行引擎（原生 C#）。
    ///
    /// 忠实复刻 drpy2 的四段解析语义：
    ///   · categoryParse —— 分类列表（fyclass / fypage / [第一页|后续页] / （表达式）分页）
    ///   · searchParse   —— 搜索（** 关键词占位、get/post/postjson）
    ///   · detailParse   —— 详情（二级 = "*" / "js:" / 结构化对象）
    ///   · playParse     —— 免嗅（lazy 片段 + parse/jx 决策）
    /// 列表字段下标与 drpy 一致：p0 列表、p1 名称、p2 图片、p3 备注、p4 链接（可 `+` 拼接、`*` 继承一级）。
    /// </summary>
    public sealed class DrpyEngine : IDisposable
    {
        public DrpyRule Rule { get; }
        public bool JsMode { get; private set; }
        /// <summary>引擎执行日志（规则脚本里的 print/log、以及内部报错），便于排错。</summary>
        public IReadOnlyList<string> Logs => _js.Logs;

        private readonly DrpyJs _js;
        private readonly HttpClient _http;
        private readonly string _siteBase;
        private string _lastHomeHtml = "";

        private DrpyEngine(DrpyRule rule, DrpyJs js, HttpClient http, string siteBase, bool jsMode)
        {
            Rule = rule;
            _js = js;
            _http = http;
            _siteBase = siteBase;
            JsMode = jsMode;
        }

        // ==================== 加载 ====================

        /// <summary>加载并初始化一个 drpy 站点。</summary>
        public static async Task<DrpyEngine> CreateAsync(string siteScriptPath, string siteBase, string ruleKey,
            string? expectHost = null)
        {
            var scriptUrl = HtmlSelect.UrlJoin(siteBase, siteScriptPath);
            var http = HttpFactory.Create(30, withCookies: true);

            string code;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var bytes = await http.GetByteArrayAsync(scriptUrl, cts.Token);
                code = HttpFactory.DecodeText(bytes, "utf-8", "");
            }
            if (string.IsNullOrWhiteSpace(code))
                throw new DrpyScriptException($"站点脚本为空：{scriptUrl}", null);

            var js = new DrpyJs(siteBase, http, ruleKey);
            js.Execute(code);

            // 模式判定：优先 rule 声明式；否则尝试纯 JS 模式（$.home / $.category ...）
            JsonObject? raw = null;
            try { raw = js.GetRuleJson(); } catch { }

            if (raw != null && raw.Count > 0)
            {
                var rule = DrpyRule.From(raw);
                if (!string.IsNullOrEmpty(expectHost) && string.IsNullOrEmpty(rule.Host))
                    rule.Host = expectHost;
                var eng = new DrpyEngine(rule, js, http, siteBase, false);
                eng.InitFetchParams();
                eng.Validate();
                return eng;
            }

            // 纯 JS 模式
            var hasJs = false;
            try
            {
                var v = js.Eval("(typeof $ === 'object' && $ !== null && " +
                                "(typeof $.home === 'function' || typeof $.homeVod === 'function' || " +
                                " typeof $.category === 'function' || typeof $.detail === 'function' || " +
                                " typeof $.search === 'function'))");
                hasJs = v.AsBoolean();
            }
            catch { }

            if (hasJs)
            {
                var empty = DrpyRule.From(new JsonObject { ["title"] = ruleKey, ["host"] = expectHost ?? "" });
                empty.Host = expectHost ?? "";
                var engJs = new DrpyEngine(empty, js, http, siteBase, true);
                engJs.InitFetchParams();
                return engJs;
            }

            throw new DrpyScriptException("该脚本既没有 rule 规则对象，也没有 $.home/$.category 等入口函数。", null);
        }

        private void Validate()
        {
            if (!JsMode && string.IsNullOrEmpty(Rule.Host) && string.IsNullOrEmpty(Rule.HomeUrl)
                && string.IsNullOrEmpty(Rule.Url))
                throw new DrpyScriptException("规则缺少 host / url，无法访问站点。", null);
        }

        /// <summary>构建 drpy 的 rule_fetch_params（默认请求头）。</summary>
        private void InitFetchParams()
        {
            var hdr = new Dictionary<string, object>();
            foreach (var kv in Rule.Headers) hdr[kv.Key] = kv.Value;
            if (!Rule.Headers.ContainsKey("User-Agent"))
                hdr["User-Agent"] = HttpFactory.MobileUserAgent;

            _js.SetGlobal("rule_fetch_params", new Dictionary<string, object> { ["headers"] = hdr });
            _js.SetGlobal("fetch_params", new Dictionary<string, object> { ["headers"] = hdr });
        }

        // ==================== 对外操作 ====================

        public async Task<List<Category>> GetCategoriesAsync()
        {
            var list = new List<Category>();
            var cls = Rule.GetClassList();
            if (cls.Count > 0)
            {
                foreach (var (id, name) in cls) list.Add(new Category { Id = id, Name = name });
                return list;
            }

            // class_parse：从首页源码里解析分类（形如 ".nav li;a&&Text;a&&href"）
            if (!string.IsNullOrWhiteSpace(Rule.ClassParse))
            {
                try
                {
                    var parts = Rule.ClassParse.Split(';');
                    if (parts.Length >= 3)
                    {
                        var html = await FetchAsync(Rule.HomeUrl);
                        _lastHomeHtml = html;
                        var nodes = HtmlSelect.Pdfa(html, parts[0]);
                        foreach (var el in nodes)
                        {
                            var name = HtmlSelect.Pdfh(el, parts[1]).Trim();
                            var href = HtmlSelect.Pdfh(el, parts[2]).Trim();
                            if (name.Length == 0) continue;
                            list.Add(new Category { Id = href, Name = name });
                        }
                    }
                }
                catch { }
            }

            if (list.Count == 0)
                list.Add(new Category { Id = "__home__", Name = "推荐" });
            return list;
        }

        /// <summary>首页推荐（无分类时也用它当默认内容）。</summary>
        public async Task<List<VodItem>> HomeAsync()
        {
            if (JsMode) return await JsCallList("home", Array.Empty<object>());

            if (Rule.HomeRule == null) return new List<VodItem>();

            if (DrpyRule.RulePrefix(Rule.HomeRule) == "js:")
            {
                var url = Rule.HomeUrl;
                var r = await RunJsListAsync(DrpyRule.StrValue(Rule.HomeRule), new Dictionary<string, object?>
                {
                    ["MY_URL"] = url, ["input"] = url, ["TYPE"] = "home", ["MY_CATE"] = "", ["MY_PAGE"] = 1
                });
                return r;
            }

            // 首页与分类同构：借用 categoryParse 的字段布局
            if (Rule.HomeRule is JsonValue hv)
            {
                var items = await ParseListAsync(DrpyRule.StrValue(Rule.HomeRule), Rule.HomeUrl, "", 1,
                    false, "");
                return items;
            }
            return new List<VodItem>();
        }

        /// <summary>分类列表（对应 categoryParse）。</summary>
        public async Task<List<VodItem>> CategoryAsync(string tid, int pg)
        {
            if (JsMode) return await JsCallList("category", new object[] { tid, pg, false, new Dictionary<string, object>() });

            if (tid == "__home__" || string.IsNullOrEmpty(tid)) return await HomeAsync();

            var ruleNode = Rule.CateRule ?? Rule.HomeRule;
            var ruleStr = DrpyRule.StrValue(ruleNode);
            if (string.IsNullOrWhiteSpace(ruleStr)) return new List<VodItem>();

            var url = BuildCategoryUrl(Rule.Url, tid, pg);
            _js.SetGlobal("MY_CATE", tid);

            if (ruleStr.Trim().StartsWith("js:"))
                return await RunJsListAsync(ruleStr, new Dictionary<string, object?>
                {
                    ["MY_URL"] = url, ["input"] = url, ["MY_CATE"] = tid,
                    ["MY_PAGE"] = pg, ["TYPE"] = "cate"
                });

            return await ParseListAsync(ruleStr, url, tid, pg,
                Rule.IsJsonCate || IsJsonPrefixed(ruleNode),
                Rule.DetailUrl.Length > 0 ? Rule.DetailUrl : "");
        }

        /// <summary>搜索（对应 searchParse）。</summary>
        public async Task<List<VodItem>> SearchAsync(string wd, int pg)
        {
            if (JsMode) return await JsCallList("search", new object[] { wd, true, pg });

            if (string.IsNullOrWhiteSpace(Rule.SearchUrl)) return new List<VodItem>();

            // 搜索 === "*" 时复用一级规则（drpy 语义）
            var searchStr = DrpyRule.StrValue(Rule.SearchRule);
            if (string.IsNullOrWhiteSpace(searchStr) || searchStr.Trim() == "*")
                searchStr = DrpyRule.StrValue(Rule.CateRule) ?? "";
            if (string.IsNullOrWhiteSpace(searchStr)) return new List<VodItem>();

            var url = BuildSearchUrl(Rule.SearchUrl, wd, pg);
            _js.SetGlobal("MY_CATE", "");

            if (searchStr.Trim().StartsWith("js:"))
                return await RunJsListAsync(searchStr, new Dictionary<string, object?>
                {
                    ["MY_URL"] = url, ["input"] = url, ["KEY"] = wd,
                    ["MY_PAGE"] = pg, ["TYPE"] = "search"
                });

            // 搜索地址可用 `url;post` / `url;postjson` 指定请求方式
            var postMode = "";
            var semi = url.Split(';');
            if (semi.Length > 1)
            {
                var m = semi[1].Trim().ToLowerInvariant();
                if (m.StartsWith("post")) postMode = m;
            }

            return await ParseListAsync(searchStr, url, "", pg,
                IsJsonPrefixed(Rule.SearchRule), Rule.DetailUrl, postMode);
        }

        /// <summary>详情（对应 detailParse）。</summary>
        public async Task<VodDetail?> DetailAsync(string id)
        {
            if (JsMode) return await JsCallDetail(id);

            // drpy：id 里含 $ 时，前半是分类、后半是详情地址
            var fyclass = "";
            var vodUrl = id;
            if (id.Contains('$'))
            {
                var t = id.Split('$');
                if (t.Length >= 2) { fyclass = t[0]; vodUrl = t[1]; }
            }

            var detailUrlPart = vodUrl.Split("@@")[0];
            string url;
            if (!detailUrlPart.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !detailUrlPart.Contains('/'))
                url = Rule.DetailUrl.Replace("fyid", detailUrlPart).Replace("fyclass", fyclass);
            else if (detailUrlPart.Contains('/'))
                url = HtmlSelect.UrlJoin(Rule.HomeUrl, detailUrlPart);
            else
                url = detailUrlPart;

            _js.SetGlobal("MY_URL", url);
            _js.SetGlobal("input", url);

            // 二级访问前
            if (!string.IsNullOrWhiteSpace(Rule.DetailBefore))
            {
                try
                {
                    _js.RunBlock(Rule.DetailBefore, new Dictionary<string, object?> { ["input"] = url, ["MY_URL"] = url });
                }
                catch { }
            }

            var p = Rule.DetailRule;
            var pStr = DrpyRule.StrValue(p).Trim();

            // ---- 情形一：一级直链嗅探 ----
            if (pStr == "*")
            {
                var ex = id.Split("@@");
                var name = ex.Length > 1 ? ex[1] : "片名";
                var pic = ex.Length > 2 ? ex[2] : "";
                return new VodDetail
                {
                    Id = id,
                    Name = name,
                    Pic = pic,
                    Remarks = detailUrlPart,
                    Content = url,
                    Actor = "没有二级,只有一级链接直接嗅探播放",
                    Lines = new List<PlayLine>
                    {
                        new PlayLine { Name = "道长在线", Episodes = new List<Episode>
                            { new Episode { Name = "嗅探播放", Url = vodUrl.Split("@@")[0] } } }
                    }
                };
            }

            // ---- 情形二：内联 JS ----
            if (pStr.StartsWith("js:"))
            {
                _js.SetGlobal("VOD", new Dictionary<string, object>());
                try
                {
                    var ret = _js.RunBlock(pStr, new Dictionary<string, object?>
                    {
                        ["input"] = url, ["MY_URL"] = url, ["MY_CATE"] = fyclass, ["TYPE"] = "detail"
                    });
                    var vodJson = _js.Eval("JSON.stringify(VOD)").AsString();
                    var detail = ParseVodJson(vodJson, id);
                    if (detail != null) { ApplyImgFix(detail); return detail; }

                    // 片段直接 return 了对象
                    if (ret.IsObject() || ret.IsArray())
                        return ParseVodJson(_js.StringifyValue(ret), id);
                }
                catch (Exception ex)
                {
                    _js.Log("二级 js 执行失败: " + ex.GetType().Name + ": " + ex.Message);
                }
                return null;
            }

            // ---- 情形三：结构化二级对象 ----
            if (p is JsonObject po)
                return await DetailFromObjectAsync(po, url, id, fyclass);

            // ---- 情形四：二级为空 —— 做一次通用兜底抽取，避免整条链断掉 ----
            return await GenericDetailAsync(url, id);
        }

        /// <summary>免嗅解析（对应 playParse）。</summary>
        public async Task<DrpyPlayInfo> PlayAsync(string flag, string url)
        {
            var myUrl = url;
            if (!myUrl.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var b = Convert.FromBase64String(Pad64(myUrl));
                    myUrl = System.Text.Encoding.UTF8.GetString(b);
                }
                catch { }
            }
            try { myUrl = Uri.UnescapeDataString(myUrl); } catch { }

            var input = myUrl;
            var common = new DrpyPlayInfo
            {
                Url = input,
                Flag = flag,
                Parse = IsSpecialUrl(input) ? 0 : 1,
                Jx = TellIsJx(input) ? 1 : 0
            };

            var lazyInfo = common;
            if (Rule.PlayParse && !string.IsNullOrWhiteSpace(Rule.Lazy))
            {
                try
                {
                    var lazyCode = Rule.Lazy.Trim();
                    var ret = _js.RunBlock(lazyCode, new Dictionary<string, object?>
                    {
                        ["input"] = input, ["MY_URL"] = input, ["flag"] = flag, ["MY_FLAG"] = flag
                    });

                    // drpy 的约定：lazy 片段结束后回读全局 input，
                    //   是对象 -> 取对象里的 url/parse/jx；是字符串 -> 直接当播放地址。
                    var after = _js.GetGlobal("input");
                    if (after.IsObject())
                    {
                        var o = after.AsObject();
                        lazyInfo = new DrpyPlayInfo { Url = input, Flag = flag };
                        var u = o.Get("url");
                        if (!u.IsUndefined() && !u.IsNull()) lazyInfo.Url = u.ToString();
                        var pr = o.Get("parse");
                        if (!pr.IsUndefined() && pr.IsNumber()) lazyInfo.Parse = (int)pr.AsNumber();
                        var jx = o.Get("jx");
                        if (!jx.IsUndefined() && jx.IsNumber()) lazyInfo.Jx = (int)jx.AsNumber();
                        try
                        {
                            foreach (var kv in o.GetOwnProperties())
                            {
                                var name = kv.Key.ToString();
                                var val = kv.Value?.Value;
                                if (string.IsNullOrEmpty(name) || val == null || val.IsUndefined()) continue;
                                lazyInfo.Extra[name] = val.ToString() ?? "";
                            }
                        }
                        catch { }
                    }
                    else if (after.IsString())
                    {
                        lazyInfo.Url = after.AsString();
                    }
                    else if (ret.IsObject())
                    {
                        // 少数规则用 return 返回对象
                        var o = ret.AsObject();
                        lazyInfo = new DrpyPlayInfo { Url = input, Flag = flag };
                        var u = o.Get("url");
                        if (!u.IsUndefined() && !u.IsNull()) lazyInfo.Url = u.ToString();
                    }
                }
                catch (Exception ex)
                {
                    _js.Log("免嗅失败: " + ex.Message);
                }
            }

            // drpy：未声明 play_json 时统一给 {jx:0, parse:1}
            if (Rule.Raw["play_json"] == null)
            {
                lazyInfo.Jx = 0;
                lazyInfo.Parse = 1;
            }

            await Task.CompletedTask;
            return lazyInfo;
        }

        // ==================== 列表解析 ====================

        /// <summary>
        /// 通用列表解析（categoryParse / searchParse 的公共部分）。
        /// 字段下标与 drpy 完全一致：0 列表、1 名称、2 图片、3 备注、4 链接。
        /// </summary>
        private async Task<List<VodItem>> ParseListAsync(string ruleStr, string url, string tid, int pg,
            bool isJson, string detailUrl, string postMode = "")
        {
            var outp = new List<VodItem>();
            var raw = ruleStr.Trim();
            if (raw.StartsWith("json:")) { isJson = true; raw = raw.Substring(5); }
            else if (raw.StartsWith("jsp:")) raw = raw.Substring(4);
            else if (raw.StartsWith("jq:")) raw = raw.Substring(3);

            var p = raw.Split(';');
            if (p.Length < 5) return outp;

            // 一级规则用于补全 "*" 字段
            var fallback = DrpyRule.StrValue(Rule.CateRule).Trim();
            if (fallback.StartsWith("js:")) fallback = "";
            var pp = fallback.Length > 0
                ? fallback.Replace("json:", "").Replace("jsp:", "").Replace("jq:", "").Split(';')
                : Array.Empty<string>();

            var p1 = GetPP(p, 1, pp, 1);
            var p2 = GetPP(p, 2, pp, 2);
            var p3 = GetPP(p, 3, pp, 3);
            var p4 = GetPP(p, 4, pp, 4);

            string html;
            try
            {
                html = postMode.Length > 0
                    ? await FetchPostAsync(url, postMode)
                    : await FetchAsync(url);
            }
            catch (Exception ex)
            {
                _js.Log($"列表请求失败 {url} : {ex.Message}");
                return outp;
            }
            if (string.IsNullOrWhiteSpace(html)) return outp;

            _js.SetGlobal("MY_URL", url);
            _lastHomeHtml = html;

            try
            {
                if (isJson)
                {
                    var jsonText = DrpyJs.DealJson(html);
                    JsonNode? root = null;
                    try { root = JsonNode.Parse(jsonText); } catch { }

                    if (root != null)
                    {
                        var arr = JsonPathLite.Eval(root, p[0]);
                        foreach (var node in EnumerateArray(arr))
                        {
                            var name = JsonField(node, p1).Trim();
                            if (name.Length == 0) continue;
                            var pic = HtmlSelect.UrlJoin(url, JsonField(node, p2));
                            var link = JsonLink(node, p4, detailUrl);
                            outp.Add(new VodItem
                            {
                                Id = detailUrl.Length > 0 && !string.IsNullOrEmpty(tid) ? tid + "$" + link : link,
                                Name = name,
                                Pic = pic,
                                Remarks = JsonField(node, p3).Trim()
                            });
                        }
                    }
                }
                else
                {
                    var list = HtmlSelect.Pdfa(html, p[0]);
                    foreach (var it in list)
                    {
                        var name = HtmlSelect.Pdfh(it, p1).Replace("\n", "").Replace("\t", "").Trim();
                        if (name.Length == 0) continue;

                        var link = string.Join("$", p4.Split('+')
                            .Select(x => detailUrl.Length > 0
                                ? HtmlSelect.Pdfh(it, x)
                                : HtmlSelect.Pd(it, x, url)));
                        var pic = HtmlSelect.Pd(it, p2, url);

                        outp.Add(new VodItem
                        {
                            Id = detailUrl.Length > 0 && !string.IsNullOrEmpty(tid) ? tid + "$" + link : link,
                            Name = name,
                            Pic = pic,
                            Remarks = HtmlSelect.Pdfh(it, p3).Replace("\n", "").Replace("\t", "").Trim()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _js.Log("列表解析失败: " + ex.Message);
            }

            ApplyImgFix(outp);
            return outp;
        }

        /// <summary>执行列表类 js 片段：优先取 return 值，其次取全局 VODS。</summary>
        private async Task<List<VodItem>> RunJsListAsync(string code, Dictionary<string, object?> vars)
        {
            _js.SetGlobal("VODS", new List<object>());
            var outp = new List<VodItem>();
            try
            {
                var ret = _js.RunBlock(code, vars);
                string json;
                if (ret.IsArray() || ret.IsObject()) json = _js.StringifyValue(ret);
                else json = _js.Eval("JSON.stringify(VODS)").AsString();

                var arr = JsonNode.Parse(json) as JsonArray;
                if (arr == null) return outp;
                foreach (var n in arr)
                {
                    if (n == null) continue;
                    outp.Add(new VodItem
                    {
                        Id = FieldOf(n, "vod_id"),
                        Name = FieldOf(n, "vod_name"),
                        Pic = FieldOf(n, "vod_pic"),
                        Remarks = FieldOf(n, "vod_remarks")
                    });
                }
            }
            catch (Exception ex)
            {
                _js.Log("列表 js 执行失败: " + ex.GetType().Name + ": " + ex.Message);
            }
            await Task.CompletedTask;
            ApplyImgFix(outp);
            return outp;
        }

        // ==================== 详情解析 ====================

        private async Task<VodDetail?> DetailFromObjectAsync(JsonObject po, string url, string id, string fyclass)
        {
            string html;
            try { html = await FetchAsync(url); }
            catch (Exception ex)
            {
                _js.Log("详情请求失败: " + ex.Message);
                return null;
            }

            var isJson = GetBool(po, "is_json");
            if (isJson) html = DrpyJs.DealJson(html);

            var d = new VodDetail { Id = id, Name = "片名" };
            _js.SetGlobal("MY_URL", url);

            // 重定向：允许先执行一段 js 换掉 html
            var redirect = GetStr(po, "重定向");
            if (redirect.StartsWith("js:"))
            {
                try
                {
                    var r = _js.RunBlock(redirect, new Dictionary<string, object?> { ["input"] = url, ["MY_URL"] = url });
                    if (r.IsString()) html = r.AsString();
                }
                catch { }
            }

            // 基本信息
            var titleRule = GetStr(po, "title");
            if (titleRule.Length > 0)
            {
                var t = titleRule.Split(';');
                d.Name = Clean(ExtractField(html, t[0], isJson));
                if (t.Length > 1) d.TypeName = Clean(ExtractField(html, t[1], isJson)).Replace(" ", "");
            }

            var descRule = GetStr(po, "desc");
            if (descRule.Length > 0)
            {
                var t = descRule.Split(';');
                d.Remarks = Clean(ExtractField(html, t[0], isJson));
                if (t.Length > 1) d.Year = Clean(ExtractField(html, t[1], isJson));
                if (t.Length > 2) d.Area = Clean(ExtractField(html, t[2], isJson));
                if (t.Length > 3) d.Actor = Clean(ExtractField(html, t[3], isJson));
                if (t.Length > 4) d.Director = Clean(ExtractField(html, t[4], isJson));
            }

            var contentRule = GetStr(po, "content");
            if (contentRule.Length > 0)
                d.Content = Clean(ExtractField(html, contentRule.Split(';')[0], isJson));

            var imgRule = GetStr(po, "img");
            if (imgRule.Length > 0)
                d.Pic = HtmlSelect.UrlJoin(url, ExtractField(html, imgRule.Split(';')[0], isJson));

            // 线路（tabs）
            var tabs = new List<string>();
            var tabsRule = GetStr(po, "tabs");
            if (tabsRule.StartsWith("js:"))
            {
                try
                {
                    _js.RunBlock(tabsRule, new Dictionary<string, object?> { ["input"] = url, ["MY_URL"] = url });
                    var t = _js.Eval("JSON.stringify(TABS)").AsString();
                    if (JsonNode.Parse(t) is JsonArray ta)
                        foreach (var x in ta) tabs.Add(x?.ToString() ?? "");
                }
                catch { }
            }
            else if (tabsRule.Length > 0)
            {
                var tabParts = tabsRule.Split(';');
                var tabText = GetStr(po, "tab_text");
                if (tabText.Length == 0) tabText = "body&&Text";
                foreach (var el in HtmlSelect.Pdfa(html, tabParts[0]))
                {
                    var name = Clean(HtmlSelect.Pdfh(el, tabText));
                    if (name.Length == 0) name = "线路";
                    if (!string.IsNullOrEmpty(Rule.TabExclude) && System.Text.RegularExpressions.Regex.IsMatch(name, Rule.TabExclude))
                        continue;
                    tabs.Add(name);
                }
            }
            if (tabs.Count == 0) tabs.Add("道长在线");

            // 剧集（lists）
            var lines = new List<PlayLine>();
            var listsRule = GetStr(po, "lists");
            if (listsRule.StartsWith("js:"))
            {
                try
                {
                    _js.RunBlock(listsRule, new Dictionary<string, object?> { ["input"] = url, ["MY_URL"] = url });
                    var t = _js.Eval("JSON.stringify(LISTS)").AsString();
                    if (JsonNode.Parse(t) is JsonArray la)
                    {
                        foreach (var group in la)
                        {
                            var pl = new PlayLine();
                            if (group is JsonArray ga)
                                foreach (var item in ga)
                                {
                                    var s = item?.ToString() ?? "";
                                    var idx = s.IndexOf('$');
                                    pl.Episodes.Add(idx > 0
                                        ? new Episode { Name = s.Substring(0, idx), Url = s.Substring(idx + 1) }
                                        : new Episode { Name = "播放", Url = s });
                                }
                            if (pl.Episodes.Count > 0) lines.Add(pl);
                        }
                    }
                }
                catch { }
            }
            else if (listsRule.Length > 0)
            {
                var listText = GetStr(po, "list_text");
                if (listText.Length == 0) listText = "body&&Text";
                var listUrl = GetStr(po, "list_url");
                if (listUrl.Length == 0) listUrl = "a&&href";
                var prefix = GetStr(po, "list_url_prefix");

                for (int i = 0; i < tabs.Count; i++)
                {
                    var p1r = listsRule.Replace("#idv", tabs[i]).Replace("#id", i.ToString());
                    var pl = new PlayLine { Name = tabs[i] };
                    foreach (var el in HtmlSelect.Pdfa(html, p1r))
                    {
                        var epName = Clean(HtmlSelect.Pdfh(el, listText));
                        var epUrl = prefix + HtmlSelect.Pd(el, listUrl, url);
                        if (epUrl.Trim().Length == 0) continue;
                        pl.Episodes.Add(new Episode { Name = epName.Length > 0 ? epName : "播放", Url = epUrl });
                    }
                    if (pl.Episodes.Count > 0) lines.Add(pl);
                }
            }

            d.Lines = lines;
            ApplyImgFix(d);
            return d;
        }

        /// <summary>二级为空时的通用兜底：尽力从页面元信息里取标题/封面/简介，并给一条嗅探线路。</summary>
        private async Task<VodDetail?> GenericDetailAsync(string url, string id)
        {
            var d = new VodDetail { Id = id, Name = "片名" };
            try
            {
                var html = await FetchAsync(url);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    d.Name = Clean(HtmlSelect.Pdfh(html, "h1&&Text"));
                    if (d.Name.Length == 0) d.Name = Clean(HtmlSelect.Pdfh(html, "meta[property=og:title]&&content"));
                    if (d.Name.Length == 0) d.Name = Clean(HtmlSelect.Pdfh(html, "title&&Text"));
                    if (d.Name.Length == 0) d.Name = id;

                    d.Pic = HtmlSelect.Pd(html, "meta[property=og:image]&&content", url);
                    if (d.Pic.Length == 0) d.Pic = HtmlSelect.Pd(html, ".pic img&&src", url);

                    d.Content = Clean(HtmlSelect.Pdfh(html, "meta[property=og:description]&&content"));
                    if (string.IsNullOrWhiteSpace(d.Content)) d.Content = "该源未提供简介。";
                    d.Remarks = "通用解析";
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(d.Name)) d.Name = id;
            d.Lines = new List<PlayLine>
            {
                new PlayLine { Name = "嗅探播放", Episodes = new List<Episode>
                    { new Episode { Name = "播放", Url = url } } }
            };
            return d;
        }

        private VodDetail? ParseVodJson(string json, string id)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
            JsonNode? n;
            try { n = JsonNode.Parse(json); } catch { return null; }

            JsonNode? vod = n;
            if (n is JsonObject o && o["list"] is JsonArray la && la.Count > 0) vod = la[0];
            if (vod is not JsonObject v) return null;

            var d = new VodDetail
            {
                Id = FieldOf(v, "vod_id").Length > 0 ? FieldOf(v, "vod_id") : id,
                Name = FieldOf(v, "vod_name"),
                Pic = FieldOf(v, "vod_pic"),
                TypeName = FieldOf(v, "type_name"),
                Year = FieldOf(v, "vod_year"),
                Area = FieldOf(v, "vod_area"),
                Actor = FieldOf(v, "vod_actor"),
                Director = FieldOf(v, "vod_director"),
                Remarks = FieldOf(v, "vod_remarks"),
                Content = FieldOf(v, "vod_content")
            };

            var from = FieldOf(v, "vod_play_from");
            var playUrl = FieldOf(v, "vod_play_url");
            if (playUrl.Length > 0)
            {
                var flags = from.Length > 0 ? from.Split("$$$") : new[] { "道长在线" };
                var groups = playUrl.Split("$$$");
                for (int i = 0; i < groups.Length; i++)
                {
                    var pl = new PlayLine { Name = i < flags.Length ? flags[i] : "线路" };
                    foreach (var ep in groups[i].Split('#'))
                    {
                        if (string.IsNullOrWhiteSpace(ep)) continue;
                        var idx = ep.IndexOf('$');
                        pl.Episodes.Add(idx > 0
                            ? new Episode { Name = ep.Substring(0, idx), Url = ep.Substring(idx + 1) }
                            : new Episode { Name = "播放", Url = ep });
                    }
                    if (pl.Episodes.Count > 0) d.Lines.Add(pl);
                }
            }
            ApplyImgFix(d);
            return d;
        }

        // ==================== JS 纯模式 ====================

        private async Task<List<VodItem>> JsCallList(string fn, object[] args)
        {
            var outp = new List<VodItem>();
            try
            {
                var call = $"$.{fn}";
                var v = _js.Eval($"(typeof {call} === 'function') ? JSON.stringify(($.{fn}).apply(null, ["
                             + string.Join(",", args.Select(a => a is string s ? "\"" + s.Replace("\"", "\\\"") + "\"" : a.ToString()))
                             + "])) : null");
                if (v.IsNull() || v.IsUndefined()) return outp;
                var json = v.AsString();
                if (JsonNode.Parse(json) is JsonArray arr)
                    foreach (var n in arr)
                        if (n != null)
                            outp.Add(new VodItem
                            {
                                Id = FieldOf(n, "vod_id"),
                                Name = FieldOf(n, "vod_name"),
                                Pic = FieldOf(n, "vod_pic"),
                                Remarks = FieldOf(n, "vod_remarks")
                            });
            }
            catch (Exception ex)
            {
                _js.Log($"$.{fn} 调用失败: " + ex.Message);
            }
            await Task.CompletedTask;
            return outp;
        }

        private async Task<VodDetail?> JsCallDetail(string id)
        {
            try
            {
                var v = _js.Eval("(typeof $.detail === 'function') ? JSON.stringify($.detail(\""
                                 + id.Replace("\"", "\\\"") + "\")) : null");
                if (v.IsNull() || v.IsUndefined()) return null;
                return ParseVodJson(v.AsString(), id);
            }
            catch (Exception ex)
            {
                _js.Log("$.detail 调用失败: " + ex.Message);
                return null;
            }
            finally { await Task.CompletedTask; }
        }

        // ==================== HTTP ====================

        private async Task<string> FetchAsync(string url)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Rule.TimeoutSeconds));
            return await FetchCoreAsync(url, HttpMethod.Get, null, cts.Token);
        }

        private async Task<string> FetchPostAsync(string url, string postMode)
        {
            var seg = url.Split(';');
            var real = seg[0].Split('#');
            var target = real[0];
            var body = real.Length > 1 ? real[1] : "";
            if (postMode == "postjson")
            {
                try { body = JsonNode.Parse(body)?.ToJsonString() ?? "{}"; } catch { body = "{}"; }
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Rule.TimeoutSeconds));
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = postMode == "postjson"
                    ? "application/json;charset=UTF-8"
                    : "application/x-www-form-urlencoded"
            };
            return await FetchCoreAsync(target, HttpMethod.Post, (body, headers), cts.Token);
        }

        private async Task<string> FetchCoreAsync(string url, HttpMethod method,
            (string body, Dictionary<string, string> headers)? post, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(method, url);
            foreach (var kv in Rule.Headers)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            if (!Rule.Headers.ContainsKey("User-Agent"))
                req.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.MobileUserAgent);
            if (!Rule.Headers.ContainsKey("Referer") && !string.IsNullOrEmpty(Rule.Host))
                req.Headers.TryAddWithoutValidation("Referer", Rule.Host);

            if (post != null)
            {
                req.Content = new StringContent(post.Value.body, System.Text.Encoding.UTF8,
                    post.Value.headers.TryGetValue("Content-Type", out var ctv) ? ctv : "application/x-www-form-urlencoded");
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return HttpFactory.DecodeText(bytes, null, resp.Content.Headers.ContentType?.ToString() ?? "");
        }

        // ==================== 小工具 ====================

        private static string GetPP(string[] p, int i, string[] pp, int j)
        {
            try
            {
                if (p[i] == "*" && pp.Length > j) return pp[j];
                return p[i];
            }
            catch { return ""; }
        }

        private static bool IsJsonPrefixed(JsonNode? node)
            => DrpyRule.RulePrefix(node) == "json:";

        /// <summary>构造分类页地址（fyclass / fypage / [第一页|后续页] / （表达式））。</summary>
        public static string BuildCategoryUrl(string urlTpl, string tid, int pg)
        {
            var url = urlTpl.Replace("fyclass", tid);
            url = ApplyFirstPageRule(url, pg);

            if (url.Contains("fypage"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(url, @"\((.*?)\)");
                if (m.Success)
                {
                    var expr = m.Groups[1].Value.Replace("fypage", pg.ToString());
                    var val = EvalIntExpr(expr);
                    url = url.Replace(m.Groups[0].Value, val).Replace("(", "").Replace(")", "");
                }
                else url = url.Replace("fypage", pg.ToString());
            }
            return url;
        }

        /// <summary>构造搜索页地址（** 关键词 / fypage / [第一页|后续页]）。</summary>
        public static string BuildSearchUrl(string urlTpl, string wd, int pg)
        {
            var url = urlTpl.Replace("**", Uri.EscapeDataString(wd));
            if (pg == 1 && url.Contains('[') && url.Contains(']') && !url.Contains('#'))
            {
                var i = url.IndexOf('['); var j = url.IndexOf(']');
                url = url.Substring(i + 1, j - i - 1);
            }
            else if (pg > 1 && url.Contains('[') && url.Contains(']') && !url.Contains('#'))
            {
                var i = url.IndexOf('[');
                url = url.Substring(0, i);
            }

            if (url.Contains("fypage"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(url, @"\((.*?)\)");
                if (m.Success)
                {
                    var expr = m.Groups[1].Value.Replace("fypage", pg.ToString());
                    var val = EvalIntExpr(expr);
                    url = url.Replace(m.Groups[0].Value, val).Replace("(", "").Replace(")", "");
                }
                else url = url.Replace("fypage", pg.ToString());
            }
            return url;
        }

        private static string ApplyFirstPageRule(string url, int pg)
        {
            if (!url.Contains('[') || !url.Contains(']')) return url;
            var i = url.IndexOf('['); var j = url.IndexOf(']');
            if (i < 0 || j <= i) return url;
            var first = url.Substring(i + 1, j - i - 1);
            var rest = url.Substring(0, i);
            return pg == 1 ? first : rest;
        }

        /// <summary>只支持 + - * / 与数字的极简表达式求值（drpy 的 fypage 表达式）。</summary>
        private static string EvalIntExpr(string expr)
        {
            try
            {
                var dt = new System.Data.DataTable();
                var v = dt.Compute(expr, "");
                return Convert.ToInt64(v).ToString();
            }
            catch { return expr; }
        }

        private static bool IsSpecialUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;
            return url.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("thunder:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("push:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断是否是需要「解析」才能播的地址（drpy 的 tellIsJx）。</summary>
        private static bool TellIsJx(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var u = url.ToLowerInvariant();
            if (u.Contains(".m3u8") || u.Contains(".mp4") || u.Contains(".flv")
                || u.Contains(".mkv") || u.Contains(".avi") || u.Contains(".ts")
                || u.Contains(".mp3") || u.Contains(".m4a")) return false;
            return u.StartsWith("http");
        }

        /// <summary>drpy 的 图片来源 / 图片替换 后处理。</summary>
        private void ApplyImgFix(List<VodItem> list)
        {
            foreach (var it in list)
            {
                if (!string.IsNullOrEmpty(Rule.ImgSource) && it.Pic.StartsWith("http"))
                    it.Pic += Rule.ImgSource;
                if (!string.IsNullOrEmpty(Rule.ImgReplace) && Rule.ImgReplace.Contains("=>"))
                {
                    var t = Rule.ImgReplace.Split("=>");
                    if (t.Length >= 2 && it.Pic.StartsWith("http"))
                        it.Pic = it.Pic.Replace(t[0], t[1]);
                }
            }
        }

        private void ApplyImgFix(VodDetail d)
        {
            if (!string.IsNullOrEmpty(Rule.ImgSource) && d.Pic.StartsWith("http"))
                d.Pic += Rule.ImgSource;
            if (!string.IsNullOrEmpty(Rule.ImgReplace) && Rule.ImgReplace.Contains("=>"))
            {
                var t = Rule.ImgReplace.Split("=>");
                if (t.Length >= 2 && d.Pic.StartsWith("http"))
                    d.Pic = d.Pic.Replace(t[0], t[1]);
            }
        }

        private string ExtractField(string html, string selector, bool isJson)
        {
            if (string.IsNullOrWhiteSpace(selector)) return "";
            if (isJson)
            {
                try
                {
                    var root = JsonNode.Parse(DrpyJs.DealJson(html));
                    return JsonPathLite.ToText(JsonPathLite.Eval(root, selector));
                }
                catch { return ""; }
            }
            return HtmlSelect.Pdfh(html, selector);
        }

        private static string Clean(string s)
            => (s ?? "").Replace("\n", "").Replace("\t", "").Trim();

        private static string FieldOf(JsonNode n, string key)
        {
            if (n is JsonObject o && o.TryGetPropertyValue(key, out var v) && v != null)
            {
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return s ?? "";
                return v.ToString();
            }
            return "";
        }

        private string JsonField(JsonNode item, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            // 非链接字段的 `+` 视为字符串拼接
            if (path.Contains('+'))
                return string.Concat(path.Split('+').Select(x => JsonPathLite.ToText(JsonPathLite.Eval(item, x))));
            return JsonPathLite.ToText(JsonPathLite.Eval(item, path));
        }

        private string JsonLink(JsonNode item, string path, string detailUrl)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var parts = path.Split('+').Select(x => JsonPathLite.ToText(JsonPathLite.Eval(item, x)));
            var link = string.Join("$", parts);
            return detailUrl.Length > 0 ? link : HtmlSelect.UrlJoin(Rule.HomeUrl, link);
        }

        private static IEnumerable<JsonNode> EnumerateArray(JsonNode? node)
        {
            if (node is JsonArray a)
                foreach (var x in a) if (x != null) yield return x;
            else if (node != null) yield return node;
        }

        private static string GetStr(JsonObject o, string key)
        {
            if (!o.TryGetPropertyValue(key, out var v) || v == null) return "";
            return v is JsonValue jv ? jv.ToString() ?? "" : v.ToJsonString();
        }

        private static bool GetBool(JsonObject o, string key)
        {
            if (!o.TryGetPropertyValue(key, out var v) || v == null) return false;
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue<bool>(out var b)) return b;
                return jv.ToString() == "true" || jv.ToString() == "1";
            }
            return false;
        }

        private static string Pad64(string s)
        {
            s = s.Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return s;
        }

        public void Dispose()
        {
            try { _js.Dispose(); } catch { }
            try { _http.Dispose(); } catch { }
        }
    }
}
