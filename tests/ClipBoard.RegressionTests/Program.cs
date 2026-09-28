using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBoard.Models;
using ClipBoard.Services;

namespace ClipBoard;

// 不启动 App、注册热键或接触用户数据；直接验证实际使用的存储代码。
internal static class App
{
    public static AppSettings Settings { get; } = new();
}

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        (string Name, Action Test)[] tests =
        [
            ("同大小不同 GIF 都保留，完全相同的 GIF 去重", GifDeduplication),
            ("首行相同但下方不同的图片都保留", ImageDeduplication),
            ("GIF 收藏不随历史删除而丢失", GifFavoriteSurvivesHistoryDeletion),
            ("GIF 顶置后重启仍能读取", PinnedGifSurvivesReload),
            ("删除旧版共享 GIF 的一个收藏不损坏另一个", LegacySharedGifSurvivesDeletion),
            ("删除整个收藏夹不损坏其他收藏夹", SharedGifSurvivesFolderDeletion),
            ("原图尺寸在历史、顶置和收藏中保存", ImageDimensionsRoundTrip),
            ("保存失败后可以重新刷新", FailedSaveCanBeRetried),
            ("同步刷新等待正在进行的保存结束", FlushWaitsForWriter),
            ("旧数据缺失尺寸时从原图恢复", LegacyImageDimensions),
            ("200 条上限只淘汰历史，不损坏收藏", HistoryLimitPreservesFavorites),
            ("删除顶置图片清理文件", PinnedImageDeletion),
            ("历史使用顺序在重启后保留", PromotedHistoryRoundTrip),
            ("仅忽略自身写入的剪贴板更新", SelfUpdateDoesNotIgnoreNewCopies),
            ("无内容的条目报告复制失败", MissingContentCannotBeCopied),
            ("GIF 下载保留完整数据", GifDownload),
            ("服务器停止发送 GIF 正文时可以取消", GifBodyCancellation),
            ("慢 GIF 后的文本按捕获顺序交付", ClipboardDeliveryOrder),
            ("退出时保存下载队列里的静态图和文本", ClipboardQueueOnExit),
            ("顶置条目的使用顺序在重启后保留", PinnedOrderRoundTrip),
        ];
        int failures = 0;
        foreach (var (name, test) in tests)
        {
            try { test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Gif(byte paletteValue = 0)
    {
        var bytes = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        bytes[13] = paletteValue;
        return bytes;
    }

    private static BitmapSource Image(int width = 2, int height = 2, byte lastPixel = 0)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        pixels[^4] = lastPixel;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void GifDeduplication()
    {
        using var f = new Fixture();
        f.History.AddGif(Gif());
        f.History.AddGif(Gif(80));
        Check(f.History.Items.Count == 2, "不同 GIF 被文件大小判断误删");
        f.History.AddGif(Gif(80));
        Check(f.History.Items.Count == 2, "相同 GIF 未去重");
    }

    private static void ImageDeduplication()
    {
        using var f = new Fixture();
        f.History.AddImage(Image());
        f.History.AddImage(Image(lastPixel: 80));
        Check(f.History.Items.Count == 2, "第二张图片被首行判断误删");
        f.History.AddImage(Image(lastPixel: 80));
        Check(f.History.Items.Count == 2, "相同图片未去重");
    }

    private static void GifFavoriteSurvivesHistoryDeletion()
    {
        using var f = new Fixture();
        f.History.AddGif(Gif());
        var original = f.History.Items[0];
        var folder = f.Favorites.CreateFolder("收藏");
        f.Favorites.AddToFolder(original, folder);
        f.History.Remove(original);
        f.Persistence.FlushSync();
        var restored = new FavoritesStore(f.Persistence, f.Persistence.Load());
        Check(restored.Folders.Single().Items.Single().GifBytes?.SequenceEqual(Gif()) == true, "收藏的 GIF 文件已被删除");
    }

    private static void PinnedGifSurvivesReload()
    {
        using var f = new Fixture();
        f.History.AddGif(Gif());
        f.Favorites.PinHistory(f.History.Items[0], f.History);
        f.Persistence.FlushSync();
        var restored = new FavoritesStore(f.Persistence, f.Persistence.Load());
        Check(restored.PinnedHistory.Single().GifBytes?.SequenceEqual(Gif()) == true, "顶置后的 GIF 文件不存在");
        f.Favorites.UnpinHistory(f.Favorites.PinnedHistory.Single(), f.History);
        Check(f.Persistence.LoadGifBlob(f.History.Items.Single().GifBlobName!) != null, "取消顶置损坏 GIF");
    }

    private static void LegacySharedGifSurvivesDeletion()
    {
        using var f = new Fixture();
        var folder = f.Favorites.CreateFolder("旧收藏");
        var name = f.Persistence.SaveGifBlob(Gif());
        var first = new ClipItem { Kind = ClipKind.Gif, FolderId = folder.Id, GifBlobName = name };
        var second = first.Clone();
        folder.Items.Add(first);
        folder.Items.Add(second);
        f.Favorites.RemoveFavorite(first);
        Check(f.Persistence.LoadGifBlob(second.GifBlobName!) != null, "仍被引用的 GIF 被清理");
        f.Favorites.RemoveFavorite(second);
        Check(!File.Exists(Path.Combine(f.Persistence.BlobsDir, name)), "最后一条引用删除后文件未清理");
    }

    private static void SharedGifSurvivesFolderDeletion()
    {
        using var f = new Fixture();
        var first = f.Favorites.CreateFolder("一");
        var second = f.Favorites.CreateFolder("二");
        var name = f.Persistence.SaveGifBlob(Gif());
        first.Items.Add(new ClipItem { Kind = ClipKind.Gif, FolderId = first.Id, GifBlobName = name });
        second.Items.Add(new ClipItem { Kind = ClipKind.Gif, FolderId = second.Id, GifBlobName = name });
        f.Favorites.DeleteFolder(first);
        Check(f.Persistence.LoadGifBlob(name) != null, "其他文件夹的共享 GIF 被清理");
    }

    private static void ImageDimensionsRoundTrip()
    {
        using var f = new Fixture();
        f.History.AddImage(Image(1024, 600));
        var folder = f.Favorites.CreateFolder("原图");
        f.Favorites.AddToFolder(f.History.Items[0], folder);
        f.Favorites.PinHistory(f.History.Items[0], f.History);
        f.History.AddImage(Image(1024, 600));
        f.Persistence.FlushSync();
        var data = JsonSerializer.Deserialize<PersistedData>(File.ReadAllText(Path.Combine(f.Root, "data.json")))!;
        foreach (var item in data.History.Concat(data.PinnedHistory).Concat(data.Favorites))
            Check(item.PixelW == 1024 && item.PixelH == 600, "保存后原图尺寸丢失");
    }

    private static void LegacyImageDimensions()
    {
        using var f = new Fixture();
        string name = f.Persistence.SaveImageBlob(Image(800, 600));
        f.Persistence.SaveDebounced(new PersistedData
        {
            History = [new ClipItem { Kind = ClipKind.Image, ImageBlobName = name }],
        });
        f.Persistence.FlushSync();
        var restored = f.Persistence.Load().History.Single();
        Check(restored.PixelW == 800 && restored.PixelH == 600, "旧数据原图尺寸未恢复");
    }

    private static void HistoryLimitPreservesFavorites()
    {
        using var f = new Fixture();
        f.History.AddGif(Gif());
        var original = f.History.Items[0];
        var folder = f.Favorites.CreateFolder("收藏");
        f.Favorites.AddToFolder(original, folder);
        for (int i = 0; i < 200; i++) f.History.AddText(i.ToString());
        Check(f.History.Items.Count == 200, "历史条目上限改变");
        Check(f.Persistence.LoadGifBlob(original.GifBlobName!) == null, "淘汰后未清理历史 GIF");
        Check(f.Persistence.LoadGifBlob(folder.Items.Single().GifBlobName!) != null, "淘汰历史破坏收藏");
    }

    private static void PinnedImageDeletion()
    {
        using var f = new Fixture();
        f.History.AddImage(Image());
        f.Favorites.PinHistory(f.History.Items.Single(), f.History);
        var item = f.Favorites.PinnedHistory.Single();
        Check(f.Persistence.LoadImageBlob(item.ImageBlobName!) != null, "顶置后丢失原图");
        f.Favorites.RemovePinnedHistory(item);
        Check(!File.Exists(Path.Combine(f.Persistence.BlobsDir, item.ImageBlobName!)), "顶置原图文件未清理");
    }

    private static void PromotedHistoryRoundTrip()
    {
        using var f = new Fixture();
        f.History.AddText("先复制");
        f.History.AddText("后复制");
        f.Persistence.FlushSync();
        f.History.PromoteToTop(f.History.Items[1]);
        f.Persistence.FlushSync();
        Check(f.Persistence.Load().History[0].Text == "先复制", "使用后的排序未保存");
    }

    private static void SelfUpdateDoesNotIgnoreNewCopies()
    {
        using var f = new Fixture();
        SetField(f.History, "_selfUpdateSequence", (uint)42);
        Check(f.History.IsSelfUpdate(42), "未识别自身更新");
        Check(!f.History.IsSelfUpdate(43), "紧接着复制的新内容被忽略");
        SetField(f.History, "_isWritingClipboard", true);
        Check(f.History.IsSelfUpdate(44), "写入期间的中间更新未被忽略");
    }

    private static void MissingContentCannotBeCopied()
    {
        using var f = new Fixture();
        foreach (var kind in Enum.GetValues<ClipKind>())
            Check(!f.History.CopyToClipboard(new ClipItem { Kind = kind }), $"空的 {kind} 被报告为复制成功");
    }

    private static void GifDownload()
    {
        var bytes = Gif();
        var result = WithHttpServer(async stream =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(bytes);
        }, url => GifHelper.DownloadGifAsync(url)).GetAwaiter().GetResult();
        Check(result?.SequenceEqual(bytes) == true, "下载后的 GIF 数据不完整");
    }

    private static void GifBodyCancellation()
    {
        using var headerSent = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var result = WithHttpServer(async stream =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n"u8.ToArray());
            headerSent.Set();
            await Task.Delay(500);
        }, async url =>
        {
            var download = GifHelper.DownloadGifAsync(url, cancellation.Token);
            Check(headerSent.Wait(TimeSpan.FromSeconds(5)), "测试服务器未发送响应头");
            cancellation.Cancel();
            var winner = await Task.WhenAny(download, Task.Delay(250));
            Check(winner == download, "正文读取没有响应取消");
            return await download;
        }).GetAwaiter().GetResult();
        Check(result == null, "取消后仍返回不完整 GIF");
    }

    private static void PinnedOrderRoundTrip()
    {
        using var f = new Fixture();
        f.History.AddText("较早顶置");
        f.History.Items[0].Timestamp = DateTime.Now.AddMinutes(-1);
        f.Favorites.PinHistory(f.History.Items[0], f.History);
        f.Favorites.PinnedHistory[0].Timestamp = DateTime.Now.AddMinutes(-1);
        f.History.AddText("较晚顶置");
        f.Favorites.PinHistory(f.History.Items[0], f.History);
        f.Favorites.PinnedHistory.Move(1, 0);
        f.Favorites.Save();
        f.Persistence.FlushSync();
        var restored = new FavoritesStore(f.Persistence, f.Persistence.Load());
        Check(restored.PinnedHistory[0].Text == "较早顶置", "恢复时按创建时间覆盖了保存的排序");
    }

    private static ClipboardMonitor MonitorWithoutSystemListener()
    {
        // 只驱动事件交付队列，不调用构造函数注册真正的剪贴板监听。
        var monitor = (ClipboardMonitor)RuntimeHelpers.GetUninitializedObject(typeof(ClipboardMonitor));
        SetField(monitor, "_window", new System.Windows.Window());
        SetField(monitor, "_shutdown", new CancellationTokenSource());
        SetField(monitor, "_pendingChanges", Task.CompletedTask);
        SetField(monitor, "_queuedChanges", new List<ClipboardChangedEventArgs>());
        return monitor;
    }

    private static void Queue(ClipboardMonitor monitor, ClipboardChangedEventArgs item, string? url = null)
        => typeof(ClipboardMonitor).GetMethod("QueueChange", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, [item, url]);

    private static void ClipboardDeliveryOrder()
    {
        using var monitor = MonitorWithoutSystemListener();
        var delivered = new List<ClipboardChangedEventArgs>();
        monitor.ClipboardChanged += (_, item) => delivered.Add(item);
        var bitmap = Image();
        var operation = WithHttpServer(async stream =>
        {
            await Task.Delay(100);
            var bytes = Gif();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(bytes);
        }, async url =>
        {
            Queue(monitor, new ClipboardChangedEventArgs { Kind = ClipKind.Image, Image = bitmap }, url);
            Queue(monitor, new ClipboardChangedEventArgs { Kind = ClipKind.Text, Text = "后复制" });
            var pending = (Task)typeof(ClipboardMonitor).GetField("_pendingChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;
            await pending;
            return null;
        });
        PumpDispatcherUntil(operation);
        Check(delivered.Count == 2 && delivered[0].Kind == ClipKind.Gif
            && delivered[1].Text == "后复制", "下载改变了历史交付顺序");
    }

    private static void ClipboardQueueOnExit()
    {
        using var monitor = MonitorWithoutSystemListener();
        var delivered = new List<ClipboardChangedEventArgs>();
        monitor.ClipboardChanged += (_, item) => delivered.Add(item);
        var bitmap = Image();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = WithHttpServer(async stream =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n"u8.ToArray());
            headersSent.SetResult();
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }, async url =>
        {
            Queue(monitor, new ClipboardChangedEventArgs { Kind = ClipKind.Image, Image = bitmap }, url);
            Queue(monitor, new ClipboardChangedEventArgs { Kind = ClipKind.Text, Text = "退出前复制" });
            await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await dispatcher.InvokeAsync(monitor.Dispose); }
            finally { disposed.SetResult(); }
            return null;
        });
        PumpDispatcherUntil(operation);
        Check(delivered.Count == 2 && delivered[0].Image == bitmap
            && delivered[1].Text == "退出前复制", "退出丢弃或重复交付了队列内容");
    }

    private static void PumpDispatcherUntil(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            if (task.IsCompleted || elapsed.Elapsed > TimeSpan.FromSeconds(12)) frame.Continue = false;
        };
        timer.Start();
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(task.IsCompleted, "交付队列验证超时");
        task.GetAwaiter().GetResult();
    }

    private static async Task<byte[]?> WithHttpServer(Func<NetworkStream, Task> respond, Func<string, Task<byte[]?>> download)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            await respond(stream);
        });
        try { return await download($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/test.gif"); }
        finally { await server; }
    }

    private static void FailedSaveCanBeRetried()
    {
        using var f = new Fixture();
        var blocker = Path.Combine(f.Root, "data.json.tmp");
        Directory.CreateDirectory(blocker);
        f.History.AddText("待保存");
        f.Persistence.FlushSync();
        Directory.Delete(blocker);
        f.Persistence.FlushSync();
        Check(f.Persistence.Load().History.SingleOrDefault()?.Text == "待保存", "写入失败后快照被丢弃");
    }

    private static void FlushWaitsForWriter()
    {
        using var f = new Fixture();
        var snapshot = new PersistedData { History = [new ClipItem { Kind = ClipKind.Text, Text = "最新" }] };
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new BlockingConverter(entered, release));
        SetField(f.Persistence, "_jsonOpts", options);
        f.Persistence.SaveDebounced(snapshot);
        var writer = Task.Run(f.Persistence.FlushSync);
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "未进入写入阶段");
        using var flushStarted = new ManualResetEventSlim();
        var flush = Task.Run(() => { flushStarted.Set(); f.Persistence.FlushSync(); });
        bool returnedBeforeWrite;
        try
        {
            Check(flushStarted.Wait(TimeSpan.FromSeconds(5)), "未开始退出刷新");
            returnedBeforeWrite = flush.Wait(TimeSpan.FromMilliseconds(150));
        }
        finally { release.Set(); Task.WaitAll(writer, flush); }
        Check(!returnedBeforeWrite, "退出刷新提前返回，后台写入可能在进程退出时中断");
        Check(f.Persistence.Load().History.Single().Text == "最新", "最终快照不正确");
    }

    private sealed class BlockingConverter(ManualResetEventSlim entered, ManualResetEventSlim release)
        : System.Text.Json.Serialization.JsonConverter<PersistedData>
    {
        public override PersistedData? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => JsonSerializer.Deserialize<PersistedData>(ref reader);
        public override void Write(Utf8JsonWriter writer, PersistedData value, JsonSerializerOptions options)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("写入等待超时");
            JsonSerializer.Serialize(writer, value);
        }
    }

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ClipBoard.RegressionTests", Guid.NewGuid().ToString("N"));
        public PersistenceService Persistence { get; }
        public FavoritesStore Favorites { get; }
        public HistoryStore History { get; }

        public Fixture()
        {
            Persistence = new PersistenceService(Root);
            Favorites = new FavoritesStore(Persistence, new PersistedData());
            History = new HistoryStore(Favorites);
            History.SetPersistence(Persistence);
            Favorites.SetHistoryStore(History);
        }

        public void Dispose()
        {
            Persistence.FlushSync();
            string fullRoot = Path.GetFullPath(Root);
            string testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClipBoard.RegressionTests")) + Path.DirectorySeparatorChar;
            Check(fullRoot.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase), "清理路径超出测试临时目录");
            Directory.Delete(fullRoot, recursive: true);
        }
    }
}
