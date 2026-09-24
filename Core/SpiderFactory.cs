using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TVBoxPC.Core.Drpy;

namespace TVBoxPC.Core
{
    /// <summary>配置里 sites[] 的一项，解析成便于界面使用的结构。</summary>
    public class SiteEntry
    {
        public JsonNode Node = null!;
        public string Key = "";
        public string Name = "";
        public string Api = "";
        public string Ext = "";
        public string SpiderClass = "";
        /// <summary>该站点所属配置文件的绝对地址（多仓时用于解析 ./js/xxx.js 这类相对引用）。</summary>
        public string Base = "";
        public int TypeNum;
        public bool Searchable = true;
        public bool Hidden;

        public override string ToString() => Name;
    }

    /// <summary>
    /// 把配置站点分派成具体的站点客户端：
    ///   · type 0/1（苹果CMS）或 api 为 URL          -> AppleCmsSite
    ///   · api 以 csp_XPath 开头 / ext 是套娃规则     -> XPathSpider（自写解析）
    ///   · api 为 .js（drpy 家族）或 ext 为 .js       -> DrpySite（原生 drpy 规则引擎）
    ///   · 其它蜘蛛（csp_AppTT / csp_Bili 等）        -> UnavailableSite（给出明确原因）
    /// </summary>
    public static class SpiderFactory
    {
        public static SiteEntry? Parse(JsonNode site)
        {
            if (site == null) return null;
            try
            {
                var e = new SiteEntry
                {
                    Node = site,
                    Key = site["key"]?.ToString() ?? site["name"]?.ToString() ?? Guid.NewGuid().ToString("N"),
                    Name = site["name"]?.ToString() ?? site["key"]?.ToString() ?? "源",
                    Api = site["api"]?.ToString() ?? "",
                    Ext = site["ext"]?.ToString() ?? "",
                    Base = site["__base"]?.ToString() ?? "",
                    TypeNum = GetInt(site["type"], 0),
                    Hidden = GetInt(site["hide"], 0) == 1
                };
                e.Searchable = GetInt(site["searchable"], 1) != 0;
                if (e.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase))
                    e.SpiderClass = e.Api;
                return e;
            }
            catch
            {
                // 极端配置（字段类型古怪）不应中断整体加载
                return null;
            }
        }

