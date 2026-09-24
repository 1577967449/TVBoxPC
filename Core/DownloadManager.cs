using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TVBoxPC.Core
{
    /// <summary>下载任务状态。</summary>
    public enum DlState { Queued, Running, Paused, Done, Failed, Canceled }

    /// <summary>新建下载任务时可覆盖的参数（对齐猫抓 m3u8 解析器底部那一排设置）。</summary>
    public class DlOptions
    {
        /// <summary>本任务并发分段数；0 = 用全局设置。</summary>
        public int Concurrency { get; set; }
        /// <summary>分段失败重试次数；-1 = 用默认（2）。</summary>
        public int SegmentRetry { get; set; } = -1;
        /// <summary>只下第 from~to 个分片（1 基，闭区间）；0 = 从第一个 / 到最后一个。</summary>
        public int FromSegment { get; set; }
        public int ToSegment { get; set; }
        /// <summary>只下某时间段（秒）；ToTime &lt;= 0 表示到结尾。给了时间就以时间为准。</summary>
        public double FromTime { get; set; }
        public double ToTime { get; set; }
        /// <summary>手填密钥（32 位十六进制或 base64）与 IV（十六进制）。留空则用 m3u8 里自带的。</summary>
        public string? ManualKey { get; set; }
        public string? ManualIv { get; set; }
        /// <summary>多码率清单里选第几档（按解析出的顺序，0 = 最高画质）；-1 = 自动取最高。</summary>
        public int VariantIndex { get; set; } = -1;
        /// <summary>把原始 m3u8（与密钥）一起存进产物目录 —— 猫抓的「原始 m3u8」能力，便于离线复用与排障。</summary>
        public bool SavePlaylist { get; set; }
        /// <summary>产物格式：auto / .ts / .mp4 / .aac。</summary>
        public string OutputFormat { get; set; } = "auto";
        /// <summary>额外的请求头（覆盖源自带同名头）。</summary>
        public Dictionary<string, string>? ExtraHeaders { get; set; }
    }

    /// <summary>
    /// 下载管理器 —— v1.6 按浏览器插件「猫抓(cat-catch)」的 m3u8 下载器重做。
    ///
    /// 相比 v1.5（只能"从头下一整集，要么成功要么失败"），补齐了这些能力：
    ///   · **任务状态机**：排队 / 下载中 / 已暂停 / 完成 / 失败 / 已取消；
    ///   · **暂停 + 继续**：因为是「分批下载、按序落盘」，文件永远停在分片边界上，
    ///     所以继续时可以直接 append（并记住已落盘的分片数），不必重头再来；
    ///   · **速度 / 剩余时间(ETA)**：按分片完成度估算，比按字节估更稳（分段大小差异很大）；
    ///   · **范围下载**：只下第 N~M 个分片，或只下某分钟到某分钟（超长剧集先下关键段）；
    ///   · **多码率选择**：master playlist 列出全部画质档，用户点哪档下哪档（旧版只看最高）；
    ///   · **手填密钥**：16 进制 / base64 都收，配合 m3u8 里没有密钥的一次性流；
    ///   · **EXT-X-BYTERANGE**：同文件按字节切片的流，按 Range 请求头精确取字节；
    ///   · **原始 m3u8 留档**：可选把 playlist（与密钥）写进产物目录；
    ///   · **单任务重试 / 删除**，**重试次数与并发数可按任务覆盖**；
    ///   · **命令导出**：见 Core/DownloadCommand.cs。
    ///
    /// 保留 v1.5 修掉的五个「必然失败」坑（分段地址拼接、一败全丢、进度恒 0、`|` 头没剥、AES 用 PKCS7）。
    /// </summary>
    public class DownloadManager
    {
        /// <summary>全局默认并发分段数（可被任务级覆盖）。</summary>
        public int Concurrency { get; set; } = 12;
        /// <summary>全局默认分段重试次数。</summary>
        public int SegmentRetry { get; set; } = 2;

        private readonly object _lock = new();
        private readonly List<WorkItem> _queue = new();
        private readonly List<TaskInfo> _tasks = new();
        private bool _running;
        private CancellationTokenSource? _cts;
        private Task? _sampler;

        /// <summary>下载根目录。默认 %USERPROFILE%\Downloads\TVBoxPC。</summary>
        public string RootDir { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TVBoxPC");

        /// <summary>任务列表发生变化时触发（界面用它做即时刷新；轮询刷新仍然保留作为兜底）。</summary>
        public event Action? Changed;

        private void RaiseChanged() { try { Changed?.Invoke(); } catch { } }

        /// <summary>外部引擎路径（存在才用）。</summary>
        public string? EnginePath { get; }
        /// <summary>系统里能找到的 ffmpeg（用于把 ts 重封装成 mp4）。没有则为 null。</summary>
        public string? FfmpegPath { get; }

        public DownloadManager()
        {
            var baseDir = AppContext.BaseDirectory;
            EnginePath =
                FindFile(Path.Combine(baseDir, "engine", "N_m3u8DL-RE.exe")) ??
                FindFile(Path.Combine(baseDir, "engine", "N_m3u8DL-CLI.exe")) ??
                FindFile(Path.Combine(baseDir, "N_m3u8DL-RE.exe")) ??
                FindFile(Path.Combine(baseDir, "N_m3u8DL-CLI.exe"));
            FfmpegPath =
                FindFile(Path.Combine(baseDir, "engine", "ffmpeg.exe")) ??
                SearchPath("ffmpeg.exe");
        }

        private static string? FindFile(string p) => File.Exists(p) ? p : null;

        /// <summary>在 PATH 里找可执行文件（不依赖 shell，避免弹黑框）。</summary>
        private static string? SearchPath(string exe)
        {
            try
            {
                var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
                foreach (var d in paths)
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    var full = Path.Combine(d.Trim(), exe);
                    if (File.Exists(full)) return full;
                }
            }
            catch { }
            return null;
        }

        // ===================== 对外属性（界面用） =====================
        public IReadOnlyList<TaskInfo> Tasks { get { lock (_lock) return _tasks.ToList(); } }
        public bool IsBusy { get { lock (_lock) return _running; } }
        public int Total { get { lock (_lock) return _tasks.Count; } }
        public int DoneCount { get { lock (_lock) return _tasks.Count(t => t.State == DlState.Done); } }
        public string Current { get { lock (_lock) return _tasks.FirstOrDefault(t => t.State == DlState.Running)?.Name ?? ""; } }
        public int Percent { get { lock (_lock) return _tasks.FirstOrDefault(t => t.State == DlState.Running)?.Percent ?? 0; } }
        public bool HasFinished { get { lock (_lock) return _tasks.Any(t => t.State is DlState.Done or DlState.Failed); } }

        // ===================== 入队 =====================

        /// <summary>
        /// 提交一批下载。返回真正加入队列的条数。
        /// 同一目录下同名且**未完成**的任务会被跳过 —— 否则连点两次「下载整部」会排两遍。
        /// </summary>
        public int Enqueue(string folder, IEnumerable<(string name, string url)> items, DlOptions? opt = null)
        {
            int added = 0;
            lock (_lock)
            {
                foreach (var (name, url) in items)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var sname = Sanitize(name);
                    if (_tasks.Any(t => t.State != DlState.Done && t.State != DlState.Failed
                                        && t.Folder == folder && t.Name == sname)) continue;
                    var task = new TaskInfo { Name = sname, Folder = folder, Url = url, Options = opt ?? new DlOptions() };
                    _tasks.Add(task);
                    _queue.Add(new WorkItem { Task = task, Url = url });
                    added++;
                }
                if (added > 0) EnsureRunner();
            }
            RaiseChanged();
            return added;
        }

        /// <summary>手动新建一个下载任务（猫抓那种"把 m3u8 地址粘进来就能下"）。</summary>
        public TaskInfo AddManual(string name, string url, DlOptions opt, string folder = "手动下载")
        {
            var task = new TaskInfo
            {
                Name = Sanitize(name),
                Folder = Sanitize(folder),
                Url = url,
                Options = opt,
            };
            lock (_lock)
            {
                _tasks.Add(task);
                _queue.Add(new WorkItem { Task = task, Url = url });
                EnsureRunner();
            }
            RaiseChanged();
            return task;
        }

        /// <summary>必须在持锁状态下调用。</summary>
        private void EnsureRunner()
        {
            if (_running) return;
            if (_queue.Count == 0) return;
            _running = true;
            _cts ??= new CancellationTokenSource();
            _ = Task.Run(RunLoop);
            if (_sampler == null || _sampler.IsCompleted)
                _sampler = Task.Run(SampleLoop);
        }

        // ===================== 任务控制 =====================
        public void Pause(TaskInfo t)
        {
            lock (_lock)
            {
                if (t.State is DlState.Done or DlState.Failed or DlState.Paused) return;
                t.PauseRequested = true;
                t.State = DlState.Paused;
                // 取消它自己的 token，让正在跑的批立刻退出；已落盘的分片保留，继续时接着下
                try { t.Cts?.Cancel(); } catch { }
            }
            RaiseChanged();
        }

        public void Resume(TaskInfo t)
        {
            lock (_lock)
            {
                if (t.State != DlState.Paused) return;
                t.PauseRequested = false;
                t.State = DlState.Queued;
                t.Error = null;
                t.Cts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
                if (!_queue.Any(w => w.Task == t)) _queue.Add(new WorkItem { Task = t, Url = t.Url });
                EnsureRunner();
            }
            RaiseChanged();
        }

        /// <summary>失败/取消的任务重来一次。</summary>
        public void Retry(TaskInfo t)
        {
            lock (_lock)
            {
                if (t.State is DlState.Done or DlState.Running or DlState.Queued) return;
                t.State = DlState.Queued;
                t.Error = null;
                t.Warning = null;
                t.PauseRequested = false;
                t.Percent = 0;
                t.DoneSegments = 0;
                t.Bytes = 0;
                t.ResumeFromSegment = 0;
                t.ResumeFromBytes = 0;
                t.Cts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
                if (!_queue.Any(w => w.Task == t)) _queue.Add(new WorkItem { Task = t, Url = t.Url });
                EnsureRunner();
            }
            RaiseChanged();
        }

        /// <summary>从列表里删掉一个任务（进行中的先取消）。</summary>
        public void Remove(TaskInfo t)
        {
            lock (_lock)
            {
                try { t.Cts?.Cancel(); } catch { }
                t.State = DlState.Canceled;
                _queue.RemoveAll(w => w.Task == t);
                _tasks.Remove(t);
            }
            RaiseChanged();
        }

        /// <summary>取消所有未开始/进行中的任务。</summary>
        public void CancelAll()
        {
            lock (_lock)
            {
                foreach (var w in _queue) w.Task.State = DlState.Canceled;
                _queue.Clear();
                foreach (var t in _tasks.Where(t => t.State is DlState.Running or DlState.Queued))
                {
                    t.State = DlState.Canceled;
                    try { t.Cts?.Cancel(); } catch { }
                }
                try { _cts?.Cancel(); } catch { }
                _cts = null;
                _running = false;
            }
            RaiseChanged();
        }

        /// <summary>清掉已结束（完成/失败/取消）的任务行，保留正在跑的和暂停的。</summary>
        public int ClearFinished()
        {
            int n;
            lock (_lock)
            {
                n = _tasks.RemoveAll(t => t.State is DlState.Done or DlState.Failed or DlState.Canceled);
                _queue.RemoveAll(w => !_tasks.Contains(w.Task));
            }
            RaiseChanged();
            return n;
        }

        // ===================== 主循环 =====================
        private async Task RunLoop()
        {
            while (true)
            {
                WorkItem? item;
                lock (_lock)
                {
                    // 只取「排队中」的：暂停的留在列表里，等用户点继续（继续时会重新入队）
                    item = _queue.FirstOrDefault(w => w.Task.State == DlState.Queued);
                    if (item != null) _queue.Remove(item);
                    if (item == null || (_cts?.IsCancellationRequested ?? false))
                    {
                        _running = false;
                        break;
                    }
                }

                item.Task.State = DlState.Running;
                item.Task.StartedAt = DateTime.Now;
                item.Task.Cts ??= CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
                RaiseChanged();

                try
                {
                    await DownloadOne(item.Task);
                    if (item.Task.State == DlState.Paused)
                    {
                        // 被暂停：保留进度，啥也不标记
                    }
                    else
                    {
                        item.Task.State = DlState.Done;
                        item.Task.Percent = 100;
                        item.Task.FinishedAt = DateTime.Now;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (item.Task.State != DlState.Paused) item.Task.State = DlState.Canceled;
                }
                catch (Exception ex)
                {
                    item.Task.Error = Friendly(ex);
                    item.Task.State = DlState.Failed;
                    item.Task.FinishedAt = DateTime.Now;
                }
                item.Task.SpeedBps = 0;
                RaiseChanged();
            }
        }

        /// <summary>每秒采一次速度，供界面显示「速度 / 剩余时间」。</summary>
        private async Task SampleLoop()
        {
            while (true)
            {
                await Task.Delay(1000);
                List<TaskInfo> running;
                lock (_lock)
                {
                    if (!_running && _tasks.All(t => t.State != DlState.Running)) break;
                    running = _tasks.Where(t => t.State == DlState.Running).ToList();
                }
                foreach (var t in running) t.SampleSpeed(DateTime.Now);
                if (running.Count > 0) RaiseChanged();
            }
        }

        private static string Friendly(Exception ex) => ex switch
        {
            // 注意顺序：TaskCanceledException 派生自 OperationCanceledException（超时走前者）
            TaskCanceledException => "请求超时（30 秒）。这个源可能很慢或已失效。",
            OperationCanceledException => "已取消",
            HttpRequestException hre when hre.StatusCode != null =>
                $"服务器返回 {hre.StatusCode}（{(int)hre.StatusCode}）——地址可能已失效，或该资源需要 Referer / 防盗链校验。",
            _ => ex.Message
        };

        // ===================== 单个任务 =====================
        private async Task DownloadOne(TaskInfo task)
        {
            var ct = task.Cts?.Token ?? CancellationToken.None;
            var target = MediaUrl.Parse(task.Url);
            if (target.Kind == MediaKind.Unsupported)
                throw new Exception(target.Note ?? "这个协议无法下载。");

            var url = target.Url;
            // 源里带的头 + 用户手填的头（后者覆盖前者）
            var headers = new Dictionary<string, string>(target.Headers, StringComparer.OrdinalIgnoreCase);
            if (task.Options.ExtraHeaders != null)
                foreach (var kv in task.Options.ExtraHeaders)
                    if (!kv.Key.StartsWith("__")) headers[kv.Key] = kv.Value;

            task.CleanUrl = url;
            task.Headers = headers;

            var outDir = Path.Combine(RootDir, Sanitize(task.Folder));
            Directory.CreateDirectory(outDir);

            if (!string.IsNullOrEmpty(EnginePath) && !HlsParser.LooksLikeM3u8(url))
            {
                await RunExternalEngine(url, task, outDir, headers, ct);
                return;
            }
            if (!string.IsNullOrEmpty(EnginePath) && HlsParser.LooksLikeM3u8(url) && task.Options.FromSegment == 0
                && task.Options.ToTime <= 0 && string.IsNullOrEmpty(task.Options.ManualKey))
            {
                // 外部引擎在"整集下载"这条主路径上更稳（多线程/重封装都由它做），
                // 但只要用户用到了范围/手填密钥这类猫抓式细粒度设置，就走内置（引擎命令行不覆盖这些）
                await RunExternalEngine(url, task, outDir, headers, ct);
                return;
            }

            if (!HlsParser.LooksLikeM3u8(url))
            {
                var ext = Path.GetExtension(url.Split('?')[0]);
                if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".mp4";
                var outPath = Path.Combine(outDir, task.Name + ext);
                await DownloadPlainFile(url, outPath, headers, task, ct);
                return;
            }

            await DownloadHls(url, task, outDir, headers, ct);
        }

        // ===================== 外部引擎：N_m3u8DL =====================
        private async Task RunExternalEngine(string url, TaskInfo task, string outDir,
            Dictionary<string, string> headers, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = EnginePath!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-u"); psi.ArgumentList.Add(url);
            psi.ArgumentList.Add("--save-name"); psi.ArgumentList.Add(task.Name);
            psi.ArgumentList.Add("--save-dir"); psi.ArgumentList.Add(outDir);
            foreach (var kv in headers)
            {
                if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                {
                    psi.ArgumentList.Add("--header");
                    psi.ArgumentList.Add($"{kv.Key}: {kv.Value}");
                }
            }
            psi.ArgumentList.Add("--auto-exit");

            using var proc = Process.Start(psi) ?? throw new Exception("无法启动下载引擎");
            var buf = new char[1024];
            var tail = new StringBuilder();
            while (!proc.StandardOutput.EndOfStream)
            {
                int n = await proc.StandardOutput.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n <= 0) break;
                var s = new string(buf, 0, n);
                tail.Append(s);
                if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
                var p = ParseEnginePercent(s, task.Percent);
                if (p != task.Percent) { task.Percent = p; }
            }
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new Exception($"下载引擎返回错误码 {proc.ExitCode}：{Condense(tail.ToString(), 200)}");
            task.OutputPath = outDir;
        }

        private static int ParseEnginePercent(string line, int fallback)
        {
            int idx = line.LastIndexOf('%');
            if (idx > 0)
            {
                int s = idx - 1;
                while (s >= 0 && (char.IsDigit(line[s]) || line[s] == '.')) s--;
                var num = line.Substring(s + 1, idx - s - 1);
                if (double.TryParse(num, out var v)) return (int)Math.Clamp(v, 0, 100);
            }
            return fallback;
        }

        // ===================== 普通文件直下（支持暂停后续传） =====================
        private async Task DownloadPlainFile(string url, string outPath, Dictionary<string, string> headers,
            TaskInfo task, CancellationToken ct)
        {
            long already = 0;
            if (File.Exists(outPath) && task.ResumeFromBytes > 0)
            {
                already = new FileInfo(outPath).Length;
                if (already < task.ResumeFromBytes) already = 0;   // 文件被外部改过，别赌
            }

            var req = MakeRequest(url, headers);
            if (already > 0) req.Headers.TryAddWithoutValidation("Range", $"bytes={already}-");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                var reason = resp.ReasonPhrase;
                resp.Dispose();
                throw new HttpRequestException($"请求失败：{code} {reason}", null, (HttpStatusCode)code);
            }
            bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed) already = 0;

            var total = (resp.Content.Headers.ContentLength ?? 0) + already;
            task.TotalBytes = total;

            await using var inStream = await resp.Content.ReadAsStreamAsync(ct);
            await using var outFs = new FileStream(outPath, already > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read, 1 << 16, true);
            var buf = new byte[81920];
            long read = already;
            int n;
            while ((n = await inStream.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
            {
                await outFs.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                task.Bytes = read;
                task.ResumeFromBytes = read;
                if (total > 0) task.Percent = (int)Math.Clamp(read * 100 / total, 0, 99);
            }
            await outFs.FlushAsync(ct);
            task.OutputPath = outPath;
            task.Bytes = read;
            task.TotalBytes = read;
        }

        // ===================== HLS =====================
        private async Task DownloadHls(string url, TaskInfo task, string outDir,
            Dictionary<string, string> headers, CancellationToken ct)
        {
            var notes = new List<string>();          // 累积给用户看的提示，最后一次性写进 Warning
            var (text, finalUrl) = await FetchText(url, headers, ct);

            // master playlist：取用户挑的那档（默认最高带宽）
            for (int hop = 0; hop < 3 && text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase); hop++)
            {
                var info0 = HlsParser.Parse(text, finalUrl);
                var variant = PickVariant(info0, task.Options.VariantIndex);
                if (variant == null) break;
                task.VariantLabel = variant.Label;
                var vurl = variant.Url;
                var got = await FetchText(vurl, headers, ct);
                text = got.text;
                finalUrl = got.url;
            }

            var info = HlsParser.Parse(text, finalUrl);
            if (info.Segments.Count == 0)
                throw new Exception("这个 m3u8 里没有可下载的分段（可能是直播流或空列表）。");

            // 手填密钥：盖到所有分片上（m3u8 里没有密钥或密钥过期时用）
            var manualKey = HlsParser.ParseManualKey(task.Options.ManualKey ?? "");
            if (manualKey != null)
            {
                var iv = HlsParser.ParseHex(task.Options.ManualIv ?? "") ?? HlsParser.ParseHex("0x0");
                if (iv == null || iv.Length != 16) iv = new byte[16];
                foreach (var s in info.Segments)
                    s.Key = new HlsParser.KeySpec { Method = HlsParser.KeyMethod.Aes128, ManualKey = manualKey, Iv = iv };
                notes.Add("已使用你手填的密钥解密。");
            }

            // 范围选择：给了时间就以时间为准，否则按分片序号
            var segs = info.Segments;
            if (task.Options.ToTime > 0 || task.Options.FromTime > 0)
            {
                segs = HlsParser.SliceByTime(segs, task.Options.FromTime, task.Options.ToTime);
                notes.Add($"只下载 {HlsParser.FormatDuration(task.Options.FromTime)} ~ " +
                          $"{HlsParser.FormatDuration(task.Options.ToTime)} 这一段（{segs.Count} 个分片）。");
            }
            else if (task.Options.FromSegment > 0 || task.Options.ToSegment > 0)
            {
                segs = HlsParser.Slice(segs, task.Options.FromSegment, task.Options.ToSegment);
                notes.Add($"只下载第 {task.Options.FromSegment} ~ {task.Options.ToSegment} 个分片（共 {segs.Count} 个）。");
            }
            if (segs.Count == 0) throw new Exception("选定的分片范围里没有分片，检查一下起始/结束值。");

            // 产物扩展名
            var ext = task.Options.OutputFormat switch
            {
                ".ts" or ".mp4" or ".aac" => task.Options.OutputFormat,
                _ => info.Ext,
            };
            if (task.Options.OutputFormat == ".mp4" && info.Ext == ".ts")
                ext = ".ts";     // 真转封装要 ffmpeg，放在下完之后的 remux 步骤里做（见下）
            task.TotalSegments = segs.Count;

            var outPath = Path.Combine(outDir, task.Name + ext);
            task.OutputPath = outPath;

            // 暂停后继续：文件里已经有前 N 个分片了（按序落盘，天然停在分片边界）
            bool resume = task.ResumeFromSegment > 0 && task.ResumeFromSegment < segs.Count
                          && File.Exists(outPath) && new FileInfo(outPath).Length > 0;
            int startIdx = resume ? task.ResumeFromSegment : 0;
            long written = resume ? new FileInfo(outPath).Length : 0;

            await using var outFs = new FileStream(outPath, resume ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read, 1 << 16, true);

            // fMP4 的初始化段必须写在最前面（续传时已经在文件里了，别重复写）
            if (!resume && !string.IsNullOrEmpty(info.MapUri))
            {
                var init = await FetchBytes(info.MapUri!, headers, ct, null);
                if (init != null && init.Length > 0) await outFs.WriteAsync(init, ct);
            }

            // 原始 m3u8（与密钥）留档：猫抓的「原始 m3u8」，离线复用 / 排障都靠它
            if (task.Options.SavePlaylist)
            {
                try
                {
                    var m3u = Path.Combine(outDir, task.Name + ".m3u8");
                    await File.WriteAllTextAsync(m3u, info.RawText, Encoding.UTF8, ct);
                    var keyUri = info.Segments.FirstOrDefault(s => s.Key?.Uri != null)?.Key?.Uri;
                    if (keyUri != null)
                    {
                        var kb = await FetchBytes(keyUri, headers, ct, null);
                        if (kb != null && kb.Length > 0)
                            await File.WriteAllBytesAsync(Path.Combine(outDir, task.Name + ".key"), kb, ct);
                    }
                    task.SidecarPath = m3u;
                }
                catch { /* 留档失败不影响下载 */ }
            }

            int skipped = 0;
            int batchSize = Math.Clamp(task.Options.Concurrency > 0 ? task.Options.Concurrency : Concurrency, 1, 32);
            int retry = task.Options.SegmentRetry >= 0 ? task.Options.SegmentRetry : SegmentRetry;

            for (int start = startIdx; start < segs.Count; start += batchSize)
            {
                // 暂停检查点：只在这里停 —— 文件永远完整地停在分片边界上
                if (task.PauseRequested)
                {
                    task.ResumeFromSegment = start;
                    task.DoneSegments = start;
                    task.Bytes = written;
                    await outFs.FlushAsync(CancellationToken.None);
                    return;
                }
                ct.ThrowIfCancellationRequested();

                int count = Math.Min(batchSize, segs.Count - start);
                var batch = new Task<byte[]?>[count];
                for (int i = 0; i < count; i++)
                    batch[i] = FetchSegmentWithRetry(segs[start + i], headers, ct, retry);
                var data = await Task.WhenAll(batch);

                for (int i = 0; i < count; i++)
                {
                    if (data[i] == null || data[i]!.Length == 0)
                    {
                        // 单段失败不毁掉整集（老代码就是死在这里：9 段广告跳转 404 → 整集丢弃）
                        skipped++;
                        continue;
                    }
                    await outFs.WriteAsync(data[i]!, ct);
                    written += data[i]!.Length;
                }

                int doneNow = start + count;
                task.DoneSegments = doneNow;
                task.ResumeFromSegment = doneNow;
                task.Bytes = written;
                task.Percent = (int)Math.Clamp((long)doneNow * 100 / segs.Count, 0, 99);
            }

            await outFs.FlushAsync(ct);
            task.OutputPath = outPath;
            task.Bytes = written;
            task.SkippedSegments = skipped;

            if (written == 0)
                throw new Exception($"所有 {segs.Count} 个分段都下载失败。可能是地址已失效、或需要 Referer（该地址没带）。");

            if (skipped > 0)
                notes.Add($"有 {skipped}/{segs.Count} 个分段没下到（多为源里的广告跳转段），已跳过。文件可能有个别断点。");

            task.Warning = notes.Count > 0 ? string.Join(" ", notes) : null;

            // 用户要 mp4、而分片是 ts：有 ffmpeg 就重封装（不重编码，很快）
            if (task.Options.OutputFormat == ".mp4" && ext == ".ts")
                await TryRemuxToMp4(task, outPath, notes, ct);
        }

        /// <summary>ts → mp4（-c copy 重封装）。没装 ffmpeg 就把话说清楚，别让用户以为是程序坏了。</summary>
        private async Task TryRemuxToMp4(TaskInfo task, string tsPath, List<string> notes, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(FfmpegPath))
            {
                notes.Add("已下载为 .ts。你要的是 mp4，但本机没找到 ffmpeg —— " +
                          "装一个 ffmpeg（放在程序 engine 目录或 PATH 里）再重试，或用任务上的「复制命令」交给外部工具。");
                task.Warning = string.Join(" ", notes);
                return;
            }
            try
            {
                var mp4 = Path.ChangeExtension(tsPath, ".mp4");
                var psi = new ProcessStartInfo
                {
                    FileName = FfmpegPath!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(tsPath);
                psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
                psi.ArgumentList.Add("-bsf:a"); psi.ArgumentList.Add("aac_adtstoasc");
                psi.ArgumentList.Add("-y"); psi.ArgumentList.Add(mp4);

                using var p = Process.Start(psi);
                if (p == null) { notes.Add("启动 ffmpeg 失败，已保留 .ts。"); return; }
                var errTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(ct);
                var err = await errTask;
                if (p.ExitCode == 0 && File.Exists(mp4))
                {
                    task.OutputPath = mp4;
                    notes.Add("已用 ffmpeg 重封装为 mp4（-c copy，未重编码）。");
                }
                else notes.Add("ffmpeg 重封装失败，已保留 .ts：" + Condense(err, 160));
            }
            catch (Exception ex)
            {
                notes.Add("ffmpeg 重封装出错，已保留 .ts：" + ex.Message);
            }
            finally
            {
                task.Warning = string.Join(" ", notes);
            }
        }

        private static HlsParser.Variant? PickVariant(HlsParser.Info info, int index)
        {
            if (info.Variants.Count == 0) return null;
            if (index >= 0 && index < info.Variants.Count) return info.Variants[index];
            return info.Variants[0];      // 已按带宽降序，[0] 就是最高画质
        }

        private async Task<byte[]?> FetchSegmentWithRetry(HlsParser.Segment seg,
            Dictionary<string, string> headers, CancellationToken ct, int retry)
        {
            for (int attempt = 0; attempt <= retry; attempt++)
            {
                try
                {
                    var raw = await FetchBytes(seg.Url, headers, ct, seg.RangeHeader);
                    if (raw == null || raw.Length == 0) { await Task.Delay(120, ct); continue; }
                    if (seg.Key == null || seg.Key.Method != HlsParser.KeyMethod.Aes128) return raw;
                    return DecryptAes128(raw, seg.Key, seg.Seq);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    if (attempt == retry) return null;
                    try { await Task.Delay(250 * (attempt + 1), ct); } catch { return null; }
                }
            }
            return null;
        }

        // ===================== HTTP =====================
        // 懒创建：必须在 App.OnStartup 把 HttpFactory.UseSystemProxy 设好之后再建，
        // 否则取到的是「默认值」而不是用户的设置。
        private static readonly Lazy<HttpClient> _http = new(() => new HttpClient(new HttpClientHandler
        {
            UseProxy = HttpFactory.UseSystemProxy,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 16,
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        });
        private static HttpClient Http => _http.Value;

        private static HttpRequestMessage MakeRequest(string url, Dictionary<string, string>? headers)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.DefaultUserAgent);
            if (headers != null)
                foreach (var kv in headers)
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            return req;
        }

        private static async Task<HttpResponseMessage> SendAsync(string url, Dictionary<string, string>? headers,
            CancellationToken ct, HttpCompletionOption opt = HttpCompletionOption.ResponseContentRead,
            string? rangeHeader = null)
        {
            var req = MakeRequest(url, headers);
            if (!string.IsNullOrEmpty(rangeHeader))
                req.Headers.TryAddWithoutValidation("Range", rangeHeader);
            var resp = await Http.SendAsync(req, opt, ct);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                var code = (int)resp.StatusCode;
                var reason = resp.ReasonPhrase;
                resp.Dispose();
                throw new HttpRequestException($"请求失败：{code} {reason}", null, (HttpStatusCode)code);
            }
            return resp;
        }

        private static async Task<byte[]?> FetchBytes(string url, Dictionary<string, string>? headers,
            CancellationToken ct, string? rangeHeader = null)
        {
            using var resp = await SendAsync(url, headers, ct, HttpCompletionOption.ResponseContentRead, rangeHeader);
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }

        private static async Task<(string text, string url)> FetchText(string url,
            Dictionary<string, string>? headers, CancellationToken ct)
        {
            using var resp = await SendAsync(url, headers, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var text = HttpFactory.DecodeText(bytes, null, resp.Content.Headers.ContentType?.ToString());
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            return (text, finalUrl);
        }

        // ===================== 地址预览（猫抓的「解析」按钮） =====================

        /// <summary>解析结果摘要，给「解析地址」按钮显示：总时长、分片数、画质列表、是否加密。</summary>
        public class Preview
        {
            public bool Ok { get; set; }
            public string Message { get; set; } = "";
            public bool IsMaster { get; set; }
            public int SegmentCount { get; set; }
            public double Duration { get; set; }
            public bool Encrypted { get; set; }
            public bool HasByteRange { get; set; }
            public string Ext { get; set; } = ".ts";
            public List<HlsParser.Variant> Variants { get; set; } = new();
            public string? FinalUrl { get; set; }
            /// <summary>解析用的最终 media playlist 地址（多码率时会自动跟进第一档）。</summary>
            public string? MediaUrl { get; set; }
            public Dictionary<string, string> Headers { get; set; } = new();
        }

        public async Task<Preview> PreviewAsync(string rawUrl, Dictionary<string, string>? extraHeaders = null,
            int variantIndex = -1, CancellationToken ct = default)
        {
            var p = new Preview();
            try
            {
                var target = MediaUrl.Parse(rawUrl);
                if (target.Kind == MediaKind.Unsupported)
                {
                    p.Message = target.Note ?? "这个协议无法下载。";
                    return p;
                }
                var headers = new Dictionary<string, string>(target.Headers, StringComparer.OrdinalIgnoreCase);
                if (extraHeaders != null)
                    foreach (var kv in extraHeaders)
                        if (!kv.Key.StartsWith("__")) headers[kv.Key] = kv.Value;

                var (text, finalUrl) = await FetchText(target.Url, headers, ct);
                var info = HlsParser.Parse(text, finalUrl);

                p.FinalUrl = finalUrl;
                p.Headers = headers;
                if (info.IsMaster)
                {
                    p.IsMaster = true;
                    p.Variants = info.Variants;
                    var v = PickVariant(info, variantIndex);
                    if (v == null) { p.Message = "多码率清单里没有可用的画质档。"; return p; }
                    var got = await FetchText(v.Url, headers, ct);
                    info = HlsParser.Parse(got.text, got.url);
                    p.MediaUrl = got.url;
                    p.Message = $"多码率清单：{p.Variants.Count} 个画质档，已选「{v.Label}」。";
                }
                else
                {
                    p.MediaUrl = finalUrl;
                    p.Message = "";
                }

                p.SegmentCount = info.Segments.Count;
                p.Duration = info.TotalDuration;
                p.Encrypted = info.Encrypted;
                p.HasByteRange = info.HasByteRange;
                p.Ext = info.Ext;
                p.Ok = info.Segments.Count > 0;
                if (!p.Ok) p.Message += "这份清单里没有分片（可能是直播或空列表）。";
                return p;
            }
            catch (Exception ex)
            {
                p.Message = "解析失败：" + Friendly(ex);
                return p;
            }
        }

        // ===================== AES-128 =====================
        private static byte[] DecryptAes128(byte[] data, HlsParser.KeySpec key, int seq)
        {
            byte[]? keyBytes = key.ManualKey;
            if (keyBytes == null && !string.IsNullOrEmpty(key.Uri))
            {
                keyBytes = _keyCache.TryGetValue(key.Uri!, out var cached) ? cached : null;
                if (keyBytes == null)
                {
                    keyBytes = FetchBytes(key.Uri!, null, CancellationToken.None).GetAwaiter().GetResult();
                    if (keyBytes == null || keyBytes.Length == 0)
                        throw new Exception("拿不到解密密钥：" + key.Uri);
                    _keyCache[key.Uri!] = keyBytes;
                }
            }
            if (keyBytes == null) return data;

            var iv = key.Iv ?? BuildIv(seq);

            using var aes = Aes.Create();
            aes.Key = keyBytes;
            aes.IV = iv.Length == 16 ? iv : BuildIv(seq);
            aes.Mode = CipherMode.CBC;
            // ★ HLS 的 AES-128-CBC 是整块加密、没有 padding。
            // 用 PKCS7 会在数据长度正好为 16 倍数时按最后一个字节当 padding 长度剥掉，导致流损坏。
            aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();

            int whole = data.Length - (data.Length % 16);
            if (whole == 0) return data;
            var outp = dec.TransformFinalBlock(data, 0, whole);
            if (whole == data.Length) return outp;
            var merged = new byte[outp.Length + (data.Length - whole)];
            Buffer.BlockCopy(outp, 0, merged, 0, outp.Length);
            Buffer.BlockCopy(data, whole, merged, outp.Length, data.Length - whole);
            return merged;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _keyCache = new();

        private static byte[] BuildIv(int seq)
        {
            var iv = new byte[16];
            for (int i = 0; i < 8; i++) iv[15 - i] = (byte)((long)seq >> (8 * i));
            return iv;
        }

        // ===================== 工具 =====================
        internal static string Sanitize(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in s ?? "") sb.Append(invalid.Contains(c) ? '_' : c);
            var r = sb.ToString().Trim().TrimEnd('.', ' ');
            if (r.Length > 120) r = r[..120];
            return r.Length == 0 ? "未命名" : r;
        }

        private static string Condense(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s[..max] + "…";
        }

        // ===================== 内部类型 =====================
        public class TaskInfo
        {
            public string Name { get; set; } = "";
            public string Folder { get; set; } = "";
            /// <summary>原始地址（可能含 |头参数）。</summary>
            public string Url { get; set; } = "";
            /// <summary>剥掉头参数后的干净地址。</summary>
            public string CleanUrl { get; set; } = "";
            /// <summary>实际请求头（源自带 + 用户手填）。</summary>
            public Dictionary<string, string> Headers { get; set; } = new();
            public DlOptions Options { get; set; } = new();

            public DlState State { get; set; } = DlState.Queued;
            public int Percent { get; set; }
            public string? Error { get; set; }
            /// <summary>非致命提示（例如「跳过了 9 个分段」「已用 ffmpeg 重封装」）。</summary>
            public string? Warning { get; set; }

            public int TotalSegments { get; set; }
            public int DoneSegments { get; set; }
            public int SkippedSegments { get; set; }
            /// <summary>暂停时记下的续传点（已落盘的分片数 / 字节数）。</summary>
            public int ResumeFromSegment { get; set; }
            public long ResumeFromBytes { get; set; }

            public long Bytes { get; set; }
            public long TotalBytes { get; set; }
            /// <summary>产物路径（文件或目录）。</summary>
            public string? OutputPath { get; set; }
            /// <summary>保存到产物目录的原始 m3u8 路径。</summary>
            public string? SidecarPath { get; set; }
            /// <summary>多码率清单里实际选中的那一档（解析后填）。</summary>
            public string? VariantLabel { get; set; }

            public DateTime StartedAt { get; set; }
            public DateTime FinishedAt { get; set; }
            public bool PauseRequested { get; set; }

            // ---- 速度 / ETA ----
            public double SpeedBps { get; internal set; }
            private long _lastBytes;
            private DateTime _lastSample = DateTime.MinValue;

            /// <summary>每秒采一次速度（滑动，取瞬时差；界面显示足够准）。</summary>
            internal void SampleSpeed(DateTime now)
            {
                if (_lastSample == DateTime.MinValue)
                {
                    _lastSample = now; _lastBytes = Bytes; return;
                }
                var dt = (now - _lastSample).TotalSeconds;
                if (dt <= 0) return;
                var inst = (Bytes - _lastBytes) / dt;
                // 平滑一下，避免数字乱跳
                SpeedBps = SpeedBps <= 0 ? inst : SpeedBps * 0.6 + inst * 0.4;
                _lastBytes = Bytes; _lastSample = now;
            }

            /// <summary>剩余时间（秒）。按「已完成分片数」估 —— 分段大小差异大，按字节估会乱跳。</summary>
            public double EtaSeconds
            {
                get
                {
                    if (State != DlState.Running) return 0;
                    var elapsed = (DateTime.Now - StartedAt).TotalSeconds;
                    if (TotalSegments > 0)
                    {
                        // 续传时已下的分片要算进来
                        int doneTotal = DoneSegments;
                        if (doneTotal <= 0 || elapsed <= 0) return 0;
                        var per = elapsed / Math.Max(1, doneTotal - 0);
                        return Math.Max(0, per * (TotalSegments - doneTotal));
                    }
                    if (SpeedBps > 1 && TotalBytes > 0) return Math.Max(0, (TotalBytes - Bytes) / SpeedBps);
                    return 0;
                }
            }

            /// <summary>界面上的一行状态文字。</summary>
            public string StateText => State switch
            {
                DlState.Queued => "排队中",
                DlState.Running => $"{Percent}%",
                DlState.Paused => "已暂停",
                DlState.Done => "完成",
                DlState.Failed => "失败",
                _ => "已取消",
            };

            internal CancellationTokenSource? Cts;
        }

        private class WorkItem { public TaskInfo Task = new(); public string Url = ""; }
    }
}
