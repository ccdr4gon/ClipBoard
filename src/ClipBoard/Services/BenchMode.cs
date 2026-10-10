using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipBoard.Services;

/// <summary>
/// 离屏性能基准。以环境变量 CLIPBOARD_BENCH=&lt;目录&gt; 启动时：
/// 数据只读写 &lt;目录&gt;\profile（调用方事先放好一份数据副本），诊断日志也写在 &lt;目录&gt;；
/// 不占单实例锁，不注册热键、托盘、剪贴板监听和自启动；面板在所有屏幕之外打开且不激活，
/// 不会抢正在使用电脑的人的焦点。跑完把耗时、内存和每个标签页的截图写进 &lt;目录&gt; 后退出。
/// </summary>
internal static class BenchMode
{
    public static string? Root { get; } =
        Environment.GetEnvironmentVariable("CLIPBOARD_BENCH") is { Length: > 0 } dir ? Path.GetFullPath(dir) : null;
    public static bool Enabled => Root != null;
    public static string ProfileDir => Path.Combine(Root!, "profile");

    public static async Task RunAsync(MainWindow window)
    {
        var result = new Dictionary<string, object>();
        try
        {
            await Idle();
            using var process = Process.GetCurrentProcess();
            result["readyMs"] = Math.Round((DateTime.Now - process.StartTime).TotalMilliseconds);
            process.Refresh();
            result["readyCpuMs"] = Math.Round(process.TotalProcessorTime.TotalMilliseconds);
            result["readyUiCpuMs"] = Math.Round(UiThreadCpuMs());
            result["afterStartup"] = Memory(process);

            // 照发布版的时间线：启动完成后先有几次复制，再在 ApplicationIdle 预热面板，预热之后、首次打开之前又有几次复制。
            // CLIPBOARD_BENCH_PREWARM=0 时不预热，测冷的首次打开（预热之前的版本就是这样）。
            result["capturesBeforePrewarm"] = await CaptureProbe();
            if (Environment.GetEnvironmentVariable("CLIPBOARD_BENCH_PREWARM") != "0")
            {
                double ui = UiThreadCpuMs();
                var sw = Stopwatch.StartNew();
                await Dispatcher.CurrentDispatcher.InvokeAsync(window.Prewarm, DispatcherPriority.ApplicationIdle);
                await Idle();
                result["prewarmMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                result["prewarmUiCpuMs"] = Math.Round(UiThreadCpuMs() - ui, 1);
                result["afterPrewarm"] = Memory(process);
                result["capturesAfterPrewarm"] = await CaptureProbe();
            }

            // 打开 / 关闭面板若干次：第一次是预热之后的首次打开（不预热时包含首次 Loaded：建标签页、模板），之后是常态。
            var openMs = new List<double>();
            var openCpuMs = new List<double>();
            var openUiCpuMs = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                double ui = UiThreadCpuMs();
                var sw = Stopwatch.StartNew();
                window.ShowPanelOffscreen();
                await Idle();
                openMs.Add(Math.Round(sw.Elapsed.TotalMilliseconds, 1));
                openUiCpuMs.Add(Math.Round(UiThreadCpuMs() - ui, 1));
                await Task.Delay(700); // 让渲染线程把这一帧画完，再读整个进程的 CPU 时间
                process.Refresh();
                openCpuMs.Add(Math.Round((process.TotalProcessorTime - cpu).TotalMilliseconds, 1));
                if (i == 0) result["afterFirstOpen"] = Memory(process);
                if (i < 5) { window.HidePanel(); await Idle(); await Task.Delay(200); }
            }
            result["openMs"] = openMs;
            result["openCpuMs"] = openCpuMs;
            result["openUiCpuMs"] = openUiCpuMs;

            // 每个标签页：切换耗时 + 截图（用来逐像素比较外观是否变化）。
            var tabs = new List<object>();
            int index = 0;
            foreach (var item in window.Tabs.Items.OfType<TabItem>().ToList())
            {
                double ui = UiThreadCpuMs();
                var sw = Stopwatch.StartNew();
                window.Tabs.SelectedItem = item;
                await Idle();
                double ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                double uiCpuMs = Math.Round(UiThreadCpuMs() - ui, 1);
                string file = $"tab-{index:D2}.png";
                Snapshot(window, Path.Combine(Root!, file));
                tabs.Add(new { index, ms, uiCpuMs, file });
                index++;
            }
            result["tabs"] = tabs;
            window.Tabs.SelectedIndex = 0;
            await Idle();

            // 搜索：逐个关键词设置搜索框，计到过滤和重新布局完成。
            var search = new List<object>();
            foreach (var query in new[] { "a", "e", "的", "http", "1", "zzzz-no-match", "" })
            {
                double ui = UiThreadCpuMs();
                var sw = Stopwatch.StartNew();
                window.SearchBox.Text = query;
                await Idle();
                search.Add(new { query, ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1), uiCpuMs = Math.Round(UiThreadCpuMs() - ui, 1) });
            }
            result["search"] = search;
            Snapshot(window, Path.Combine(Root!, "history-after-search.png"));

            // 滚动历史列表到底再回到顶：衡量条目容器的创建 / 回收开销。
            // 历史列表按条目滚动（虚拟化），图片页按像素滚动（卡片网格）。
            var scrolls = new List<object>();
            foreach (int tab in new[] { 0, 1 })
            {
                if (window.Tabs.Items.Count <= tab || window.Tabs.Items[tab] is not TabItem { Content: DependencyObject content }) continue;
                window.Tabs.SelectedIndex = tab;
                await Idle();
                if (FindDescendant<ScrollViewer>(content) is not { } viewer) continue;
                double step = viewer.CanContentScroll ? 3 : 120;
                double ui = UiThreadCpuMs();
                var sw = Stopwatch.StartNew();
                int steps = 0;
                for (double y = 0; y <= viewer.ScrollableHeight + step && steps < 400; y += step, steps++)
                {
                    viewer.ScrollToVerticalOffset(y);
                    await Idle();
                }
                scrolls.Add(new { tab, steps, ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1), uiCpuMs = Math.Round(UiThreadCpuMs() - ui, 1) });
                viewer.ScrollToTop();
                await Idle();
            }
            result["scroll"] = scrolls;
            window.Tabs.SelectedIndex = 0;
            await Idle();
            result["afterUse"] = Memory(process);