        /// <summary>
        /// 容错读取整数字段：配置里 type / hide / searchable 常见写成数字，也常见写成字符串
        /// （如 "type": "3"）。用 GetValue&lt;int&gt; 直接读字符串会抛异常，这里两种都兼容。
        /// </summary>
        private static int GetInt(JsonNode? n, int dflt)
        {
            if (n == null) return dflt;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                var s = v.ToString();
                if (int.TryParse(s, out var j)) return j;
                if (bool.TryParse(s, out var b)) return b ? 1 : 0;
            }
            return dflt;
        }

        public static Task<ISiteClient> CreateAsync(SiteEntry e)
        {
            // ---------- 1) drpy 家族（api/ext 指向 .js）----------
            if (IsDrpySource(e))
            {
                var script = DrpyScriptOf(e);
                if (script == null)
                    return Task.FromResult<ISiteClient>(new UnavailableSite(e.Key, e.Name,
                        "该源是 drpy 的直播/列表脚本（ext 不是站点 js），当前按直播源处理，不在点播列表里。"));

                return Task.FromResult<ISiteClient>(
                    new DrpySite(e.Key, e.Name, script, e.Base, e.Searchable));
            }

            // ---------- 2) 蜘蛛源 ----------
            if (e.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) || IsSpiderType(e))
            {
                // 套娃（XPath）家族：自写解析
                if (e.Api.StartsWith("csp_XPath", StringComparison.OrdinalIgnoreCase))
                    return CreateXPathAsync(e, "该套娃蜘蛛的 ext 规则无法解析（可能规则地址失效）。");

                // ext 本身是套娃规则 JSON 的，也用 XPath 引擎尽力解析
                if (LooksLikeXPathConfig(e.Ext))
                    return CreateXPathAsync(e, null);

                // api 是普通 URL 的 type:3 源，按苹果CMS 直连尝试
                if (e.Api.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<ISiteClient>(new AppleCmsSite(e.Key, e.Name, e.Api, e.Searchable));

                // ★ 主路线：桌面 JVM 桥 —— 原版蜘蛛直接跑（覆盖全部 csp_XXX）
                var bridge = Jar.JvmBridge.Instance;
                if (Jar.JarRegistry.CanBridge(e.Api) && bridge.Configured && Jar.JvmRuntime.Available)
                    return Task.FromResult<ISiteClient>(
                        new Jar.JarSite(e.Key, e.Name, e.Searchable, e.Api, e.Ext));

                // ★ 兜底：C# 原生实现（需要 jvm/ 缺失，或该源没有声明蜘蛛包）
                var native = Jar.JarRegistry.NativeFactory(e.Api);
                if (native != null)
                    return Task.FromResult<ISiteClient>(
                        new Jar.NativeJarSite(e.Key, e.Name, e.Searchable, native));

                return Task.FromResult<ISiteClient>(new UnavailableSite(e.Key, e.Name,
                    DescribeUnsupportedSpider(e)));
            }

            // ---------- 3) 常规苹果CMS 源 ----------
            if (!string.IsNullOrEmpty(e.Api))
                return Task.FromResult<ISiteClient>(new AppleCmsSite(e.Key, e.Name, e.Api, e.Searchable));

            return Task.FromResult<ISiteClient>(new UnavailableSite(e.Key, e.Name, "该源缺少 api 字段。"));
        }

        private static async Task<ISiteClient> CreateXPathAsync(SiteEntry e, string? failReason)
        {
            var sp = await XPathSpider.Create(e.Key, e.Name, e.Ext);
            if (sp != null) return sp;
            return new UnavailableSite(e.Key, e.Name,
                failReason ?? "该源的规则无法解析。");
        }

        private static bool IsSpiderType(SiteEntry e) => e.TypeNum == 3;

        // ==================== drpy 判定 ====================

        /// <summary>
        /// 是否属于 drpy 家族：api 指向 .js（drpy 框架或站点脚本），
        /// 或 type:3 且 ext 指向 .js（站点脚本）。
        /// </summary>
        public static bool IsDrpySource(SiteEntry e)
        {
            if (LooksLikeJs(e.Api) && !e.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)) return true;
            if (IsSpiderType(e) && LooksLikeJs(e.Ext)) return true;
            return false;
        }

        /// <summary>
        /// 取出 drpy 的「站点脚本」地址。
        /// 约定（与 TVBox 的 csp_Drpy 一致）：api 是 drpy 框架，**ext 才是站点脚本**。
        /// 因此：
        ///   · ext 是 .js             -> 用它；
        ///   · ext 非空但不是 .js     -> 说明这是直播/列表数据（如 .txt），不属于点播站点，返回 null；
        ///   · ext 为空               -> 仅当 api 本身不像框架文件时才当作站点脚本（兼容少数配置）。
        /// </summary>
        public static string? DrpyScriptOf(SiteEntry e)
        {
            if (!string.IsNullOrWhiteSpace(e.Ext))
                return LooksLikeJs(e.Ext) ? e.Ext.Trim() : null;

            if (LooksLikeJs(e.Api) && !IsFrameworkScript(e.Api)) return e.Api.Trim();
            return null;
        }

        /// <summary>是否是 drpy 框架文件（drpy2.min.js / drpy2.js / lf_live_min.js 之类），而非站点脚本。</summary>
        private static bool IsFrameworkScript(string path)
        {
            var name = path;
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            name = name.ToLowerInvariant();
            return name.Contains("drpy") || name.Contains("live_min");
        }

        private static bool LooksLikeJs(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim();
            if (!t.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && !t.Contains(".js?")) return false;
            return true;
        }

        // ==================== 可用性评分 ====================

        /// <summary>
        /// 可用性评分，用于界面标注与「自动挑一个最可能可用的源」：
        ///   4 = 真正的苹果CMS（type 0/1 + http api），最可靠
        ///   3 = drpy 源（原生规则引擎）
        ///   2 = csp_XPath 套娃家族 / csp_XXX 专属蜘蛛（走桌面 JVM 桥）
        ///   1 = type 3 但 api 是 http（可能是苹果CMS，也可能不兼容）
        ///   0 = 其它（没有 jvm 运行时、也不是原生实现，或缺少 api）
        /// </summary>
        public static int AvailabilityScore(SiteEntry e)
        {
            bool http = e.Api.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            bool xpath = e.Api.StartsWith("csp_XPath", StringComparison.OrdinalIgnoreCase)
                         || (e.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) && LooksLikeXPathConfig(e.Ext));
            if (IsDrpySource(e) && DrpyScriptOf(e) != null) return 3;
            if ((e.TypeNum == 0 || e.TypeNum == 1) && http) return 4;
            if (xpath) return 2;
            // 专属蜘蛛：有 JVM 运行时就能跑（原版蜘蛛原样执行）
            if (e.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)
                && Jar.JarRegistry.CanBridge(e.Api)
                && Jar.JvmRuntime.Available)
                return 2;
            if (http) return 1;
            return 0;
        }

        public static bool PredictAvailable(SiteEntry e) => AvailabilityScore(e) > 0;

        private static string DescribeUnsupportedSpider(SiteEntry e)
        {
            var spider = string.IsNullOrEmpty(e.Api) ? "未知蜘蛛" : e.Api;
            var jar = Jar.JvmRuntime.Available;
            if (!jar)
                return $"该源（{spider}）是编译在蜘蛛包里的专属蜘蛛，需要桌面 JVM 运行时才能执行，\n" +
                       "但当前缺少 jvm/ 目录（jre + stubs.jar + libs + d2j），随发行包一起分发。";

            var bridge = Jar.JvmBridge.Instance;
            if (!bridge.Configured)
                return $"该源（{spider}）是专属蜘蛛，需要配置里顶层 spider 字段指向蜘蛛包（./jar/xxx.txt）。\n" +
                       "当前配置没有该字段，无法运行。";

            if (!Jar.JarRegistry.CanBridge(e.Api))
                return $"该源（{spider}）的 api 名不是合法的蜘蛛类名，无法通过 JVM 桥调用。";

            return $"该源（{spider}）暂不可用。请查看右侧日志：可能是蜘蛛包下载/转换失败，或站点本身失效。";
        }

        private static bool LooksLikeXPathConfig(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;
            var t = ext.TrimStart();
            if (t.StartsWith("{"))
                return t.Contains("\"list\"") || t.Contains("\"homeUrl\"") || t.Contains("\"cateManual\"");
            // 形如 http.../xxx.json 的规则地址也算
            return ext.Contains("://") && ext.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>批量解析 sites[]。</summary>
        public static List<SiteEntry> ParseAll(JsonNode? sites)
        {
            var list = new List<SiteEntry>();
            if (sites is not JsonArray arr) return list;
            foreach (var s in arr)
            {
                var e = Parse(s!);
                if (e != null && !e.Hidden) list.Add(e);
            }
            return list;
        }
    }
}
