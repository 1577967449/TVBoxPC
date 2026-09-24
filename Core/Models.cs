using System.Collections.Generic;

namespace TVBoxPC.Core
{
    /// <summary>影片条目（列表/搜索结果里的一个卡片）。</summary>
    public class VodItem
    {
        public string Id = "";
        public string Name = "";
        public string Pic = "";
        public string Remarks = "";
        // 聚合搜索时用于标记来源站点，便于点击后回到对应站点打开详情
        public string SiteKey = "";
        public string SiteName = "";
    }

    /// <summary>分类。</summary>
    public class Category
    {
        public string Id = "";
        public string Name = "";
    }

    /// <summary>一集（一条播放地址）。</summary>
    public class Episode
    {
        public string Name = "";
        public string Url = "";
    }

    /// <summary>一条播放线路（对应 TVBox 的 vod_play_from 里的一项）。</summary>
    public class PlayLine
    {
        public string Name = "";
        public List<Episode> Episodes = new();
    }

    /// <summary>影片详情。</summary>
    public class VodDetail
    {
        public string Id = "";
        public string Name = "";
        public string Pic = "";
        public string TypeName = "";
        public string Year = "";
        public string Area = "";
        public string Actor = "";
        public string Director = "";
        public string Remarks = "";
        public string Content = "";
        public List<PlayLine> Lines = new();
    }

    /// <summary>直播源（配置里 lives[] 的一项）。</summary>
    public class LiveSource
    {
        public string Name = "";
        public string Url = "";
    }
}
