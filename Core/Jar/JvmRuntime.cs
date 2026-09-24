using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TVBoxPC.Core.Jar
{
    /// <summary>
    /// 桌面 JVM 蜘蛛桥的「运行时资产定位」。
    ///
    /// 背景：TVBox 生态里有几十个源（csp_XXX）的解析算法**写死在作者编译好的
    /// dex/jar 蜘蛛包里**，且做了字符串加密 + 控制流混淆，逐个用 C# 重写不现实。
    /// 本工程改为在桌面 JRE 上**原样运行**这些蜘蛛：宿主桥 = Android 桩类
    /// （stubs.jar）+ SpiderRunner（反射调用）+ dex2jar（DEX→JAR）。
    ///
    /// 运行时目录布局（随程序分发，位于 exe 同级 jvm/）：
    ///   jvm/jre/bin/java.exe      —— 精简 JRE 17（Temurin JRE）
    ///   jvm/stubs/stubs.jar       —— 243 个 Android / catvod 仿真类 + SpiderRunner
    ///   jvm/libs/*.jar            —— okhttp / gson / jsoup / org.json / bcprov / kotlin…
    ///   jvm/d2j/*.jar             —— dex-tools（把配置里下载到的 dex 转成 JVM 能加载的 jar）
    ///
    /// 数据目录（可写，与程序目录分开，避免 Program Files 只读）：
    ///   %LOCALAPPDATA%/TVBoxPC/spider/           蜘蛛包原始文件（按 md5 命名）
    ///   %LOCALAPPDATA%/TVBoxPC/spider/jar/       转换后的 class jar（ClassLoader 用）
    ///   %LOCALAPPDATA%/TVBoxPC/spider/sandbox/   蜘蛛可见的 cacheDir 沙箱
    /// </summary>
    public static class JvmRuntime
    {
        private static string? _jvmDir;
        private static string? _dataDir;
        private static bool _probed;
        private static string? _missing;

        /// <summary>jvm 目录（含 jre / stubs / libs / d2j）。找不到时为 null。</summary>
        public static string? JvmDir
        {
            get { Probe(); return _jvmDir; }
        }

        /// <summary>可写数据目录。</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
                _dataDir = Path.Combine(root, "TVBoxPC", "spider");
                try { Directory.CreateDirectory(_dataDir); } catch { }
                return _dataDir;
            }
        }

        public static string JarCacheDir => Sub("jar");
        public static string SandboxDir => Sub("sandbox");

        private static string Sub(string name)
        {
            var p = Path.Combine(DataDir, name);
            try { Directory.CreateDirectory(p); } catch { }
            return p;
        }

        /// <summary>是否具备运行 JVM 蜘蛛的全部资产。</summary>
        public static bool Available { get { Probe(); return _missing == null; } }

        /// <summary>不可用原因（用于界面提示）。</summary>
        public static string? MissingReason { get { Probe(); return _missing; } }

        /// <summary>
        /// 依次尝试：程序目录/jvm、程序目录/../jvm、开发树（沿上级目录找 TVBoxPC/jvm）。
        /// 发布版与开发版都能命中，避免"开发时好用、打包后失效"。
        /// </summary>
        private static void Probe()
        {
            if (_probed) return;
            _probed = true;

            var candidates = new List<string>();
            // 显式覆盖（排障/绿色版自定义位置）：设置环境变量 TVBOXPC_JVM_DIR 指向 jvm 目录
            var env = Environment.GetEnvironmentVariable("TVBOXPC_JVM_DIR");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env!);

            var baseDir = AppContext.BaseDirectory;
            candidates.Add(Path.Combine(baseDir, "jvm"));
            candidates.Add(Path.Combine(baseDir, "..", "jvm"));

            // 开发树兜底：从 exe 目录往上找 6 层，找名字叫 jvm 且含 jre/bin 的目录
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                candidates.Add(Path.Combine(dir.FullName, "jvm"));
                dir = dir.Parent;
            }

            foreach (var c in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(c);
                    if (!Directory.Exists(full)) continue;
                    if (!File.Exists(Path.Combine(full, "jre", "bin", "java.exe"))) continue;
                    if (!File.Exists(Path.Combine(full, "stubs", "stubs.jar"))) continue;
                    _jvmDir = full;
                    return;
                }
                catch { }
            }

            _missing = "缺少 JVM 蜘蛛运行时（jvm/ 目录：jre + stubs.jar + libs + d2j）。\n" +
                       "该目录随发行包一起分发；若你用的是源码目录，请确认 TVBoxPC\\jvm 存在。";
        }

        public static string JavaExe => Path.Combine(JvmDir!, "jre", "bin", "java.exe");
        public static string StubsJar => Path.Combine(JvmDir!, "stubs", "stubs.jar");
        public static string LibsDir => Path.Combine(JvmDir!, "libs");
        public static string D2jDir => Path.Combine(JvmDir!, "d2j");

        /// <summary>SpiderRunner + 桩 + 依赖 的 classpath 前缀（蜘蛛包 jar 由调用方追加）。</summary>
        public static string BaseClassPath()
        {
            var parts = new List<string> { StubsJar };
            if (Directory.Exists(LibsDir))
                foreach (var f in Directory.GetFiles(LibsDir, "*.jar").OrderBy(x => x))
                    parts.Add(f);
            return string.Join(";", parts);
        }

        public static string D2jClassPath()
        {
            if (!Directory.Exists(D2jDir)) return "";
            return string.Join(";", Directory.GetFiles(D2jDir, "*.jar").OrderBy(x => x));
        }

        /// <summary>java.exe 是否可执行（找不到 jvm 时返回 false，不抛）。</summary>
        public static bool CanRun
        {
            get
            {
                try { return Available && File.Exists(JavaExe); }
                catch { return false; }
            }
        }

        /// <summary>
        /// 判断配置里下载到的蜘蛛包类型。
        ///   DexInZip —— 标准 CatVod 蜘蛛包：其实是个 zip，里面装着 classes.dex（如 fan.txt）
        ///   Jar      —— 普通 Java jar，可直接上 classpath
        ///   Dex      —— 裸 dex 文件
        /// </summary>
        public static SpiderPackageKind Detect(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var head = new byte[8];
                int n = fs.Read(head, 0, 8);
                if (n < 4) return SpiderPackageKind.Unknown;

                // 'dex\n'
                if (head[0] == 0x64 && head[1] == 0x65 && head[2] == 0x78 && head[3] == 0x0A)
                    return SpiderPackageKind.Dex;

                // 'PK\x03\x04' —— 再看里面是不是有 classes.dex
                if (head[0] == 0x50 && head[1] == 0x4B)
                {
                    fs.Position = 0;
                    using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
                    bool hasDex = zip.Entries.Any(e =>
                        e.FullName.Equals("classes.dex", StringComparison.OrdinalIgnoreCase));
                    return hasDex ? SpiderPackageKind.DexInZip : SpiderPackageKind.Jar;
                }
            }
            catch { }
            return SpiderPackageKind.Unknown;
        }

        /// <summary>
        /// 构造 java 子进程启动信息。
        /// 用 ArgumentList（不用拼接字符串）—— 蜘蛛的 ext 里大量含引号/空格/中文/&amp;，
        /// 手工拼命令行必然踩引号转义坑；ArgumentList 由 .NET 负责转义，空串也能正确传入。
        /// </summary>
        public static ProcessStartInfo NewPsi()
        {
            var psi = new ProcessStartInfo(JavaExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = DataDir
            };
            // 清掉 JAVA_TOOL_OPTIONS：否则 java 会往 stderr 打 "Picked up ..."，
            // 而且用户环境里的 -Xmx/代理等设置会干扰我们对蜘蛛行为的判断。
            psi.Environment["JAVA_TOOL_OPTIONS"] = "";
            return psi;
        }
    }

    public enum SpiderPackageKind
    {
        Unknown,
        Jar,
        DexInZip,
        Dex
    }
}
