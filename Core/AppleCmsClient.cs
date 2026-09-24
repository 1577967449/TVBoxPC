using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 苹果CMS 源客户端（TVBox 里 type:0 / type:1 的点播源）。
    /// 直接 HTTP 拉取，无需浏览器/代理；端点规则：
    ///   分类    api?ac=list
    ///   列表    api?ac=videolist&amp;pg=&amp;t=（t 为分类 id，可空=最新）
    ///   详情    api?ac=detail&amp;ids=
    ///   搜索    api?ac=videolist&amp;wd=
    /// </summary>
    public class AppleCmsClient
    {
        private static readonly HttpClient Http = HttpFactory.Create(25);

        private static string BuildApi(string api, Dictionary<string, string> p)
        {
            // 有些源默认返回 XML（如 .../api.php/provide/vod/at/xml/），统一改成 JSON 才能解析
            api = api.Replace("/at/xml/", "/at/json/").Replace("/at/xml", "/at/json");
            var sep = api.Contains('?') ? '&' : '?';
            var q = string.Join('&', System.Linq.Enumerable.Select(p, kv => Uri.EscapeDataString(kv.Key) + '=' + Uri.EscapeDataString(kv.Value)));
            return api + sep + q;
        }

        public async Task<JsonNode?> Fetch(string api, Dictionary<string, string> p)
        {
            var url = BuildApi(api, p);
            var json = await Http.GetStringAsync(url);
            return JsonNode.Parse(json);
        }

        public async Task<List<(string Id, string Name)>> GetCategories(string api)
        {
            try
            {
                var node = await Fetch(api, new() { { "ac", "list" } });
                var cls = node?["class"];
                var list = new List<(string, string)>();
                if (cls is JsonArray arr)
                    foreach (var c in arr)
                        list.Add((c?["type_id"]?.ToString() ?? "", c?["type_name"]?.ToString() ?? ""));
                return list;
            }
            catch { return new(); }
        }

        public async Task<List<JsonNode>> GetList(string api, int pg, string? t)
        {
            var p = new Dictionary<string, string> { { "ac", "videolist" }, { "pg", pg.ToString() } };
            if (!string.IsNullOrEmpty(t)) p["t"] = t!;
            var node = await Fetch(api, p);
            var list = new List<JsonNode>();
            if (node?["list"] is JsonArray arr)
                foreach (var x in arr) list.Add(x!);
            return list;
        }

        public async Task<JsonNode?> GetDetail(string api, string ids)
        {
            var node = await Fetch(api, new() { { "ac", "detail" }, { "ids", ids } });
            if (node?["list"] is JsonArray arr && arr.Count > 0) return arr[0];
            return null;
        }

        public async Task<List<JsonNode>> Search(string api, string wd, int pg = 1)
        {
            var node = await Fetch(api, new() { { "ac", "videolist" }, { "wd", wd }, { "pg", pg.ToString() } });
            var list = new List<JsonNode>();
            if (node?["list"] is JsonArray arr)
                foreach (var x in arr) list.Add(x!);
            return list;
        }
    }
}
