using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using TVBoxPC.Core;

namespace TVBoxPC
{
    public partial class MainWindow : Window
    {
        private readonly ConfigLoader _cfg = new();
        private readonly DownloadManager _dl = new();
        private readonly AppSettings _settings = AppSettings.Load();
        private readonly LiveParser _live = new();
        private readonly HistoryStore _store = new();
        private LibVLC? _libvlc;
        /// <summary>内置播放器实例：**只创建一次并反复复用**。
        /// 旧写法每次播放都 new 一个再把 videoView.MediaPlayer 换掉，旧实例从不释放，
        /// 既泄漏又把 VideoView 的 HwndHost 反复重新挂接，是黑屏的常见来源。</summary>
        private LibVLCSharp.Shared.MediaPlayer? _mp;
        private string? _libvlcError;
        private MediaTarget? _currentTarget;
        private CancellationTokenSource? _aggCts;

        // 站点
        private JsonNode? _config;
        private List<SiteEntry> _sites = new();
        private readonly ConcurrentDictionary<string, ISiteClient> _clients = new();
        private ISiteClient? _client;
        private SiteEntry? _site;
        private string? _currentCatId;
        private readonly List<Button> _catButtons = new();
        private bool _suppressSiteEvent;

        // 浏览状态
        private int _homePage = 1;
        private string _browseMode = "list";   // list | search
        private string _searchKw = "";

        // 直播
        private List<LiveSource> _lives = new();
        private List<LiveParser.LiveChannel> _channels = new();

        // 详情
        private VodDetail? _detail;
        private string _detailSiteKey = "";
        private string _detailSiteName = "";
        private List<PlayLine> _lines = new();
        private int _currentLine;
        private List<Episode> _episodes = new();

        // 播放
        private string? _currentPlayUrl;
        private readonly System.Windows.Threading.DispatcherTimer _poll = new();
        /// <summary>当前在播的是第几集（_episodes 的下标），-1 = 还没播过。用于高亮选集、切源后定位同一集。</summary>
        private int _playingEpIndex = -1;
        /// <summary>起播时刻。用来分辨「刚点下去就结束」（其实没拿到流）和「真的播完了」。</summary>
        private DateTime _playStartAt = DateTime.MinValue;
        private LibVLCSharp.Shared.Media? _curMedia;
        /// <summary>播放页选集：是否倒序、当前第几页。</summary>
        private bool _epReverse;
        private int _pvPage;
        private const int PvPageSize = 60;
        /// <summary>全屏相关：进入前的窗口状态，退出时原样还原。</summary>
        private bool _isFull;
        private WindowStyle _prevStyle;
        private WindowState _prevState;
        private ResizeMode _prevResize;
        private GridLength _prevEpRowHeight;
        private GridLength _prevDlWidth;

        // 内置默认接口（首次启动依次尝试）
        private static readonly string[] DefaultConfigs =
        {
            "https://gh-proxy.com/https://raw.githubusercontent.com/gaotianliuyun/gao/master/0821.json",
            "https://raw.githubusercontent.com/gaotianliuyun/gao/master/0821.json"
        };

