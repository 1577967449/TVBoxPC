using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Jint;
using Jint.Native;

namespace TVBoxPC.Core.Drpy
{
    /// <summary>把 AngleSharp 元素包装成 JS 可见的句柄，使 pdfh(it, sel) 能作用于 pdfa 返回的条目。</summary>
    public sealed class ElementBox
    {
        public IElement Element { get; }
        public ElementBox(IElement el) => Element = el;
        public override string ToString() => Element.TextContent ?? "";
    }

    /// <summary>
    /// drpy 的 JS 宿主环境。
    ///
    /// drpy 站点脚本（如 `./js/360影视.js`）只声明规则对象或内联 `js:` 片段，
    /// 真正的宿主能力（网络、HTML 抽取、加密、存储）由 TVBox 运行时注入。
    /// 本类用 C# 原生实现同一套宿主 API 并注入 Jint，从而在 PC 端复现 drpy 行为，
    /// 全程不依赖任何 JAR / Java / 第三方 JS 运行时。
    /// </summary>
    public sealed class DrpyJs : IDisposable
    {
        private readonly Engine _e;
        private readonly HttpClient _http;
        private readonly Dictionary<string, string> _store = new();
        private readonly List<string> _logs = new();

        /// <summary>站点脚本所在的绝对基地址（用于把 ./js/xxx.js 之类的相对引用转成绝对地址）。</summary>
        public string SiteBase { get; }
        /// <summary>该规则集的唯一键（drpy 用它做本地存储命名空间）。</summary>
        public string RuleKey { get; }
        /// <summary>规则里声明的 host（部分脚本在加载期就要用）。</summary>
        public string Host { get; set; } = "";

        public IReadOnlyList<string> Logs => _logs;

        static DrpyJs()
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        }

        public DrpyJs(string siteBase, HttpClient http, string ruleKey)
        {
            SiteBase = siteBase;
            _http = http;
            RuleKey = ruleKey;

            _e = new Engine(o => o
                .Strict(false)
                .TimeoutInterval(TimeSpan.FromSeconds(20))
                .MaxStatements(3_000_000)
                .LimitRecursion(256));

            RegisterHostApi();
            Execute(Shim.Prelude);
        }

        // ==================== 执行 ====================

        /// <summary>执行一段 JS（异常包装成 DrpyScriptException）。</summary>
        public void Execute(string code)
        {
            try { _e.Execute(code); }
            catch (Exception ex) { throw new DrpyScriptException(ex.Message, ex); }
        }

        public JsValue Eval(string expr)
        {
            try { return _e.Evaluate(expr); }
            catch (Exception ex) { throw new DrpyScriptException(ex.Message, ex); }
        }

        /// <summary>设置/覆盖一个全局变量。</summary>
        public void SetGlobal(string name, object? value) => _e.SetValue(name, value);

        public JsValue GetGlobal(string name) => _e.GetValue(name);

        /// <summary>把任意 JS 值序列化成 JSON 文本（数组/对象都能正确序列化）。</summary>
        public string StringifyValue(JsValue v) => Stringify(v);

        /// <summary>把规则对象（var rule = {...}）导出成 JsonObject。</summary>
        public JsonObject? GetRuleJson()
        {
            try
            {
                var v = _e.Evaluate("(typeof rule === 'undefined' || rule === null) ? null : JSON.stringify(rule)");
                if (v.IsNull() || v.IsUndefined()) return null;
                var s = v.AsString();
                if (string.IsNullOrWhiteSpace(s) || s == "null") return null;
                return JsonNode.Parse(s) as JsonObject;
            }
            catch { return null; }
        }

