using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TVBoxPC.Core
{
    /// <summary>苹果CMS 源（TVBox 里 type 0/1）适配为统一站点客户端。</summary>
    public class AppleCmsSite : ISiteClient
    {
        private readonly AppleCmsClient _cms = new();
        private readonly string _api;

        public string Key { get; }
        public string Name { get; }
        public bool Searchable { get; }
        public bool Available => true;
        public string? UnavailableReason => null;

        public AppleCmsSite(string key, string name, string api, bool searchable)
        {
            Key = key; Name = name; _api = api; Searchable = searchable;
        }

        public async Task<List<Category>> GetCategories()
        {
            var raw = await _cms.GetCategories(_api);
            var list = new List<Category>();
            foreach (var (id, nm) in raw) list.Add(new Category { Id = id, Name = nm });
            return list;
        }

        public async Task<List<VodItem>> GetList(string? categoryId, int page)
        {
            var raw = await _cms.GetList(_api, page, categoryId);
            var list = new List<VodItem>();
            foreach (var v in raw) list.Add(ToItem(v));
            return list;
        }

        public async Task<VodDetail?> GetDetail(string id)
        {
            var v = await _cms.GetDetail(_api, id);
            if (v == null) return null;
            return new VodDetail
            {
                Id = v["vod_id"]?.ToString() ?? id,
                Name = v["vod_name"]?.ToString() ?? "",
                Pic = v["vod_pic"]?.ToString() ?? "",
                TypeName = v["type_name"]?.ToString() ?? "",
                Year = v["vod_year"]?.ToString() ?? "",
                Area = v["vod_area"]?.ToString() ?? "",
                Actor = v["vod_actor"]?.ToString() ?? "",
                Director = v["vod_director"]?.ToString() ?? "",
                Remarks = v["vod_remarks"]?.ToString() ?? "",
                Content = v["vod_content"]?.ToString() ?? "",
                Lines = PlayListParser.Parse(v["vod_play_from"]?.ToString(), v["vod_play_url"]?.ToString())
            };
        }

        public async Task<List<VodItem>> Search(string keyword, int page)
        {
            var raw = await _cms.Search(_api, keyword, page);
            var list = new List<VodItem>();
            foreach (var v in raw) list.Add(ToItem(v));
            return list;
        }

        private static VodItem ToItem(JsonNode v) => new()
        {
            Id = v["vod_id"]?.ToString() ?? "",
            Name = v["vod_name"]?.ToString() ?? "",
            Pic = v["vod_pic"]?.ToString() ?? "",
            Remarks = v["vod_remarks"]?.ToString() ?? ""
        };
    }
}
