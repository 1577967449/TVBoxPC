using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 加载 TVBox 配置。同时支持两种格式：
    ///   1) 单仓 JSON（含 sites / parses / lives）
    ///   2) 多仓索引 JSON（含 urls[]，逐层递归拉取子仓并合并 sites/parses/lives）
    /// 兼容 TVBox 配置里常见的 // 行注释与尾部逗号（JsonCommentHandling.Skip + AllowTrailingCommas）。
    /// </summary>
    public class ConfigLoader
    {
        private static readonly HttpClient Http = HttpFactory.Create(25);

        private static readonly JsonDocumentOptions DocOpts = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        public async Task<JsonNode?> LoadFromUrl(string url)
        {
            var json = await Http.GetStringAsync(url);
            return await ParseAndMerge(json, url);
        }

        public async Task<JsonNode?> LoadFromText(string json, string? baseUrl = null)
        {
            return await ParseAndMerge(json, baseUrl);
        }

        private async Task<JsonNode?> ParseAndMerge(string json, string? baseUrl)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(json, documentOptions: DocOpts);
            }
            catch
            {
                // 极个别配置里混入了非标准注释，做一次粗清洗再解析
                root = JsonNode.Parse(Sanitize(json), documentOptions: DocOpts);
            }
            if (root == null) return null;

            // 多仓：urls[] 是子仓地址列表
            if (root["urls"] is JsonArray urls)
            {
                var sites = new JsonArray();
                var parses = new JsonArray();
                var lives = new JsonArray();

                // 复制一份待处理队列（多仓可能多层嵌套）
                var queue = new System.Collections.Generic.List<string>();
                foreach (var u in urls) if (u != null) queue.Add(u.ToString());

                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int guard = 0;
                while (queue.Count > 0 && guard++ < 200)
                {
                    var subUrl = queue[0];
                    queue.RemoveAt(0);
                    if (!seen.Add(subUrl)) continue;
                    try
                    {
                        var subBase = Resolve(baseUrl, subUrl);
                        var subJson = await Http.GetStringAsync(subBase);
                        var sub = JsonNode.Parse(subJson, documentOptions: DocOpts);
                        if (sub == null) continue;
                        // 子仓里若有 spider 字段而主仓没有，继承过来 —— 否则 csp_XXX 蜘蛛源
                        // 会因为没有蜘蛛包而全部不可用（多仓配置很常见：索引仓只有 urls，
                        // spider 写在子仓里）。
                        if (root["spider"] == null && sub["spider"] != null)
                            root["spider"] = sub["spider"]!.DeepClone();
                        // 先给子仓的站点打上该子仓的绝对地址，drpy 源的 ./js/xxx.js 才能定位
                        AnnotateSites(sub["sites"], subBase);
                        MergeArray(sub["sites"], sites);
                        MergeArray(sub["parses"], parses);
                        MergeArray(sub["lives"], lives);
                        if (sub["urls"] is JsonArray nested)
                            foreach (var nu in nested) if (nu != null) queue.Add(nu.ToString());
                    }
                    catch
                    {
                        // 子仓失效则跳过，不阻断整体
                    }
                }

                root["sites"] = sites;
                root["parses"] = parses;
                root["lives"] = lives;
                if (root is JsonObject o) o.Remove("urls");
            }

            // 给站点打上「所在配置的绝对地址」，供蜘蛛源解析相对资源（如 ./lib/drpy2.min.js）
            AnnotateSites(root["sites"], baseUrl);

            // 根节点也记一份自身地址：顶层 spider 字段常写成 "./jar/fan.txt"（相对配置），
            // JVM 桥要靠它把相对路径补成绝对地址。
            if (!string.IsNullOrWhiteSpace(baseUrl) && root is JsonObject ro && ro["__base"] == null)
                ro["__base"] = baseUrl;

            return root;
        }

        /// <summary>
        /// 为每个站点补充 __base 字段（所在配置文件的绝对地址）。
        /// 已有 __base 的（来自子仓）不覆盖，保证多仓混合时各自解析到正确位置。
        /// </summary>
        private static void AnnotateSites(JsonNode? sites, string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return;
            if (sites is not JsonArray arr) return;
            foreach (var s in arr)
            {
                if (s is not JsonObject o) continue;
                if (o.ContainsKey("__base")) continue;
                o["__base"] = baseUrl;
            }
        }

        private static void MergeArray(JsonNode? src, JsonArray dst)
        {
            if (src is JsonArray arr)
                foreach (var x in arr) dst.Add(x?.DeepClone());
        }

        private static string Resolve(string? baseUrl, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out _)) return url;
            if (baseUrl != null && Uri.TryCreate(new Uri(baseUrl), url, out var rel)) return rel.ToString();
            return url;
        }

        /// <summary>粗清洗：移除不在字符串内的 // 行注释（兜底用，正常应被 JsonCommentHandling 处理）</summary>
        private static string Sanitize(string json)
        {
            var outLines = new System.Collections.Generic.List<string>();
            bool inStr = false;
            char prev = '\0';
            var sb = new System.Text.StringBuilder();
            foreach (var ch in json)
            {
                if (ch == '"' && prev != '\\') inStr = !inStr;
                sb.Append(ch);
                if (ch == '\n')
                {
                    var line = sb.ToString();
                    sb.Clear();
                    if (!inStr && line.TrimStart().StartsWith("//"))
                        continue; // 丢弃注释行
                    outLines.Add(line);
                }
                prev = ch;
            }
            if (sb.Length > 0) outLines.Add(sb.ToString());
            return string.Join("", outLines);
        }
    }
}
