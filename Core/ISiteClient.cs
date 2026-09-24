using System.Collections.Generic;
using System.Threading.Tasks;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 统一的「站点客户端」抽象。
    /// 苹果CMS 源(type 0/1) 与 蜘蛛源(type 3) 都实现该接口，
    /// 这样界面层不关心底层差异，聚合搜索也能统一遍历。
    /// </summary>
    public interface ISiteClient
    {
        string Key { get; }
        string Name { get; }
        /// <summary>是否支持搜索（聚合搜索只遍历支持搜索的站点）。</summary>
        bool Searchable { get; }
        /// <summary>该客户端是否真的可用（未支持的蜘蛛源为 false，界面可给出提示）。</summary>
        bool Available { get; }
        /// <summary>不可用时给用户的说明。</summary>
        string? UnavailableReason { get; }

        Task<List<Category>> GetCategories();
        Task<List<VodItem>> GetList(string? categoryId, int page);
        Task<VodDetail?> GetDetail(string id);
        Task<List<VodItem>> Search(string keyword, int page);
    }

    /// <summary>不可用/未支持站点的占位实现。</summary>
    public class UnavailableSite : ISiteClient
    {
        public string Key { get; }
        public string Name { get; }
        public bool Searchable => false;
        public bool Available => false;
        public string? UnavailableReason { get; }

        public UnavailableSite(string key, string name, string reason)
        {
            Key = key; Name = name; UnavailableReason = reason;
        }

        public Task<List<Category>> GetCategories() => Task.FromResult(new List<Category>());
        public Task<List<VodItem>> GetList(string? categoryId, int page) => Task.FromResult(new List<VodItem>());
        public Task<VodDetail?> GetDetail(string id) => Task.FromResult<VodDetail?>(null);
        public Task<List<VodItem>> Search(string keyword, int page) => Task.FromResult(new List<VodItem>());
    }
}
