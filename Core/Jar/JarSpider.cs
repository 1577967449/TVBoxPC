using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Dom;

namespace TVBoxPC.Core.Jar
{
    /// <summary>
    /// 自定义蜘蛛（原 TVBox 里解析算法写在 JAR 里的那一批）的 C# 原生基类。
    ///
    /// 对应原项目的 com.github.catvod.crawler.Spider：
    ///   homeContent / categoryContent / detailContent / searchContent / playerContent
    /// 这里改成返回本工程的模型（Category / VodItem / VodDetail），更贴合界面层。
    ///
    /// 子类只需实现自己关心的那几个方法，其它走默认空实现。
    /// </summary>
    public abstract class JarSpider
    {
        /// <summary>站点 key / 名称（用于日志与来源标记）。</summary>
        public string Key = "";
        public string Name = "";
        public bool Searchable = true;

        /// <summary>配置里的 ext 原样（可能是 URL、JSON、AES 密文…）。</summary>
        protected string Ext = "";

        /// <summary>站点所属配置的绝对地址，用于解析 ./xxx 相对引用。</summary>
        protected string BaseUrl = "";

        /// <summary>带 Cookie 容器的 HttpClient（蜘蛛常需要保持会话）。</summary>
        protected HttpClient Http = null!;
        protected int TimeoutSeconds = 20;

        public abstract string SpiderName { get; }

        public virtual Task InitAsync()
        {
            Http = HttpFactory.Create(TimeoutSeconds, withCookies: true);
            return Task.CompletedTask;
        }

        // ==================== 子类可重写的能力 ====================

        public virtual Task<List<Category>> CategoriesAsync() => Task.FromResult(new List<Category>());
        public virtual Task<List<VodItem>> ListAsync(string? tid, int page) => Task.FromResult(new List<VodItem>());
        public virtual Task<List<VodItem>> SearchAsync(string wd, int page) => Task.FromResult(new List<VodItem>());
        public virtual Task<VodDetail?> DetailAsync(string id) => Task.FromResult<VodDetail?>(null);

        /// <summary>是否支持「首页推荐」与「分类」分开（多数蜘蛛同一套）。</summary>
        public virtual bool HasCategories => true;

        /// <summary>该蜘蛛是否支持搜索。</summary>
        public virtual bool SupportsSearch => true;

        // ==================== 网络 ====================

        protected async Task<string> Fetch(string url, Dictionary<string, string>? headers = null,
                                           string? encoding = null, string? referer = null)
        {
            url = UrlJoin(url);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(req, headers, referer);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return HttpFactory.DecodeText(bytes, encoding, resp.Content.Headers.ContentType?.ToString());
        }

