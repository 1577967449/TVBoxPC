using System;
using System.Collections.Generic;

namespace TVBoxPC.Core.Jar
{
    /// <summary>
    /// 蜘蛛源（配置里 api = "csp_XXX"）的注册表。
    ///
    /// 两条执行路线，按优先级：
    ///   1) **JVM 桥（主路线）** —— 原版蜘蛛跑在桌面 JRE 上，覆盖全部 csp_ 源，
    ///      不用逐个重写、作者更新跟着配置一起更新。见 <see cref="JvmBridge"/>。
    ///   2) **C# 原生实现（兜底）** —— 当发行包里缺失 jvm/ 运行时时，
    ///      至少让已经重写好的少数源还能用（见 Spiders/ 目录）。
    /// </summary>
    public static class JarRegistry
    {
        /// <summary>"csp_SixV" → "SixV"（蜘蛛类在 com.github.catvod.spider 下）。</summary>
        public static string ClassShortName(string api)
        {
            if (string.IsNullOrWhiteSpace(api)) return "";
            var t = api.Trim();
            return t.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) ? t[4..] : t;
        }

        /// <summary>能否交给 JVM 桥执行（名字必须是合法 Java 标识符，避免注入到命令行）。</summary>
        public static bool CanBridge(string api)
        {
            var n = ClassShortName(api);
            if (n.Length == 0) return false;
            foreach (var ch in n)
                if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '$') return false;
            return true;
        }

        /// <summary>蜘蛛全类名。</summary>
        public static string FullClassName(string api) => "com.github.catvod.spider." + ClassShortName(api);

        /// <summary>C# 原生实现的工厂（没有对应实现时返回 null）。</summary>
        public static Func<JarSpider>? NativeFactory(string api)
        {
            switch (api)
            {
                case "csp_SixV": return () => new Spiders.SixV();
                default: return null;
            }
        }

        /// <summary>已原生重写的蜘蛛数量（界面提示用；主路线仍是 JVM 桥）。</summary>
        public static int NativeImplementedCount => 1;
    }

    /// <summary>
    /// C# 原生蜘蛛的 ISiteClient 适配器（兜底路线）。
    /// 蜘蛛实例懒创建，并加并发闸避免同一站点被反复并发请求。
    /// </summary>
    public sealed class NativeJarSite : ISiteClient
    {
        private readonly Func<JarSpider> _factory;
        private readonly System.Threading.SemaphoreSlim _gate = new(1, 1);
        private JarSpider? _spider;
        private string _reason = "蜘蛛未初始化";

        public string Key { get; }
        public string Name { get; }
        public bool Searchable { get; }
        public bool Available { get; private set; }
        public string? UnavailableReason => Available ? null : _reason;

        public NativeJarSite(string key, string name, bool searchable, Func<JarSpider> factory)
        {
            Key = key; Name = name; Searchable = searchable; _factory = factory;
        }

        private async System.Threading.Tasks.Task<JarSpider?> GetAsync()
        {
            if (_spider != null) return _spider;
            try
            {
                var sp = _factory();
                sp.Key = Key;
                sp.Name = Name;
                sp.Searchable = Searchable;
                await sp.InitAsync();
                _spider = sp;
                Available = true;
                return sp;
            }
            catch (Exception ex)
            {
                _reason = "蜘蛛初始化失败：" + ex.Message;
                return null;
            }
        }

        private async System.Threading.Tasks.Task<T> Once<T>(
            Func<JarSpider, System.Threading.Tasks.Task<T>> action, T fallback)
        {
            var sp = await GetAsync();
            if (sp == null) return fallback;
            await _gate.WaitAsync();
            try { return await action(sp); }
            catch (Exception ex)
            {
                _reason = "请求失败：" + ex.Message;
                return fallback;
            }
            finally { _gate.Release(); }
        }

        public System.Threading.Tasks.Task<List<Category>> GetCategories()
            => Once(sp => sp.CategoriesAsync(), new List<Category>());

        public System.Threading.Tasks.Task<List<VodItem>> GetList(string? categoryId, int page)
            => Once(sp => sp.ListAsync(categoryId, page), new List<VodItem>());

        public System.Threading.Tasks.Task<List<VodItem>> Search(string keyword, int page)
            => Once(sp => sp.SearchAsync(keyword, page), new List<VodItem>());

        public System.Threading.Tasks.Task<VodDetail?> GetDetail(string id)
            => Once<VodDetail?>(async sp => await sp.DetailAsync(id), null);
    }
}
