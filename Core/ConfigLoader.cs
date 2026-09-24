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
        // 子仓抓取用更短超时（15s）+ 有界并发（16），避免「顺序 × 25s × 大量失效链接」导致导入假死
        private static readonly HttpClient HttpFast = HttpFactory.Create(15);
        private const int MaxConcurrency = 16;

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

                // ★ 关键修复：顶层自带的内容先种进去，再与子仓合并。
                // 旧逻辑新建空数组并把顶层站点整个覆盖掉，导致「带 urls 的合并配置」
                // 在子仓大面积失效时只剩空白、整份配置用不了。现在顶层源 + 子仓源共存
                // （符合 TVBox/CatVod 约定），网上随便扒的源也能直接加载、坏源自动跳过。
                var siteKeys = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var parseNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var liveNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                MergeArray(root["sites"], sites, siteKeys, "key");
                MergeArray(root["parses"], parses, parseNames, "name");
                MergeArray(root["lives"], lives, liveNames, "name");

                // 复制一份待处理队列（多仓可能多层嵌套）；seen 跨层去重
                var queue = new System.Collections.Generic.List<string>();
                foreach (var u in urls) if (u != null) queue.Add(u.ToString());

                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int guard = 0;
                // 有界并发：同一层的所有子仓链接并行抓取（上限 MaxConcurrency），
                // 失效链接的 15s 超时只阻塞该批次，而非像旧版那样「顺序 × 25s」逐个累加，
                // 从而避免导入大体积多仓配置时假死。合并动作在 WhenAll 之后顺序执行，线程安全。
                using var sem = new System.Threading.SemaphoreSlim(MaxConcurrency);
                while (queue.Count > 0 && guard++ < 200)
                {
                    // 取当前层、去重、并标记已访问（顺序执行，保证 seen 线程安全）
                    var level = queue.Distinct(StringComparer.OrdinalIgnoreCase)
                                     .Where(u => seen.Add(u)).ToList();
                    queue = new System.Collections.Generic.List<string>();

                    var tasks = level.Select(async u =>
                    {
                        await sem.WaitAsync();
                        try
                        {
                            var subBase = Resolve(baseUrl, u);
                            var subJson = await HttpFast.GetStringAsync(subBase);
                            return (Url: u, Sub: JsonNode.Parse(subJson, documentOptions: DocOpts), Base: subBase);
                        }
                        catch
                        {
                            // 子仓失效则跳过，不阻断整体
                            return (Url: u, Sub: (JsonNode?)null, Base: (string?)null);
                        }
                        finally { sem.Release(); }
                    });

                    var results = await Task.WhenAll(tasks);
                    foreach (var (_, sub, subBase) in results)
                    {
                        if (sub == null) continue;
                        // 子仓里若有 spider 字段而主仓没有，继承过来 —— 否则 csp_XXX 蜘蛛源
                        // 会因为没有蜘蛛包而全部不可用（多仓配置很常见：索引仓只有 urls，
                        // spider 写在子仓里）。
                        if (root["spider"] == null && sub["spider"] != null)
                            root["spider"] = sub["spider"]!.DeepClone();
                        // 先给子仓的站点打上该子仓的绝对地址，drpy 源的 ./js/xxx.js 才能定位
                        AnnotateSites(sub["sites"], subBase);
                        MergeArray(sub["sites"], sites, siteKeys, "key");
                        MergeArray(sub["parses"], parses, parseNames, "name");
                        MergeArray(sub["lives"], lives, liveNames, "name");
                        if (sub["urls"] is JsonArray nested)
                            foreach (var nu in nested) if (nu != null) queue.Add(nu.ToString());
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

        /// <summary>
        /// 合并一个数组到 dst，同时自动识别并剔除坏条目：
        ///   · 跳过非对象元素（结构错误）
        ///   · 跳过缺少 key/name 的条目
        ///   · 按 keyField 去重（同一 key 只保留首次出现，避免重复源互相覆盖）
        /// 这样加载「网上随便扒的」配置时，畸形/重复/失效的源不会拖垮整体，也不必让用户去手改 JSON。
        /// </summary>
        private static void MergeArray(JsonNode? src, JsonArray dst, System.Collections.Generic.HashSet<string> keys, string keyField)
        {
            if (src is not JsonArray arr) return;
            foreach (var x in arr)
            {
                if (x is not JsonObject o) continue;
                var key = o[keyField]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!keys.Add(key)) continue;
                dst.Add(x.DeepClone());
            }
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
