using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 播放失败后的「为什么会失败」探测：只取响应头，不下载正文。
    ///
    /// 设计取舍：**不在播放前探测**。多数源是直链，提前探测只会给每次播放加一次往返。
    /// 改成「先播，失败（VLC 抛 EncounteredError）再探」，把结论写给人看 ——
    /// 之前的问题是黑屏且一句话都没有，用户根本无从判断是源坏了、地址要解析、还是网络不通。
    /// </summary>
    public static class MediaProbe
    {
        private static readonly HttpClient Http = BuildClient();

        private static HttpClient BuildClient()
        {
            // 不用 HttpFactory：那里给 DefaultRequestHeaders 预置了 UA，
            // 而我们要按播放地址自带的 UA 覆盖，同一受限头会被 .NET 拒绝。
            var handler = new HttpClientHandler
            {
                UseProxy = HttpFactory.UseSystemProxy,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        }

        /// <summary>返回一句人话结论；探测本身失败时返回 null（调用方就只报播放器原始错误）。</summary>
        public static async Task<string?> ExplainAsync(MediaTarget t)
        {
            if (t.Kind != MediaKind.Direct) return t.Note;

            // ★ 先看协议：HttpClient 探不了 file:// 之类。实测拿本地下载产物走 --play 自检时，
            //   这里会抛「The 'file' scheme is not supported」，然后被归到「网络层问题（DNS/代理/被墙）」
            //   —— 对本地文件是完全误导的结论。非 HTTP 流直接跳过探测。
            var scheme = "";
            try { scheme = new Uri(t.Url).Scheme; } catch { }
            if (scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps)
                return $"这个地址不是 HTTP 流（{(scheme.Length == 0 ? "未知" : scheme)}://），不做网络探测。\n" +
                       "本地文件播放失败时：先确认文件是否下载完整、路径是否还在（改名/移动/被清理都会导致播不了）。";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, t.Url);
                req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                foreach (var kv in t.Headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                if (!t.HasHeaders) req.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.DefaultUserAgent);

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                var code = (int)resp.StatusCode;
                var ctype = (resp.Content.Headers.ContentType?.MediaType ?? "").ToLowerInvariant();

                if (code is 401 or 403)
                    return $"地址能连通，但服务器返回 {code}（拒绝访问）。\n" +
                           (t.HasHeaders
                               ? "该地址已带上 Referer/UA，仍被拒 —— 多半是防盗链校验或需要登录 Cookie。"
                               : "该地址没有携带 Referer/UA，部分站点会因此拒绝；可换其它线路试。");

                if (code == 404) return "地址返回 404，这条播放地址在源站已失效，换个线路或换个源。";
                if (code >= 400) return $"地址返回 HTTP {code}，源站拒绝了这次请求。";

                if (ctype.StartsWith("text/html"))
                    return "这个地址返回的是**网页**（Content-Type: text/html），不是媒体流。\n" +
                           "说明该源属于「需要解析」类型：要先经过解析接口换成真实 m3u8 才能播。\n" +
                           "本程序目前不做解析，请换一个直链源（源名带 🕷JAR / 苹果CMS 的多为直链）。";

                if (ctype.Contains("mpegurl") || ctype.StartsWith("video/") || ctype.StartsWith("audio/")
                    || ctype == "application/octet-stream" || ctype == "binary/octet-stream")
                    return $"地址本身是正常媒体流（HTTP {code}，{ctype}），" +
                           "所以问题在播放环节：可先试「用外部播放器打开」对比；\n" +
                           "若外部能播而内置不能，多半是系统代理拦截了播放器流量（设置里关掉「使用系统代理」再重启）。";

                return $"地址返回 HTTP {code}，Content-Type: {ctype}（不是常见媒体类型），源站可能已改版。";
            }
            catch (Exception ex)
            {
                return "连这个地址都探测失败：" + ex.Message + "\n" +
                       "说明是网络层问题（DNS/代理/被墙）。若系统里挂着失效代理，去设置里关掉「使用系统代理」后重启。";
            }
        }
    }
}
