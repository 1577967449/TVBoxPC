using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TVBoxPC.Core.Jar
{
    /// <summary>
    /// JVM 蜘蛛桥 —— 把「配置里的 csp_XXX 源」交给桌面 JRE 上的原版蜘蛛执行。
    ///
    /// 一、为什么要这样做
    ///   TVBox 的 csp_XXX 源分两类：
    ///     · 一类是通用壳（csp_XPath 等），规则是明文 JSON —— 本工程已用 C# 实现；
    ///     · 另一类把解析算法**编译进作者自己的 dex/jar**，还叠加了字符串加密
    ///       （U+06DF..U+06E8 阿拉伯字符当数字 token，配合控制流混淆分派）
    ///       —— 逐个反混淆后用 C# 重写既慢又永远追不上作者更新。
    ///   实测结论：这些蜘蛛包的外部依赖只有 2 个 catvod 类 + 65 个 android 类 +
    ///   gson/okhttp/org.json/jsoup，**完全可以在桌面 JVM 上跑**。于是改为"原样执行"。
    ///
    /// 二、一次调用的完整链路
    ///   1) 配置顶层 spider 字段（形如 "./jar/fan.txt;md5;xxxx"）→ 下载 + md5 校验 + 缓存；
    ///   2) 蜘蛛包通常是 DEX（或在 zip 里装 classes.dex）→ dex2jar 转成 class jar 并缓存
    ///      （转换产物按 md5 命名，二次启动零成本）；
    ///   3) 起子进程：
    ///        java -noverify -Xmx512m -Dfile.encoding=UTF-8
    ///             -Dtvbox.spiderCacheDir=<沙箱>
    ///             -cp "stubs.jar;libs/*;fan.jar"
    ///             SpiderRunner <fan.jar> <com.github.catvod.spider.Xxx> <method> <ext> [args…]
    ///      并把 stdout 当作唯一结果通道（SpiderRunner 已把蜘蛛自己的打印全部改道 stderr）；
    ///   4) 解析 stdout 的 JSON 成界面模型。
    ///
    /// 三、踩过的坑（都已在本文件里处理）
    ///   · 蜘蛛 <init>/<clinit> 里有 System.out.println 调试残留 → 由 SpiderRunner 隔离；
    ///   · 蜘蛛会**清理** getCacheDir()，沙箱必须与 jar 缓存分目录，否则缓存被删导致全体
    ///     ClassNotFoundException；
    ///   · ext 里含引号/空格/&/中文 → 必须用 ArgumentList 传参，不能拼命令行；
    ///   · OKHttp 走 ProxySelector，只有显式给 -Dhttp(s).proxyHost 才生效；
    ///   · 每个源首次调用都要付 JVM 启动成本，必须限制并发（默认 3）并缓存首页分类。
    /// </summary>
    public sealed class JvmBridge
    {
        public static JvmBridge Instance { get; } = new();

        /// <summary>并发上限：每次调用都是一个 JVM，开太多会把机器拖垮。</summary>
        private readonly SemaphoreSlim _slots = new(3, 3);

        /// <summary>转换/下载的去重锁，避免同时有多个站点触发同一份包的转换。</summary>
        private readonly SemaphoreSlim _prepareLock = new(1, 1);

        private readonly ConcurrentDictionary<string, string?> _loaded = new();   // spec → jar 路径（null=失败）
        private readonly ConcurrentDictionary<string, CacheEntry> _homeCache = new();

        private sealed class CacheEntry
        {
            public string? Json;
            public DateTime At;
        }

        /// <summary>已解析好的蜘蛛包（所有站点共享同一份）。</summary>
        public string? JarPath { get; private set; }
        public bool Ready => !string.IsNullOrEmpty(JarPath) && File.Exists(JarPath);

        // ==================== 配置入口 ====================

        private string _spec = "";
        private string _baseUrl = "";
        public bool UseProxy { get; set; }

        /// <summary>
        /// 声明本次配置使用的蜘蛛包。形如 "./jar/fan.txt;md5;a1b2…" 或 "https://x/spider.jar"。
        /// 只记录，不做 IO —— 真正的下载/转换延迟到第一次 invoke 时（避免加载配置时卡界面）。
        /// </summary>
        public void Configure(string? spiderSpec, string? configBaseUrl, bool useProxy)
        {
            var spec = (spiderSpec ?? "").Trim();
            var bas = configBaseUrl ?? "";
            UseProxy = useProxy;
            if (spec == _spec && bas == _baseUrl) return;
            _spec = spec; _baseUrl = bas;
            JarPath = null;
            LastError = null;
            _homeCache.Clear();
            _loaded.Clear();
        }

        public bool Configured => !string.IsNullOrWhiteSpace(_spec);

        // ==================== 准备（下载 + 转换） ====================

        private static (string Url, string? Md5) SplitSpec(string spec)
        {
            // TVBox 约定： "<url>;md5;<hex>" 或 "<url>;md5;<hex>;<extra>"
            var parts = spec.Split(';');
            var url = parts.Length > 0 ? parts[0].Trim() : "";
            string? md5 = null;
            for (int i = 0; i + 1 < parts.Length; i += 2)
                if (parts[i].Trim().Equals("md5", StringComparison.OrdinalIgnoreCase))
                    md5 = parts[i + 1].Trim().ToLowerInvariant();
            return (url, md5);
        }

        private static string Md5OfFile(string path)
        {
            using var md5 = MD5.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
        }

        /// <summary>下载（若需要）并转换为可上 classpath 的 jar。失败返回 null 并写 LastError。</summary>
        public async Task<string?> PrepareAsync()
        {
            if (Ready) return JarPath;
            if (!JvmRuntime.CanRun)
            {
                LastError = JvmRuntime.MissingReason;
                return null;
            }
            if (!Configured)
            {
                LastError = "该配置没有声明顶层 spider 字段，无法运行 JAR 蜘蛛源。";
                return null;
            }

            await _prepareLock.WaitAsync();
            try
            {
                if (Ready) return JarPath;

                var (url, wantMd5) = SplitSpec(_spec);
                if (string.IsNullOrWhiteSpace(url))
                {
                    LastError = "spider 字段为空。";
                    return null;
                }

                // ---------- 1) 取到本地原始文件 ----------
                var rawPath = Path.Combine(JvmRuntime.DataDir, "raw",
                    (wantMd5 ?? Hash(url)).ToLowerInvariant() + ".bin");
                Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);

                if (!File.Exists(rawPath) || (wantMd5 != null && Md5OfFile(rawPath) != wantMd5))
                {
                    var abs = Resolve(url);
                    var bytes = await DownloadAsync(abs);
                    if (bytes == null || bytes.Length < 64)
                    {
                        LastError = "蜘蛛包下载失败：" + abs + "（检查网络/代理设置）";
                        return null;
                    }
                    await File.WriteAllBytesAsync(rawPath, bytes);
                }

                if (wantMd5 != null)
                {
                    var got = Md5OfFile(rawPath);
                    if (got != wantMd5)
                    {
                        // md5 不符：文件多半是半截缓存或被 CDN 改写，删掉重下一次
                        try { File.Delete(rawPath); } catch { }
                        LastError = $"蜘蛛包 md5 校验失败（期望 {wantMd5[..8]}… 实际 {got[..8]}…），已清除缓存，请重试。";
                        return null;
                    }
                }

                var cacheKey = (wantMd5 ?? Md5OfFile(rawPath)).ToLowerInvariant();

                // ---------- 2) 转成 class jar ----------
                var kind = JvmRuntime.Detect(rawPath);
                if (kind == SpiderPackageKind.Jar)
                {
                    JarPath = rawPath;          // 本身就是普通 jar，直接上 classpath
                }
                else
                {
                    var outJar = Path.Combine(JvmRuntime.JarCacheDir, cacheKey + ".jar");
                    if (!File.Exists(outJar))
                    {
                        var ok = await ConvertAsync(rawPath, kind, outJar, cacheKey);
                        if (!ok) return null;
                    }
                    JarPath = outJar;
                }

                LastError = null;
                return JarPath;
            }
            catch (Exception ex)
            {
                LastError = "准备蜘蛛运行时失败：" + ex.Message;
                return null;
            }
            finally { _prepareLock.Release(); }
        }

        private string Resolve(string url)
        {
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
            if (string.IsNullOrEmpty(_baseUrl)) return url;
            try
            {
                // 配置文件来自网络：走 URI 相对解析
                if (_baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return new Uri(new Uri(_baseUrl), url).ToString();
                // 配置文件是本地文件：相对它所在目录解析（spider 常写成 "./jar/fan.txt"）
                var dir = Path.GetDirectoryName(_baseUrl);
                if (!string.IsNullOrEmpty(dir))
                    return Path.GetFullPath(Path.Combine(dir, url.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch { }
            return url;
        }

        private static string Hash(string s)
        {
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        }

        private static async Task<byte[]?> DownloadAsync(string url)
        {
            // 本地文件配置：spider 可能就躺在配置同级的 jar/ 目录里
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try { return File.Exists(url) ? await File.ReadAllBytesAsync(url) : null; }
                catch { return null; }
            }
            try
            {
                var http = HttpFactory.Create(60);
                return await http.GetByteArrayAsync(url);
            }
            catch
            {
                // 系统代理残留（ProxyEnable=1 但服务已停）时改直连再试一次
                try
                {
                    var http = HttpFactory.CreateDirect(60);
                    return await http.GetByteArrayAsync(url);
                }
                catch { return null; }
            }
        }

        /// <summary>DEX → JAR（用 dex-tools 的 Dex2jarCmd）。</summary>
        private async Task<bool> ConvertAsync(string rawPath, SpiderPackageKind kind, string outJar, string cacheKey)
        {
            var work = Path.Combine(JvmRuntime.DataDir, "work", cacheKey);
            Directory.CreateDirectory(work);

            string dexPath = rawPath;
            if (kind == SpiderPackageKind.DexInZip)
            {
                dexPath = Path.Combine(work, "classes.dex");
                try
                {
                    using var zip = System.IO.Compression.ZipFile.OpenRead(rawPath);
                    var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName.Equals("classes.dex", StringComparison.OrdinalIgnoreCase))
                        ?? zip.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith(".dex", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                    {
                        LastError = "蜘蛛包是个 zip，但里面没有 classes.dex。";
                        return false;
                    }
                    entry.ExtractToFile(dexPath, true);
                }
                catch (Exception ex)
                {
                    LastError = "解出 classes.dex 失败：" + ex.Message;
                    return false;
                }
            }

            var d2jCp = JvmRuntime.D2jClassPath();
            if (string.IsNullOrEmpty(d2jCp))
            {
                LastError = "缺少 dex2jar 组件（jvm/d2j/*.jar）。";
                return false;
            }

            var psi = JvmRuntime.NewPsi();
            foreach (var a in new[]
            {
                "-Xmx256m", "-XX:+UseSerialGC", "-XX:TieredStopAtLevel=1",
                "-XX:ReservedCodeCacheSize=32m", "-Dfile.encoding=UTF-8",
                "-cp", d2jCp, "com.googlecode.dex2jar.tools.Dex2jarCmd",
                dexPath, "-o", outJar, "--force"
            }) psi.ArgumentList.Add(a);

            try
            {
                using var p = Process.Start(psi);
                if (p == null) { LastError = "无法启动 dex2jar。"; return false; }
                var errTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
                var err = await errTask;
                if (p.ExitCode != 0 || !File.Exists(outJar) || new FileInfo(outJar).Length < 1024)
                {
                    LastError = $"dex2jar 转换失败（exit={p.ExitCode}）：{Tail(err, 300)}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = "dex2jar 转换异常：" + ex.Message;
                return false;
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            }
        }

        private static string Tail(string? s, int n)
            => string.IsNullOrEmpty(s) ? "" : (s!.Length <= n ? s : s[^n..]);

        // ==================== 调用 ====================

        /// <summary>蜘蛛 stderr（日志）最近一次内容，界面可显示用于排错。</summary>
        public string LastLog { get; private set; } = "";

        /// <summary>
        /// 最近一次调用的**硬错误**。为 null 表示"蜘蛛跑完了，只是没给内容"——
        /// 这两者必须区分：网盘搜索源天生不实现 homeContent（返回空是正常行为），
        /// 若把空结果当成错误，界面会把这类源整体标成不可用。
        /// </summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// 调用蜘蛛方法。method ∈ homeContent / categoryContent / detailContent /
        /// searchContent / playerContent。返回 stdout 的 JSON 原文（失败返回 null）。
        /// </summary>
        public async Task<string?> InvokeAsync(string classShortName, string method, string ext,
                                               params string[] args)
        {
            var jar = await PrepareAsync();
            if (jar == null) return null;

            var cls = classShortName.StartsWith("com.github.catvod.spider.", StringComparison.Ordinal)
                ? classShortName
                : "com.github.catvod.spider." + classShortName;

            await _slots.WaitAsync();
            try
            {
                var psi = JvmRuntime.NewPsi();
                psi.ArgumentList.Add("-noverify");
                psi.ArgumentList.Add("-Xmx512m");
                psi.ArgumentList.Add("-Dfile.encoding=UTF-8");
                psi.ArgumentList.Add("-Dtvbox.spiderCacheDir=" + JvmRuntime.SandboxDir);
                psi.ArgumentList.Add("-Dhttp.nonProxyHosts=localhost|127.0.0.1");
                if (UseProxy)
                {
                    // OKHttp 默认走 ProxySelector，只有显式给系统属性才生效
                    psi.ArgumentList.Add("-Dhttp.proxyHost=127.0.0.1");
                    psi.ArgumentList.Add("-Dhttp.proxyPort=7897");
                    psi.ArgumentList.Add("-Dhttps.proxyHost=127.0.0.1");
                    psi.ArgumentList.Add("-Dhttps.proxyPort=7897");
                }
                psi.ArgumentList.Add("-cp");
                psi.ArgumentList.Add(JvmRuntime.BaseClassPath() + ";" + jar);
                psi.ArgumentList.Add("SpiderRunner");
                psi.ArgumentList.Add(jar);
                psi.ArgumentList.Add(cls);
                psi.ArgumentList.Add(method);
                psi.ArgumentList.Add(ext ?? "");
                foreach (var a in args) psi.ArgumentList.Add(a ?? "");

                using var p = Process.Start(psi);
                if (p == null) { LastError = "无法启动 java 子进程。"; return null; }

                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                p.StandardInput.Close();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    LastLog = "调用超时（75s）：" + cls + "." + method;
                    return null;
                }

                var stdout = (await outTask).Trim();
                var stderr = await errTask;
                LastLog = Condense(stderr);

                if (string.IsNullOrEmpty(stdout))
                {
                    // 蜘蛛主动返回空（不实现该方法）≠ 出错：只有 stderr 里真有异常才算硬错误
                    LastError = ExtractError(stderr);
                    return null;
                }
                LastError = null;
                // 契约：stdout 只有一行结果 JSON。多行说明蜘蛛又往结果通道写了东西，
                // 兜底取"第一个以 { 或 [ 开头的行"，避免白丢一次请求。
                var line = stdout.Split('\n').Select(x => x.Trim())
                    .FirstOrDefault(x => x.StartsWith("{") || x.StartsWith("[")) ?? stdout;
                return line;
            }
            catch (Exception ex)
            {
                LastError = "调用蜘蛛失败：" + ex.Message;
                return null;
            }
            finally { _slots.Release(); }
        }

        /// <summary>首页分类带短时缓存（分类极少变，来回切源时不必每次都起 JVM）。</summary>
        public async Task<string?> HomeContentCachedAsync(string classShortName, string ext)
        {
            var key = classShortName + "\u0001" + ext;
            if (_homeCache.TryGetValue(key, out var c) &&
                (DateTime.UtcNow - c.At).TotalMinutes < 10)
                return c.Json;
            var json = await InvokeAsync(classShortName, "homeContent", ext);
            _homeCache[key] = new CacheEntry { Json = json, At = DateTime.UtcNow };
            return json;
        }

        private static string Condense(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var lines = s.Split('\n')
                .Select(x => x.TrimEnd())
                .Where(x => x.Length > 0 && !x.StartsWith("OpenJDK 64-Bit"))
                .ToList();
            return lines.Count <= 40 ? string.Join("\n", lines)
                                     : string.Join("\n", lines.Take(40)) + "\n…";
        }

        private static string? ExtractError(string stderr)
        {
            var lines = stderr.Split('\n');
            var e = lines.LastOrDefault(l => l.StartsWith("[SpiderRunner.ERROR]"));
            if (e != null) return e["[SpiderRunner.ERROR] ".Length..].Trim();
            // 被蜘蛛自己 catch 掉的异常经 SpiderDebug 落到 stderr
            var d = lines.Where(l => l.StartsWith("D/SpiderLog:"))
                         .Select(l => l["D/SpiderLog:".Length..].Trim())
                         .Where(l => l.Length > 0 && !l.Contains("自定义爬虫代码加载成功"))
                         .ToList();
            if (d.Count > 0) return d[^1];
            return null;
        }
    }
}