        private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0x1d, 0x1d, 0x24));
        private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0x2c, 0x2c, 0x34));
        private static readonly Brush IdleBg = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30));
        /// <summary>选集被勾选时的高亮底色。</summary>
        private static readonly Brush SelBg = new SolidColorBrush(Color.FromRgb(0x3d, 0x2a, 0x1e));

        public MainWindow()
        {
            InitializeComponent();
            InitLibVlc();
            ApplyDownloadSettings();
            // 下载器状态一变就刷新面板（下载线程里每秒会触发多次，OnDlChanged 里做了合并）
            _dl.Changed += OnDlChanged;
            _poll.Interval = TimeSpan.FromSeconds(1);
            _poll.Tick += (_, _) => { RefreshTasks(); UpdatePlayBar(); };
            _poll.Start();

            // 进度条：拖动过程中不要每挪一像素就 seek 一次（VLC 会卡成幻灯片），
            // 只在松手时定位一次；拖动期间只刷新时间文字。
            slSeek.AddHandler(Thumb.DragStartedEvent,
                new DragStartedEventHandler((_, _) => _seekDragging = true));
            slSeek.AddHandler(Thumb.DragCompletedEvent,
                new DragCompletedEventHandler((_, _) => { _seekDragging = false; ApplySeek(); }));
            Loaded += OnLoaded;
            // 全屏观看时按 Esc 退出（不然没有标题栏可以点）
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape || !_isFull) return;
                ExitFullScreen();
                e.Handled = true;
            };
        }

        /// <summary>把设置里的下载参数应用到下载器（保存设置时也会调一次，不必重启）。</summary>
        private void ApplyDownloadSettings()
        {
            try
            {
                _dl.RootDir = _settings.ResolvedDownloadDir;
                _dl.Concurrency = _settings.DownloadConcurrency is > 0 and <= 64 ? _settings.DownloadConcurrency : 12;
            }
            catch { }
        }

        /// <summary>
        /// 初始化 LibVLC。失败时把原因**留下来**（不再像以前那样静默 fallback 到外部播放器 ——
        /// 那样用户只看到"内置播放器点了没反应"，完全不知道是缺原生库还是别的问题）。
        /// </summary>
        private void InitLibVlc()
        {
            try
            {
                LibVLCSharp.Shared.Core.Initialize();
            }
            catch (Exception ex)
            {
                // Probe 失败不致命：LibVLCSharp 也会自己找 libvlc/win-x64，继续试
                _libvlcError = "Core.Initialize 失败：" + ex.Message;
            }
            try
            {
                _libvlc = new LibVLC(
                    "--no-video-title-show",
                    "--network-caching=1500",
                    "--http-reconnect",
                    "--adaptive-maxwidth=1920");
                _libvlcError = null;
            }
            catch (Exception ex)
            {
                _libvlc = null;
                _libvlcError = (string.IsNullOrEmpty(_libvlcError) ? "" : _libvlcError + "；")
                               + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(App.StartupPlayUrl))
            {
                // 自检模式：不等配置了，直接播一个地址并留日志
                Play(App.StartupPlayUrl!, "命令行自检");
                _ = WritePlayCheckAsync(App.StartupPlayUrl!);
                return;
            }
            if (App.StartupDownloadSim)
            {
                // 自检模式：走一遍「勾选选集 → 下载选中」，把每段结果写 dlcheck.log
                await RunDownloadSimAsync();
                return;
            }
            if (!string.IsNullOrEmpty(App.StartupPlaySimKeyword))
            {
                // 自检模式：连播三集，验证"点剧集立刻播放完毕"是否修好，写 playsim.log
                await RunPlaySimAsync(App.StartupPlaySimKeyword!);
                return;
            }

            if (!string.IsNullOrEmpty(_settings.LastConfig))
            {
                await LoadConfig(_settings.LastConfig!);
                if (_sites.Count > 0) return;
            }
            foreach (var url in DefaultConfigs)
            {
                await LoadConfig(url);
                if (_sites.Count > 0) return;
            }
            ShowEmptyState("尚未加载到可用配置。\n请点右上角「导入配置」，填入你的 TVBox 接口地址（单仓或多仓均可）。");
        }

        /// <summary>
        /// 把这次播放的关键事实落到 playcheck.log：解析出的地址、头、VLC 状态与时长、
        /// 以及界面状态条上的那句失败原因。出问题时把这份日志发出来即可定位。
        /// </summary>
        private async Task WritePlayCheckAsync(string rawUrl)
        {
            var lines = new List<string>
            {
                "===== TVBox PC 内置播放器自检 =====",
                "时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "原始地址：" + rawUrl,
            };
            try
            {
                await Task.Delay(12000);
                var t = _currentTarget;
                lines.Add("解析结果：" + (t == null ? "(无)" : t.Kind + "  " + MediaUrl.Describe(t)));
                lines.Add("LibVLC 初始化：" + (_libvlc == null ? "失败 → " + (_libvlcError ?? "未知") : "成功"));
                lines.Add("播放器：仅内置 LibVLC（外部播放器支持已移除）");
                if (_mp != null)
                {
                    lines.Add($"播放器状态：{_mp.State}  IsPlaying={_mp.IsPlaying}  Length={_mp.Length}ms  Time={_mp.Time}ms");
                }
                lines.Add("界面状态条：" + (playerStatus.Text.Length == 0 ? "(空 = 正常播放中)" : playerStatus.Text));
                if (t != null) lines.Add("地址探测：" + (await MediaProbe.ExplainAsync(t) ?? "(无结论)"));

                var body = string.Join(Environment.NewLine, lines);
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "playcheck.log"), body);
            }
            catch (Exception ex)
            {
                lines.Add("自检自身出错：" + ex.Message);
                try { await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "playcheck.log"), string.Join(Environment.NewLine, lines)); } catch { }
            }
            finally
            {
                // ★ LibVLC 的非托管线程会拖住进程不退出：实测 --play 走完、日志已经写好，
                //   进程还挂在 tasklist 里，用户会看到一个不动的窗口。
                //   自检是一次性动作，收掉播放器后直接退。
                try { _mp?.Stop(); } catch { }
                try { _libvlc?.Dispose(); } catch { }
                Environment.Exit(0);
            }
        }

        // ===================== 配置加载 =====================
        private async Task LoadConfig(string rawInput)
        {
            try
            {
                tbImportMsg.Text = "加载中…";
                var input = NormalizeConfigInput(rawInput);
                JsonNode? node;
                if (input.Contains("://"))
                    node = await _cfg.LoadFromUrl(input);
                else if (File.Exists(input))
                    node = await _cfg.LoadFromText(await File.ReadAllTextAsync(input), input);
                else if (input.Contains('\\') || input.Contains('/'))
                {
                    tbImportMsg.Text = "失败：找不到文件 " + input;
                    return;
                }
                else
                    node = await _cfg.LoadFromText(input);
                if (node == null) { tbImportMsg.Text = "解析失败：返回内容不是有效配置。"; return; }
                _config = node;

                // 把配置顶层 spider 字段（"./jar/fan.txt;md5;xxx"）交给 JVM 桥：
                // csp_XXX 这类「解析算法编译在蜘蛛包里」的源，靠它原样运行。
                // 只记录不做 IO —— 真正下载/转换延迟到第一次用到蜘蛛源时，避免卡住加载。
                Core.Jar.JvmBridge.Instance.Configure(
                    node["spider"]?.ToString(),
                    node["__base"]?.ToString() ?? (input.Contains("://") ? input : null),
                    _settings.UseSystemProxy);

                _sites = SpiderFactory.ParseAll(node["sites"]);

                _lives = new List<LiveSource>();
                if (node["lives"] is JsonArray lives)
                    foreach (var l in lives)
                    {
                        var name = l?["name"]?.ToString() ?? "直播";
                        var url = l?["url"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(url)) _lives.Add(new LiveSource { Name = name, Url = url });
                    }

                _clients.Clear();
                _suppressSiteEvent = true;
                cbSites.Items.Clear();
                foreach (var s in _sites)
                {
                    cbSites.Items.Add(new ComboBoxItem
                    {
                        Content = SiteLabel(s),
                        Tag = s,
                        ToolTip = SiteTooltip(s)
                    });
                }
                _suppressSiteEvent = false;

                // 自检模式不要写盘：否则跑一次 --playsim/--dlsim 就把用户的「上次配置」改掉了
                _settings.LastConfig = input;
                if (!_headless) _settings.Save();
                tbImportMsg.Text = $"已加载 {_sites.Count} 个站点" + (_lives.Count > 0 ? $" / {_lives.Count} 个直播源" : "");
                ShowOverlay(dlgImport, false);

                // 默认选中「可用性评分最高」的源（先苹果CMS，再套娃，最后 http 型 type3）
                int best = 0, bestScore = -1;
                for (int i = 0; i < _sites.Count; i++)
                {
                    var sc = SpiderFactory.AvailabilityScore(_sites[i]);
                    if (sc > bestScore) { bestScore = sc; best = i; }
                }
                if (cbSites.Items.Count > 0)
                    cbSites.SelectedIndex = best;

                if (_sites.Count == 0)
                    ShowEmptyState("配置里没有可用站点（可能全是蜘蛛源或 hide=1）。请换一个配置。");
            }
            catch (Exception ex)
            {
                tbImportMsg.Text = "失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 清洗用户从剪贴板/文件属性里复制来的路径：去掉方向控制符、BOM、两端引号、file:// 前缀。
        /// 这样「填本机文件路径」时不会因为混入不可见字符而报 "0xE2 is an invalid start"。
        /// </summary>
        private static string NormalizeConfigInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;
            var sb = new System.Text.StringBuilder(input.Length);
            foreach (var c in input)
            {
                // 去掉 Unicode 方向控制字符与 BOM（粘贴路径时常见）
                if (c is '\u200E' or '\u200F' or '\u202A' or '\u202B' or '\u202C' or
                    '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069' or '\uFEFF')
                    continue;
                sb.Append(c);
            }
            var s = sb.ToString().Trim();
            // file:///C:\... 或 file://C:\... -> C:\...
            if (s.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
            else if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
            // 去掉两端引号（直引号 / 弯引号）
            while (s.Length >= 2 &&
                   ((s[0] == '"' && s[^1] == '"') ||
                    (s[0] == '\'' && s[^1] == '\'') ||
                    (s[0] == '“' && s[^1] == '”') ||
                    (s[0] == '‘' && s[^1] == '’')))
            {
                s = s.Substring(1, s.Length - 2).Trim();
            }
            return s;
        }

        /// <summary>源下拉里显示的名字：标出该源的解析类型，不可用的标 ⚠。</summary>
        private static string SiteLabel(SiteEntry s)
        {
            if (SpiderFactory.AvailabilityScore(s) == 0) return "⚠ " + s.Name;
            if (SpiderFactory.IsDrpySource(s)) return "🕷JS " + s.Name;
            // 专属蜘蛛（解析算法编译在蜘蛛包里）：走桌面 JVM 桥原样执行
            if (s.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)
                && !s.Api.StartsWith("csp_XPath", StringComparison.OrdinalIgnoreCase))
                return "🕷JAR " + s.Name;
            if (s.TypeNum == 3) return "🕷 " + s.Name;
            return s.Name;
        }

        private static string SiteTooltip(SiteEntry s)
            => SpiderFactory.AvailabilityScore(s) switch
            {
                4 => "苹果CMS 源（最稳）",
                3 => "drpy 蜘蛛源（原生 JS 规则引擎解析）",
                2 => s.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)
                     ? "专属蜘蛛源（原版蜘蛛在桌面 JVM 上运行；首次会下载并转换蜘蛛包）"
                     : "csp_XPath 套娃源",
                1 => "type3 + HTTP 接口源",
                _ => "暂不可用：需要桌面 JVM 运行时（jvm/ 目录），或该配置未声明 spider 字段"
            };

        private async void CbSites_SelectionChanged(object sender, SelectionChangedEventArgs e)        {
            if (_suppressSiteEvent) return;
            if (cbSites.SelectedItem is ComboBoxItem it && it.Tag is SiteEntry s)
                await SelectSite(s);
        }

        private async Task SelectSite(SiteEntry s)
        {
            _site = s;
            _currentCatId = null;
            _catButtons.Clear();
            catTabs.Children.Clear();

            if (!_clients.TryGetValue(s.Key, out var client))
            {
                client = await SpiderFactory.CreateAsync(s);
                _clients[s.Key] = client;
            }
            _client = client;

            if (!client.Available)
            {
                ShowEmptyState($"[{s.Name}] 此源暂不可用：\n{client.UnavailableReason}");
                return;
            }

            var cats = await client.GetCategories();

            // 「只搜索」源（网盘搜索类：盘搜/夸搜/易搜/豆瓣搜索…）本来就没有分类页，
            // homeContent 返回空是正常的 —— 明确提示用户改用搜索，而不是给一面空墙。
            if (cats.Count == 0 && client is Core.Jar.JarSite js && js.OnlySearch && client.Searchable)
            {
                ShowEmptyState($"[{s.Name}] 该源没有分类页（网盘/搜索类源通常如此）。\n\n" +
                               "请在顶部搜索框输入片名后点「搜索」，或点「聚合搜索」同时搜多个源。");
                return;
            }

            AddCatTab("最新", null);
            if (_lives.Count > 0) AddCatTab("📺 直播", "__LIVE__");
            foreach (var c in cats) AddCatTab(c.Name, c.Id);

            if (_catButtons.Count > 0) SetActiveCat(0);
            await LoadHome(1);
        }

        private void AddCatTab(string name, string? id)
        {
            var b = new Button
            {
                Content = name,
                Height = 30,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(12, 0, 12, 0),
                Tag = id,
                Background = IdleBg,
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                FontSize = 13
            };
            b.Click += (_, _) => OnCatClick(b);
            _catButtons.Add(b);
            catTabs.Children.Add(b);
        }

        private void SetActiveCat(int index)
        {
            for (int i = 0; i < _catButtons.Count; i++)
                _catButtons[i].Background = i == index ? Brushes.OrangeRed : IdleBg;
        }

        private void SetActiveCatByButton(Button b)
        {
            foreach (var x in _catButtons) x.Background = IdleBg;
            b.Background = Brushes.OrangeRed;
        }

        private async void OnCatClick(Button b)
        {
            SetActiveCatByButton(b);
            var id = b.Tag as string;
            if (id == "__LIVE__") { await LoadLive(); return; }
            _currentCatId = id;
            await LoadHome(1);
        }

        // ===================== 首页网格 =====================
        private async Task LoadHome(int pg)
        {
            if (_client == null || !_client.Available) return;
            _homePage = pg;
            _browseMode = "list";
            List<VodItem> list;
            try { list = await _client.GetList(_currentCatId, pg); }
            catch (Exception ex)
            {
                ShowEmptyState($"加载失败：{ex.Message}\n\n可在左上角下拉切换其它源（建议选名称前没有 ⚠ 的站点）。");
                return;
            }

            if (pg == 1) homePanel.Children.Clear();
            foreach (var v in list) AddCard(v);

            if (pg == 1 && list.Count == 0)
            {
                homeTip.Text = "该分类暂无内容，换个分类、或换一个源试试。";
                homeTip.Visibility = Visibility.Visible;
            }
            else homeTip.Visibility = Visibility.Collapsed;

            btnLoadMore.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowHome();
        }

        private void AddCard(VodItem v)
        {
            var border = new Border
            {
                Width = 132,
                Margin = new Thickness(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            var sp = new StackPanel();
            var img = new Image { Width = 130, Height = 180, Stretch = Stretch.Fill };
            ImageLoader.LoadInto(img, v.Pic);
            var tb = new TextBlock
            {
                Text = v.Name + (string.IsNullOrEmpty(v.Remarks) ? "" : " " + v.Remarks),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 130,
                TextWrapping = TextWrapping.Wrap
            };
            sp.Children.Add(img);
            sp.Children.Add(tb);
            border.Child = sp;
            var id = v.Id;
            border.MouseLeftButtonUp += (_, _) => _ = OpenDetail(id);
            homePanel.Children.Add(border);
        }

        // ===================== 详情 =====================
        private async Task OpenDetail(string id, SiteEntry? siteOverride = null)
        {
            var client = _client;
            var entry = _site;
            if (siteOverride != null)
            {
                if (!_clients.TryGetValue(siteOverride.Key, out var c))
                {
                    c = await SpiderFactory.CreateAsync(siteOverride);
                    _clients[siteOverride.Key] = c;
                }
                client = c;
                entry = siteOverride;
            }
            if (client == null || !client.Available) return;

            VodDetail? d;
            try { d = await client.GetDetail(id); }
            catch { d = null; }
            if (d == null) { MessageBox.Show("打开详情失败，该源可能已失效。"); return; }

            _detail = d;
            _detailSiteKey = entry?.Key ?? "";
            _detailSiteName = entry?.Name ?? "";
            // 新片子：把"正在播第几集"清掉，否则选集网格里会误亮上一部剧的同一集
            _playingEpIndex = -1;
            FillPlayerInfo();

            dPoster.Source = null;
            ImageLoader.LoadInto(dPoster, d.Pic);
            dName.Text = d.Name;
            dMeta.Text = string.Join(" / ", new[] { d.TypeName, d.Year, d.Area, d.Director, d.Actor }
                .Where(x => !string.IsNullOrEmpty(x)));
            dDesc.Text = HtmlRules.StripHtml(d.Content);
            UpdateFavButton();

            _lines = d.Lines;
            _currentLine = 0;
            detailLines.Children.Clear();
            if (_lines.Count > 1)
            {
                dLinesLabel.Visibility = Visibility.Visible;
                for (int i = 0; i < _lines.Count; i++)
                {
                    var idx = i;
                    var b = new Button
                    {
                        Content = _lines[i].Name,
                        MinWidth = 80,
                        Height = 26,
                        Margin = new Thickness(0, 0, 6, 6),
                        FontSize = 12,
                        Padding = new Thickness(10, 0, 10, 0),
                        Background = i == 0 ? Brushes.OrangeRed : IdleBg,
                        Foreground = Brushes.White,
                        BorderBrush = Brushes.Transparent
                    };
                    b.Click += (_, _) => SelectLine(idx);
                    detailLines.Children.Add(b);
                }
            }
            else dLinesLabel.Visibility = Visibility.Collapsed;

            BuildPlayerLines();
            SelectLine(0);

            pnlHome.Visibility = Visibility.Collapsed;
            pnlList.Visibility = Visibility.Collapsed;
            pnlPlayer.Visibility = Visibility.Collapsed;
            pnlDetail.Visibility = Visibility.Visible;
        }

        private void SelectLine(int idx)
        {
            if (idx < 0 || idx >= _lines.Count) return;
            _currentLine = idx;
            _episodes = _lines[idx].Episodes;

            for (int i = 0; i < detailLines.Children.Count; i++)
                if (detailLines.Children[i] is Button b)
                    b.Background = i == idx ? Brushes.OrangeRed : IdleBg;

            // 播放页右侧那排线路按钮也要跟着亮
            for (int i = 0; i < pvLines.Children.Count; i++)
                if (pvLines.Children[i] is Button pb)
                    pb.Background = i == idx ? Brushes.OrangeRed : IdleBg;

            detailEpisodes.Children.Clear();
            rightEpisodes.Children.Clear();
            foreach (var ep in _episodes)
            {
                detailEpisodes.Children.Add(EpisodeChip(ep, false));
                rightEpisodes.Children.Add(EpisodeChip(ep, true));
            }

            // 播放页下方的选集网格：换了线路就整体重建，并回到第 1 页
            _pvPage = 0;
            BuildPlayEpisodes();
            UpdateSelInfo();
        }

        private UIElement EpisodeChip(Episode ep, bool withCheck)
        {
            var border = new Border
            {
                Margin = new Thickness(4),
                Padding = new Thickness(8, 4, 8, 4),
                Background = CardBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            CheckBox? cb = null;
            if (withCheck)
            {
                cb = new CheckBox
                {
                    Tag = ep,
                    IsChecked = false,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                sp.Children.Add(cb);
            }
            sp.Children.Add(new TextBlock { Text = ep.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            border.Child = sp;

            if (withCheck && cb != null)
            {
                // ★ 右栏（下载用）：整块点击 = 勾选 / 取消勾选。
                // 旧写法这里绑的是 Play —— 用户点分集的**名字**想勾选，结果直接跳到播放页，
                // 勾选状态一点没变，回到下载面板点"下载选中"就提示"请先勾选"，
                // 表现出来就是「选了分集没反应」。
                border.MouseLeftButtonUp += (_, e) =>
                {
                    cb.IsChecked = cb.IsChecked != true;
                    e.Handled = true;
                };
                border.ToolTip = "点一下勾选 / 取消勾选";
                void Sync()
                {
                    bool on = cb.IsChecked == true;
                    border.Background = on ? SelBg : CardBg;
                    border.BorderBrush = on ? Brushes.Orange : CardBorder;
                }
                cb.Checked += (_, _) => { Sync(); UpdateSelInfo(); };
                cb.Unchecked += (_, _) => { Sync(); UpdateSelInfo(); };
                Sync();
            }
            else
            {
                border.MouseLeftButtonUp += (_, _) => Play(ep.Url, ep.Name, true, _episodes.IndexOf(ep));
                border.ToolTip = "点击播放";
            }
            return border;
        }

        /// <summary>更新「已选 N / 共 M」提示。</summary>
        private void UpdateSelInfo()
        {
            int sel = SelectedEpisodes().Count;
            tbSelInfo.Text = _episodes.Count == 0 ? "" : $"已选 {sel} / 共 {_episodes.Count}";
        }

        /// <summary>读出右栏里被勾选的选集。</summary>
        private List<Episode> SelectedEpisodes() =>
            rightEpisodes.Children.OfType<Border>()
                .Select(b => (b.Child as StackPanel)?.Children.OfType<CheckBox>().FirstOrDefault())
                .Where(cb => cb?.IsChecked == true && cb.Tag is Episode)
                .Select(cb => (Episode)cb!.Tag!)
                .ToList();

        private void BtnSelAll_Click(object sender, RoutedEventArgs e) => SetAllChecked(true);
        private void BtnSelNone_Click(object sender, RoutedEventArgs e) => SetAllChecked(false);

        private void SetAllChecked(bool on)
        {
            foreach (var b in rightEpisodes.Children.OfType<Border>())
                if ((b.Child as StackPanel)?.Children.OfType<CheckBox>().FirstOrDefault() is { } cb)
                    cb.IsChecked = on;
            UpdateSelInfo();
        }

        // ===================== 播放页（TVBox 排布：视频左上 / 信息右侧 / 选集下方）=====================
        /// <summary>把详情填进播放页右侧的信息栏。</summary>
        private void FillPlayerInfo()
        {
            var d = _detail;
            if (d == null) return;

            pvTitle.Text = d.Name;

            var meta = new List<string>();
            if (!string.IsNullOrEmpty(_detailSiteName)) meta.Add("来源：" + _detailSiteName);
            if (!string.IsNullOrEmpty(d.Remarks)) meta.Add(d.Remarks);
            if (!string.IsNullOrEmpty(d.Year)) meta.Add("年份：" + d.Year);
            if (!string.IsNullOrEmpty(d.Area)) meta.Add("地区：" + d.Area);
            if (!string.IsNullOrEmpty(d.TypeName)) meta.Add("类型：" + d.TypeName);
            pvMeta.Text = string.Join("    ", meta);

            pvActor.Text = string.IsNullOrEmpty(d.Actor) ? "" : "演员：" + d.Actor;
            pvDirector.Text = string.IsNullOrEmpty(d.Director) ? "" : "导演：" + d.Director;
            pvDesc.Text = string.IsNullOrEmpty(d.Content) ? "" : "内容简介：" + HtmlRules.StripHtml(d.Content);
            pvSourceTag.Text = string.IsNullOrEmpty(_detailSiteKey) ? (_site?.Key ?? "—") : _detailSiteKey;
            pvAddress.Text = "播放地址：（还没开始播）";
        }

        /// <summary>播放页右侧的线路按钮（和详情页那排是同一套动作，两处都要能点）。</summary>
        private void BuildPlayerLines()
        {
            pvLines.Children.Clear();
            for (int i = 0; i < _lines.Count; i++)
            {
                var idx = i;
                var name = string.IsNullOrEmpty(_lines[i].Name) ? $"线路{i + 1}" : _lines[i].Name;
                var b = new Button
                {
                    Content = name,
                    MinWidth = 60,
                    Height = 26,
                    FontSize = 12,
                    Padding = new Thickness(10, 0, 10, 0),
                    Margin = new Thickness(0, 0, 6, 6),
                    Background = i == _currentLine ? Brushes.OrangeRed : IdleBg,
                    Foreground = Brushes.White,
                    BorderBrush = Brushes.Transparent
                };
                b.Click += (_, _) => SelectLine(idx);
                pvLines.Children.Add(b);
            }
        }

        /// <summary>播放页下方的选集网格（TVBox 那种；集数多时按页切，免得一次铺几千个控件卡住）。</summary>
        private void BuildPlayEpisodes()
        {
            playEpisodes.Children.Clear();
            pvGroups.Children.Clear();

            int total = _episodes.Count;
            int pages = (total + PvPageSize - 1) / PvPageSize;
            pvGroupRow.Visibility = total > PvPageSize ? Visibility.Visible : Visibility.Collapsed;

            var all = _episodes.Select((e, i) => (ep: e, idx: i)).ToList();
            if (_epReverse) all.Reverse();

            List<(Episode ep, int idx)> slice;
            if (total > PvPageSize)
            {
                slice = all.Skip(_pvPage * PvPageSize).Take(PvPageSize).ToList();
                for (int p = 0; p < pages; p++)
                {
                    int pi = p;
                    var b = new Button
                    {
                        Content = $"{p * PvPageSize + 1}-{Math.Min(total, (p + 1) * PvPageSize)}",
                        MinWidth = 54,
                        Height = 24,
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 6, 6),
                        Background = p == _pvPage ? Brushes.OrangeRed : IdleBg,
                        Foreground = Brushes.White,
                        BorderBrush = Brushes.Transparent
                    };
                    b.Click += (_, _) => { _pvPage = pi; BuildPlayEpisodes(); };
                    pvGroups.Children.Add(b);
                }
            }
            else slice = all;

            foreach (var (ep, idx) in slice) playEpisodes.Children.Add(PlayEpChip(ep, idx));
            UpdatePlayEpHighlight();
        }

        private UIElement PlayEpChip(Episode ep, int index)
        {
            var border = new Border
            {
                MinWidth = 92,
                Margin = new Thickness(4),
                Padding = new Thickness(10, 5, 10, 5),
                Background = CardBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Tag = index,
                ToolTip = "点击播放这一集"
            };
            border.Child = new TextBlock { Text = ep.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            border.MouseLeftButtonUp += (_, _) => Play(ep.Url, ep.Name, true, index);
            return border;
        }

        /// <summary>把正在播的那一集在下方选集网格里点亮。</summary>
        private void UpdatePlayEpHighlight()
        {
            foreach (var b in playEpisodes.Children.OfType<Border>())
            {
                bool on = b.Tag is int i && i == _playingEpIndex;
                b.Background = on ? new SolidColorBrush(Color.FromRgb(0x3d, 0x24, 0x18)) : CardBg;
                b.BorderBrush = on ? Brushes.Orange : CardBorder;
            }
        }

        /// <summary>正在播的集不在当前页时自动翻过去。</summary>
        private void EnsureEpPageVisible()
        {
            int total = _episodes.Count;
            if (total <= PvPageSize || _playingEpIndex < 0 || _playingEpIndex >= total) return;
            var pos = _epReverse ? total - 1 - _playingEpIndex : _playingEpIndex;
            var want = pos / PvPageSize;
            if (want != _pvPage) { _pvPage = want; BuildPlayEpisodes(); }
        }

        /// <summary>播放页「← 返回」：回影片详情页（那里右侧才是完整下载面板）。</summary>
        private void BtnBackFromPlayer_Click(object sender, RoutedEventArgs e)
        {
            try { _mp?.Stop(); } catch { }
            SetPlayStatus("");
            ExitFullScreen();
            if (_detail != null)
            {
                pnlPlayer.Visibility = Visibility.Collapsed;
                pnlHome.Visibility = Visibility.Collapsed;
                pnlList.Visibility = Visibility.Collapsed;
                pnlDetail.Visibility = Visibility.Visible;
            }
            else ShowHome();
        }

        // ---- 播放控制条（进度 / 暂停 / 音量 / 倍速 / 上下集 / 全屏）----
        private bool _seekDragging;
        private bool _suppressSeek;
        private static readonly float[] Speeds = { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f };

        /// <summary>每秒刷一次控制条：进度、时间、播放/暂停图标。</summary>
        private void UpdatePlayBar()
        {
            if (_mp == null || pnlPlayer.Visibility != Visibility.Visible) return;

            long len = 0, pos = 0;
            bool playing = false;
            try
            {
                len = _mp.Length;
                pos = _mp.Time;
                playing = _mp.IsPlaying;
            }
            catch { }

            tbTime.Text = FmtMs(pos) + " / " + FmtMs(len);
            if (!_seekDragging && len > 0)
            {
                _suppressSeek = true;
                slSeek.Value = Math.Clamp(pos * 1000.0 / len, 0, 1000);
                _suppressSeek = false;
            }
            btnPlayPause.Content = playing ? "⏸" : "▶";
        }

        private void ApplySeek()
        {
            if (_mp == null || _suppressSeek) return;
            long len = 0;
            try { len = _mp.Length; } catch { }
            if (len <= 0) return;
            try { _mp.Time = (long)(len * slSeek.Value / 1000.0); } catch { }
        }

        private void SlSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSeek || _mp == null) return;
            long len = 0;
            try { len = _mp.Length; } catch { }
            if (len <= 0) return;

            if (_seekDragging)
                tbTime.Text = FmtMs((long)(len * e.NewValue / 1000.0)) + " / " + FmtMs(len);
            else
                ApplySeek();
        }

        private void SlVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (tbVol != null) tbVol.Text = ((int)e.NewValue).ToString();
            if (_mp == null) return;
            try { _mp.Volume = (int)e.NewValue; } catch { }
        }

        private void CbSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_mp == null) return;      // 还没起播：XAML 里预设的 SelectedIndex 会走到这里
            var i = cbSpeed.SelectedIndex;
            if (i < 0 || i >= Speeds.Length) return;
            try
            {
                _mp.SetRate(Speeds[i]);
                SetPlayStatus($"播放倍速：{Speeds[i]}x");
            }
            catch (Exception ex) { SetPlayStatus("设置倍速失败：" + ex.Message); }
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mp == null) { SetPlayStatus("还没有开始播放。"); return; }
            try
            {
                if (_mp.IsPlaying) _mp.Pause();
                else _mp.Play();
            }
            catch (Exception ex) { SetPlayStatus("播放/暂停失败：" + ex.Message); }
            UpdatePlayBar();
        }

        private void BtnPrevEp_Click(object sender, RoutedEventArgs e) => GoEpisode(-1);
        private void BtnNextEp_Click(object sender, RoutedEventArgs e) => GoEpisode(1);

        /// <summary>上一集 / 下一集（比手动回去点选集省事，全屏时尤其有用）。</summary>
        private void GoEpisode(int delta)
        {
            if (_episodes.Count == 0) { SetPlayStatus("当前没有可切换的选集。"); return; }
            int cur = _playingEpIndex < 0 ? 0 : _playingEpIndex;
            int nxt = cur + delta;
            if (nxt < 0) { SetPlayStatus("已经是第一集了。"); return; }
            if (nxt >= _episodes.Count) { SetPlayStatus("已经是最后一集了。"); return; }
            Play(_episodes[nxt].Url, _episodes[nxt].Name, true, nxt);
        }

        /// <summary>把控制条上的音量 / 倍速应用到播放器（每次起播后调一次）。</summary>
        private void ApplyBarToPlayer()
        {
            if (_mp == null) return;
            try { _mp.Volume = (int)slVolume.Value; } catch { }
            var i = cbSpeed.SelectedIndex;
            if (i >= 0 && i < Speeds.Length)
            {
                try { _mp.SetRate(Speeds[i]); } catch { }
            }
        }

        // ---- 右侧下载面板：伸缩栏 ----
        private double _dlPaneWidth = 330;

        /// <summary>收起 / 展开右侧下载面板。收起后内容区（含播放页）就占满整宽。</summary>
        private void ToggleDlPane()
        {
            bool open = dlCol.Width.Value > 1;
            if (open)
            {
                _dlPaneWidth = dlCol.Width.Value;
                dlCol.Width = new GridLength(0);
                dlHandleArrow.Text = "❮";
                if (btnPlayerDlPanel != null) btnPlayerDlPanel.Content = "显示下载";
            }
            else
            {
                dlCol.Width = new GridLength(_dlPaneWidth <= 1 ? 330 : _dlPaneWidth);
                dlHandleArrow.Text = "❯";
                if (btnPlayerDlPanel != null) btnPlayerDlPanel.Content = "下载面板";
            }
        }

        private void DlHandle_Click(object sender, MouseButtonEventArgs e) => ToggleDlPane();

        private void BtnPlayerDlPanel_Click(object sender, RoutedEventArgs e) => ToggleDlPane();

        // ---- 覆盖层（弹窗）与视频画面的关系 ----
        /// <summary>
        /// 显示 / 隐藏一个弹窗，并同步决定视频画面要不要收起来。
        /// ★ 为什么必须这么做：`vlc:VideoView` 是 HwndHost（一个原生子窗口），
        ///   它**永远浮在所有 WPF 元素之上** —— 这是 WPF 的 airspace 硬限制，
        ///   调 ZIndex、改 Panel 顺序都没用。所以弹窗打开时先把视频画面 Collapsed，
        ///   否则弹窗会被视频盖住（"切源时视频挡住切源列表"就是这么来的）。
        /// </summary>
        private void ShowOverlay(Grid dlg, bool show)
        {
            dlg.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            UpdateOverlayMode();
            if (_headless) _simLog?.Invoke($"   [探针] ShowOverlay({dlg?.Name}, show={show})");
        }

        /// <summary>只要有任何一个覆盖层开着，就把视频画面收起来。</summary>
        private void UpdateOverlayMode()
        {
            if (videoView == null) return;
            bool any =
                (dlgSwitchSrc != null && dlgSwitchSrc.Visibility == Visibility.Visible) ||
                (dlgNewDl != null && dlgNewDl.Visibility == Visibility.Visible) ||
                (dlgCmds != null && dlgCmds.Visibility == Visibility.Visible) ||
                (dlgImport != null && dlgImport.Visibility == Visibility.Visible) ||
                (dlgSettings != null && dlgSettings.Visibility == Visibility.Visible);
            videoView.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (any)
            {
                // HwndHost 是原生子窗口，不是 WPF 画出来的。把它 Collapsed 之后，
                // 它原来占的那块像素**不会**被自动重画 —— 屏幕上会留一帧"残影"
                // （截图实测：切源弹窗出来了，左边还挂着上一秒的视频画面）。
                // 把承载它的容器标脏、逼 WPF 重绘一次，残影就抹掉了。
                if (videoView.Parent is UIElement host) host.InvalidateVisual();
            }
        }

        /// <summary>全屏：只留视频画面（对应 TVBox 那个「全屏」按钮）。</summary>
        private void BtnFullScreen_Click(object sender, RoutedEventArgs e)
        {
            // 探针：自检时万一"自己进了全屏"，把调用栈写下来，别靠猜
            if (_headless)
                _simLog?.Invoke("   [探针] BtnFullScreen_Click 被调用。栈：" +
                    string.Join(" ← ", Environment.StackTrace.Split('\n').Take(7).Select(x => x.Trim())));

            if (_isFull) { ExitFullScreen(); return; }

            _prevStyle = WindowStyle;
            _prevState = WindowState;
            _prevResize = ResizeMode;
            _prevEpRowHeight = playEpRow.Height;

            topBar.Visibility = Visibility.Collapsed;
            catBar.Visibility = Visibility.Collapsed;
            playerTopBar.Visibility = Visibility.Collapsed;
            playerStatusBox.Visibility = Visibility.Collapsed;
            pvInfoPane.Visibility = Visibility.Collapsed;
            playBottom.Visibility = Visibility.Collapsed;
            // 全屏时右侧下载面板也要让位，否则视频区被挤掉 330px
            _prevDlWidth = dlCol.Width;
            dlHandle.Visibility = Visibility.Collapsed;
            dlCol.Width = new GridLength(0);
            playBodyRow.Height = new GridLength(1, GridUnitType.Star);
            playEpRow.Height = new GridLength(0);

            // ★★ 全屏时右侧那条"很长的空白"就是这里来的 ★★
            // 信息栏虽然 Collapsed 了，但它所在的那一列还挂着 Width="1*" —— 列宽跟子元素可不可见
            // 无关，实测视频只占 1476/2560 ≈ 57.6%（正是 1.35:1 的比例），剩下 1084px 全是黑底空档。
            // 另外 MinWidth="300" 不清掉的话，列宽根本压不到 0，所以两样都得处理。
            vidCol.Width = new GridLength(1, GridUnitType.Star);
            vidCol.MinWidth = 0;
            vidSepCol.Width = new GridLength(0);
            infoCol.Width = new GridLength(0);
            infoCol.MinWidth = 0;

            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;

            _isFull = true;
            btnFullScreen.Content = "退出全屏";
            btnFullScreen2.Content = "退出全屏";
        }

        /// <summary>退出全屏。返回 / 切页时也要调，免得卡在全屏里出不来。</summary>
        private void ExitFullScreen()
        {
            if (!_isFull) return;

            WindowState = WindowState.Normal;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;

            topBar.Visibility = Visibility.Visible;
            catBar.Visibility = Visibility.Visible;
            playerTopBar.Visibility = Visibility.Visible;
            pvInfoPane.Visibility = Visibility.Visible;
            playBottom.Visibility = Visibility.Visible;
            dlHandle.Visibility = Visibility.Visible;
            dlCol.Width = _prevDlWidth;
            playBodyRow.Height = new GridLength(0.55, GridUnitType.Star);
            playEpRow.Height = _prevEpRowHeight;
            // 列宽还原（跟全屏时那三行一一对应，改一处必须改两处）
            vidCol.MinWidth = 240;
            vidCol.Width = new GridLength(1.35, GridUnitType.Star);
            vidSepCol.Width = new GridLength(1);
            infoCol.MinWidth = 300;
            infoCol.Width = new GridLength(1, GridUnitType.Star);
            playerStatusBox.Visibility = string.IsNullOrEmpty(playerStatus.Text)
                ? Visibility.Collapsed : Visibility.Visible;

            WindowState = _prevState;
            _isFull = false;
            btnFullScreen.Content = "全屏";
            btnFullScreen2.Content = "全屏";
        }

        /// <summary>用剧名在当前源里再搜一次（TVBox 的「快速搜索」）。</summary>
        private void BtnQuickSearch_Click(object sender, RoutedEventArgs e)
        {
            if (_detail == null || string.IsNullOrWhiteSpace(_detail.Name)) return;
            tbSearch.Text = _detail.Name;
            _ = DoSearch();
        }

        private void BtnIntro_Click(object sender, RoutedEventArgs e)
        {
            if (_detail == null) return;
            var txt = HtmlRules.StripHtml(_detail.Content);
            MessageBox.Show(string.IsNullOrWhiteSpace(txt) ? "这个源没有提供剧情简介。" : txt,
                "简介 · " + _detail.Name);
        }

        // 信息栏那行地址文字绑 MouseLeftButtonUp，按钮排里的「复制地址」绑 Click ——
        // 两个事件签名不同，所以各留一个入口，都走同一个实现。
        private void PvAddress_MouseUp(object sender, MouseButtonEventArgs e) => CopyPlayUrl();
        private void PvAddress_Click(object sender, RoutedEventArgs e) => CopyPlayUrl();

        /// <summary>复制当前播放地址。</summary>
        private void CopyPlayUrl()
        {
            var u = _currentTarget?.Url;
            if (string.IsNullOrEmpty(u)) { SetPlayStatus("还没有开始播放，没有地址可复制。"); return; }
            try
            {
                Clipboard.SetText(u);
                SetPlayStatus("播放地址已复制到剪贴板：" + u);
            }
            catch (Exception ex) { SetPlayStatus("复制失败：" + ex.Message); }
        }

        private void BtnPvOrder_Click(object sender, RoutedEventArgs e)
        {
            _epReverse = !_epReverse;
            btnPvOrder.Content = _epReverse ? "正序" : "倒序";
            _pvPage = 0;
            BuildPlayEpisodes();
        }

        /// <summary>播放页「下载本集」：直接下正在看的这一集（没播过就下第 1 集）。</summary>
        private void BtnPvDlCur_Click(object sender, RoutedEventArgs e)
        {
            if (_detail == null || _episodes.Count == 0)
            {
                Info("还没有打开一部剧（或这部剧没有选集）。", "暂无可下载的选集");
                return;
            }
            var idx = (_playingEpIndex >= 0 && _playingEpIndex < _episodes.Count) ? _playingEpIndex : 0;
            var ep = _episodes[idx];
            EnqueueDownloads(new List<(string, string)> { (ep.Name, ep.Url) }, $"本集 {ep.Name}");
        }

        // ---- 切源：拿剧名去别的源里找同一部剧 ----
        private void BtnSwitchClose_Click(object sender, RoutedEventArgs e) => ShowOverlay(dlgSwitchSrc, false);

        private async void BtnSwitchSrc_Click(object sender, RoutedEventArgs e) => await RunSwitchSearchAsync();

        /// <summary>切源的实现（抽出来是为了让自检能 await 它 —— async void 没法等）。</summary>
        private async Task RunSwitchSearchAsync()
        {
            var d = _detail;
            if (d == null || string.IsNullOrWhiteSpace(d.Name)) return;

            var kw = d.Name;
            var keepEp = _playingEpIndex;          // 切换后尽量停回同一集

            ShowOverlay(dlgSwitchSrc, true);
            swBody.Children.Clear();
            swHead.Text = $"正在用「{kw}」去其它源里找同一部剧…";

            var targets = _sites
                .Where(s => s.Searchable && SpiderFactory.PredictAvailable(s))
                .OrderByDescending(SpiderFactory.AvailabilityScore)
                .Take(_settings.AggLimit <= 0 ? 30 : _settings.AggLimit)
                .ToList();

            if (targets.Count == 0)
            {
                swHead.Text = "当前配置里没有可搜索的源。";
                return;
            }

            int total = targets.Count, done = 0, hits = 0;
            var gate = new SemaphoreSlim(12);
            var jvmGate = new SemaphoreSlim(2);

            var tasks = targets.Select(async s =>
            {
                List<VodItem> got = new();
                try
                {
                    await gate.WaitAsync();
                    try
                    {
                        ISiteClient? c = _clients.TryGetValue(s.Key, out var cached) ? cached : null;
                        if (c == null)
                        {
                            c = await WithTimeout(() => SpiderFactory.CreateAsync(s), 12000);
                            if (c == null) return;
                            _clients[s.Key] = c;
                        }
                        bool isJvm = c is Core.Jar.JarSite;
                        if (!c.Available || !c.Searchable) return;

                        if (isJvm) await jvmGate.WaitAsync();
                        try
                        {
                            var r = await WithTimeout(() => c.Search(kw, 1), _settings.AggTimeoutSeconds * 1000);
                            if (r != null) got.AddRange(r);
                        }
                        finally { if (isJvm) jvmGate.Release(); }
                    }
                    finally { gate.Release(); }

                    if (got.Count > 0)
                        Dispatcher.Invoke(() =>
                        {
                            hits++;
                            // 同一个源可能命中好几条，取前 6 条够挑了
                            foreach (var v in got.Take(6)) swBody.Children.Add(SwitchRow(s, v, keepEp));
                            swHead.Text = $"「{kw}」：已有 {hits} 个源给出结果（共搜 {total} 个源）";
                        });
                }
                catch { }
                finally
                {
                    var n = Interlocked.Increment(ref done);
                    if (n >= total)
                        Dispatcher.Invoke(() =>
                            swHead.Text = hits == 0
                                ? $"其它源都没搜到「{kw}」。可以试试点「快速搜索」在当前源里换个条目。"
                                : $"「{kw}」：{hits} 个源有结果（共搜 {total} 个源）。点一条即切换。");
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        private UIElement SwitchRow(SiteEntry s, VodItem v, int keepEp)
        {
            var border = new Border
            {
                Background = CardBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = v.Name,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            sp.Children.Add(new TextBlock
            {
                Text = s.Name + (string.IsNullOrEmpty(v.Remarks) ? "" : "    " + v.Remarks),
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8a, 0x8a, 0x92))
            });
            border.Child = sp;

            border.MouseLeftButtonUp += async (_, _) =>
            {
                ShowOverlay(dlgSwitchSrc, false);
                try { _mp?.Stop(); } catch { }
                await OpenDetail(v.Id, s);
                // 尽量跳回原来那一集（新源集数可能更少，越界就不强制）
                if (keepEp >= 0 && keepEp < _episodes.Count)
                    Play(_episodes[keepEp].Url, _episodes[keepEp].Name, true, keepEp);
            };
            return border;
        }

        private void UpdateFavButton()
        {
            if (_detail == null) return;
            var fav = _store.IsFavorite(_detailSiteKey, _detail.Id);
            btnFavToggle.Content = fav ? "★ 已收藏" : "☆ 收藏";
            btnFavToggle.Foreground = fav ? Brushes.Orange : Brushes.White;
            if (btnFavToggle2 != null) btnFavToggle2.Content = fav ? "★ 已收藏" : "☆ 收藏";
        }

        private void BtnFavToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_detail == null) return;
            var item = new HistoryItem
            {
                SiteKey = _detailSiteKey,
                SiteName = _detailSiteName,
                VodId = _detail.Id,
                Name = _detail.Name,
                Pic = _detail.Pic,
                Time = DateTime.Now
            };
            var fav = _store.ToggleFavorite(item);
            UpdateFavButton();
            MessageBox.Show(fav ? "已收藏：" + _detail.Name : "已取消收藏：" + _detail.Name);
        }

        // ===================== 播放 =====================
        private void Play(string url, string epName, bool history = true, int epIndex = -1)
        {
            _currentPlayUrl = url;
            _playingEpIndex = epIndex;
            playerTitle.Text = (_detail?.Name ?? "") + " - " + epName;
            SetPlayStatus("");

            if (_detail != null && history)
                _store.AddHistory(new HistoryItem
                {
                    SiteKey = _detailSiteKey,
                    SiteName = _detailSiteName,
                    VodId = _detail.Id,
                    Name = _detail.Name,
                    Pic = _detail.Pic,
                    EpisodeName = epName,
                    EpisodeUrl = url,
                    Time = DateTime.Now
                });

            // ★ 先把地址解析干净：剥掉 "|User-Agent=…&Referer=…" 这层头参数。
            //   旧代码直接 new Uri(原始串)，竖线会被原样保留，播放器于是去请求一个含 `|` 的
            //   路径 —— 必然失败，而且一句话提示都没有。
            var target = MediaUrl.Parse(url, SiteBase());
            _currentTarget = target;

            // 播放页信息栏跟着更新（地址、当前集）
            pvAddress.Text = "播放地址：" + target.Url;
            pvEpHint.Text = epName.Length > 0 ? "正在播 " + epName : "";
            EnsureEpPageVisible();
            UpdatePlayEpHighlight();

            if (target.Kind != MediaKind.Direct)
            {
                ShowPlayerWithMessage($"{target.Note}\n\n（地址：{target.Url}）");
                return;
            }

            if (_libvlc == null)
            {
                ShowPlayerWithMessage(
                    "内置播放器（LibVLC）没有加载成功，无法播放。\n\n" +
                    "原因：" + (_libvlcError ?? "未知") + "\n\n" +
                    "请确认程序目录下存在 libvlc\\win-x64\\libvlc.dll（安装包自带）。");
                return;
            }

            pnlHome.Visibility = Visibility.Collapsed;
            pnlList.Visibility = Visibility.Collapsed;
            pnlDetail.Visibility = Visibility.Collapsed;
            pnlPlayer.Visibility = Visibility.Visible;

            _playStartAt = DateTime.Now;
            try
            {
                EnsurePlayer();
                var media = new LibVLCSharp.Shared.Media(_libvlc, target.Url, LibVLCSharp.Shared.FromType.FromLocation);

                // 把 Referer / UA 交给 VLC（很多 m3u8 缺这个就是 403 → 黑屏）
                var opts = new List<string>();
                MediaUrl.AddVlcOptions(opts, target);
                foreach (var o in opts) media.AddOption(o);

                // ★ 这里**不要**先 _mp.Stop()。LibVLC 在停止/换媒体的瞬间会抛一次 EndReached，
                //   而事件回调是排队到 UI 线程执行的 —— 它会覆盖掉下面那句"开始播放…"，
                //   于是用户看到的就是「点一下剧集，状态立刻变成"播放结束。"」。
                //   Play(media) 本身就会接管当前媒体，不需要先 Stop。
                _mp!.Play(media);

                var old = _curMedia;
                _curMedia = media;
                try { old?.Dispose(); } catch { }

                ApplyBarToPlayer();     // 把控制条上的音量 / 倍速应用到这次播放
                UpdatePlayBar();

                SetPlayStatus(target.HasHeaders
                    ? "已带上请求头开始播放：" + string.Join(" / ", target.Headers.Keys)
                    : "开始播放…");
            }
            catch (Exception ex)
            {
                ShowPlayerWithMessage("内置播放器启动失败：" + ex.Message + "\n\n地址：" + target.Url);
            }
        }

        /// <summary>视频区上方的状态条：播放中/失败原因都写这里，避免"黑屏且零提示"。</summary>
        private void SetPlayStatus(string text)
        {
            playerStatus.Text = text;
            playerStatusBox.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowPlayerWithMessage(string msg)
        {
            pnlHome.Visibility = Visibility.Collapsed;
            pnlList.Visibility = Visibility.Collapsed;
            pnlDetail.Visibility = Visibility.Collapsed;
            pnlPlayer.Visibility = Visibility.Visible;
            SetPlayStatus(msg);
        }

        /// <summary>
        /// 把一段界面更新**排队**进 UI 线程（不阻塞调用方）。
        /// 专给 libvlc 的事件回调线程用 —— 那边绝不能阻塞，详见 EnsurePlayer 里的注释。
        /// 另外 BeginInvoke 在程序退出、Dispatcher 已关闭时会抛异常，这里统一吞掉。
        /// </summary>
        private void UI(Action a)
        {
            try { Dispatcher.BeginInvoke(a); } catch { }
        }

        /// <summary>创建并绑定唯一的 MediaPlayer（只做一次）。</summary>
        private void EnsurePlayer()
        {
            if (_mp != null) return;
            _mp = new LibVLCSharp.Shared.MediaPlayer(_libvlc!);
            videoView.MediaPlayer = _mp;

            // ★★★ 这里必须是 BeginInvoke（排队），不能是 Invoke（阻塞等待）★★★
            //
            // LibVLC 的事件是「谁触发谁回调」——vlc_event_send 直接同步遍历监听器，
            // 所以 Play/Buffer/EndReached 这些回调跑在 **input 线程**上，不是 UI 线程。
            // 而 MediaPlayer 的时间属性内部是 input_Control()，那是**阻塞**的：
            //   UI 线程：slSeek 拖动 → _mp.Time = x  → 阻塞，等 input 线程应答
            //   input 线程：seek 引发 Buffering → 回调里 Dispatcher.Invoke() → 阻塞，等 UI 线程
            // 两边互等 = 死锁，界面直接卡住不动（实测：拖进度条 30% 时整个窗口 Responding=False）。
            // BeginInvoke 只把工作排进 UI 队列、立刻返回，input 线程就能去应答那个 seek。
            _mp.Playing += (_, _) => UI(() =>
            {
                if (pnlPlayer.Visibility == Visibility.Visible) SetPlayStatus("");
            });
            _mp.Buffering += (_, e) => UI(() =>
            {
                if (pnlPlayer.Visibility != Visibility.Visible) return;
                SetPlayStatus(e.Cache < 100 ? $"缓冲中 {e.Cache:F0}%" : "");
            });
            _mp.EndReached += (_, _) => UI(OnEndReached);
            _mp.EncounteredError += (_, _) => _ = ExplainFailureAsync();
        }

        /// <summary>
        /// 「播放结束」这条提示以前**只要收到 EndReached 就写**。而 LibVLC 在换媒体/停止时也会
        /// 抛一次 EndReached，回调又排在 UI 线程队列里 —— 结果就是点一下剧集，状态立刻变成
        /// "播放结束。"，看着像程序坏了。
        /// 现在按事实分两类：起播不到 2.5 秒、且拿不到总时长 → 根本没拿到流；否则才是真播完。
        /// </summary>
        private void OnEndReached()
        {
            if (pnlPlayer.Visibility != Visibility.Visible) return;

            double sec = (DateTime.Now - _playStartAt).TotalSeconds;
            long len = 0, pos = 0;
            try { len = _mp?.Length ?? 0; pos = _mp?.Time ?? 0; } catch { }

            if (sec < 2.5 && len <= 0)
            {
                SetPlayStatus(
                    "⚠ 这条地址没有返回能播的视频流（已停下）。\n" +
                    "常见原因：① 地址带时效、已过期；② 需要 Referer/Cookie 防盗链；" +
                    "③ 这条线路本身是坏的、或源站限流；④ 系统代理失效导致拿不到流。\n" +
                    "可以试：换一集看看 → 点「切源」换个源 → 或在「设置」里取消系统代理后重开程序。");
                return;
            }
            SetPlayStatus($"播放结束（总时长 {FmtMs(len)}，已播到 {FmtMs(pos)}）。");
        }

        private static string FmtMs(long ms)
        {
            if (ms <= 0) return "未知";
            var ts = TimeSpan.FromMilliseconds(ms);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        }

        /// <summary>
        /// 播放失败时不要只丢一句"播放失败"：去探一下这个地址到底返回了什么，
        /// 把「403 防盗链 / 返回的是网页需要解析 / 网络层不通」这几种情况区分开。
        /// </summary>
        private async Task ExplainFailureAsync()
        {
            var t = _currentTarget;
            string msg;
            if (t == null) msg = "播放失败（没有可分析的地址）。";
            else
            {
                var why = await MediaProbe.ExplainAsync(t);
                msg = "❌ 内置播放器播放失败。\n\n" + (why ?? "播放器没有给出具体原因。") +
                      "\n\n可点上方「用外部播放器打开」对比一下：外部能播而内置不能，通常是系统代理问题。";
            }
            UI(() =>
            {
                // 已经离开播放页就别再往状态条上写东西了
                if (pnlPlayer.Visibility == Visibility.Visible) SetPlayStatus(msg);
            });
        }

        /// <summary>相对播放地址的补全基址（拿站点 api 或配置地址）。</summary>
        private string? SiteBase()
        {
            var s = _site;
            if (s == null) return null;
            if (s.Api.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return s.Api;
            return string.IsNullOrEmpty(s.Base) ? null : s.Base;
        }

        // ===================== 直播 =====================
        private async Task LoadLive()
        {
            ShowHome();
            homeTip.Visibility = Visibility.Collapsed;
            btnLoadMore.Visibility = Visibility.Collapsed;
            homePanel.Children.Clear();
            _channels = new List<LiveParser.LiveChannel>();
            homePanel.Children.Add(new TextBlock { Text = "正在加载直播源…", Foreground = Brushes.Gray, Margin = new Thickness(10) });

            foreach (var src in _lives)
            {
                var ch = await _live.Parse(src.Url);
                _channels.AddRange(ch);
            }
            homePanel.Children.Clear();
            if (_channels.Count == 0)
            {
                homeTip.Text = "直播源为空或加载失败。";
                homeTip.Visibility = Visibility.Visible;
                return;
            }
            foreach (var c in _channels)
            {
                var border = new Border
                {
                    Width = 150,
                    Margin = new Thickness(6),
                    Background = CardBg,
                    BorderBrush = CardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(8, 6, 8, 6)
                };
                var label = string.IsNullOrEmpty(c.Group) ? c.Name : $"[{c.Group}] {c.Name}";
                border.Child = new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                var u = c.Url; var n = c.Name;
                border.MouseLeftButtonUp += (_, _) => Play(u, n);
                homePanel.Children.Add(border);
            }
        }

        // ===================== 搜索 / 聚合搜索 =====================
        private void TbSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) _ = DoSearch();
        }

        /// <summary>搜索框清空后自动回到当前源的分类首页，避免删完关键字还卡在「无结果」提示。</summary>
        private void TbSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_client == null || !_client.Available) return;
            if (string.IsNullOrWhiteSpace(tbSearch.Text) && _browseMode == "search")
            {
                _searchKw = "";
                _browseMode = "list";
                _ = LoadHome(1);
            }
        }

        private void BtnSearch_Click(object sender, RoutedEventArgs e) => _ = DoSearch();
        private void BtnAgg_Click(object sender, RoutedEventArgs e) => _ = DoAggSearch();

        private async Task DoSearch()
        {
            var kw = tbSearch.Text.Trim();
            if (string.IsNullOrEmpty(kw) || _client == null) return;
            _searchKw = kw;
            _browseMode = "search";
            await LoadSearch(1);
        }

        private async Task LoadSearch(int pg)
        {
            if (_client == null) return;
            _homePage = pg;
            var list = await _client.Search(_searchKw, pg);
            if (pg == 1) homePanel.Children.Clear();
            foreach (var v in list) AddCard(v);
            if (pg == 1 && list.Count == 0)
            {
                homeTip.Text = $"「{_searchKw}」在当前源没有结果，试试「聚合搜索」。";
                homeTip.Visibility = Visibility.Visible;
            }
            else homeTip.Visibility = Visibility.Collapsed;
            btnLoadMore.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowHome();
        }

        /// <summary>
        /// 聚合搜索。
        ///
        /// 旧实现慢在哪（三个都改掉了）：
        ///   1) `_sites.Take(30)` —— 取的是**配置顺序的前 30 个**源，里面常混着死源、
        ///      需登录源、HomeContent 为空的网盘源。30 个名额白耗，出结果的自然少。
        ///      → 改为「先按可用性评分排序、剔掉评分为 0 的，再取前 N」。
        ///   2) 没有单源超时 —— 一个卡住的源（JVM 冷启动/死域名 DNS 等待）会把
        ///      整批 `Task.WhenAll` 拖到最后一刻，用户干等到最后才看见任何东西。
        ///      → 每个源独立 8 秒（可在设置里改），超时就当它没结果，不拖累别人。
        ///   3) 全部完成才渲染 —— 明明 2 秒就有源返回了，界面却一直"正在搜索"。
        ///      → 改成**谁先回谁先上图**（增量渲染），并显示"已完成 x/N"。
        /// </summary>
        private async Task DoAggSearch()
        {
            var kw = tbSearch.Text.Trim();
            if (string.IsNullOrEmpty(kw)) { MessageBox.Show("请先输入要搜索的关键词。"); return; }

            // 重复点「聚合搜索」时先掐掉上一轮，否则两批结果会混在一起
            try { _aggCts?.Cancel(); } catch { }
            var cts = new CancellationTokenSource();
            _aggCts = cts;

            _browseMode = "agg";
            ShowList();
            listPanel.Children.Clear();

            int limit = _settings.AggLimit <= 0 ? 30 : _settings.AggLimit;
            var targets = _sites
                .Where(s => s.Searchable && SpiderFactory.PredictAvailable(s))
                .OrderByDescending(SpiderFactory.AvailabilityScore)
                .Take(limit)
                .ToList();

            if (targets.Count == 0)
            {
                listTitle.Text = $"聚合搜索 · {kw}";
                listPanel.Children.Add(Hint("当前配置里没有可搜索的源（源可能都没加载成功）。"));
                return;
            }

            int total = targets.Count;
            int finished = 0;
            int hits = 0;

            var head = new TextBlock
            {
                Text = $"聚合搜索 · {kw} · 0/{total} 个源",
                Foreground = Brushes.Orange,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 4, 0, 4)
            };
            var prog = Hint($"正在并发搜索 {total} 个源（按可用性排序，单个源最多等 {_settings.AggTimeoutSeconds} 秒）…");
            var body = new StackPanel();
            listPanel.Children.Add(head);
            listPanel.Children.Add(prog);
            listPanel.Children.Add(body);

            // 两条闸门：总并发 12；其中 JVM 蜘蛛源额外限 2 —— 每次调用要起一个 java 进程，
            // 并发太多反而互相抢 IO/内存（每个 -Xmx512m）。
            var gate = new SemaphoreSlim(12);
            var jvmGate = new SemaphoreSlim(2);

            var tasks = targets.Select(async s =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                List<VodItem> got = new();
                try
                {
                    await gate.WaitAsync(cts.Token);
                    try
                    {
                        bool isJvm = false;
                        ISiteClient? c = _clients.TryGetValue(s.Key, out var cached) ? cached : null;
                        if (c == null)
                        {
                            c = await WithTimeout(() => SpiderFactory.CreateAsync(s), 12000);
                            if (c == null) return;
                            _clients[s.Key] = c;
                        }
                        isJvm = c is Core.Jar.JarSite;
                        if (!c.Available || !c.Searchable) return;

                        if (isJvm) await jvmGate.WaitAsync(cts.Token);
                        try
                        {
                            var r = await WithTimeout(() => c.Search(kw, 1), _settings.AggTimeoutSeconds * 1000);
                            if (r != null)
                                foreach (var v in r)
                                {
                                    v.SiteKey = s.Key;
                                    v.SiteName = s.Name;
                                    got.Add(v);
                                }
                        }
                        finally { if (isJvm) jvmGate.Release(); }
                    }
                    finally { gate.Release(); }

                    if (got.Count > 0 && !cts.IsCancellationRequested)
                    {
                        sw.Stop();
                        var ms = sw.ElapsedMilliseconds;
                        var name = s.Name;
                        var items = got;
                        Dispatcher.Invoke(() =>
                        {
                            hits++;
                            AppendAggGroup(body, name, items, ms);
                            head.Text = $"聚合搜索 · {kw} · 已出 {hits} 个源 / 共 {total} 个源";
                        });
                    }
                }
                catch { }
                finally
                {
                    var n = Interlocked.Increment(ref finished);
                    if (!cts.IsCancellationRequested)
                        Dispatcher.Invoke(() =>
                        {
                            prog.Text = n >= total
                                ? $"搜索完成：{total} 个源，命中 {hits} 个。"
                                : $"进行中… 已完成 {n}/{total} 个源（有结果会立刻显示在上面）";
                            if (n >= total && hits == 0)
                                body.Children.Add(Hint("所有可搜索的源都没有结果。可换关键词，或改用单源搜索。"));
                        });
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        /// <summary>聚合搜索里一个源的结果分组（增量加进列表）。</summary>
        private void AppendAggGroup(Panel body, string siteName, List<VodItem> items, long elapsedMs)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"{siteName}（{items.Count}）  {elapsedMs / 1000.0:F1}s",
                Foreground = Brushes.Orange,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 12, 0, 4)
            });
            foreach (var v in items)
                body.Children.Add(RowItem(v, () =>
                {
                    var site = _sites.FirstOrDefault(x => x.Key == v.SiteKey);
                    if (site != null) _ = OpenDetail(v.Id, site);
                }));
        }

        /// <summary>给一个任务套上超时。超时后返回 default，并在后台把异常吃掉，避免未处理异常。</summary>
        private static async Task<T?> WithTimeout<T>(Func<Task<T>> work, int ms) where T : class
        {
            Task<T> t;
            try { t = work(); } catch { return null; }
            var done = await Task.WhenAny(t, Task.Delay(ms));
            if (done != t)
            {
                _ = t.ContinueWith(x => { _ = x.Exception; }, TaskScheduler.Default);
                return null;
            }
            try { return await t; } catch { return null; }
        }

        // ===================== 历史 / 收藏 =====================
        private void BtnHistory_Click(object sender, RoutedEventArgs e) => ShowHistory();
        private void BtnFav_Click(object sender, RoutedEventArgs e) => ShowFavorites();

        private void ShowHistory()
        {
            listTitle.Text = $"播放历史（{_store.History.Count}）";
            listPanel.Children.Clear();
            if (_store.History.Count == 0) listPanel.Children.Add(Hint("暂无播放历史。"));
            foreach (var h in _store.History)
                listPanel.Children.Add(RowItem(ToVodItem(h), () => OpenFromHistory(h)));
            ShowList();
        }

        private void ShowFavorites()
        {
            listTitle.Text = $"我的收藏（{_store.Favorites.Count}）";
            listPanel.Children.Clear();
            if (_store.Favorites.Count == 0) listPanel.Children.Add(Hint("还没有收藏。进详情页点「☆ 收藏」即可加入。"));
            foreach (var h in _store.Favorites)
                listPanel.Children.Add(RowItem(ToVodItem(h), () => OpenFromHistory(h)));
            ShowList();
        }

        private static VodItem ToVodItem(HistoryItem h) => new()
        {
            Id = h.VodId,
            Name = h.Name,
            Pic = h.Pic,
            Remarks = h.EpisodeName,
            SiteKey = h.SiteKey,
            SiteName = h.SiteName
        };

        private void OpenFromHistory(HistoryItem h)
        {
            var site = _sites.FirstOrDefault(s => s.Key == h.SiteKey);
            if (site == null) { MessageBox.Show("这条记录对应的源已不在当前配置中。"); return; }
            for (int i = 0; i < cbSites.Items.Count; i++)
                if (cbSites.Items[i] is ComboBoxItem it && it.Tag is SiteEntry se && se.Key == site.Key)
                {
                    _suppressSiteEvent = true;
                    cbSites.SelectedIndex = i;
                    _suppressSiteEvent = false;
                    break;
                }
            _ = OpenDetail(h.VodId, site);
        }

        // ===================== 通用列表行 =====================
        private TextBlock Hint(string text) => new()
        {
            Text = text,
            Foreground = Brushes.Gray,
            FontSize = 13,
            Margin = new Thickness(6, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        private UIElement RowItem(VodItem v, Action onClick)
        {
            var border = new Border
            {
                Margin = new Thickness(4, 3, 4, 3),
                Padding = new Thickness(8),
                Background = CardBg,
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var img = new Image { Width = 48, Height = 66, Stretch = Stretch.Fill };
            ImageLoader.LoadInto(img, v.Pic);
            Grid.SetColumn(img, 0);

            var sp = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = v.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(v.Remarks))
                sp.Children.Add(new TextBlock { Text = v.Remarks, FontSize = 12, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(sp, 1);

            if (!string.IsNullOrEmpty(v.SiteName))
            {
                var tag = new TextBlock { Text = v.SiteName, FontSize = 11, Foreground = Brushes.Orange, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
                Grid.SetColumn(tag, 2);
                g.Children.Add(tag);
            }
            g.Children.Add(img);
            g.Children.Add(sp);
            border.Child = g;
            border.MouseLeftButtonUp += (_, _) => onClick();
            return border;
        }

        // ===================== 播放链路自检（--playsim）=====================
        /// <summary>
        /// 连点三集，把每次的状态条、播放器状态/时长、以及播放页各区块的实际像素写进 playsim.log。
        /// 关键判据：**第 2、3 次点剧集时状态条不能再出现"播放结束。"** ——
        /// 那正是 LibVLC 换媒体时抛的假 EndReached 造成的"点了就播完"。
        /// </summary>
        private async Task RunPlaySimAsync(string kw)
        {
            var log = new List<string>
            {
                "===== TVBox PC 播放页自检（连播三集）=====",
                "时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "关键词：" + kw,
            };
            _headless = true;

            // 配合外部抓图脚本：设了 TBX_SHOT=1 时，在三个关键画面上各停 12 秒，
            // 并在日志里写下标记行 —— 抓图脚本盯日志，看到标记就抓，时间点就是确定的，
            // 不用靠"大概等 25 秒"这种赌运气的写法。
            bool shoot = Environment.GetEnvironmentVariable("TBX_SHOT") == "1";
            async Task ShotAsync(string what)
            {
                if (!shoot) return;
                L($"  [截图] 现在抓「{what}」（停 12 秒）");
                await Task.Delay(12000);
            }

            // ★ 每写一行就落一次盘 —— 自检要是卡在某一步，也能看见"卡在哪一行"，
            //   而不是整份日志都在最后统一写、卡住了就什么都没有。
            void Dump()
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "playsim.log"),
                        string.Join(Environment.NewLine, log));
                }
                catch { }
            }
            void L(string s) { log.Add(s); Dump(); }
            _simLog = L;

            // 自检期间让窗口对鼠标完全不敏感 —— 否则别的程序（比如我在旁边跑的截屏脚本
            // 把窗口置顶）可能让鼠标正好落在某个按钮上，把按钮点了，污染自检结果。
            IsHitTestVisible = false;

            try
            {
                foreach (var url in DefaultConfigs)
                {
                    await LoadConfig(url);
                    if (_sites.Count > 0) break;
                }
                if (_sites.Count == 0) { L("没有加载到任何源，自检结束。"); return; }

                var site = _sites
                    .Where(s => s.Searchable && SpiderFactory.PredictAvailable(s))
                    .OrderByDescending(SpiderFactory.AvailabilityScore)
                    .FirstOrDefault();
                if (site == null) { L("没有可搜索的源，自检结束。"); return; }

                L($"使用源：{site.Name}（{site.Key}）");
                var client = await SpiderFactory.CreateAsync(site);
                _clients[site.Key] = client;
                _site = site;
                _client = client;
                if (!client.Available) { L("该源不可用：" + client.UnavailableReason); return; }

                var hits = await client.Search(kw, 1);
                L($"搜索「{kw}」命中 {hits.Count} 条");
                if (hits.Count == 0) { L("没有结果，自检结束。"); return; }

                // 优先挑"集数 >= 3"的，这样才验证得了切集
                VodDetail? det = null;
                VodItem? pick = null;
                foreach (var v in hits.Take(6))
                {
                    try
                    {
                        var dd = await client.GetDetail(v.Id);
                        if (dd == null) continue;
                        if (det == null) { det = dd; pick = v; }
                        if (dd.Lines.Count > 0 && dd.Lines[0].Episodes.Count >= 3) { det = dd; pick = v; break; }
                    }
                    catch { }
                }
                if (det == null) { L("打开详情失败，自检结束。"); return; }

                await OpenDetail(pick!.Id, site);
                await Task.Delay(500);
                if (_detail == null || _episodes.Count == 0) { L("详情里没有选集，自检结束。"); return; }

                L("");
                L($"影片：{_detail.Name}   线路 {_lines.Count} 条   选集 {_episodes.Count} 集");

                // ---- 连播三集：重点看第 2、3 次会不会假报"播放结束" ----
                int n = Math.Min(3, _episodes.Count);
                for (int i = 0; i < n; i++)
                {
                    var ep = _episodes[i];
                    L("");
                    L($"【第 {i + 1} 次点剧集】{ep.Name}    地址：{Cut(ep.Url, 110)}");
                    Play(ep.Url, ep.Name, false, i);
                    await Task.Delay(6000);
                    L($"   状态条：{(playerStatus.Text.Length == 0 ? "(空 = 正常播放中)" : playerStatus.Text)}");
                    L($"   播放器：State={_mp?.State}  IsPlaying={_mp?.IsPlaying}  Length={_mp?.Length}ms  Time={_mp?.Time}ms");
                    L($"   高亮集下标 = {_playingEpIndex}（应为 {i}）   提示行 = {pvEpHint.Text}");
                }

                // ---- 播放页布局：这时 pnlPlayer 已经可见，量出来的才是真实布局 ----
                await Task.Delay(400);
                L("");
                L("【布局】播放页各区块的实际位置与尺寸（播放中量取）");
                L($"   播放页可见 = {pnlPlayer.Visibility}");
                L($"   视频区   {videoView.ActualWidth:F0} x {videoView.ActualHeight:F0}   位于 {Pos(videoView)}");
                L($"   信息区   {pvInfoPane.ActualWidth:F0} x {pvInfoPane.ActualHeight:F0}   位于 {Pos(pvInfoPane)}");
                L($"   选集区   {playBottom.ActualWidth:F0} x {playBottom.ActualHeight:F0}   位于 {Pos(playBottom)}");
                L($"   标题：{Cut(pvTitle.Text, 46)}");
                L($"   信息行：{Cut(pvMeta.Text, 100)}");
                L($"   演员：{Cut(pvActor.Text, 70)}");
                L($"   源标签：{pvSourceTag.Text}   线路按钮 {pvLines.Children.Count} 个   选集卡片 {playEpisodes.Children.Count} 个   分页按钮 {pvGroups.Children.Count} 个");
                L("   按钮排：" + string.Join(" / ", new[] { btnFullScreen, btnQuickSearch, btnIntro, btnFavToggle2, btnSwitchSrc, btnDlCurEp, btnCopyUrl }
                    .Select(b => b.Content?.ToString())));
                L($"   按钮排实际 {pvBtns.Children.Count} 个（应为 7：全屏/快速搜索/简介/收藏/切源/下载本集/复制地址）");
                L($"   按钮条可用宽度 = 信息栏 {pvInfoPane.ActualWidth:F0} - 按钮条左右 padding 24 = {pvInfoPane.ActualWidth - 24:F0}px（4×(84+6)=360，正好 4 个一行）");
                // 按钮排：谁在第几行、右边界到哪，量化出来才看得出"有没有被裁掉 / 右侧空多少"
                string BtnBox(Button b)
                {
                    try
                    {
                        var p = b.TransformToAncestor(pvBtns).Transform(new Point(0, 0));
                        return $"[{b.Content}]{b.ActualWidth:F0}px@{p.X:F0}(右{p.X + b.ActualWidth:F0})";
                    }
                    catch { return $"[{b.Content}]?"; }
                }
                L("   按钮排布局：" + string.Join("  ", pvBtns.Children.OfType<Button>().Select(BtnBox)));
                // 光有"在第几行"还不够 —— 信息栏是个滚动区，内容比它高就会被卷到下面看不见，
                // 所以要把"按钮排整体有没有落在可视高度里"也算出来。
                double btnBottom = 0, btnTop = 0;
                try
                {
                    var bp = pvBtns.TransformToAncestor(pvInfoPane).Transform(new Point(0, 0));
                    btnTop = bp.Y;
                    btnBottom = bp.Y + pvBtns.ActualHeight;
                }
                catch { }
                L($"   信息栏可视高度 {pvInfoPane.ActualHeight:F0}px   按钮排整体 {pvBtns.ActualWidth:F0}x{pvBtns.ActualHeight:F0} 位于 Y={btnTop:F0}（底边 {btnBottom:F0}）");
                L($"   → 按钮排{(btnBottom <= pvInfoPane.ActualHeight ? "完整落在可视区内，不用滚" : "有部分被卷到下面了，得往下滚才看得见")}");
                L($"   控制条按钮：上一集[{btnPrevEp.Content}] 播放[{btnPlayPause.Content}] 下一集[{btnNextEp.Content}] 全屏[{btnFullScreen2.Content}]");
                L($"   视频画面可见 = {videoView.Visibility}（正常播放时应为 Visible）");
                await ShotAsync("播放页：视频左上 / 信息右侧 / 选集下方 / 7 个按钮 / 底部控制条");

                // ---- 下载伸缩栏：收起后播放页应该跟着变宽 ----
                L("");
                L("【下载伸缩栏】点把手收起 / 展开，看播放页是否跟着变宽");
                L($"   初始：  下载栏宽 {dlCol.Width.Value:F0}   播放页实际宽 {pnlPlayer.ActualWidth:F0}   把手[{dlHandleArrow.Text}]");
                ToggleDlPane();
                await Task.Delay(400);
                L($"   收起后：下载栏宽 {dlCol.Width.Value:F0}   播放页实际宽 {pnlPlayer.ActualWidth:F0}   把手[{dlHandleArrow.Text}]");
                ToggleDlPane();
                await Task.Delay(400);
                L($"   再展开：下载栏宽 {dlCol.Width.Value:F0}   播放页实际宽 {pnlPlayer.ActualWidth:F0}   把手[{dlHandleArrow.Text}]");

                // ---- 播放控制条：每个控件都真点一遍，看播放器有没有反应 ----
                L("");
                L("【播放控制条】");
                L($"   可见 = {playBar.Visibility}   实际高度 {playBar.ActualHeight:F0}px");

                L("   → 正在点「暂停」…");
                BtnPlayPause_Click(this, new RoutedEventArgs());
                await Task.Delay(1600);      // 控制条每秒刷一次，等够一拍再读按钮图标，否则读到的是旧值
                L($"   点「暂停」  → State={_mp?.State} IsPlaying={_mp?.IsPlaying} 按钮[{btnPlayPause.Content}]");
                L("   → 正在点「继续」…");
                BtnPlayPause_Click(this, new RoutedEventArgs());
                await Task.Delay(1600);
                L($"   再点一次    → State={_mp?.State} IsPlaying={_mp?.IsPlaying} 按钮[{btnPlayPause.Content}]");
                L($"   时间显示：{tbTime.Text}   进度条 {slSeek.Value:F0}/1000   音量 {slVolume.Value:F0}   倍速档 {cbSpeed.SelectedIndex}");

                L("   → 正在把音量拉到 40…");
                slVolume.Value = 40;
                await Task.Delay(400);
                L($"   音量拉到 40 → 播放器 Volume={_mp?.Volume}");

                L("   → 正在把倍速切到 1.5x…");
                cbSpeed.SelectedIndex = 4;      // 1.5x
                await Task.Delay(600);
                L($"   倍速选 1.5x → 播放器 Rate={_mp?.Rate}");

                // 每一步前面都先写一行「→ 正在…」：万一某步把界面卡死，
                // 日志最后一行就能告诉我们是卡在哪一步，而不是只有上一步的结果。
                L("   → 正在拖进度条到 30%…（以前就是这一步把窗口卡死的：set_time 与事件回调互等）");
                slSeek.Value = 300;             // 30%
                ApplySeek();
                await Task.Delay(1800);
                L($"   进度拖到 30% → 播放器 Time={_mp?.Time}ms（总长 {_mp?.Length}ms）");
                cbSpeed.SelectedIndex = 2;      // 还原 1.0x

                L("   → 正在点「下一集」…");
                BtnNextEp_Click(this, new RoutedEventArgs());
                await Task.Delay(3500);
                L($"   点「下一集」→ 现在第 {_playingEpIndex + 1} 集" +
                        $"（{(_playingEpIndex >= 0 && _playingEpIndex < _episodes.Count ? _episodes[_playingEpIndex].Name : "?")}）" +
                        $"  State={_mp?.State}  Length={_mp?.Length}ms");

                // 全屏：控制条要留着，信息栏/选集区/下载栏要收起来
                L("   → 正在进全屏…");
                BtnFullScreen_Click(this, new RoutedEventArgs());
                await Task.Delay(1500);
                L($"   全屏后：控制条={playBar.Visibility}(高 {playBar.ActualHeight:F0})  信息栏={pvInfoPane.Visibility}  " +
                        $"选集区={playBottom.Visibility}  下载栏宽={dlCol.Width.Value:F0}  控制条全屏键[{btnFullScreen2.Content}]");
                L($"   全屏视频区 {videoView.ActualWidth:F0} x {videoView.ActualHeight:F0} @{Pos(videoView)}（要铺满整屏宽，右侧不留空档）");
                L($"   列宽：视频[{vidCol.Width}] 分隔[{vidSepCol.Width}] 信息[{infoCol.Width}]  视频列 MinWidth={vidCol.MinWidth:F0} 信息列 MinWidth={infoCol.MinWidth:F0}");
                await ShotAsync("全屏：控制条仍在（进度/暂停/音量/倍速），下载栏收起，右侧不再空一块");
                L("   → 正在退出全屏…");
                BtnFullScreen_Click(this, new RoutedEventArgs());
                await Task.Delay(1500);
                L($"   退出全屏：控制条={playBar.Visibility}  信息栏={pvInfoPane.Visibility}  " +
                        $"下载栏宽={dlCol.Width.Value:F0}  全屏键[{btnFullScreen2.Content}]");
                L($"   退出后视频区 {videoView.ActualWidth:F0} x {videoView.ActualHeight:F0}   列宽：视频[{vidCol.Width}] 信息[{infoCol.Width}] MinWidth {vidCol.MinWidth:F0}/{infoCol.MinWidth:F0}");

                // ---- 源下拉框配色（以前是白底浅灰字，看不清）----
                L("");
                L("【源下拉框配色】");
                L($"   cbSites  Style={(cbSites.Style == null ? "无" : "已套用 DarkCombo")}   下拉项 {cbSites.Items.Count} 个");
                var fgB = cbSites.Foreground as SolidColorBrush;
                var bgB = cbSites.Background as SolidColorBrush;
                L("   字色 " + (fgB == null ? "?" : $"#{fgB.Color.R:X2}{fgB.Color.G:X2}{fgB.Color.B:X2}")
                        + "   底色 " + (bgB == null ? "?" : $"#{bgB.Color.R:X2}{bgB.Color.G:X2}{bgB.Color.B:X2}")
                        + "（深底亮字才算修好）");

                // ---- 切源：拿剧名去别的源里找同一部剧 ----
                L("");
                L($"【切源】用「{_detail.Name}」去其它源里找…");
                await RunSwitchSearchAsync();
                L($"   弹窗可见 = {dlgSwitchSrc.Visibility}   列出候选 {swBody.Children.Count} 条");
                L($"   ★ 此时视频画面必须收起（HwndHost 永远盖住 WPF 弹窗）：videoView = {videoView.Visibility}（应为 Collapsed）");
                L($"   提示行：{swHead.Text}");
                await ShotAsync("切源弹窗：视频画面必须让位（HwndHost 挡弹窗）");
                ShowOverlay(dlgSwitchSrc, false);
                L($"   关掉弹窗后 videoView = {videoView.Visibility}（应恢复 Visible）");

                L("");
                L("【按钮可用性】");
                L($"   全屏={btnFullScreen.Content}  快速搜索={btnQuickSearch.Content}  简介={btnIntro.Content}  收藏={btnFavToggle2.Content}  切源={btnSwitchSrc.Content}");
                L($"   下载本集(信息栏)={btnDlCurEp.Content}  复制地址={btnCopyUrl.Content}  下载本集(选集栏)={btnPvDlCur.Content}");
                L($"   下载整部={btnPvDlAll.Content}  倒序={btnPvOrder.Content}");
                L($"   播放页返回={btnBackPlayer.Content}  下载面板={btnPlayerDlPanel.Content}  切源弹窗={dlgSwitchSrc.Visibility}");

                L("");
                L("=== 自检结束 ===");
            }
            catch (Exception ex) { L("自检出错：" + ex); }
            finally
            {
                try { _mp?.Stop(); } catch { }
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "playsim.log"),
                        string.Join(Environment.NewLine, log));
                }
                catch { }
                try { _libvlc?.Dispose(); } catch { }
                Environment.Exit(0);
            }
        }

        /// <summary>控件在父容器里的左上角坐标（用来看"视频是不是真在左上、选集是不是真在下方"）。</summary>
        private static string Pos(FrameworkElement el)
        {
            try
            {
                if (el.Parent is not Visual parent) return "?";
                var p = el.TransformToAncestor(parent).Transform(new Point(0, 0));
                return $"{p.X:F0},{p.Y:F0}";
            }
            catch { return "?"; }
        }

        private static string Cut(string s, int n)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

        // ===================== 下载 =====================

        /// <summary>命令行自检模式：此时不弹任何对话框（否则无人值守的自检会卡在 MessageBox 上）。</summary>
        private bool _headless;
        private Action<string>? _simLog;

        private void Info(string msg, string title = "下载", MessageBoxImage icon = MessageBoxImage.Information)
        {
            if (_headless) { _simLog?.Invoke($"[对话框·{title}] " + msg.Replace("\n", " ⏎ ")); return; }
            MessageBox.Show(msg, title, MessageBoxButton.OK, icon);
        }

        private bool Confirm(string msg, string title)
        {
            if (_headless) { _simLog?.Invoke($"[对话框·{title}·自动确认] " + msg.Replace("\n", " ⏎ ")); return true; }
            return MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        /// <summary>
        /// `--dlsim`：用真实 GUI 走一遍猫抓式的完整下载链路，把每一步结果写 dlcheck.log。
        /// 覆盖：界面元素 → 解析地址（真跑网络）→ 勾选选集 → 提交下载 → 暂停（确认停在分片边界）→
        /// 继续（确认是接着下而不是重头来）→ 命令导出 → 新建下载（手填 Referer + 分片范围）。
        /// 这是对**真实二进制**做端到端验证的唯一干净手段（GUI 点不到按钮时靠它）。
        /// </summary>
        private async Task RunDownloadSimAsync()
        {
            var log = new StringBuilder();
            void L(string s)
            {
                log.AppendLine(s);
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "dlcheck.log"), log.ToString()); } catch { }
            }

            _headless = true;      // 自检无人值守：所有对话框改成写日志
            _simLog = L;

            const string m3u8 = "https://s3.bfllvip.com/video/qingyuniandiyiji/136c9bdde962/index.m3u8";

            // 自检产物丢到临时目录，不要污染用户的下载夹
            _dl.RootDir = Path.Combine(Path.GetTempPath(), "TVBoxPC-dlsim");
            L($"自检下载目录 = {_dl.RootDir}");

            _detail = new VodDetail { Id = "sim", Name = "下载链路自检" };
            _detailSiteKey = "sim";
            _detailSiteName = "自检";
            _episodes = new List<Episode>
            {
                new() { Name = "第01集", Url = m3u8 },
                new() { Name = "第02集带Referer", Url = m3u8 + "|Referer=https://s3.bfllvip.com/" },
                new() { Name = "第03集", Url = m3u8 },
            };
            _lines = new List<PlayLine> { new() { Name = "自检线路", Episodes = _episodes } };
            SelectLine(0);
            ShowHome();

            L("=== 下载链路自检（--dlsim，v1.6）===");
            L("");

            // ---------- 1 界面元素 ----------
            L("【1】界面元素");
            // 用 ReferenceEquals 判空：写成 `== null` 会让编译器的可空流分析把字段标成"可能为空"，
            // 后面正常的 `dlgNewDl.Visibility` 就会冒出 CS8602 警告。
            L($"   新建下载按钮 btnNewDl = {(ReferenceEquals(btnNewDl, null) ? "缺" : "有")}");
            L($"   新建下载对话框 dlgNewDl = {(ReferenceEquals(dlgNewDl, null) ? "缺" : "有")}（应默认隐藏，实际 {dlgNewDl!.Visibility}）");
            L($"   命令对话框 dlgCmds = {(ReferenceEquals(dlgCmds, null) ? "缺" : "有")}");
            L($"   外部引擎 = {_dl.EnginePath ?? "未携带（走内置下载器）"}");
            L($"   本机 ffmpeg = {_dl.FfmpegPath ?? "没找到（选 mp4 时会保留 .ts 并提示）"}");
            L("");

            // ---------- 2 解析地址 ----------
            L("【2】解析地址 PreviewAsync（真跑网络）");
            var pv = await _dl.PreviewAsync(m3u8);
            L($"   ok={pv.Ok} 分片={pv.SegmentCount} 时长={HlsParser.FormatDuration(pv.Duration)} " +
              $"加密={pv.Encrypted} byterange={pv.HasByteRange} 产物={pv.Ext}");
            L($"   多码率={pv.IsMaster} 档数={pv.Variants.Count}");
            L($"   摘要：{pv.Message}");
            L("");

            // ---------- 3 选集与勾选 ----------
            L("【3】选集与勾选");
            L($"   选集 chip 数 = {rightEpisodes.Children.Count}（应为 3）");
            await Task.Delay(800);   // 等布局算完
            L($"   选集区可视高度   = {epScroll.ActualHeight:F0} px");
            L($"   下载任务区可视高度 = {dlScroll.ActualHeight:F0} px   ★ 老版这里会是 0，任务完全看不见");

            var chip0 = rightEpisodes.Children.OfType<Border>().FirstOrDefault();
            if (chip0 == null) { L("失败：没有生成选集 chip"); Application.Current.Shutdown(); return; }
            chip0.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            await Task.Delay(250);
            L($"   点第 1 集的文字区域 -> {tbSelInfo.Text}   勾选数={SelectedEpisodes().Count}");
            if ((rightEpisodes.Children.OfType<Border>().ElementAtOrDefault(1)?.Child as StackPanel)
                ?.Children.OfType<CheckBox>().FirstOrDefault() is { } cb2)
                cb2.IsChecked = true;
            await Task.Delay(250);
            L($"   再勾第 2 集 -> {tbSelInfo.Text}   勾选数={SelectedEpisodes().Count}");
            L("");

            // ---------- 4 「下载选中」链路 ----------
            L("【4】「下载选中」链路（勾选已在【3】做完，这里提交它）");
            BtnDlSelected_Click(this, new RoutedEventArgs());
            await Task.Delay(500);
            var picked = _dl.Tasks.ToList();
            L($"   入队后任务数 = {picked.Count}   面板行数 = {dlTasks.Children.Count}   摘要 = {tbDlSummary.Text}");
            L($"   第 1 条 = {picked.FirstOrDefault()?.Name}  state={picked.FirstOrDefault()?.State}");

            // 这两条都是整集（2705 段 / 500MB+）。全部停掉：① 别让它把队列堵住、把带宽和磁盘吃掉；
            // ② 顺便验证「运行中暂停」和「排队中暂停」两条路径。
            foreach (var t in picked) _dl.Pause(t);
            await Task.Delay(800);
            L("   把它们都停掉 -> " + string.Join(" / ", picked.Select(x => $"{x.Name}={x.State}")));
            L("   （它们会一直挂着，用来验证「已暂停」状态的按钮）");
            L("");

            // ---------- 5 新建下载（手填头 + 范围 + 留档）----------
            L("【5】新建下载（手填 Referer、只下第 1~400 个分片、并发 8、保存原始 m3u8）");
            BtnNewDl_Click(this, new RoutedEventArgs());
            tbNewUrl.Text = m3u8;
            tbNewReferer.Text = "https://s3.bfllvip.com/";
            tbNewName.Text = "自检-范围下载";
            tbNewFolder.Text = "下载链路自检";
            tbNewFromSeg.Text = "1";
            tbNewToSeg.Text = "400";
            tbNewConc.Text = "8";
            cbNewSavePl.IsChecked = true;
            L($"   对话框已弹出 = {dlgNewDl.Visibility}");
            BtnNewDlOk_Click(this, new RoutedEventArgs());
            await Task.Delay(400);

            var manual = _dl.Tasks.FirstOrDefault(x => x.Name == "自检-范围下载");
            L($"   已入队 = {manual != null}   任务总数 = {_dl.Tasks.Count}");
            if (manual != null)
                L($"   对话框已收起 = {dlgNewDl.Visibility}（应为 Collapsed）" +
                  $"   手填头 = {string.Join(", ", manual.Options.ExtraHeaders?.Keys ?? Enumerable.Empty<string>())}");
            L($"   任务面板行数 = {dlTasks.Children.Count}");
            L("");

            var first = manual ?? _dl.Tasks.FirstOrDefault();

            // ---------- 5 观察进度 ----------
            L("【6】观察进度（速度 / 剩余时间 / 分片计数）");
            for (int i = 1; i <= 6; i++)
            {
                await Task.Delay(2000);
                L($"   t+{i * 2,2}s 行数={dlTasks.Children.Count} 摘要={tbDlSummary.Text}  " +
                  $"state={first?.State} pct={first?.Percent} 段={first?.DoneSegments}/{first?.TotalSegments} " +
                  $"速度={first?.SpeedBps / 1024:F0}KB/s 剩余={HlsParser.FormatDuration(first?.EtaSeconds ?? 0)} " +
                  $"已写={first?.Bytes / 1024.0 / 1024.0:F2}MB");
            }
            L("");

            // ---------- 6 暂停 ----------
            L("【7】暂停（应停在分片边界，已落盘分片保留）");
            int pausedAt = 0;
            if (first != null)
            {
                _dl.Pause(first);
                await Task.Delay(1500);
                pausedAt = first.ResumeFromSegment;
                L($"   暂停后 state={first.State} 已落盘分片={first.ResumeFromSegment} 字节={first.Bytes} 进度={first.Percent}%");
                L($"   该行显示的按钮：{DescribeTaskButtons(first)}");
            }
            L("");

            // ---------- 7 继续 ----------
            L("【8】继续（应从续传点接着下，不重头来）");
            if (first != null)
            {
                _dl.Resume(first);
                await Task.Delay(6000);
                L($"   继续后 state={first.State} 段={first.DoneSegments}/{first.TotalSegments} 进度={first.Percent}%");
                L($"   续传判据：暂停时 {pausedAt} 段 → 现在 {first.DoneSegments} 段，" +
                  $"{(first.DoneSegments > pausedAt ? "✓ 确实接着下" : first.State == DlState.Done ? "✓ 已完成（任务本来就很短）" : "✗ 没有推进")}");
            }
            L("");

            // ---------- 8 命令导出 ----------
            L("【9】外部工具命令导出（猫抓式「复制命令」）");
            if (first != null)
            {
                ShowCommands(first);
                var txt = tbCmds.Text;
                L($"   命令框可见={dlgCmds?.Visibility} 文本长度={txt.Length}");
                foreach (var tool in new[] { "ffmpeg", "N_m3u8DL-RE", "aria2c" })
                    L($"   含 {tool}：{txt.Contains(tool)}");
                L($"   含 Referer：{txt.Contains("Referer")}");
                L($"   含分片地址：{txt.Contains(".ts") || txt.Contains(".m3u8")}");
                var ff = txt.Split('\n').FirstOrDefault(x => x.StartsWith("ffmpeg"));
                L($"   ffmpeg 行：{(ff == null ? "（没有）" : ff.Substring(0, Math.Min(150, ff.Length)) + "…")}");
                dlgCmds!.Visibility = Visibility.Collapsed;
            }
            L("");

            // ---------- 9 等范围任务收尾 ----------
            L("【10】等「范围下载」任务收尾（最多 90 秒）");
            for (int i = 0; i < 45 && manual != null
                 && manual.State is DlState.Running or DlState.Queued or DlState.Paused; i++)
                await Task.Delay(2000);

            if (manual != null)
            {
                L($"   最终 state={manual.State} 进度={manual.Percent}% 段={manual.DoneSegments}/{manual.TotalSegments} " +
                  $"跳过={manual.SkippedSegments}");
                L($"   产物={manual.OutputPath}");
                if (!string.IsNullOrEmpty(manual.OutputPath) && File.Exists(manual.OutputPath))
                {
                    var fi = new FileInfo(manual.OutputPath);
                    L($"   产物大小={fi.Length / 1024.0 / 1024.0:F2}MB   ★ 只下了 400 段，应明显小于整集");
                }
                if (!string.IsNullOrEmpty(manual.SidecarPath) && File.Exists(manual.SidecarPath))
                    L($"   原始 m3u8 已留档 = {manual.SidecarPath}（{new FileInfo(manual.SidecarPath).Length} 字节）");
                L($"   提示：{manual.Warning ?? "-"}");
                L($"   错误：{manual.Error ?? "-"}");
            }

            // ---------- 10 任务行按钮映射 ----------
            L("");
            L("【11】各状态下的按钮映射（状态机 → 界面）");
            foreach (var t in _dl.Tasks) L($"   {t.Name}：state={t.State} → {DescribeTaskButtons(t)}");

            // ---------- 12 失败状态与重试 ----------
            L("");
            L("【12】失败状态与重试（拿一个必然连不上的地址）");
            var badTask = _dl.AddManual("自检-必然失败", "http://127.0.0.1:9/nope.m3u8", new DlOptions(), "下载链路自检");
            for (int i = 0; i < 20 && badTask.State is DlState.Queued or DlState.Running; i++) await Task.Delay(1000);
            L($"   state={badTask.State}  错误={badTask.Error ?? "-"}");
            L($"   按钮：{DescribeTaskButtons(badTask)}");
            if (badTask.State == DlState.Failed)
            {
                _dl.Retry(badTask);
                await Task.Delay(1000);
                L($"   点「重试」后 state={badTask.State}（应回到 Queued/Running）");
            }

            _dl.CancelAll();
            L("");
            L("=== 自检结束（已取消未完成的下载）===");
            Application.Current.Shutdown();
        }

        /// <summary>
        /// 自检用：把某条任务在界面上**实际渲染出来**的按钮文案列出来
        /// （验证「状态 → 按钮」的映射，不用肉眼看界面）。
        /// </summary>
        private string DescribeTaskButtons(DownloadManager.TaskInfo t)
        {
            _dlSelected = t;
            _taskSig = "";
            RefreshTasks();

            foreach (var b in dlTasks.Children.OfType<Border>())
            {
                if (b.Child is not StackPanel sp) continue;
                var head = sp.Children.OfType<Grid>().FirstOrDefault();
                var titleTb = head?.Children.OfType<TextBlock>().FirstOrDefault();
                if (titleTb == null || !titleTb.Text.Contains(t.Name)) continue;
                var wp = sp.Children.OfType<WrapPanel>().FirstOrDefault();
                if (wp != null)
                    return string.Join(" / ", wp.Children.OfType<Button>().Select(x => x.Content?.ToString() ?? ""));
            }
            return "（界面上没找到该行）";
        }

        private void BtnDlAll_Click(object sender, RoutedEventArgs e)
        {
            if (_episodes.Count == 0)
            {
                Info("还没有加载出选集。\n请先打开一部剧（中间出现选集列表），右侧面板才会列出可下载的分集。",
                    "暂无可下载的选集");
                return;
            }
            // 一部剧动辄几百集、一集几百 MB —— 直接开下很容易把磁盘填满，先问一句
            if (_episodes.Count > 3 &&
                !Confirm($"将下载全部 {_episodes.Count} 集，一集可能几百 MB（整部可能上百 GB），也会花很长时间。\n\n确定继续吗？",
                    "下载整部"))
                return;
            EnqueueDownloads(_episodes.Select(x => (x.Name, x.Url)).ToList(), $"全集 {_episodes.Count} 集");
        }

        private void BtnDlSelected_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedEpisodes();
            if (sel.Count == 0)
            {
                Info("还没有勾选分集。\n\n在右侧「本剧选集」里点一下分集即可勾选（不用去点那个小方框），\n或直接点「全选」。",
                    "请先勾选要下载的分集");
                return;
            }
            EnqueueDownloads(sel.Select(x => (x.Name, x.Url)).ToList(), $"选中 {sel.Count} 集");
        }

        /// <summary>
        /// 提交下载。★ 关键点：提交后**立刻**刷新任务面板并给出明确反馈。
        /// 旧实现提交完什么都不做（只等 1 秒的定时器），加上右栏布局把任务区压成 0 高度，
        /// 用户点完完全看不到任何变化，就是「点了没反应」。
        /// </summary>
        private void EnqueueDownloads(List<(string name, string url)> items, string what)
        {
            // 先把根本下不了的剔出去（网盘链接 / 磁力 / 网页地址）——
            // 否则它们只会变成一排永远失败的任务，看着像程序坏了。
            var usable = new List<(string name, string url)>();
            int unusable = 0;
            foreach (var (n, u) in items)
                if (MediaUrl.Parse(u).Kind == MediaKind.Direct) usable.Add((n, u));
                else unusable++;

            if (usable.Count == 0)
            {
                Info(
                    $"{what}里没有可以直接下载的地址。\n\n" +
                    "常见原因：这个源给的是网盘链接 / 磁力链接 / 需要解析的网页地址，\n" +
                    "程序无法直接下载（这类要先用网盘客户端或解析接口拿到真实流）。\n" +
                    "换个线路或换个源试试。",
                    "无法下载", MessageBoxImage.Warning);
                return;
            }

            var name = _detail?.Name ?? "未命名";
            int added = _dl.Enqueue(DownloadManager.Sanitize(name), usable);
            _taskSig = "";                       // 强制下一次重绘
            RefreshTasks();                      // 不等定时器
            bool full = false;
            long free = 0;
            try
            {
                var drv = new DriveInfo(Path.GetPathRoot(_dl.RootDir) ?? "C:" + Path.DirectorySeparatorChar);
                free = drv.AvailableFreeSpace;
                full = free < 2L * 1024 * 1024 * 1024;   // 剩不到 2GB 提个醒
            }
            catch { }

            var msg = added == 0
                ? $"{what}已经在下载队列里了（未重复添加）。"
                : $"已加入 {added} 个下载任务。";
            if (unusable > 0) msg += $"\n\n另有 {unusable} 集是网盘/磁力等无法直接下载的地址，已跳过。";
            if (full) msg += $"\n\n⚠ 磁盘剩余空间只有 {free / 1024.0 / 1024 / 1024:F1} GB，下载整部很容易写满。";
            msg += $"\n\n保存到：{_dl.RootDir}";

            tbDlSummary.Text = added > 0 ? $"⟵ 刚加入 {added} 个任务" : tbDlSummary.Text;
            Info(msg, "下载");
        }

        private void BtnOpenDlDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_dl.RootDir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dl.RootDir}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("打开下载目录失败：" + ex.Message); }
        }

        private void BtnClearDl_Click(object sender, RoutedEventArgs e)
        {
            _dl.ClearFinished();
            _taskSig = "";
            RefreshTasks();
        }

        private void BtnCancelDl_Click(object sender, RoutedEventArgs e)
        {
            _dl.CancelAll();
            _taskSig = "";
            RefreshTasks();
        }

        /// <summary>任务面板签名：内容没变就不重建（否则每秒重建一次会让滚动位置乱跳、界面闪）。</summary>
        private string _taskSig = "";
        /// <summary>下载面板里选中的任务（「命令」按钮导出命令行时用）。</summary>
        private DownloadManager.TaskInfo? _dlSelected;
        private bool _dlRefreshPending;

        /// <summary>下载器状态变化 → 合并到一次 UI 刷新（下载线程每秒会触发好几次，不能每次都重建面板）。</summary>
        private void OnDlChanged()
        {
            if (_dlRefreshPending) return;
            _dlRefreshPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _dlRefreshPending = false;
                RefreshTasks();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RefreshTasks()
        {
            var tasks = _dl.Tasks;

            int total = tasks.Count;
            int done = tasks.Count(t => t.State == DlState.Done);
            int running = tasks.Count(t => t.State == DlState.Running);
            int paused = tasks.Count(t => t.State == DlState.Paused);
            int failed = tasks.Count(t => t.State == DlState.Failed);

            tbDlSummary.Text = total == 0
                ? ""
                : $"（{done}/{total}"
                  + (running > 0 ? " · 下载中" : paused > 0 ? " · 已暂停" : _dl.IsBusy ? " · 准备中" : " · 已结束")
                  + (failed > 0 ? $" · {failed} 失败" : "")
                  + "）";
            btnCancelDl.IsEnabled = _dl.IsBusy || paused > 0;
            btnClearDl.IsEnabled = done + failed > 0;

            // 签名里带上速度（KB 取整）：这样「速度 / 剩余时间」是活的，而不用每次都白重建
            var sb = new StringBuilder();
            foreach (var t in tasks)
                sb.Append(t.Name).Append('|').Append(t.State).Append('|').Append(t.Percent)
                  .Append('|').Append(t.Error).Append('|').Append(t.Warning).Append('|')
                  .Append(t.DoneSegments).Append('|').Append((long)(t.SpeedBps / 1024)).Append(';');
            var sig = sb.ToString();
            if (sig == _taskSig) return;
            _taskSig = sig;

            var scrollOff = dlScroll.VerticalOffset;
            dlTasks.Children.Clear();
            foreach (var t in tasks) dlTasks.Children.Add(BuildTaskRow(t));

            if (tasks.Count == 0)
                dlTasks.Children.Add(new TextBlock
                {
                    Text = "还没有下载任务。\n\n· 中间打开一部剧 → 右侧出现选集 → 勾选后点「下载选中」\n" +
                           "· 或者点上面的「+ 新建下载」，直接把 m3u8 / mp4 地址粘进来",
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap
                });

            // 重建后把滚动位置放回去（WPF 重建内容会把 ScrollViewer 顶回顶部）
            Dispatcher.BeginInvoke(new Action(() => dlScroll.ScrollToVerticalOffset(scrollOff)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>造一个下载任务卡片（可点击选中，「命令」按钮用选中的那条）。</summary>
        private Border BuildTaskRow(DownloadManager.TaskInfo t)
        {
            bool selected = ReferenceEquals(t, _dlSelected);

            var card = new Border
            {
                Background = selected ? SelBg : CardBg,
                BorderBrush = selected ? Brushes.Orange : CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 8, 9, 8),
                Margin = new Thickness(0, 0, 0, 8),
            };
            var row = new StackPanel();
            card.Child = row;
            card.MouseLeftButtonUp += (_, _) =>
            {
                _dlSelected = t;
                _taskSig = "";
                RefreshTasks();
            };

            // ---- 标题行：图标 + 名称 + 状态 ----
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var (icon, color) = t.State switch
            {
                DlState.Running => ("▶ ", Brushes.White),
                DlState.Queued => ("… ", Brushes.Silver),
                DlState.Paused => ("⏸ ", Brushes.Orange),
                DlState.Done => ("✓ ", Brushes.LightGreen),
                DlState.Failed => ("✕ ", Brushes.OrangeRed),
                _ => ("⊘ ", Brushes.Gray),
            };

            var title = new TextBlock
            {
                Text = icon + t.Name,
                FontSize = 12,
                Foreground = color,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = t.CleanUrl.Length > 0 ? t.CleanUrl : t.Url,
            };
            Grid.SetColumn(title, 0);

            var state = new TextBlock
            {
                Text = t.StateText,
                FontSize = 12,
                Foreground = t.State == DlState.Failed ? Brushes.OrangeRed : Brushes.Orange,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(state, 1);
            head.Children.Add(title);
            head.Children.Add(state);
            row.Children.Add(head);

            // ---- 进度条 ----
            row.Children.Add(new ProgressBar
            {
                Height = 6,
                Maximum = 100,
                Value = t.State == DlState.Done ? 100 : t.Percent,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = t.State == DlState.Failed ? Brushes.OrangeRed : Brushes.Orange,
                Background = Brushes.DimGray,
            });

            // ---- 详情行：大小 · 速度 · 剩余 · 分段 ----
            var bits = new List<string>();
            if (t.Bytes > 0) bits.Add(HumanSize(t.Bytes));
            if (t.State == DlState.Running && t.SpeedBps > 1024)
            {
                bits.Add(HumanSize((long)t.SpeedBps) + "/s");
                var eta = t.EtaSeconds;
                if (eta > 0) bits.Add("剩余 " + HlsParser.FormatDuration(eta));
            }
            if (t.TotalSegments > 0)
                bits.Add($"分段 {t.DoneSegments}/{t.TotalSegments}" +
                         (t.SkippedSegments > 0 ? $"（跳过 {t.SkippedSegments}）" : ""));
            if (!string.IsNullOrEmpty(t.VariantLabel)) bits.Add(t.VariantLabel!);
            if (t.State == DlState.Paused && t.ResumeFromSegment > 0)
                bits.Add($"已存 {t.ResumeFromSegment} 段，可继续");

            if (bits.Count > 0)
                row.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", bits),
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });

            if (!string.IsNullOrEmpty(t.Warning))
                row.Children.Add(new TextBlock
                {
                    Text = "⚠ " + t.Warning,
                    FontSize = 11,
                    Foreground = Brushes.Goldenrod,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });

            if (!string.IsNullOrEmpty(t.Error))
            {
                row.Children.Add(new TextBlock
                {
                    Text = t.Error,
                    FontSize = 11,
                    Foreground = Brushes.OrangeRed,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
                if (t.Error.Contains("403") || t.Error.Contains("404") || t.Error.Contains("Referer"))
                    row.Children.Add(new TextBlock
                    {
                        Text = "提示：地址可能已失效，或该资源需要 Referer 防盗链（源没给出）。换个线路/源，或点「新建下载」手填 Referer 再试。",
                        FontSize = 11,
                        Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 3, 0, 0),
                    });
            }

            // ---- 按钮行（照着猫抓：每个任务都能暂停/继续/重试/删掉/交外部工具）----
            var btns = new WrapPanel();

            switch (t.State)
            {
                case DlState.Running:
                case DlState.Queued:
                    btns.Children.Add(SmallBtn("暂停", () => { _dl.Pause(t); }));
                    break;
                case DlState.Paused:
                    btns.Children.Add(SmallBtn("继续", () => { _dl.Resume(t); }));
                    btns.Children.Add(SmallBtn("删除", () => { if (ReferenceEquals(_dlSelected, t)) _dlSelected = null; _dl.Remove(t); }));
                    break;
                case DlState.Failed:
                case DlState.Canceled:
                    btns.Children.Add(SmallBtn("重试", () => { _dl.Retry(t); }));
                    btns.Children.Add(SmallBtn("删除", () => { if (ReferenceEquals(_dlSelected, t)) _dlSelected = null; _dl.Remove(t); }));
                    break;
                default:
                    btns.Children.Add(SmallBtn("播放", () => PlayOutput(t)));
                    btns.Children.Add(SmallBtn("定位文件", () => RevealOutput(t)));
                    btns.Children.Add(SmallBtn("删除", () => { if (ReferenceEquals(_dlSelected, t)) _dlSelected = null; _dl.Remove(t); }));
                    break;
            }
            btns.Children.Add(SmallBtn("复制命令", () =>
            {
                _dlSelected = t;
                ShowCommands(t);
            }));

            row.Children.Add(btns);
            return card;
        }

        private static Button SmallBtn(string text, Action click)
        {
            var b = new Button
            {
                Content = text,
                Height = 22,
                FontSize = 11,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 4, 6, 0),
                Background = IdleBg,
                Foreground = Brushes.White,
                BorderBrush = CardBorder,
            };
            b.Click += (_, _) => { try { click(); } catch { } };
            return b;
        }

        /// <summary>用内置/外部播放器播已下载的产物（file:// 地址，走同一条播放链路）。</summary>
        private void PlayOutput(DownloadManager.TaskInfo t)
        {
            var path = t.OutputPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                // 外部引擎模式下 OutputPath 是目录，找里面最新的媒体文件
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    path = Directory.EnumerateFiles(path)
                        .Where(f => { var e = Path.GetExtension(f).ToLowerInvariant();
                                      return e is ".ts" or ".mp4" or ".mkv" or ".flv" or ".aac" or ".mp3" or ".m4a"; })
                        .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Info("还没找到下载好的文件。\n\n" + (t.OutputPath ?? ""), "播放下载内容");
                return;
            }

            var uri = new Uri(path).AbsoluteUri;
            Play(uri, "已下载：" + t.Name, history: false);
        }

        private void RevealOutput(DownloadManager.TaskInfo t)
        {
            try
            {
                var p = t.OutputPath;
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
                else
                {
                    Directory.CreateDirectory(_dl.RootDir);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dl.RootDir}\"") { UseShellExecute = true });
                }
            }
            catch { }
        }

        // ===================== 新建下载（猫抓风：粘地址 → 解析 → 设参数 → 下） =====================
        private void BtnNewDl_Click(object sender, RoutedEventArgs e)
        {
            tbNewPreview.Text = "还没解析。点「解析地址」可以先看清这份清单有多少分片、多长、要不要密钥。";
            pnlNewVariant.Visibility = Visibility.Collapsed;
            cbNewVariant.Items.Clear();
            tbNewDlMsg.Text = "";

            // 每次打开都要清掉上一次的"一次性输入"，否则会带着上一个源的头/范围去下另一个源 —— 那种错很难查
            tbNewUrl.Text = "";
            tbNewReferer.Text = "";
            tbNewUa.Text = "";
            tbNewExtra.Text = "";
            tbNewFromSeg.Text = "";
            tbNewToSeg.Text = "";
            tbNewFromTime.Text = "";
            tbNewToTime.Text = "";
            tbNewKey.Text = "";
            tbNewIv.Text = "";
            tbNewName.Text = _detail?.Name ?? "";
            cbNewFormat.SelectedIndex = 0;
            cbNewSavePl.IsChecked = false;

            tbNewConc.Text = (_settings.DownloadConcurrency is > 0 and <= 64 ? _settings.DownloadConcurrency : 12).ToString();
            tbNewRetry.Text = "2";
            if (tbNewFolder.Text.Trim().Length == 0) tbNewFolder.Text = _detail?.Name ?? "手动下载";
            ShowOverlay(dlgNewDl, true);
            tbNewUrl.Focus();
        }

        private void BtnNewDlCancel_Click(object sender, RoutedEventArgs e) => ShowOverlay(dlgNewDl, false);

        private void TbNewUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 地址一改，之前那份解析结果就不作数了
            tbNewPreview.Text = "地址已改动，请重新点「解析地址」。";
            pnlNewVariant.Visibility = Visibility.Collapsed;
            cbNewVariant.Items.Clear();
        }

        private async void BtnNewParse_Click(object sender, RoutedEventArgs e)
        {
            var url = tbNewUrl.Text.Trim();
            if (url.Length == 0) { tbNewDlMsg.Text = "请先填下载地址。"; return; }

            btnNewParse.IsEnabled = false;
            tbNewPreview.Text = "正在解析…";
            try
            {
                var headers = CollectHeaderBoxes();
                var p = await _dl.PreviewAsync(url, headers, cbNewVariant.SelectedIndex);
                if (!p.Ok)
                {
                    tbNewPreview.Text = "✕ " + p.Message;
                    pnlNewVariant.Visibility = Visibility.Collapsed;
                    return;
                }

                var parts = new List<string>
                {
                    $"总时长 {(p.Duration > 0 ? HlsParser.FormatDuration(p.Duration) : "未知")}",
                    $"{p.SegmentCount} 个分片",
                };
                if (p.HasByteRange) parts.Add("含字节范围切片（EXT-X-BYTERANGE）");
                if (p.Encrypted) parts.Add("AES-128 加密（自动解密）");
                if (p.Ext != ".ts") parts.Add("产物 " + p.Ext);

                if (p.IsMaster && p.Variants.Count > 0)
                {
                    cbNewVariant.Items.Clear();
                    foreach (var v in p.Variants) cbNewVariant.Items.Add(v.Label);
                    cbNewVariant.SelectedIndex = 0;
                    pnlNewVariant.Visibility = Visibility.Visible;
                    pnlNewVariant.Tag = p;      // 记住这次解析（换画质时不用重新解析）
                    tbNewPreview.Text = $"✓ 多码率清单，{p.Variants.Count} 个画质档，已选最高档解析：{string.Join(" · ", parts)}";
                }
                else
                {
                    pnlNewVariant.Visibility = Visibility.Collapsed;
                    tbNewPreview.Text = "✓ 解析成功：" + string.Join(" · ", parts);
                }

                if (tbNewName.Text.Trim().Length == 0) tbNewName.Text = SuggestName(url);
            }
            catch (Exception ex)
            {
                tbNewPreview.Text = "✕ 解析出错：" + ex.Message;
            }
            finally { btnNewParse.IsEnabled = true; }
        }

        /// <summary>从「新建下载」对话框里收集手填的请求头。</summary>
        private Dictionary<string, string> CollectHeaderBoxes()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (tbNewReferer.Text.Trim().Length > 0) headers["Referer"] = tbNewReferer.Text.Trim();
            if (tbNewUa.Text.Trim().Length > 0) headers["User-Agent"] = tbNewUa.Text.Trim();
            foreach (var raw in tbNewExtra.Text.Split('\n'))
            {
                var s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                var c = s.IndexOf(':');
                if (c <= 0) continue;
                headers[s[..c].Trim()] = s[(c + 1)..].Trim();
            }
            return headers;
        }

        /// <summary>只解析「其它头」那一格（每行一个 <c>Key: Value</c>）；格式不对返回 false 并给出那行原文。</summary>
        private bool TryCollectExtraHeaders(out Dictionary<string, string> headers, out string bad)
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bad = "";
            foreach (var raw in tbNewExtra.Text.Split('\n'))
            {
                var s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                var c = s.IndexOf(':');
                if (c <= 0) { bad = s; return false; }
                headers[s[..c].Trim()] = s[(c + 1)..].Trim();
            }
            return true;
        }

        private void BtnNewDlOk_Click(object sender, RoutedEventArgs e)
        {
            var url = tbNewUrl.Text.Trim();
            if (url.Length == 0) { tbNewDlMsg.Text = "请先填下载地址。"; return; }

            // 「其它头」格式不对要当场说清楚，别让它悄悄变成下载失败
            if (!TryCollectExtraHeaders(out var extra, out var bad))
            {
                tbNewDlMsg.Text = "「其它头」每行要写成「Key: Value」，这行看不懂：" + bad;
                return;
            }
            // ★ 上面那两个独立输入框（Referer / User-Agent）必须一起带上。
            //   自检实测：只读「其它头」那一格时，用户在 Referer 框里填的东西会被静默丢掉 ——
            //   表现就是"填了 Referer 还是 403"，而且看不出哪里错了。
            if (tbNewReferer.Text.Trim().Length > 0) extra["Referer"] = tbNewReferer.Text.Trim();
            if (tbNewUa.Text.Trim().Length > 0) extra["User-Agent"] = tbNewUa.Text.Trim();

            var opt = new DlOptions
            {
                Concurrency = IntOr(tbNewConc.Text, 0),
                SegmentRetry = IntOr(tbNewRetry.Text, -1),
                FromSegment = IntOr(tbNewFromSeg.Text, 0),
                ToSegment = IntOr(tbNewToSeg.Text, 0),
                FromTime = ParseTimeBox(tbNewFromTime),
                ToTime = ParseTimeBox(tbNewToTime),
                ManualKey = tbNewKey.Text.Trim(),
                ManualIv = tbNewIv.Text.Trim(),
                VariantIndex = cbNewVariant.SelectedIndex,
                SavePlaylist = cbNewSavePl.IsChecked == true,
                OutputFormat = cbNewFormat.SelectedIndex switch { 1 => ".ts", 2 => ".mp4", 3 => ".aac", _ => "auto" },
                ExtraHeaders = extra,
            };
            if (opt.ManualKey.Length > 0 && HlsParser.ParseManualKey(opt.ManualKey) == null)
            {
                tbNewDlMsg.Text = "密钥看不懂：请填 32 位十六进制（可带 0x）或 base64 的 16 字节密钥。";
                return;
            }

            var name = tbNewName.Text.Trim();
            if (name.Length == 0) name = SuggestName(url);
            var folder = tbNewFolder.Text.Trim();
            if (folder.Length == 0) folder = "手动下载";

            var task = _dl.AddManual(name, url, opt, folder);
            _dlSelected = task;
            _taskSig = "";
            ShowOverlay(dlgNewDl, false);
            RefreshTasks();
            tbDlSummary.Text = "⟵ 已加入 1 个任务";
        }

        private static int IntOr(string s, int def) => int.TryParse((s ?? "").Trim(), out var v) ? v : def;

        /// <summary>把 "750" / "12:30" / "1:02:03" 解析成秒；解析不了返回 0（= 不限制）。</summary>
        private static double ParseTimeBox(TextBox tb)
        {
            var s = tb.Text.Trim();
            if (s.Length == 0) return 0;
            if (s.Contains(':'))
            {
                double total = 0;
                foreach (var p in s.Split(':'))
                {
                    if (!double.TryParse(p.Trim(), out var v)) return 0;
                    total = total * 60 + v;
                }
                return total;
            }
            return double.TryParse(s, out var sec) ? sec : 0;
        }

        /// <summary>从一个地址猜个文件名（取路径最后一段，去掉扩展名）。</summary>
        private static string SuggestName(string url)
        {
            try
            {
                var path = url.Split('|')[0].Split('?')[0].TrimEnd('/');
                var last = path.Split('/').LastOrDefault() ?? "";
                var dot = last.LastIndexOf('.');
                if (dot > 0) last = last[..dot];
                if (last.Length > 0 && last.Length < 80) return DownloadManager.Sanitize(last);
                var host = new Uri(path).Host;
                return DownloadManager.Sanitize(host + "_" + DateTime.Now.ToString("MMdd_HHmm"));
            }
            catch { }
            return "下载_" + DateTime.Now.ToString("MMdd_HHmmss");
        }

        // ===================== 外部工具命令 =====================
        private void BtnDlCmds_Click(object sender, RoutedEventArgs e)
        {
            var t = _dlSelected ?? _dl.Tasks.LastOrDefault(x => x.State != DlState.Done);
            if (t == null) { Info("还没有下载任务。\n\n先在右侧新建一个下载，或点某条任务的「复制命令」。", "外部工具命令"); return; }
            ShowCommands(t);
        }

        private void ShowCommands(DownloadManager.TaskInfo t)
        {
            var ext = Path.GetExtension(t.OutputPath ?? "");
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".ts";

            // 请求头：优先用已经算好的（下载过就走这条），否则现场把「地址里自带的」和「用户手填的」合一遍。
            // ★ 不能只看 t.Headers —— 任务还没开跑（排队中/失败即点复制）时它是空的，
            //   会把用户手填的 Referer 悄悄丢掉，正好在"失败了想拿命令去外面试"这个最需要的时刻丢掉。
            var headers = t.Headers;
            if (headers.Count == 0)
            {
                headers = new Dictionary<string, string>(MediaUrl.Parse(t.Url).Headers, StringComparer.OrdinalIgnoreCase);
                if (t.Options.ExtraHeaders != null)
                    foreach (var kv in t.Options.ExtraHeaders)
                        if (!kv.Key.StartsWith("__")) headers[kv.Key] = kv.Value;
            }

            var ctx = new DownloadCommand.Ctx
            {
                Url = t.CleanUrl.Length > 0 ? t.CleanUrl : MediaUrl.Parse(t.Url).Url,
                SaveDir = Path.Combine(_dl.RootDir, DownloadManager.Sanitize(t.Folder)),
                SaveName = t.Name,
                Ext = ext,
                Headers = headers,
                Concurrency = t.Options.Concurrency > 0 ? t.Options.Concurrency : _settings.DownloadConcurrency,
                EnginePath = _dl.EnginePath,
            };

            var sb = new StringBuilder();
            sb.AppendLine("# " + t.Name + "（地址与请求头已按这条任务填好）");
            sb.AppendLine();
            foreach (var (tool, cmd) in DownloadCommand.All(ctx))
            {
                sb.AppendLine("# ---------- " + tool + " ----------");
                sb.AppendLine(cmd);
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(_dl.FfmpegPath)) sb.AppendLine("# 本机 ffmpeg：" + _dl.FfmpegPath);

            tbCmds.Text = sb.ToString();
            tbCmdsTitle.Text = "外部工具命令 —— " + t.Name;
            ShowOverlay(dlgCmds, true);
        }

        private void BtnCmdsCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(tbCmds.Text);
                tbDlSummary.Text = "命令已复制到剪贴板";
            }
            catch { }
        }

        private void BtnCmdsClose_Click(object sender, RoutedEventArgs e) => ShowOverlay(dlgCmds, false);

        private static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }

        // ===================== 界面切换 =====================
        private void ShowHome()
        {
            pnlHome.Visibility = Visibility.Visible;
            pnlList.Visibility = Visibility.Collapsed;
            pnlDetail.Visibility = Visibility.Collapsed;
            pnlPlayer.Visibility = Visibility.Collapsed;
        }

        private void ShowList()
        {
            pnlList.Visibility = Visibility.Visible;
            pnlHome.Visibility = Visibility.Collapsed;
            pnlDetail.Visibility = Visibility.Collapsed;
            pnlPlayer.Visibility = Visibility.Collapsed;
        }

        private void ShowEmptyState(string msg)
        {
            homePanel.Children.Clear();
            btnLoadMore.Visibility = Visibility.Collapsed;
            homeTip.Text = msg;
            homeTip.Visibility = Visibility.Visible;
            ShowHome();
        }

        private void BtnBackHome_Click(object sender, RoutedEventArgs e)
        {
            try { _mp?.Stop(); } catch { }
            SetPlayStatus("");
            ExitFullScreen();
            // 从搜索结果页返回时，不要停留在旧的搜索结果，而是回到当前源的分类首页；
            // 从聚合搜索结果页返回时，则回到聚合搜索列表（保留多源结果）。
            if (_browseMode == "agg")
            {
                ShowList();
            }
            else if (_browseMode == "search")
            {
                _searchKw = "";
                _browseMode = "list";
                _ = LoadHome(1);
            }
            else
            {
                ShowHome();
            }
        }

        private void BtnLoadMore_Click(object sender, RoutedEventArgs e)
        {
            if (_browseMode == "search") _ = LoadSearch(_homePage + 1);
            else _ = LoadHome(_homePage + 1);
        }

        // ===================== 弹窗 =====================
        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            tbCfg.Text = _settings.LastConfig ?? "";
            tbImportMsg.Text = "";
            ShowOverlay(dlgImport, true);
        }

        private void BtnFillDefault_Click(object sender, RoutedEventArgs e) => tbCfg.Text = DefaultConfigs[0];
        private void BtnImportCancel_Click(object sender, RoutedEventArgs e) => ShowOverlay(dlgImport, false);
        private async void BtnImportOk_Click(object sender, RoutedEventArgs e) => await LoadConfig(tbCfg.Text.Trim());

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            tbAggLimit.Text = (_settings.AggLimit <= 0 ? 30 : _settings.AggLimit).ToString();
            tbAggTimeout.Text = (_settings.AggTimeoutSeconds <= 0 ? 8 : _settings.AggTimeoutSeconds).ToString();
            tbDlDir.Text = _settings.DownloadDir ?? "";
            tbDlConc.Text = (_settings.DownloadConcurrency <= 0 ? 12 : _settings.DownloadConcurrency).ToString();
            cbProxy.IsChecked = _settings.UseSystemProxy;
            ShowOverlay(dlgSettings, true);
        }

        private void BtnPickDlDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // .NET 8 起 WPF 自带选目录对话框，不必再拖 WinForms
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择下载保存目录" };
                if (!string.IsNullOrWhiteSpace(tbDlDir.Text) && Directory.Exists(tbDlDir.Text.Trim()))
                    dlg.InitialDirectory = tbDlDir.Text.Trim();
                if (dlg.ShowDialog(this) == true) tbDlDir.Text = dlg.FolderName;
            }
            catch (Exception ex) { MessageBox.Show("选择目录失败：" + ex.Message); }
        }

        private void BtnSettingsCancel_Click(object sender, RoutedEventArgs e) => ShowOverlay(dlgSettings, false);

        private void BtnSettingsSave_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(tbAggLimit.Text.Trim(), out var n) && n > 0) _settings.AggLimit = n;
            if (int.TryParse(tbAggTimeout.Text.Trim(), out var s) && s > 0) _settings.AggTimeoutSeconds = s;
            _settings.DownloadDir = tbDlDir.Text.Trim();
            if (int.TryParse(tbDlConc.Text.Trim(), out var c) && c is > 0 and <= 64) _settings.DownloadConcurrency = c;
            _settings.UseSystemProxy = cbProxy.IsChecked == true;
            _settings.Save();

            // 立刻把下载设置应用到正在运行的下载器，不必重启
            ApplyDownloadSettings();

            ShowOverlay(dlgSettings, false);
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _store.ClearHistory();
            MessageBox.Show("历史已清空。");
        }
    }
}