        protected async Task<string> Post(string url, string body, Dictionary<string, string>? headers = null,
                                          string? contentType = "application/x-www-form-urlencoded", string? encoding = null)
        {
            url = UrlJoin(url);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    contentType ?? "application/x-www-form-urlencoded")
            };
            ApplyHeaders(req, headers, null);
            using var resp = await Http.SendAsync(req);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return HttpFactory.DecodeText(bytes, encoding, resp.Content.Headers.ContentType?.ToString());
        }

        protected async Task<string> PostJson(string url, string json, Dictionary<string, string>? headers = null)
            => await Post(url, json, headers, "application/json");

        protected async Task<byte[]> FetchBytes(string url, Dictionary<string, string>? headers = null)
        {
            url = UrlJoin(url);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(req, headers, null);
            using var resp = await Http.SendAsync(req);
            return await resp.Content.ReadAsByteArrayAsync();
        }

        private void ApplyHeaders(HttpRequestMessage req, Dictionary<string, string>? headers, string? referer)
        {
            req.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.DefaultUserAgent);
            req.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
            if (headers == null) return;
            foreach (var kv in headers)
            {
                if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    req.Headers.Remove("User-Agent");
                    req.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.ResolveUserAgent(kv.Value));
                }
                else if (kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                {
                    req.Headers.Remove("Referer");
                    req.Headers.TryAddWithoutValidation("Referer", kv.Value);
                }
                else if (kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    req.Headers.TryAddWithoutValidation("Cookie", kv.Value);
                }
                else
                {
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }
        }

        /// <summary>把相对地址补全成绝对地址（相对站点主域）。</summary>
        protected string UrlJoin(string url, string? baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            var b = baseUrl ?? SiteHomeUrl();
            return HtmlSelect.UrlJoin(b, url);
        }

        /// <summary>站点主域，子类通常用 ext 覆盖。</summary>
        protected virtual string SiteHomeUrl() => Ext;

        // ==================== HTML 抽取（等价 CatVod 的 Html.pdfh/pdfa/pd）====================

        protected static string Pd(string? html, string rule, string? baseUrl = null)
            => HtmlSelect.Pd(html, rule, baseUrl);

        protected static string Pdfh(string? html, string rule) => HtmlSelect.Pdfh(html, rule);
        protected static string Pdfh(IElement? el, string rule) => HtmlSelect.Pdfh(el, rule);
        protected static List<IElement> Pdfa(string? html, string rule) => HtmlSelect.Pdfa(html, rule);
        protected static List<IElement> Pdfa(IElement? el, string rule) => HtmlSelect.Pdfa(el, rule);

        protected static string Text(string? s) => HtmlSelect.StripTags(s);

        // ==================== 正则 ====================

        protected static string? Match1(string input, string pattern, int group = 1)
        {
            var m = Regex.Match(input ?? "", pattern, RegexOptions.Singleline);
            return m.Success ? (m.Groups.Count > group ? m.Groups[group].Value : m.Value) : null;
        }

        protected static List<string> MatchAll(string input, string pattern, int group = 1)
        {
            var res = new List<string>();
            foreach (Match m in Regex.Matches(input ?? "", pattern, RegexOptions.Singleline))
                if (m.Groups.Count > group) res.Add(m.Groups[group].Value);
            return res;
        }

        protected static string Unescape(string s) => WebUtility.HtmlDecode(s ?? "");

        /// <summary>去掉 HTML 标签，用于简介。</summary>
        protected static string Clean(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var t = Regex.Replace(s, "<[^>]+>", "");
            t = WebUtility.HtmlDecode(t);
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        // ==================== JSON ====================

        protected static JsonNode? Json(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text); } catch { return null; }
        }

        /// <summary>兼容 Jackson 风格的 a/b/c 取值，失败返回空串。</summary>
        protected static string Str(JsonNode? node, params string[] path)
        {
            var cur = node;
            foreach (var p in path)
            {
                if (cur == null) return "";
                cur = cur[p];
            }
            return cur?.ToString() ?? "";
        }

        protected static List<JsonNode?> Arr(JsonNode? node, params string[] path)
        {
            var cur = node;
            foreach (var p in path)
            {
                if (cur == null) return new List<JsonNode?>();
                cur = cur[p];
            }
            if (cur is JsonArray a) return a.ToList();
            return new List<JsonNode?>();
        }

        // ==================== 加解密 / 编码（CatVod 的 Utils / Util 对等物）====================

        protected static string Md5(string input)
        {
            using var md5 = MD5.Create();
            var h = md5.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
            var sb = new StringBuilder();
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        protected static string Base64Encode(string input)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(input ?? ""));

        protected static string Base64Decode(string input)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(input ?? "")); }
            catch { return ""; }
        }

        protected static string UrlEncode(string s) => Uri.EscapeDataString(s ?? "");
        protected static string UrlDecode(string s) => Uri.UnescapeDataString(s ?? "");

        /// <summary>AES-CBC/PKCS7（与 CatVod Spider 里 Crypto 的用法一致）。</summary>
        protected static byte[] AesDecryptBytes(byte[] data, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            return dec.TransformFinalBlock(data, 0, data.Length);
        }

        protected static byte[] AesEncryptBytes(byte[] data, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var enc = aes.CreateEncryptor();
            return enc.TransformFinalBlock(data, 0, data.Length);
        }

        protected static string BytesToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>把任意二进制安全地转成字符串（Latin1 往返，避免 UTF-8 破坏字节）。</summary>
        protected static string BytesToString(byte[] b) => Encoding.Latin1.GetString(b);
        protected static byte[] StringToBytes(string s) => Encoding.Latin1.GetBytes(s);

        // ==================== 结果构造 ====================

        protected static VodItem Item(string? id, string? name, string? pic = null, string? remarks = null) => new()
        {
            Id = id ?? "",
            Name = Unescape(name ?? ""),
            Pic = pic ?? "",
            Remarks = remarks ?? ""
        };

        protected static Category Cat(string id, string name) => new() { Id = id, Name = name };

        protected static Episode Ep(string name, string url) => new() { Name = name, Url = url };

        /// <summary>
        /// 从「名称$地址#名称$地址」串构造线路。CatVod 里蜘蛛普遍这么拼 vod_play_url。
        /// 集名里带 $ 时按第一个 $ 切；不带则用「第N集」。
        /// </summary>
        protected static List<Episode> ParsePlayUrl(string playUrl)
        {
            var list = new List<Episode>();
            if (string.IsNullOrWhiteSpace(playUrl)) return list;
            var parts = playUrl.Split('#');
            int n = 0;
            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                n++;
                var idx = p.IndexOf('$');
                if (idx > 0)
                    list.Add(new Episode { Name = Unescape(p.Substring(0, idx)), Url = p.Substring(idx + 1) });
                else
                    list.Add(new Episode { Name = "第" + n + "集", Url = p });
            }
            return list;
        }

        /// <summary>把多个线路按 TVBox 的 $$$ 语义装进详情。</summary>
        protected static VodDetail DetailVod(string id, string name, string pic,
                                             List<string> from, List<List<Episode>> lines)
        {
            var d = new VodDetail { Id = id, Name = Unescape(name ?? ""), Pic = pic ?? "" };
            for (int i = 0; i < from.Count && i < lines.Count; i++)
                d.Lines.Add(new PlayLine { Name = from[i], Episodes = lines[i] });
            return d;
        }

        /// <summary>
        /// 从结果串里解析出 VOD 列表（兼容 CatVod 蜘蛛直接吐 JSON 的情形）。
        /// </summary>
        protected static List<VodItem> ItemsFromVodArray(JsonNode? arr)
        {
            var res = new List<VodItem>();
            if (arr is not JsonArray a) return res;
            foreach (var x in a)
            {
                if (x == null) continue;
                res.Add(new VodItem
                {
                    Id = x["vod_id"]?.ToString() ?? "",
                    Name = x["vod_name"]?.ToString() ?? "",
                    Pic = x["vod_pic"]?.ToString() ?? "",
                    Remarks = x["vod_remarks"]?.ToString() ?? ""
                });
            }
            return res;
        }
    }
}
