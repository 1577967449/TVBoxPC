using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TVBoxPC.Core.Drpy
{
    /// <summary>
    /// drpy 规则对象（站点脚本里的 `var rule = {...}`）的强类型视图。
    /// 字段语义完全对齐 drpy2：推荐=首页、一级=分类列表、二级=详情、搜索=搜索、lazy=免嗅。
    /// </summary>
    public sealed class DrpyRule
    {
        public JsonObject Raw { get; }

        public string Title = "";
        public string Host = "";
        public string HomeUrl = "";
        public string SearchUrl = "";
        public string DetailUrl = "";
        public string Url = "";

        public string ClassName = "";
        public string ClassUrl = "";
        public string ClassParse = "";

        public bool Searchable = true;
        public bool QuickSearch;
        public bool Filterable;
        public bool PlayParse;
        public bool Multi;

        public string Lazy = "";
        public string ImgSource = "";   // 图片来源：追加在图片地址后的查询串
        public string ImgReplace = "";  // 图片替换：a=>b 或 js:
        public string DetailBefore = ""; // 二级访问前
        public string TabExclude = "";
        public int Limit;
        public int TimeoutSeconds = 20;

        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

        public JsonNode? HomeRule;    // 推荐
        public JsonNode? CateRule;    // 一级
        public JsonNode? DetailRule;  // 二级
        public JsonNode? SearchRule;  // 搜索

        public string Filter = "";
        public string FilterUrl = "";

        public bool IsJsonHome => RulePrefix(HomeRule) == "json:";
        public bool IsJsonCate => RulePrefix(CateRule) == "json:";
        public bool IsJsonSearch => RulePrefix(SearchRule) == "json:";

        private DrpyRule(JsonObject raw) => Raw = raw;

        /// <summary>从 drpy 导出的 JSON 构建规则对象，并把 host 相对地址补全为绝对地址。</summary>
        public static DrpyRule From(JsonObject o)
        {
            var r = new DrpyRule(o);

            r.Title = Str(o, "title");
            r.Host = Str(o, "host");
            r.HomeUrl = Str(o, "homeUrl");
            r.SearchUrl = Str(o, "searchUrl");
            r.DetailUrl = Str(o, "detailUrl");
            r.Url = Str(o, "url");

            r.ClassName = Str(o, "class_name");
            r.ClassUrl = Str(o, "class_url");
            r.ClassParse = Str(o, "class_parse");

            r.Searchable = Bool(o, "searchable", true);
            r.QuickSearch = Bool(o, "quickSearch", false);
            r.Filterable = Bool(o, "filterable", false);
            r.PlayParse = Bool(o, "play_parse", false);
            r.Multi = Bool(o, "multi", false);

            r.Lazy = Str(o, "lazy");
            r.ImgSource = Str(o, "图片来源");
            r.ImgReplace = Str(o, "图片替换");
            r.DetailBefore = Str(o, "二级访问前");
            r.TabExclude = Str(o, "tab_exclude");
            r.Limit = (int)Num(o, "limit", 0);
            var to = Num(o, "timeout", 20000);
            r.TimeoutSeconds = (int)Math.Clamp(to > 1000 ? to / 1000 : to, 3, 60);

            r.HomeRule = o["推荐"]?.DeepClone() ?? o["home"]?.DeepClone();
            r.CateRule = o["一级"]?.DeepClone() ?? o["category"]?.DeepClone();
            r.DetailRule = o["二级"]?.DeepClone() ?? o["detail"]?.DeepClone();
            r.SearchRule = o["搜索"]?.DeepClone() ?? o["search"]?.DeepClone();

            r.Filter = Str(o, "filter");
            r.FilterUrl = Str(o, "filter_url");

            // headers：drpy 里 UA 常用占位符 MOBILE_UA / PC_UA
            if (o["headers"] is JsonObject hs)
                foreach (var kv in hs)
                    r.Headers[kv.Key] = kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                        ? HttpFactory.ResolveUserAgent(kv.Value?.ToString())
                        : kv.Value?.ToString() ?? "";

            r.NormalizeUrls();
            return r;
        }

        /// <summary>
        /// 与 drpy 的规则初始化一致：host + 相对地址 → 绝对地址。
        /// drpy 里这步在 init 阶段完成，之后模板里的 fyclass/fypage 才能正确替换。
        /// </summary>
        private void NormalizeUrls()
        {
            if (!string.IsNullOrEmpty(Host))
            {
                if (!string.IsNullOrEmpty(HomeUrl)) HomeUrl = HtmlSelect.UrlJoin(Host, HomeUrl);
                if (!string.IsNullOrEmpty(DetailUrl)) DetailUrl = HtmlSelect.UrlJoin(Host, DetailUrl);
                if (!string.IsNullOrEmpty(SearchUrl)) SearchUrl = HtmlSelect.UrlJoin(Host, SearchUrl);
                if (!string.IsNullOrEmpty(Url))
                {
                    // drpy 允许 url 写成 "前缀[第一页|后续页]" 形式
                    if (Url.Contains('[') && Url.Contains(']'))
                    {
                        var i = Url.IndexOf('[');
                        var j = Url.IndexOf(']');
                        if (i > 0 && j > i)
                        {
                            var a = Url.Substring(0, i);
                            var b = Url.Substring(i + 1, j - i - 1);
                            Url = HtmlSelect.UrlJoin(Host, a) + "[" + HtmlSelect.UrlJoin(Host, b) + "]";
                        }
                    }
                    else
                    {
                        Url = HtmlSelect.UrlJoin(Host, Url);
                    }
                }
            }

            if (string.IsNullOrEmpty(HomeUrl)) HomeUrl = Host;
        }

        /// <summary>分类名/分类 id 列表（来自 class_name / class_url）。</summary>
        public List<(string id, string name)> GetClassList()
        {
            var list = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(ClassName)) return list;

            var names = ClassName.Split('&');
            var ids = ClassUrl.Split('&');
            for (int i = 0; i < names.Length; i++)
            {
                var n = names[i].Trim();
                if (n.Length == 0) continue;
                var id = i < ids.Length && ids[i].Trim().Length > 0 ? ids[i].Trim() : n;
                list.Add((id, n));
            }
            return list;
        }

        /// <summary>取出规则串的解析模式前缀（json: / jsp: / jq: / js:），无前缀返回 ""。</summary>
        public static string RulePrefix(JsonNode? node)
        {
            if (node is not JsonValue v) return "";
            var s = v.ToString() ?? "";
            if (s.StartsWith("json:", StringComparison.OrdinalIgnoreCase)) return "json:";
            if (s.StartsWith("jsp:", StringComparison.OrdinalIgnoreCase)) return "jsp:";
            if (s.StartsWith("jq:", StringComparison.OrdinalIgnoreCase)) return "jq:";
            if (s.StartsWith("js:", StringComparison.OrdinalIgnoreCase)) return "js:";
            return "";
        }

        public static string StrValue(JsonNode? node)
        {
            if (node == null) return "";
            return node is JsonValue v ? v.ToString() ?? "" : node.ToJsonString();
        }

        private static string Str(JsonObject o, string key)
            => o.TryGetPropertyValue(key, out var v) && v != null ? StrValue(v) : "";

        private static bool Bool(JsonObject o, string key, bool dflt)
        {
            if (!o.TryGetPropertyValue(key, out var v) || v == null) return dflt;
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue<bool>(out var b)) return b;
                var s = jv.ToString();
                if (bool.TryParse(s, out var pb)) return pb;
                if (int.TryParse(s, out var pi)) return pi != 0;
            }
            return dflt;
        }

        private static double Num(JsonObject o, string key, double dflt)
        {
            if (!o.TryGetPropertyValue(key, out var v) || v == null) return dflt;
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue<double>(out var d)) return d;
                if (double.TryParse(jv.ToString(), out var p)) return p;
            }
            return dflt;
        }
    }
}
