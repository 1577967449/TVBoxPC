using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 统一创建 HttpClient。
    /// 默认沿用「系统代理」（与浏览器一致）；若系统里残留了失效代理
    /// （例如 ProxyEnable=1 但 127.0.0.1:7897 已无服务），会导致所有网络请求失败，
    /// 此时可在设置里关掉「使用系统代理」改为直连。
    /// 注意：开关在程序启动时读取，修改后需重启程序生效。
    /// </summary>
    public static class HttpFactory
    {
        public static bool UseSystemProxy { get; set; } = true;

        /// <summary>默认 UA。部分图床/站点对「无 UA」的请求直接返回 403。</summary>
        public const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        /// <summary>移动端 UA（drpy 规则里的 MOBILE_UA 占位符对应此值）。</summary>
        public const string MobileUserAgent =
            "Mozilla/5.0 (Linux; Android 13; 2201123C Build/TP1A.220624.014) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";

        /// <summary>drpy 规则中 headers 里允许出现的 UA 占位符。</summary>
        public static string ResolveUserAgent(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return DefaultUserAgent;
            var t = v.Trim();
            if (t.Equals("MOBILE_UA", StringComparison.OrdinalIgnoreCase)) return MobileUserAgent;
            if (t.Equals("PC_UA", StringComparison.OrdinalIgnoreCase)) return DefaultUserAgent;
            if (t.Equals("UC_UA", StringComparison.OrdinalIgnoreCase))
                return "Mozilla/5.0 (Linux; U; Android 13) UCWEB/2.0 (MIDP-2.0; U; Adr 13) UCBrowser/15.0.0.1225 Mobile Safari/537.36";
            if (t.Equals("WAP_UA", StringComparison.OrdinalIgnoreCase)) return MobileUserAgent;
            return t;
        }

        /// <summary>共享 Cookie 容器：让同一会话内的登录态/验证 Cookie 能延续（drpy 蜘蛛常依赖）。</summary>
        private static CookieContainer? _sharedCookies;
        private static readonly object _lock = new();

        public static HttpClient Create(int timeoutSeconds = 25, bool withCookies = false)
        {
            return CreateCore(timeoutSeconds, withCookies, UseSystemProxy, true);
        }

        /// <summary>强制直连（不走系统代理）的客户端。</summary>
        public static HttpClient CreateDirect(int timeoutSeconds = 25, bool withCookies = false)
        {
            return CreateCore(timeoutSeconds, withCookies, false, true);
        }

        private static HttpClient CreateCore(int timeoutSeconds, bool withCookies, bool useProxy, bool allowRedirect)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = useProxy,
                AllowAutoRedirect = allowRedirect,
                AutomaticDecompression = DecompressionMethods.All
            };

            if (withCookies)
            {
                lock (_lock)
                {
                    _sharedCookies ??= new CookieContainer();
                    handler.CookieContainer = _sharedCookies;
                    handler.UseCookies = true;
                }
            }

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            return client;
        }

        // ==================== 文本解码 ====================

        static HttpFactory()
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        }

        /// <summary>
        /// 把响应字节解码成文本。
        /// 优先用调用方显式指定的编码（蜘蛛规则里的 encoding / gbk），
        /// 否则按 Content-Type 里的 charset，最后再嗅探 HTML meta，兜底 UTF-8。
        /// </summary>
        public static string DecodeText(byte[] bytes, string? explicitEncoding, string? contentType)
        {
            if (bytes == null || bytes.Length == 0) return "";

            // 1) 显式指定
            var enc = TryGetEncoding(explicitEncoding);
            // 2) Content-Type 里的 charset
            if (enc == null && !string.IsNullOrEmpty(contentType))
            {
                var m = Regex.Match(contentType, @"charset\s*=\s*""?([\w\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) enc = TryGetEncoding(m.Groups[1].Value);
            }
            // 3) HTML 头部 meta 嗅探（取前 4KB 的 ASCII 视角）
            if (enc == null)
            {
                var headLen = Math.Min(bytes.Length, 4096);
                var head = Encoding.ASCII.GetString(bytes, 0, headLen);
                var m = Regex.Match(head, @"charset\s*=\s*[""']?([\w\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) enc = TryGetEncoding(m.Groups[1].Value);
            }

            enc ??= Encoding.UTF8;
            return enc.GetString(bytes);
        }

        private static Encoding? TryGetEncoding(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var n = name.Trim().Trim('"', '\'');
            try
            {
                if (n.Equals("gbk", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("gb2312", StringComparison.OrdinalIgnoreCase))
                    return Encoding.GetEncoding("GB18030"); // 超集，覆盖 GBK/GB2312
                return Encoding.GetEncoding(n);
            }
            catch { return null; }
        }
    }
}
