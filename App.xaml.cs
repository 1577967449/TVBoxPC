using System;
using System.Windows;
using System.Windows.Threading;
using TVBoxPC.Core;

namespace TVBoxPC
{
    public partial class App : Application
    {
        /// <summary>
        /// 命令行自检：`TVBoxPC.exe --play "http://xxx/index.m3u8"` 启动后直接播这个地址，
        /// 并把播放结果写进程序目录的 playcheck.log。
        /// 用途：用户报「内置播放器播不了」时，一条命令就能拿到可分析的证据，
        /// 不必靠"我这儿好好的"互相猜。
        /// </summary>
        public static string? StartupPlayUrl;

        /// <summary>
        /// 命令行自检：`TVBoxPC.exe --dlsim` —— 走一遍「右侧选集勾选 → 点『下载选中』」
        /// 的完整界面链路（含真实下载），把结果写进程序目录的 dlcheck.log。
        /// 用途：验证「选择分集下载没反应」这类问题到底出在哪一段。
        /// </summary>
        public static bool StartupDownloadSim;

        /// <summary>
        /// 命令行自检：`TVBoxPC.exe --playsim "庆余年"` —— 加载配置 → 搜索 → 打开详情 →
        /// **连续点三集**，逐集记录状态条文本、播放器状态/时长、以及播放页各区块的实际像素位置。
        /// 用途：验证「点剧集立刻显示播放完毕」这类问题（切集时 LibVLC 抛的假 EndReached）到底修好没有，
        /// 以及 TVBox 那套排布（视频左上 / 信息右侧 / 选集下方）是不是真的生效。
        /// </summary>
        public static string? StartupPlaySimKeyword;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 先根据设置决定是否使用系统代理，再创建任何网络客户端与窗口
            try { HttpFactory.UseSystemProxy = AppSettings.Load().UseSystemProxy; } catch { }

            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--play" && i + 1 < e.Args.Length)
                    StartupPlayUrl = e.Args[i + 1];
                if (e.Args[i] == "--dlsim")
                    StartupDownloadSim = true;
                if (e.Args[i] == "--playsim")
                    StartupPlaySimKeyword = (i + 1 < e.Args.Length && !e.Args[i + 1].StartsWith("--"))
                        ? e.Args[i + 1] : "庆余年";
            }

            base.OnStartup(e);

            // 兜底：界面线程上的未处理异常不再直接崩溃退出，弹提示后继续运行
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show("出现了一个错误（已忽略，程序继续运行）：\n\n" + args.Exception.Message,
                    "TVBox PC", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    MessageBox.Show("发生严重错误：\n\n" + ex.Message,
                        "TVBox PC", MessageBoxButton.OK, MessageBoxImage.Error);
            };
        }
    }
}
