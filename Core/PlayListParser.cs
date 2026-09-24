using System.Collections.Generic;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 解析 TVBox 标准的播放地址串：
    ///   vod_play_from 用 "$$$" 分隔多条线路；
    ///   vod_play_url  与线路一一对应，组内用 "#" 分集、每条分集用 "$" 分「名称|地址」。
    /// </summary>
    public static class PlayListParser
    {
        public static List<PlayLine> Parse(string? playFrom, string? playUrl)
        {
            var lines = new List<PlayLine>();
            if (string.IsNullOrEmpty(playFrom) || string.IsNullOrEmpty(playUrl)) return lines;

            var froms = playFrom.Split("$$$");
            var urls = playUrl.Split("$$$");
            for (int i = 0; i < froms.Length; i++)
            {
                var line = new PlayLine { Name = froms[i]?.Trim() ?? $"线路{i + 1}" };
                var group = i < urls.Length ? urls[i] ?? "" : "";
                foreach (var seg in group.Split('#'))
                {
                    var s = seg.Trim();
                    if (s.Length == 0) continue;
                    var idx = s.LastIndexOf('$');
                    var name = idx > 0 ? s[..idx].Trim() : s;
                    var link = idx > 0 ? s[(idx + 1)..].Trim() : s;
                    if (string.IsNullOrEmpty(link)) continue;
                    line.Episodes.Add(new Episode { Name = string.IsNullOrEmpty(name) ? $"第{line.Episodes.Count + 1}集" : name, Url = link });
                }
                if (line.Episodes.Count > 0) lines.Add(line);
            }
            return lines;
        }
    }
}
