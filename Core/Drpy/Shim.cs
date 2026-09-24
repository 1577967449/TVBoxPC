namespace TVBoxPC.Core.Drpy
{
    /// <summary>
    /// 注入到 Jint 的纯 JS 预置代码。
    /// 只做两件事：
    ///   1) 补齐 drpy 脚本会用到、但不值得用 C# 实现的轻量工具（jinja2 模板、cheerio 桥、CryptoJS 外壳）；
    ///   2) 所有重活（网络、HTML 抽取、摘要、AES 原语）都调用 C# 宿主函数。
    /// 注意：这里不引入任何外部/第三方脚本。
    /// </summary>
    internal static class Shim
    {
        public const string Prelude = @"
var MOBILE_UA = 'Mozilla/5.0 (Linux; Android 13; 2201123C Build/TP1A.220624.014) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36';
var PC_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36';
var UC_UA = MOBILE_UA;
var SPECIAL_URL = /^(ftp|magnet|thunder|ed2k|file|javascript|push):/i;

/* ---------- jinja2 极简实现：只处理 {{ a.b }} 取值（drpy 用它渲染 url/filter_url） ---------- */
function jinja2(tpl, data) {
    if (tpl === undefined || tpl === null) return tpl;
    return String(tpl).replace(/\{\{\s*([^{}]+?)\s*\}\}/g, function (m, expr) {
        try {
            var parts = String(expr).split('.');
            var cur = data;
            for (var i = 0; i < parts.length; i++) {
                if (cur === null || cur === undefined) return '';
                cur = cur[parts[i].trim()];
            }
            return (cur === null || cur === undefined) ? '' : String(cur);
        } catch (e) { return ''; }
    });
}

/* ---------- cheerio 桥：drpy 只用 jp / jinja2；部分站点脚本会用 load(html) ---------- */
function __wrapEl(el) {
    return {
        text: function () { return pdfh(el, '&&Text'); },
        html: function () { return pdfh(el, '&&Html'); },
        val: function () { return pdfh(el, '&&value'); },
        attr: function (n) { return pdfh(el, '&&' + n); },
        find: function (s) {
            var box = {
                length: pdfa(el, s).length,
                text: function () { return pdfh(el, s + '&&Text'); },
                html: function () { return pdfh(el, s + '&&Html'); },
                attr: function (n) { return pdfh(el, s + '&&' + n); },
                eq: function (i) { var a = pdfa(el, s); return __wrapEl(a[i]); },
                each: function (fn) {
                    var a = pdfa(el, s);
                    for (var i = 0; i < a.length; i++) fn.call(__wrapEl(a[i]), i, __wrapEl(a[i]));
                }
            };
            return box;
        }
    };
}

function __cheerioLoad(html) {
    function wrap(sel) {
        sel = sel || '';
        var box = {
            length: sel ? pdfa(html, sel).length : 0,
            text: function () { return sel ? pdfh(html, sel + '&&Text') : ''; },
            html: function () { return sel ? pdfh(html, sel + '&&Html') : ''; },
            val: function () { return sel ? pdfh(html, sel + '&&value') : ''; },
            attr: function (n) { return sel ? pdfh(html, sel + '&&' + n) : ''; },
            eq: function (i) { var a = pdfa(html, sel); return __wrapEl(a[i]); },
            each: function (fn) {
                var a = pdfa(html, sel);
                for (var i = 0; i < a.length; i++) fn.call(__wrapEl(a[i]), i, __wrapEl(a[i]));
                return box;
            },
            find: function (s) {
                var sub = sel ? (sel + ' ' + s) : s;
                return {
                    length: pdfa(html, sub).length,
                    text: function () { return pdfh(html, sub + '&&Text'); },
                    html: function () { return pdfh(html, sub + '&&Html'); },
                    attr: function (n) { return pdfh(html, sub + '&&' + n); },
                    eq: function (i) { var a = pdfa(html, sub); return __wrapEl(a[i]); },
                    each: function (fn) {
                        var a = pdfa(html, sub);
                        for (var i = 0; i < a.length; i++) fn.call(__wrapEl(a[i]), i, __wrapEl(a[i]));
                    }
                };
            }
        };
        return box;
    }
    return wrap;
}

var cheerio = {
    jp: function (path, obj) { return jsp(path, obj); },
    jinja2: jinja2,
    load: __cheerioLoad,
    html: function (h) { return __cheerioLoad(h); }
};

/* ---------- CryptoJS 外壳（原语由 C# 提供，覆盖 drpy 常见的 MD5 / AES-CBC / 编码转换） ---------- */
function __WA(b64, plain) { return { __wa: true, __b64: b64, __plain: plain }; }

var CryptoJS = {
    enc: {
        Utf8: {
            parse: function (s) { return { __wa: true, __plain: String(s), __utf8: true }; },
            stringify: function (w) { return w && w.__plain !== undefined ? w.__plain : String(w); }
        },
        Hex: {
            parse: function (s) { return { __wa: true, __hex: String(s) }; }
        },
        Base64: {
            parse: function (s) { return { __wa: true, __b64: String(s) }; },
            stringify: function (w) { return w && w.__b64 !== undefined ? w.__b64 : String(w); }
        },
        Latin1: {
            parse: function (s) { return { __wa: true, __plain: String(s) }; },
            stringify: function (w) { return w && w.__plain !== undefined ? w.__plain : String(w); }
        }
    },
    MD5: function (s) { return { toString: function () { return md5(String(s)); } }; },
    mode: { CBC: 'CBC', ECB: 'ECB' },
    pad: { Pkcs7: 'Pkcs7', ZeroPadding: 'Zero' },
    AES: {
        encrypt: function (plain, key, cfg) {
            cfg = cfg || {};
            var p = (plain && plain.__plain !== undefined) ? plain.__plain : String(plain);
            var k = (key && (key.__plain !== undefined || key.__hex !== undefined))
                ? (key.__plain !== undefined ? key.__plain : key.__hex) : String(key);
            var iv = '';
            if (cfg.iv) iv = (cfg.iv.__plain !== undefined || cfg.iv.__hex !== undefined)
                ? (cfg.iv.__plain !== undefined ? cfg.iv.__plain : cfg.iv.__hex) : String(cfg.iv);
            var b64 = __aes_encrypt(p, k, iv);
            return {
                ciphertext: __WA(b64),
                toString: function () { return b64; }
            };
        },
        decrypt: function (cipher, key, cfg) {
            var c = (cipher && cipher.__b64 !== undefined) ? cipher.__b64
                : (cipher && cipher.ciphertext && cipher.ciphertext.__b64 !== undefined) ? cipher.ciphertext.__b64
                    : String(cipher);
            var k = (key && (key.__plain !== undefined || key.__hex !== undefined))
                ? (key.__plain !== undefined ? key.__plain : key.__hex) : String(key);
            cfg = cfg || {};
            var iv = '';
            if (cfg.iv) iv = (cfg.iv.__plain !== undefined || cfg.iv.__hex !== undefined)
                ? (cfg.iv.__plain !== undefined ? cfg.iv.__plain : cfg.iv.__hex) : String(cfg.iv);
            var plain = __aes_decrypt(String(c), k, iv);
            return {
                __plain: plain,
                toString: function (enc) { return plain; }
            };
        }
    }
};
function AES_encrypt_utf8(plain, key) { return __aes_encrypt(String(plain), String(key), ''); }

/* ---------- 其余小工具 ---------- */
function obj2str(o) { try { return JSON.stringify(o); } catch (e) { return ''; } }
function str2obj(s) { try { return JSON.parse(s); } catch (e) { return {}; } }
function isJson(s) { try { JSON.parse(s); return true; } catch (e) { return false; } }
function setInterval() { return 0; }
function clearInterval() { }
function setTimeout(fn) { try { if (typeof fn === 'function') fn(); } catch (e) { } return 0; }
function clearTimeout() { }
function alert(m) { print(m); }
function md5_16(s) { return md5(String(s)).substring(8, 24); }
";
    }
}
