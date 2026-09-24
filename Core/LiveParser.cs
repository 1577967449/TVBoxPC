using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace TVBoxPC.Core
{
    /// <summary>
    /// TVBox 直播源解析。配置里 lives[] 每项 { name, type, url }，url 指向：
    ///   1) TXT 格式：以 “频道名,地址” 一行一条，用 “xxx,#genre#” 作为分组标题；
    ///   2) M3U 格式：#EXTINF 携带频道名，其下一行是播放地址。
    /// 解析后得到扁平的频道列表（名称 + 播放地址），播放走内置 LibVLC / 外部播放器。
    /// </summary>
    public class LiveParser
    {
        private static readonly HttpClient Http = HttpFactory.Create(25);

        public class LiveChannel
        {
            public string Name = "";
            public string Url = "";
            public string Group = "";
        }

        public async Task<List<LiveChannel>> Parse(string url)
        {
            var result = new List<LiveChannel>();
            string txt;
            try { txt = await Http.GetStringAsync(url); }
            catch { return result; }

            if (txt.Contains("#EXTM3U") || txt.Contains("#EXTINF"))
                ParseM3U(txt, result);
            else
                ParseTxt(txt, result);
            return result;
        }

        private static void ParseTxt(string txt, List<LiveChannel> outList)
        {
            string group = "";
            using var sr = new StringReader(txt);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.EndsWith("#genre#", StringComparison.OrdinalIgnoreCase))
                {
                    group = line.Substring(0, line.Length - "#genre#".Length).Trim();
                    continue;
                }
                var comma = line.IndexOf(',');
                if (comma < 0) continue;
                var name = line[..comma].Trim();
                var addr = line[(comma + 1)..].Trim();
                if (string.IsNullOrEmpty(addr)) continue;
                outList.Add(new LiveChannel { Name = name, Url = addr, Group = group });
            }
        }

        private static void ParseM3U(string txt, List<LiveChannel> outList)
        {
            string name = "";
            string group = "";
            using var sr = new StringReader(txt);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    // 形如：#EXTINF:-1 tvg-name="X" group-title="Y",频道名
                    var comma = line.LastIndexOf(',');
                    name = comma >= 0 ? line[(comma + 1)..].Trim() : line;
                    var gt = line.IndexOf("group-title=\"", StringComparison.OrdinalIgnoreCase);
                    if (gt >= 0)
                    {
                        var end = line.IndexOf('"', gt + "group-title=\"".Length);
                        if (end > gt) group = line.Substring(gt + "group-title=\"".Length, end - gt - "group-title=\"".Length).Trim();
                    }
                }
                else if (line.Length > 0 && !line.StartsWith("#"))
                {
                    if (!string.IsNullOrEmpty(name))
                        outList.Add(new LiveChannel { Name = name, Url = line, Group = group });
                    name = "";
                }
            }
        }
    }
}
