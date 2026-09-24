using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using TVBoxPC.Core;

namespace TVBoxPC
{
    /// <summary>
    /// 海报图片加载器。
    /// 很多影视图床对「无 User-Agent」的请求直接返回 403，而 WPF 的 BitmapImage
    /// 远程加载默认不带 UA，会导致海报全部空白。这里统一用 HttpClient（带 UA）
    /// 取回字节，再在内存里解码成 BitmapImage，并做一层缓存避免重复下载。
    /// </summary>
    public static class ImageLoader
    {
        private static readonly ConcurrentDictionary<string, BitmapImage?> Cache = new();
        private static readonly HttpClientHolder Holder = new();

        private sealed class HttpClientHolder
        {
            public readonly System.Net.Http.HttpClient Client = Build();
            private static System.Net.Http.HttpClient Build()
            {
                var c = HttpFactory.Create(20);
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
                return c;
            }
        }

        /// <summary>把 url 指向的图片异步加载进 Image 控件（失败则保持空白）。</summary>
        public static void LoadInto(Image target, string? url)
        {
            if (string.IsNullOrEmpty(url)) return;

            if (Cache.TryGetValue(url, out var cached))
            {
                target.Source = cached;
                return;
            }

            var u = url;
            _ = Task.Run(async () =>
            {
                BitmapImage? bmp = null;
                try
                {
                    var bytes = await Holder.Client.GetByteArrayAsync(u);
                    using var ms = new MemoryStream(bytes);
                    bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();   // 冻结后即可跨线程使用
                }
                catch { bmp = null; }

                if (Cache.Count > 800) Cache.Clear();
                Cache[u] = bmp;

                if (bmp != null)
                {
                    try { target.Dispatcher.Invoke(() => target.Source = bmp); } catch { }
                }
            });
        }
    }
}
