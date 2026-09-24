using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TVBoxPC.Core
{
    /// <summary>
    /// 轻量 JSON Path 求值器，等价于 drpy 里 cheerio.jp 的作用
    /// （drpy 的 `json:` 规则族靠它取值）。
    ///
    /// 支持语法：
    ///   $.a.b            取属性
    ///   $.a.b[0]         数组下标（负数表示从末尾数）
    ///   $.a[*].b         遍历数组后取属性（返回数组）
    ///   $..name          递归查找任意层级的 name（返回数组）
    ///   $.a[0:3]         数组切片
    /// 不以 $ 开头的路径自动补 $. 前缀。
    /// </summary>
    public static class JsonPathLite
    {
        private sealed class Seg
        {
            public bool Recursive;
            public string? Name;
            public bool Wildcard;
            public int Index = int.MinValue;
            public int SliceFrom = int.MinValue, SliceTo = int.MinValue;
        }

        /// <summary>求值。path 非法或未命中时返回 null。</summary>
        public static JsonNode? Eval(JsonNode? root, string? path)
        {
            if (root == null || string.IsNullOrWhiteSpace(path)) return null;
            var p = path.Trim();

            // cheerio.jp 的宽松处理：`a.b` 视作 `$.a.b`
            if (!p.StartsWith("$")) p = "$." + p;

            var segs = Parse(p);
            if (segs.Count == 0) return root;

            var cur = new List<JsonNode> { root };
            foreach (var seg in segs)
            {
                var next = new List<JsonNode>();
                foreach (var node in cur)
                {
                    if (seg.Recursive) CollectRecursive(node, seg.Name!, next);
                    else Apply(node, seg, next);
                }
                if (next.Count == 0) return null;
                cur = next;
            }

            if (cur.Count == 1) return cur[0];
            var arr = new JsonArray();
            foreach (var n in cur) arr.Add(n?.DeepClone());
            return arr;
        }

        private static void Apply(JsonNode? node, Seg seg, List<JsonNode> outp)
        {
            if (node == null) return;

            if (seg.Wildcard)
            {
                if (node is JsonArray wa) foreach (var it in wa) if (it != null) outp.Add(it);
                else if (node is JsonObject wo) foreach (var kv in wo) if (kv.Value != null) outp.Add(kv.Value);
                return;
            }

            if (seg.SliceFrom != int.MinValue)
            {
                if (node is not JsonArray sa) return;
                int from = seg.SliceFrom < 0 ? Math.Max(0, sa.Count + seg.SliceFrom) : seg.SliceFrom;
                int to = seg.SliceTo == int.MinValue ? sa.Count
                       : seg.SliceTo < 0 ? Math.Max(0, sa.Count + seg.SliceTo) : seg.SliceTo;
                for (int i = from; i < to && i < sa.Count; i++)
                    if (sa[i] != null) outp.Add(sa[i]!);
                return;
            }

            if (seg.Index != int.MinValue)
            {
                if (node is not JsonArray ia) return;
                int idx = seg.Index < 0 ? ia.Count + seg.Index : seg.Index;
                if (idx >= 0 && idx < ia.Count && ia[idx] != null) outp.Add(ia[idx]!);
                return;
            }

            if (seg.Name != null)
            {
                if (node is JsonObject o && o.TryGetPropertyValue(seg.Name, out var v) && v != null)
                    outp.Add(v);
            }
        }

        private static void CollectRecursive(JsonNode? node, string name, List<JsonNode> outp)
        {
            if (node == null) return;
            if (node is JsonObject o)
            {
                foreach (var kv in o)
                {
                    if (string.Equals(kv.Key, name, StringComparison.Ordinal) && kv.Value != null)
                        outp.Add(kv.Value);
                    CollectRecursive(kv.Value, name, outp);
                }
            }
            else if (node is JsonArray a)
            {
                foreach (var it in a) CollectRecursive(it, name, outp);
            }
        }

        private static List<Seg> Parse(string p)
        {
            var segs = new List<Seg>();
            int i = 0;
            if (p.StartsWith("$")) i = 1;

            while (i < p.Length)
            {
                if (p[i] == '.')
                {
                    bool rec = (i + 1 < p.Length && p[i + 1] == '.');
                    i += rec ? 2 : 1;
                    var sb = new System.Text.StringBuilder();
                    while (i < p.Length && p[i] != '.' && p[i] != '[') sb.Append(p[i++]);
                    var name = sb.ToString();
                    if (name == "*") segs.Add(new Seg { Recursive = rec, Wildcard = true });
                    else if (name.Length > 0) segs.Add(new Seg { Recursive = rec, Name = name });
                    else if (rec) segs.Add(new Seg { Recursive = true, Wildcard = true });
                    continue;
                }

                if (p[i] == '[')
                {
                    int close = p.IndexOf(']', i);
                    if (close < 0) break;
                    var inner = p.Substring(i + 1, close - i - 1).Trim();
                    segs.Add(ParseBracket(inner));
                    i = close + 1;
                    continue;
                }

                // 容错：跳过无法识别的字符
                i++;
            }
            return segs;
        }

        private static Seg ParseBracket(string inner)
        {
            if (inner == "*") return new Seg { Wildcard = true };

            // 去掉引号：['name']
            if (inner.Length >= 2 &&
                ((inner[0] == '\'' && inner[^1] == '\'') || (inner[0] == '"' && inner[^1] == '"')))
                return new Seg { Name = inner.Substring(1, inner.Length - 2) };

            if (inner.Contains(':'))
            {
                var sp = inner.Split(':');
                var seg = new Seg();
                if (sp[0].Trim().Length > 0 && int.TryParse(sp[0].Trim(), out var f)) seg.SliceFrom = f;
                if (sp.Length > 1 && sp[1].Trim().Length > 0 && int.TryParse(sp[1].Trim(), out var t)) seg.SliceTo = t;
                if (seg.SliceFrom == int.MinValue) seg.SliceFrom = 0;
                return seg;
            }

            if (int.TryParse(inner, out var idx)) return new Seg { Index = idx };
            return new Seg { Name = inner };
        }

        /// <summary>把求值结果转成字符串（数组取首个非空，对象转 JSON）。</summary>
        public static string ToText(JsonNode? node)
        {
            if (node == null) return "";
            if (node is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s ?? "";
                return v.ToJsonString().Trim('"');
            }
            if (node is JsonArray a)
            {
                foreach (var it in a)
                {
                    var t = ToText(it);
                    if (!string.IsNullOrEmpty(t)) return t;
                }
                return "";
            }
            return node.ToJsonString();
        }
    }
}
