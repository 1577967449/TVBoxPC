using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TVBoxPC.Core.Jar
{
    /// <summary>
    /// ISiteClient 适配器：把「配置里的一个 csp_XXX 站点」接到 JVM 桥。
    ///
    /// 与 TVBox 原版的对应关系（都按 TVBox 的 JSON 字段名取值，最大化兼容）：
    ///   GetCategories ← homeContent            → {"class":[{"type_id","type_name"}]}
    ///   GetList       ← categoryContent        → {"list":[{"vod_id","vod_name",…}]}
    ///   Search        ← searchContent          → 同 categoryContent
    ///   GetDetail     ← detailContent          → {"list":[{…,"vod_play_from","vod_play_url"}]}
    /// </summary>
    public sealed class JarSite : ISiteClient
    {
        private readonly string _api;
        private readonly string _ext;
        private readonly JvmBridge _bridge = JvmBridge.Instance;

        public string Key { get; }
        public string Name { get; }
        public bool Searchable { get; }

        /// <summary>首次真正请求后才知道自己能不能用（下载/转换/网络都可能失败）。</summary>
        public bool Available { get; private set; } = true;
        public string? UnavailableReason { get; private set; }

        /// <summary>
        /// 该源没有分类页（homeContent 为空），但蜘蛛本身跑得通 —— 典型的
        /// 网盘搜索源（盘搜/夸搜/易搜…）与「只搜索」源。界面据此提示用户"请用搜索"，
        /// 而不是把它当成坏源。
        /// </summary>
        public bool OnlySearch { get; private set; }

        public JarSite(string key, string name, bool searchable, string api, string ext)
        {
            Key = key; Name = name; Searchable = searchable; _api = api; _ext = ext ?? "";
        }

        private void Fail(string reason)
        {
            Available = false;
            UnavailableReason = reason;
        }

        private async Task<JsonNode?> CallAsync(string method, params string[] args)
        {
            var json = await _bridge.InvokeAsync(JarRegistry.ClassShortName(_api), method, _ext, args);
            if (json == null)
            {
                // 关键区分：蜘蛛"没给内容"（不实现该方法，如网盘源没有分类页）
                // 与"真出错"（抛异常/超时）。前者不该把整个源标成不可用。
                if (_bridge.LastError != null) Fail(_bridge.LastError);
                return null;
            }
            try
            {
                var node = JsonNode.Parse(json);
                if (node == null && _bridge.LastError != null) Fail(_bridge.LastError);
                return node;
            }
            catch (Exception ex)
            {
                Fail("解析蜘蛛返回的 JSON 失败：" + ex.Message);
                return null;
            }
        }

        // ==================== 四个能力 ====================

        public async Task<List<Category>> GetCategories()
        {
            var list = new List<Category>();
            var json = await _bridge.HomeContentCachedAsync(JarRegistry.ClassShortName(_api), _ext);
            if (json == null)
            {
                if (_bridge.LastError != null) Fail(_bridge.LastError);
                else OnlySearch = true;           // 空首页 → 大概率是"只搜索"源
                return list;
            }
            try
            {
                var node = JsonNode.Parse(json);
                if (node?["class"] is JsonArray arr)
                {
                    foreach (var c in arr)
                    {
                        if (c == null) continue;
                        var id = c["type_id"]?.ToString() ?? "";
                        var name = c["type_name"]?.ToString() ?? "";
                        if (name.Length == 0) continue;
                        list.Add(new Category { Id = id, Name = Clean(name) });
                    }
                }
                if (list.Count == 0) OnlySearch = true;
                else { Available = true; OnlySearch = false; }
            }
            catch (Exception ex) { Fail("解析分类失败：" + ex.Message); }
            return list;
        }

        public async Task<List<VodItem>> GetList(string? categoryId, int page)
        {
            var json = await CallAsync("categoryContent", categoryId ?? "", page.ToString());
            return ParseList(json);
        }

        public async Task<List<VodItem>> Search(string keyword, int page)
        {
            var json = await CallAsync("searchContent", keyword, page.ToString());
            return ParseList(json);
        }

        public async Task<VodDetail?> GetDetail(string id)
        {
            var json = await CallAsync("detailContent", id ?? "");
            if (json?["list"] is not JsonArray arr) return null;
            var v = arr.Count > 0 ? arr[0] : null;
            if (v == null) return null;

            var d = new VodDetail
            {
                Id = Str(v, "vod_id", id),
                Name = Clean(Str(v, "vod_name", "")),
                Pic = Str(v, "vod_pic", ""),
                TypeName = Str(v, "type_name", ""),
                Year = Str(v, "vod_year", ""),
                Area = Str(v, "vod_area", ""),
                Actor = Str(v, "vod_actor", ""),
                Director = Str(v, "vod_director", ""),
                Remarks = Str(v, "vod_remarks", ""),
                Content = Clean(Str(v, "vod_content", ""))
            };

            // 多线路：vod_play_from / vod_play_url 都用 $$$ 分隔
            var from = Str(v, "vod_play_from", "");
            var playUrl = Str(v, "vod_play_url", "");
            var names = from.Length > 0 ? from.Split("$$$") : new[] { "播放" };
            var bodies = playUrl.Length > 0 ? playUrl.Split("$$$") : Array.Empty<string>();

            for (int i = 0; i < names.Length; i++)
            {
                var body = i < bodies.Length ? bodies[i] : "";
                if (body.Length == 0) continue;
                var line = new PlayLine { Name = names[i] };
                int n = 0;
                foreach (var ep in body.Split('#'))
                {
                    if (ep.Length == 0) continue;
                    n++;
                    var cut = ep.IndexOf('$');
                    if (cut > 0)
                        line.Episodes.Add(new Episode { Name = ep[..cut], Url = ep[(cut + 1)..] });
                    else
                        line.Episodes.Add(new Episode { Name = "第" + n + "集", Url = ep });
                }
                if (line.Episodes.Count > 0) d.Lines.Add(line);
            }
            return d;
        }

        // ==================== 解析助手 ====================

        private List<VodItem> ParseList(JsonNode? json)
        {
            var list = new List<VodItem>();
            if (json == null) return list;
            if (json["list"] is not JsonArray arr) return list;
            foreach (var x in arr)
            {
                if (x == null) continue;
                var id = Str(x, "vod_id", "");
                var name = Str(x, "vod_name", "");
                if (name.Length == 0 && id.Length == 0) continue;
                list.Add(new VodItem
                {
                    Id = id,
                    Name = Clean(name),
                    Pic = Str(x, "vod_pic", ""),
                    Remarks = Clean(Str(x, "vod_remarks", "")),
                    SiteKey = Key,
                    SiteName = Name
                });
            }
            if (list.Count > 0) Available = true;
            return list;
        }

        private static string Str(JsonNode n, string field, string? dflt = null)
        {
            try
            {
                var v = n[field];
                if (v == null) return dflt ?? "";
                var s = v.ToString();
                return string.IsNullOrEmpty(s) ? (dflt ?? "") : s;
            }
            catch { return dflt ?? ""; }
        }

        /// <summary>去掉简介里的 HTML 标签与多余空白。</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return HtmlSelect.StripTags(s);
        }
    }
}