            window.HidePanel();
            await Task.Delay(3000);
            result["idleAfterHide"] = Memory(process);
            process.Refresh();
            var idleCpu = process.TotalProcessorTime;
            await Task.Delay(10000);
            process.Refresh();
            result["idleCpuMsPer10s"] = Math.Round((process.TotalProcessorTime - idleCpu).TotalMilliseconds, 1);

            // 打开过之后面板隐藏时每次复制的开销（隐藏的窗口照样排版），作为上面两次的参照。放在最后，不影响前面的数。
            result["capturesWhileHidden"] = await CaptureProbe();
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
        }
        File.WriteAllText(Path.Combine(Root!, "bench.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown();
    }

    // 只算 UI 线程自己的 CPU 时间：机器很忙时墙钟时间会被别的进程拉长，这个数不会。
    private static double UiThreadCpuMs()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user);
        return (kernel + user) / 10000.0;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;

    // 模拟复制在面板这边引起的工作：历史满 200 条时一次复制是「最前插入一条 + 裁掉最后一条」两次 CollectionChanged。
    // 这里把最后一条挪到最前（同样两次事件），等布局做完，再挪回原位（也是两次事件，同样计作一次复制）；
    // 数据最后原样不变，不保存、不碰剪贴板。
    private static async Task<object> CaptureProbe()
    {
        const int rounds = 10;
        var items = App.History.Items;
        if (items.Count < 2) return new { captures = 0, ms = 0.0, uiCpuMs = 0.0 };
        await Idle();
        double ui = UiThreadCpuMs();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++)
        {
            var last = items[^1];
            items.RemoveAt(items.Count - 1);
            items.Insert(0, last);
            await Idle();
            items.RemoveAt(0);
            items.Add(last);
            await Idle();
        }
        return new { captures = rounds * 2, ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1), uiCpuMs = Math.Round(UiThreadCpuMs() - ui, 1) };
    }

    private static object Memory(Process process)
    {
        process.Refresh();
        long privateRaw = process.PrivateMemorySize64, wsRaw = process.WorkingSet64;
        var info = GC.GetGCMemoryInfo();
        long committedRaw = info.TotalCommittedBytes;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        return new
        {
            privateMB = MB(privateRaw), workingSetMB = MB(wsRaw), gcCommittedMB = MB(committedRaw),
            privateAfterGcMB = MB(process.PrivateMemorySize64), gcHeapAfterGcMB = MB(GC.GetTotalMemory(false)),
            gcCommittedAfterGcMB = MB(GC.GetGCMemoryInfo().TotalCommittedBytes),
        };
    }

    private static double MB(long bytes) => Math.Round(bytes / 1048576.0, 1);

    private static void Snapshot(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
