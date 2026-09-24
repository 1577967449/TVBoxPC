using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TVBoxPC.Core.Drpy
{
    /// <summary>
    /// 把 drpy 站点（配置里 api 指向 drpy2.min.js / 站点脚本的 type:3 源）包装成统一的 ISiteClient。
    ///
    /// 采用「懒加载」：构造时不下载脚本，首次真正取数据时才拉取并编译，
    /// 避免多仓配置里几十个 drpy 源在启动时一起联网。
    /// </summary>
    public sealed class DrpySite : ISiteClient, IDisposable
    {
        public string Key { get; }
        public string Name { get; }
        public bool Searchable { get; private set; }
        public bool Available => _available;
        public string? UnavailableReason => _reason;

        /// <summary>站点脚本地址（配置里的 ext，通常是 ./js/xxx.js）。</summary>
        public string ScriptPath { get; }
        /// <summary>配置基地址，用于把相对脚本路径补全为绝对地址。</summary>
        public string BaseUrl { get; }

        private DrpyEngine? _eng;
        private bool _available = true;
        private string? _reason;
        private bool _inited;
        private readonly SemaphoreSlim _lock = new(1, 1);

        /// <summary>全局引擎初始化并发闸门：避免几十个源同时下载脚本把带宽/CPU 打满。</summary>
        private static readonly SemaphoreSlim Gate = new(4, 4);

        public DrpySite(string key, string name, string scriptPath, string baseUrl, bool searchable)
        {
            Key = key;
            Name = name;
            ScriptPath = scriptPath;
            BaseUrl = baseUrl;
            Searchable = searchable;
        }

        private async Task<bool> EnsureAsync()
        {
            if (_inited) return _available;
            await _lock.WaitAsync();
            try
            {
                if (_inited) return _available;

                await Gate.WaitAsync();
                try
                {
                    _eng = await DrpyEngine.CreateAsync(ScriptPath, BaseUrl, Key);
                    Searchable = Searchable && _eng.Rule.Searchable;
                }
                catch (Exception ex)
                {
                    _available = false;
                    _reason = "drpy 脚本加载失败：" + ex.Message;
                }
                finally { Gate.Release(); }

                _inited = true;
                return _available;
            }
            finally { _lock.Release(); }
        }

        public async Task<List<Category>> GetCategories()
        {
            if (!await EnsureAsync()) return new List<Category>();
            try { return await _eng!.GetCategoriesAsync(); }
            catch (Exception ex) { _reason = ex.Message; return new List<Category>(); }
        }

        public async Task<List<VodItem>> GetList(string? categoryId, int page)
        {
            if (!await EnsureAsync()) return new List<VodItem>();
            try
            {
                if (string.IsNullOrEmpty(categoryId) || categoryId == "__home__")
                    return await _eng!.HomeAsync();
                return await _eng!.CategoryAsync(categoryId, Math.Max(1, page));
            }
            catch (Exception ex) { _reason = ex.Message; return new List<VodItem>(); }
        }

        public async Task<VodDetail?> GetDetail(string id)
        {
            if (!await EnsureAsync()) return null;
            try { return await _eng!.DetailAsync(id); }
            catch (Exception ex) { _reason = ex.Message; return null; }
        }

        public async Task<List<VodItem>> Search(string keyword, int page)
        {
            if (!await EnsureAsync()) return new List<VodItem>();
            try { return await _eng!.SearchAsync(keyword, Math.Max(1, page)); }
            catch (Exception ex) { _reason = ex.Message; return new List<VodItem>(); }
        }

        public void Dispose()
        {
            try { _eng?.Dispose(); } catch { }
        }
    }
}