        /// <summary>
        /// 执行内联 `js:` 片段。
        /// 片段里可能 `return` 结果（列表类），也可能写全局 `VOD`（详情类），两种都兼容。
        /// async 片段返回的 Promise 会被同步泵送后取值。
        /// </summary>
        public JsValue RunBlock(string jsCode, IDictionary<string, object?>? vars = null)
        {
            var code = jsCode.Trim();
            if (code.StartsWith("js:", StringComparison.OrdinalIgnoreCase))
                code = code.Substring(3);

            var wrapped = @"
globalThis.__drpy_ret__ = undefined;
globalThis.__drpy_err__ = '';
globalThis.__drpy_async__ = false;
(function(){
    var __r;
    try { __r = (function(){
" + code + @"
    })(); } catch (e) { globalThis.__drpy_err__ = (e && e.message) ? e.message : String(e); __r = undefined; }
    if (__r && typeof __r.then === 'function') {
        globalThis.__drpy_async__ = true;
        try {
            __r.then(function(v){ globalThis.__drpy_ret__ = v; },
                     function(e){ globalThis.__drpy_err__ = (e && e.message) ? e.message : String(e); });
        } catch (e) { globalThis.__drpy_err__ = String(e); }
    } else {
        globalThis.__drpy_ret__ = __r;
    }
})();
";
            try
            {
                if (vars != null)
                    foreach (var kv in vars) _e.SetValue(kv.Key, kv.Value);

                _e.Execute(wrapped);

                var isAsync = false;
                try { isAsync = _e.GetValue("__drpy_async__").AsBoolean(); } catch { }
                if (isAsync)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            _e.Advanced.ProcessTasks();
                            var r = _e.GetValue("__drpy_ret__");
                            if (!r.IsUndefined()) break;
                        }
                        catch { break; }
                    }
                }

                var err = "";
                try { err = _e.GetValue("__drpy_err__").AsString(); } catch { }
                if (!string.IsNullOrEmpty(err)) Log("js 片段报错: " + err);

                return _e.GetValue("__drpy_ret__");
            }
            catch (Exception ex)
            {
                throw new DrpyScriptException(ex.Message, ex);
            }
        }

        public void Log(string msg)
        {
            if (_logs.Count < 400) _logs.Add(msg);
        }

        // ==================== 宿主 API ====================

        private void RegisterHostApi()
        {
            // ---- 日志 ----
            _e.SetValue("print", new Action<object?>(o => Log(S(o))));
            _e.SetValue("log", new Action<object?>(o => Log(S(o))));
            _e.SetValue("console", new Dictionary<string, object>
            {
                ["log"] = new Action<object?>(o => Log(S(o))),
                ["info"] = new Action<object?>(o => Log(S(o))),
                ["warn"] = new Action<object?>(o => Log(S(o))),
                ["error"] = new Action<object?>(o => Log(S(o))),
                ["debug"] = new Action<object?>(o => Log(S(o)))
            });

            // ---- 常量 ----
            _e.SetValue("VERSION", "1.0.0-pc");
            _e.SetValue("HOST", "");
            _e.SetValue("RKEY", RuleKey);
            _e.SetValue("RULE_CK", "rule_cookie_" + RuleKey);
            _e.SetValue("OCR_API", "");
            _e.SetValue("fetch_params", new Dictionary<string, object>());
            _e.SetValue("rule_fetch_params", new Dictionary<string, object>());
            _e.SetValue("VOD", new Dictionary<string, object>());
            _e.SetValue("VODS", new List<object>());
            _e.SetValue("TABS", new List<object>());
            _e.SetValue("LISTS", new List<object>());
            _e.SetValue("MY_URL", "");
            _e.SetValue("MY_CATE", "");
            _e.SetValue("MY_FL", new Dictionary<string, object>());
            _e.SetValue("MY_PAGE", 1);
            _e.SetValue("TYPE", "");
            _e.SetValue("KEY", "");
            _e.SetValue("input", "");

            // ---- 网络 ----
            _e.SetValue("request", new Func<JsValue, JsValue, JsValue>(Request));
            _e.SetValue("fetch", new Func<JsValue, JsValue, JsValue>(Request));
            _e.SetValue("req", new Func<JsValue, JsValue, JsValue>(Request));
            _e.SetValue("post", new Func<JsValue, JsValue, JsValue>(Post));
            _e.SetValue("getHtml", new Func<JsValue, JsValue>(url => Request(url, JsValue.Undefined)));
            // PC 端没有 TVBox 的本地代理服务，返回空串表示「无需代理」
            _e.SetValue("getProxyUrl", new Func<string>(() => ""));
            _e.SetValue("getProxy", new Func<bool, string>(_ => ""));

            // ---- 加密原语（供 Shim 里的 CryptoJS 外壳调用）----
            _e.SetValue("__aes_encrypt", new Func<string, string, string, string>(
                (plain, key, iv) => AesCbc(plain, key, iv, true)));
            _e.SetValue("__aes_decrypt", new Func<string, string, string, string>(
                (plain, key, iv) => AesCbc(plain, key, iv, false)));

            // ---- HTML 抽取（等价 cheerio）----
            _e.SetValue("pdfh", new Func<JsValue, JsValue, string>((h, s) => HtmlPdfh(h, s)));
            _e.SetValue("pdfa", new Func<JsValue, JsValue, JsValue>((h, s) => HtmlPdfa(h, s)));
            _e.SetValue("pd", new Func<JsValue, JsValue, JsValue, string>((h, s, u) => HtmlPd(h, s, u)));
            _e.SetValue("pdfh2", new Func<JsValue, JsValue, string>((h, s) => HtmlPdfh(h, s)));
            _e.SetValue("pdfa2", new Func<JsValue, JsValue, JsValue>((h, s) => HtmlPdfa(h, s)));
            _e.SetValue("pd2", new Func<JsValue, JsValue, JsValue, string>((h, s, u) => HtmlPd(h, s, u)));

            // ---- JSON Path ----
            _e.SetValue("jsp", new Func<JsValue, JsValue, JsValue>(JspEval));

            // ---- URL ----
            _e.SetValue("urljoin", new Func<JsValue, JsValue, string>((a, b) => HtmlSelect.UrlJoin(S(a), S(b))));
            _e.SetValue("joinUrl", new Func<JsValue, JsValue, string>((a, b) => HtmlSelect.UrlJoin(S(a), S(b))));
            _e.SetValue("urlDeal", new Func<JsValue, JsValue, string>((a, b) => HtmlSelect.UrlJoin(S(b), S(a))));
            _e.SetValue("buildUrl", new Func<JsValue, JsValue, string>((u, q) => BuildUrl(S(u), q)));

            // ---- 编解码 / 摘要 ----
            _e.SetValue("base64Encode", new Func<JsValue, string>(v => Convert.ToBase64String(Encoding.UTF8.GetBytes(S(v)))));
            _e.SetValue("base64Decode", new Func<JsValue, string>(v => B64Decode(S(v))));
            _e.SetValue("md5", new Func<JsValue, string>(v => Md5(S(v))));
            _e.SetValue("encodeStr", new Func<JsValue, JsValue, string>((v, enc) => UrlEncode(S(v), S(enc))));
            _e.SetValue("decodeStr", new Func<JsValue, JsValue, string>((v, enc) => UrlDecode(S(v), S(enc))));
            _e.SetValue("encodeURIComponent", new Func<JsValue, string>(v => Uri.EscapeDataString(S(v))));
            _e.SetValue("decodeURIComponent", new Func<JsValue, string>(v => Uri.UnescapeDataString(S(v))));

            // ---- 存储 ----
            _e.SetValue("local", new LocalBridge(_store, RuleKey));
            _e.SetValue("getItem", new Func<JsValue, JsValue, string>((k, d) =>
                _store.TryGetValue(Ns(S(k)), out var v) ? v : S(d)));
            _e.SetValue("setItem", new Action<JsValue, JsValue>((k, v) => _store[Ns(S(k))] = S(v)));
            _e.SetValue("clearItem", new Action<JsValue>(k => _store.Remove(Ns(S(k)))));

            // ---- JSON 辅助 ----
            _e.SetValue("dealJson", new Func<JsValue, string>(v => DealJson(S(v))));
            _e.SetValue("obj2str", new Func<JsValue, string>(v => Stringify(v)));
            _e.SetValue("str2obj", new Func<JsValue, JsValue>(v => ParseJson(S(v)) ?? JsValue.Null));

            // ---- 其它 drpy 工具 ----
            _e.SetValue("forceOrder", new Func<JsValue, JsValue>(ForceOrder));
            _e.SetValue("tellIsJx", new Func<JsValue, bool>(v => TellIsJx(S(v))));
            _e.SetValue("setResult", new Action<JsValue>(v => _e.SetValue("VOD", v)));
            _e.SetValue("setResult2", new Action<JsValue>(v => _e.SetValue("VODS", v)));
            _e.SetValue("getHome", new Func<JsValue, string>(v => GetHtmlCached(S(v))));
            _e.SetValue("checkHtml", new Func<JsValue, bool>(v => !string.IsNullOrWhiteSpace(S(v))));
            _e.SetValue("sleep", new Action<int>(ms =>
            {
                if (ms > 0 && ms <= 5000) System.Threading.Thread.Sleep(ms);
            }));
        }

        // ==================== 网络实现 ====================

        private JsValue Request(JsValue urlArg, JsValue optArg)
        {
            if (optArg == null) optArg = JsValue.Undefined;   // fetch(url) 单参数调用时 Jint 传 null
            var url = S(urlArg);
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = HtmlSelect.UrlJoin(SiteBase, url);

            var method = "GET";
            string? body = null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var query = new Dictionary<string, string>();
            var timeout = 20;
            var encoding = "";
            var withHeaders = false;

            if (optArg.IsObject())
            {
                var o = optArg.AsObject();

                var m = o.Get("method");
                if (!m.IsUndefined() && !m.IsNull()) method = S(m).ToUpperInvariant();

                var h = o.Get("headers");
                if (h.IsObject()) CollectProps(h.AsObject(), headers);

                var b = o.Get("body");
                if (!b.IsUndefined() && !b.IsNull())
                    body = b.IsObject() || b.IsArray() ? ObjToForm(b, headers) : S(b);

                var d = o.Get("data");
                if (d.IsObject())
                {
                    var tmp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    CollectProps(d.AsObject(), tmp);
                    foreach (var kv in tmp) query[kv.Key] = kv.Value;
                }

                var t = o.Get("timeout");
                if (!t.IsUndefined() && t.IsNumber())
                {
                    var tv = (int)t.AsNumber();
                    timeout = tv > 1000 ? tv / 1000 : (tv > 0 ? tv : 20);
                }

                var enc = o.Get("encoding");
                if (!enc.IsUndefined() && !enc.IsNull()) encoding = S(enc);

                var wh = o.Get("withHeaders");
                withHeaders = !wh.IsUndefined() && wh.AsBoolean();
            }

            if (!string.IsNullOrEmpty(encoding) && !encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                headers["X-DRPY-ENC"] = encoding;

            string content;
            var respHeaders = new Dictionary<string, string>();
            try
            {
                content = HttpSend(url, method, headers, body, query, timeout, out respHeaders);
            }
            catch (Exception ex)
            {
                Log($"request 失败 {url} : {ex.Message}");
                return withHeaders
                    ? JsValue.FromObject(_e, new Dictionary<string, object>
                    { ["content"] = "", ["headers"] = new Dictionary<string, object>(), ["status"] = 0 })
                    : (JsValue)"";
            }

            if (withHeaders)
                return JsValue.FromObject(_e, new Dictionary<string, object>
                {
                    ["content"] = content,
                    ["headers"] = respHeaders.ToDictionary(k => k.Key, v => (object)v.Value),
                    ["status"] = 200
                });
            return content;
        }

        private JsValue Post(JsValue urlArg, JsValue optArg)
        {
            if (optArg == null) optArg = JsValue.Undefined;
            var url = S(urlArg);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var query = new Dictionary<string, string>();
            string? body = null;
            var timeout = 20;

            if (optArg.IsObject())
            {
                var o = optArg.AsObject();

                var h = o.Get("headers");
                if (h.IsObject()) CollectProps(h.AsObject(), headers);

                var b = o.Get("body");
                if (!b.IsUndefined() && !b.IsNull())
                    body = b.IsObject() || b.IsArray() ? ObjToForm(b, headers) : S(b);

                var d = o.Get("data");
                if (d.IsObject())
                {
                    var tmp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    CollectProps(d.AsObject(), tmp);
                    foreach (var kv in tmp) query[kv.Key] = kv.Value;
                }

                var t = o.Get("timeout");
                if (!t.IsUndefined() && t.IsNumber()) timeout = Math.Max(1, (int)t.AsNumber() / 1000);
            }

            try { return HttpSend(url, "POST", headers, body, query, timeout, out _); }
            catch (Exception ex)
            {
                Log($"post 失败 {url} : {ex.Message}");
                return "";
            }
        }

        /// <summary>读取 JS 对象的全部自有属性（Jint 4 返回的是 &lt;JsValue, PropertyDescriptor&gt;）。</summary>
        private static void CollectProps(Jint.Native.Object.ObjectInstance obj,
            Dictionary<string, string> dst)
        {
            foreach (var kv in obj.GetOwnProperties())
            {
                var name = kv.Key.ToString();
                var val = kv.Value?.Value;
                if (string.IsNullOrEmpty(name) || val == null || val.IsUndefined() || val.IsNull()) continue;
                dst[name] = val.IsObject() || val.IsArray() ? val.ToString() : S(val);
            }
        }

        private string HttpSend(string url, string method, Dictionary<string, string> headers,
            string? body, Dictionary<string, string> query, int timeoutSec,
            out Dictionary<string, string> respHeaders)
        {
            respHeaders = new Dictionary<string, string>();

            var encoding = "";
            if (headers.TryGetValue("X-DRPY-ENC", out var enc)) { encoding = enc; headers.Remove("X-DRPY-ENC"); }

            if (query.Count > 0)
            {
                var sep = url.Contains('?') ? '&' : '?';
                url += sep + string.Join("&", query.Select(kv =>
                    Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
            }

            var isPost = method.Equals("POST", StringComparison.OrdinalIgnoreCase);
            using var req = new HttpRequestMessage(isPost ? HttpMethod.Post : HttpMethod.Get, url);

            if (!headers.ContainsKey("User-Agent"))
                req.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.MobileUserAgent);
            foreach (var kv in headers)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            if (body != null && isPost)
                req.Content = new StringContent(body, Encoding.UTF8,
                    headers.TryGetValue("Content-Type", out var ct) ? ct : "application/x-www-form-urlencoded");

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            using var resp = _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                  .GetAwaiter().GetResult();

            foreach (var h in resp.Headers) respHeaders[h.Key] = string.Join(",", h.Value);
            foreach (var h in resp.Content.Headers) respHeaders[h.Key] = string.Join(",", h.Value);

            var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return HttpFactory.DecodeText(bytes, encoding,
                respHeaders.TryGetValue("Content-Type", out var ctv) ? ctv : "");
        }

        private string GetHtmlCached(string url)
        {
            var key = "__htmlcache_" + Md5(url);
            try
            {
                var cached = _e.GetValue(key);
                if (!cached.IsUndefined() && !cached.IsNull() && cached.IsString())
                    return cached.AsString();
            }
            catch { }

            var res = Request(url, JsValue.Undefined);
            var html = res.IsString() ? res.AsString() : "";
            try { _e.SetValue(key, html); } catch { }
            return html;
        }

        // ==================== 抽取实现 ====================

        private string HtmlPdfh(JsValue htmlArg, JsValue selArg)
        {
            if (htmlArg == null) return "";
            var sel = S(selArg);
            if (string.IsNullOrWhiteSpace(sel)) return "";
            if (htmlArg.IsObject() && htmlArg.ToObject() is ElementBox box)
                return HtmlSelect.Pdfh(box.Element, sel);
            return HtmlSelect.Pdfh(S(htmlArg), sel);
        }

        private JsValue HtmlPdfa(JsValue htmlArg, JsValue selArg)
        {
            if (htmlArg == null) return new JsArray(_e, Array.Empty<JsValue>());
            var sel = S(selArg);
            List<IElement> els;
            if (htmlArg.IsObject() && htmlArg.ToObject() is ElementBox box)
                els = HtmlSelect.Pdfa(box.Element, sel);
            else
                els = HtmlSelect.Pdfa(S(htmlArg), sel);

            var arr = new JsValue[els.Count];
            for (int i = 0; i < els.Count; i++)
                arr[i] = JsValue.FromObject(_e, new ElementBox(els[i]));
            return new JsArray(_e, arr);
        }

        private string HtmlPd(JsValue htmlArg, JsValue selArg, JsValue baseArg)
        {
            var raw = HtmlPdfh(htmlArg, selArg);
            var baseUrl = S(baseArg);
            if (string.IsNullOrEmpty(baseUrl))
            {
                try { baseUrl = S(_e.GetValue("MY_URL")); } catch { }
            }
            return HtmlSelect.UrlJoin(baseUrl, raw);
        }

        private JsValue JspEval(JsValue pathArg, JsValue objArg)
        {
            if (objArg == null) objArg = JsValue.Undefined;
            var path = S(pathArg);
            JsonNode? root;
            if (objArg.IsString())
            {
                try { root = JsonNode.Parse(objArg.AsString()); }
                catch { return ""; }
            }
            else if (objArg.IsObject() || objArg.IsArray())
            {
                try { root = JsonNode.Parse(Stringify(objArg)); }
                catch { return ""; }
            }
            else return "";

            var res = JsonPathLite.Eval(root, path);
            if (res == null) return "";

            var parsed = ParseJson(res.ToJsonString());
            return parsed ?? (JsValue)JsonPathLite.ToText(res);
        }

        // ==================== 工具实现 ====================

        private JsValue? ParseJson(string s)
        {
            try
            {
                _e.SetValue("__drpy_tmp_s", s ?? "");
                return _e.Evaluate(
                    "(function(){ try { return JSON.parse(__drpy_tmp_s) } catch(e) { return null } })()");
            }
            catch { return null; }
        }

        private string Stringify(JsValue v)
        {
            try
            {
                _e.SetValue("__drpy_tmp_v", v);
                return _e.Evaluate(
                    "(function(){ try { return JSON.stringify(__drpy_tmp_v) } catch(e) { return '' } })()")
                    .AsString();
            }
            catch { return "{}"; }
        }

        private static string ObjToForm(JsValue v, Dictionary<string, string> headers)
        {
            try
            {
                if (v.IsObject())
                {
                    var parts = new List<string>();
                    foreach (var kv in v.AsObject().GetOwnProperties())
                    {
                        var name = kv.Key.ToString();
                        var val = kv.Value?.Value?.ToString() ?? "";
                        parts.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(val));
                    }
                    if (parts.Count > 0)
                    {
                        if (!headers.ContainsKey("Content-Type"))
                            headers["Content-Type"] = "application/x-www-form-urlencoded";
                        return string.Join("&", parts);
                    }
                }
                return v.ToString();
            }
            catch { return ""; }
        }

        private JsValue ForceOrder(JsValue arr)
        {
            // drpy 的 forceOrder 用于按名称去重 + 稳定排序
            try
            {
                if (!arr.IsArray()) return arr;
                var a = arr.AsArray();
                var seen = new HashSet<string>();
                var list = new List<JsValue>();
                for (uint i = 0; i < a.Length; i++)
                {
                    var it = a.Get(i);
                    var name = it.IsObject() ? it.AsObject().Get("vod_name").ToString() : it.ToString();
                    if (string.IsNullOrEmpty(name) || seen.Add(name)) list.Add(it);
                }
                return new JsArray(_e, list.ToArray());
            }
            catch { return arr; }
        }

        private static bool TellIsJx(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var u = url.ToLowerInvariant();
            if (u.Contains(".m3u8") || u.Contains(".mp4") || u.Contains(".flv")
                || u.Contains(".mkv") || u.Contains(".ts") || u.StartsWith("magnet:")) return false;
            return u.StartsWith("http");
        }

        private static string BuildUrl(string url, JsValue query)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (query == null) return url;
            var pairs = new List<string>();
            try
            {
                if (query.IsObject())
                    foreach (var kv in query.AsObject().GetOwnProperties())
                    {
                        var name = kv.Key.ToString();
                        var val = kv.Value?.Value;
                        if (val == null || val.IsUndefined() || val.IsNull()) continue;
                        pairs.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(val.ToString()));
                    }
            }
            catch { }
            if (pairs.Count == 0) return url;

            var frag = "";
            var idx = url.IndexOf('#');
            if (idx >= 0) { frag = url.Substring(idx); url = url.Substring(0, idx); }
            var sep = url.Contains('?') ? '&' : '?';
            return url + sep + string.Join("&", pairs) + frag;
        }

        private static string B64Decode(string s)
        {
            try
            {
                s = s.Trim().Replace('-', '+').Replace('_', '/');
                switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
            catch { return ""; }
        }

        private static string Md5(string s)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(s ?? ""));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string UrlEncode(string s, string enc)
            => string.Concat(Encoding.UTF8.GetBytes(s).Select(b => "%" + b.ToString("X2")));

        private static string UrlDecode(string s, string enc)
        {
            var e = ResolveEncoding(enc);
            try
            {
                var bytes = new List<byte>();
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '%' && i + 2 < s.Length &&
                        byte.TryParse(s.Substring(i + 1, 2),
                            System.Globalization.NumberStyles.HexNumber, null, out var b))
                    { bytes.Add(b); i += 2; }
                    else if (s[i] == '+') bytes.Add(0x20);
                    else bytes.AddRange(e.GetBytes(s[i].ToString()));
                }
                return e.GetString(bytes.ToArray());
            }
            catch { return s; }
        }

        internal static Encoding ResolveEncoding(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Encoding.UTF8;
            try { return Encoding.GetEncoding(name.Trim()); }
            catch { return Encoding.UTF8; }
        }

        /// <summary>从 HTML 里抠出 JSON（兼容 JSONP 包裹）。</summary>
        public static string DealJson(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var t = html.Trim();
            if (t.StartsWith("{") || t.StartsWith("[")) return t;

            var i1 = t.IndexOfAny(new[] { '{', '[' });
            if (i1 < 0) return t;
            var open = t[i1];
            var close = open == '{' ? '}' : ']';
            var last = t.LastIndexOf(close);
            if (last > i1) return t.Substring(i1, last - i1 + 1);
            return t.Substring(i1);
        }

        /// <summary>AES-CBC/PKCS7 原语。encrypt=true 时返回 Base64 密文，否则把 Base64 密文解回明文。</summary>
        private static string AesCbc(string input, string key, string iv, bool encrypt)
        {
            try
            {
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                aes.Key = FixKeySize(Encoding.UTF8.GetBytes(key ?? ""));
                aes.IV = FixIv(iv);

                if (encrypt)
                {
                    var data = Encoding.UTF8.GetBytes(input ?? "");
                    using var enc = aes.CreateEncryptor();
                    return Convert.ToBase64String(enc.TransformFinalBlock(data, 0, data.Length));
                }
                else
                {
                    var data = Convert.FromBase64String((input ?? "").Trim());
                    using var dec = aes.CreateDecryptor();
                    return Encoding.UTF8.GetString(dec.TransformFinalBlock(data, 0, data.Length));
                }
            }
            catch (Exception ex)
            {
                return encrypt ? "" : "???(" + ex.Message + ")";
            }
        }

        /// <summary>把任意长度密钥规整到 16/24/32 字节（drpy 里常见 16 字节短密钥）。</summary>
        private static byte[] FixKeySize(byte[] raw)
        {
            if (raw.Length == 16 || raw.Length == 24 || raw.Length == 32) return raw;
            var size = raw.Length <= 16 ? 16 : raw.Length <= 24 ? 24 : 32;
            var buf = new byte[size];
            Array.Copy(raw, buf, Math.Min(raw.Length, size));
            return buf;
        }

        private static byte[] FixIv(string iv)
        {
            var raw = Encoding.UTF8.GetBytes(iv ?? "");
            if (raw.Length == 16) return raw;
            var buf = new byte[16];
            if (raw.Length > 0) Array.Copy(raw, buf, Math.Min(raw.Length, 16));
            return buf;
        }

        private string Ns(string k) => RuleKey + "/" + k;

        private static string S(JsValue v)
        {
            try
            {
                // 注意：Jint 调用 CLR 委托时，未传的参数会是 null（而非 Undefined）
                if (v == null || v.IsNull() || v.IsUndefined()) return "";
                if (v.IsString()) return v.AsString();
                if (v.IsBoolean()) return v.AsBoolean() ? "true" : "false";
                if (v.IsNumber()) return v.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (v.IsObject() && v.ToObject() is ElementBox box) return box.Element.TextContent ?? "";
                if (v.IsArray() || v.IsObject()) return v.ToString();
                return v.ToObject()?.ToString() ?? "";
            }
            catch { return ""; }
        }

        /// <summary>CLR 侧传入的任意对象（print/log/console 的参数）。</summary>
        private static string S(object? o)
        {
            if (o == null) return "";
            if (o is JsValue jv) return S(jv);
            return o.ToString() ?? "";
        }

        public void Dispose()
        {
            try { _e.Dispose(); } catch { }
        }
    }

    /// <summary>drpy 规则脚本执行异常。</summary>
    public sealed class DrpyScriptException : Exception
    {
        public DrpyScriptException(string message, Exception? inner) : base(message, inner) { }
    }

    /// <summary>drpy 的 local 存储桥（local.set/get/delete(ns, key, value)）。</summary>
    public sealed class LocalBridge
    {
        private readonly Dictionary<string, string> _d;
        private readonly string _defNs;
        public LocalBridge(Dictionary<string, string> d, string defNs) { _d = d; _defNs = defNs; }

        public void set(string ns, string k, string v) => _d[Key(ns, k)] = v ?? "";
        public string get(string ns, string k) => _d.TryGetValue(Key(ns, k), out var v) ? v : "";
        public void delete(string ns, string k) => _d.Remove(Key(ns, k));

        private string Key(string ns, string k)
            => (string.IsNullOrEmpty(ns) ? _defNs : ns) + "/" + k;
    }
}
