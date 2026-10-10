using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ClipBoard;
using ClipBoard.Models;
using ClipBoard.Services;
using ClipBoard.Views;
using SkiaSharp;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ClipBoard-MacTests-" + Guid.NewGuid().ToString("N"));
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static StickerMediaService Media => new(App.Persistence, OperatingSystem.IsWindows() ? Path.Combine(Repo, "tools/media") : "");
    private static StickerLibraryService Library => new(App.Favorites, App.Persistence, Media);
    [STAThread]
    private static int Main(string[] args)
    {
        App.Preview = true; App.ProfileDirectory = Root;
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        bool nativeDialogs = args.Contains("--native-dialogs");
        if (nativeDialogs)
        {
            Check(OperatingSystem.IsMacOS(), "原生弹窗测试需要 Mac");
            AppBuilder.Configure<App>().UsePlatformDetect().With(new MacOSPlatformOptions { ShowInDock = false }).SetupWithLifetime(lifetime);
        }
        else AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithLifetime(lifetime);
        int failures = 0;
        using var finish = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                (string Name, Func<Task> Run)[] tests = [
                    ("历史去重、200 条上限与置顶保留", History),
                    ("图片原件、缩略图与收藏生命周期", Images),
                    ("Telegram 导入在原面板新增标签、只更新修改项", Telegram),
                    ("GIF 预览、编辑与原件恢复", Animation),
                    ("跨平台数据重载保留贴纸发布信息", Reload),
                    ("Windows 风格历史布局、键盘选择和 Control+Command+V", PanelLayout),
                    ("重新打开清空搜索、回到顶部；单击和回车直接粘贴，Shift 为纯文本", PanelReopenAndPaste),
                    ("带格式文本重建编号、显示标记并提供纯文本粘贴", RichTextItems),
                    ("设置弹窗层级、快捷键重入与关闭后恢复", () => SettingsDialogs(false)),
                ];
                if (nativeDialogs) tests = [("Mac 原生设置弹窗层级与恢复", () => SettingsDialogs(true))];
                foreach (var test in tests)
                {
                    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
                    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
                }
                if (args.Contains("--native-smoke"))
                {
                    try { NativeSmoke(); Console.WriteLine("PASS macOS 原生剪贴板、热键、钥匙串"); }
                    catch (Exception ex) { failures++; Console.WriteLine("FAIL 原生接口：" + ex); }
                }
                Console.WriteLine($"Failures: {failures}");
            }
            finally { App.Persistence.FlushSync(); finish.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(finish.Token);
        // Root is unique and created exclusively by this test process.
        try { Directory.Delete(Root, true); } catch { }
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static Task History()
    {
        App.History.Capture(new(Text: "重复")); App.History.Capture(new(Text: "重复"));
        Check(App.History.Items.Count == 1, "相邻文本未去重");
        App.Favorites.PinHistory(App.History.Items[0], App.History);
        for (int i = 0; i < 205; i++) App.History.Capture(new(Text: "内容 " + i));
        Check(App.History.Items.Count == 200 && App.Favorites.PinnedHistory.Count == 1, "历史上限或置顶错误");
        App.Favorites.UnpinHistory(App.Favorites.PinnedHistory[0], App.History);
        Check(App.History.Items.Count == 200 && App.History.Items[0].Text == "重复", "取消置顶丢失内容");
        App.History.Clear(); return Task.CompletedTask;
    }
    private static Task Images()
    {
        byte[] png = Png(1, 1800, 1000);
        App.History.Capture(new(Image: png)); App.History.Capture(new(Image: png));
        var item = App.History.Items[0];
        Check(App.History.Items.Count == 1 && item.PixelW == 1800 && item.Image!.PixelSize.Width == 512, "缩略图替换了原始尺寸或未去重");
        var folder = App.Favorites.EnsureDefaultMemeFolder();
        App.Favorites.AddToFolder(item, folder);
        var favorite = folder.Items[^1];
        App.History.Remove(item);
        using var full = App.Persistence.LoadImageBlob(favorite.ImageBlobName!);
        Check(full?.PixelSize.Width == 1800, "删除历史损坏收藏原图");
        App.Favorites.RemoveFavorite(favorite);
        Check(!File.Exists(App.Persistence.GetBlobPath(favorite.ImageBlobName!)), "收藏移除后文件没有清理");
        return Task.CompletedTask;
    }
    private static async Task Telegram()
    {
        using var server = new FakeTelegram(); using var http = new HttpClient(server);
        using var client = new TelegramStickerClient(FakeTelegram.Token, http);
        server.Seed("MacTestPack", (Png(2), "static"), (Tgs(), "animated"));
        var remote = await client.GetSetAsync("MacTestPack", default);
        var window = (MainWindow)((ClassicDesktopStyleApplicationLifetime)App.Current!.ApplicationLifetime!).MainWindow!;
        using (var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClipBoard.Mac/Assets/menubar.png")))
            Check(new Avalonia.Controls.WindowIcon(icon) != null, "菜单栏图标未正确打包");
        window.Show();
        int count = App.Favorites.Folders.Count;
        var result = await Library.ImportTelegramAsync(client, remote, remote.Stickers, null, default, window.SelectFolder);
        Check(result.Added == 2 && result.Errors.Count == 0, string.Join(";", result.Errors));
        Check(App.Favorites.Folders.Count == count + 1 && window.VisibleItems.Count == 2, "导入没有选中原面板新标签");
        var folder = result.Folder;
        var publisher = new TelegramStickerPublisher(client, Media, Library.Save);
        await publisher.PublishAsync(await publisher.PrepareAsync(folder, 1234, "", null, default), null, default);
        string unchanged = folder.Items[1].Sticker!.PublishedFileId!;
        var item = folder.Items[0];
        Library.ReplaceLocal(folder, item, await Media.EditAsync(item, new(Caption: "Mac 修改"), default));
        var again = await Library.ImportTelegramAsync(client, remote, remote.Stickers, null, default);
        Check(again.Skipped == 2 && folder.Items[0].Sticker!.Revision == 2, "重复导入覆盖本地编辑");
        await publisher.PublishAsync(await publisher.PrepareAsync(folder, 1234, "", null, default), null, default);
        Check(server.LastOwner == 1234 && server.Creates == 1 && server.Replaces == 1 && folder.Items[1].Sticker!.PublishedFileId == unchanged, "发布影响了其他贴纸或所有者");
        window.SelectFolder(folder);
        await Task.Delay(100);
        Directory.CreateDirectory(Path.Combine(Repo, "out"));
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(Repo, "out", "macos-panel-preview.png"));
        Check(window.Bounds.Width > 0, "界面未布局");
    }
    private static async Task Animation()
    {
        var path = Path.Combine(Root, "sample.gif");
        var start = new ProcessStartInfo(Media.Tool("ffmpeg")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=96x96:rate=10:duration=1", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Check(process.ExitCode == 0, error);
        var item = await Media.ImportFileAsync(path);
        string original = item.Sticker!.OriginalBlobName;
        var edited = await Media.EditAsync(item, new(Caption: "GIF", DurationSeconds: 1), default);
        var prepared = await Media.PrepareTelegramAsync(edited, default);
        var details = await Media.InspectAsync(prepared.Path, StickerFormat.Webm, default);
        Check(prepared.Format == "video" && details.Duration <= 3 && new FileInfo(prepared.Path).Length <= 256 * 1024, "动画输出不符合规格");
        var preview = await Media.CreatePreviewAsync(edited, default);
        using var player = new GifPreview(preview);
        Check(player.Source != null, "动画首帧没有显示");
        await Task.Delay(200);
        var restored = await Media.RestoreAsync(edited, default);
        Check(restored.Sticker!.Format == StickerFormat.Gif && File.ReadAllBytes(App.Persistence.GetBlobPath(original)).SequenceEqual(File.ReadAllBytes(path)), "恢复原件错误");
    }
    private static Task Reload()
    {
        App.Favorites.Save(); App.Persistence.FlushSync();
        var data = new PersistenceService(Root).Load();
        var folder = data.Folders.Single(f => f.Telegram?.SourceSetName == "MacTestPack");
        Check(folder.Telegram!.OwnerUserId == 1234 && data.Favorites.Where(i => i.FolderId == folder.Id).All(i => i.Sticker?.PublishedFileId != null), "重载丢失发布关系");
        var store = new FavoritesStore(App.Persistence, data);
        Check(store.Folders.Single(f => f.Id == folder.Id).Items.All(i => i.Image != null), "重载丢失缩略图");
        return Task.CompletedTask;
    }
    private static void NativeSmoke()
    {
        Check(OperatingSystem.IsMacOS(), "--native-smoke 必须在专用 Mac 测试环境运行");
        System.Runtime.InteropServices.NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        MacNative.Call(MacNative.Class("NSApplication"), "sharedApplication");
        Check(MacClipboard.Write(new ClipItem { Kind = ClipKind.Text, Text = "ClipBoard native smoke" }, App.Persistence), "写入失败");
        Check(MacClipboard.Read()?.Text == "ClipBoard native smoke", "读取失败");
        App.History.Capture(new(Image: Png(4)));
        Check(MacClipboard.Write(App.History.Items[0], App.Persistence) && MacClipboard.Read()?.Image is { Length: > 0 }, "原生图片剪贴板失败");
        var file = Path.Combine(Root, "file with 中文 spaces.txt"); File.WriteAllText(file, "native smoke");
        Check(MacClipboard.Write(new ClipItem { Kind = ClipKind.Files, FilePaths = [file] }, App.Persistence)
            && MacClipboard.Read()?.Files?.Single() == file, "原生文件 URL 读写失败");
        var rich = new ClipItem { Kind = ClipKind.Text, Text = "1. 列表", RichBlobName = App.Persistence.SaveRichBlob(new RichContent("<ol><li>列表</li></ol>", null)) };
        Check(MacClipboard.Write(rich, App.Persistence) && MacClipboard.Read() is { Text: "1. 列表", Rich.Html: { } html } && html.Contains("<li>列表</li>"), "原生 HTML 格式读写失败");
        Check(MacClipboard.Write(rich, App.Persistence, plainText: true) && MacClipboard.Read() is { Text: "1. 列表", Rich: null }, "纯文本粘贴仍带格式");
        using var hotkey = new MacHotkey(() => { });
        // 自动粘贴用到的系统函数都能找到；CI 没有辅助功能授权，这里不实际发送 ⌘V。
        var services = System.Runtime.InteropServices.NativeLibrary.Load("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices");
        foreach (var name in new[] { "AXIsProcessTrusted", "CGPreflightPostEventAccess", "CGRequestPostEventAccess", "CGEventSourceCreate", "CGEventSourceSetLocalEventsFilterDuringSuppressionState", "CGEventPost" })
            Check(System.Runtime.InteropServices.NativeLibrary.TryGetExport(services, name, out _), "找不到系统函数 " + name);
        _ = MacNative.IsAccessibilityTrusted;
        var store = new TelegramConnectionStore(Root);
        store.Save(new("123456:local_test_only", 1234));
        Check(store.Load().Token == "123456:local_test_only" && !File.ReadAllText(Path.Combine(Root, "telegram-connection.json")).Contains("local_test_only"), "钥匙串保存失败");
        store.Save(new());
    }
    private static async Task PanelLayout()
    {
        var window = (MainWindow)((ClassicDesktopStyleApplicationLifetime)App.Current!.ApplicationLifetime!).MainWindow!;
        App.History.Capture(new(Text: "文字条目保持紧凑，图片直接显示在历史中。"));
        App.History.Capture(new(Image: Png(6, 320, 1000)));
        App.History.Capture(new(Text: "这是一条普通的剪贴板历史。"));
        window.SelectView("history"); window.Show();
        await Task.Delay(100);
        Check(window.Width == 780 && window.Height == 640 && window.SystemDecorations == SystemDecorations.None, "面板大小或标题栏与 Windows 布局不符");
        var list = window.GetVisualDescendants().OfType<ListBox>().Single();
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        Check(rows.Length == 3 && rows[0].Bounds.Height < 55 && rows[1].Bounds.Height > 105 && rows[1].Bounds.Width > 600, "历史仍然显示成卡片网格");
        Check(list.GetVisualDescendants().OfType<HistoryThumbnail>().Single().Bounds.Size == new Size(176, 99), "历史缩略图不再是 16:9");
        window.FocusSearch(); window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Check(list.SelectedItem != null, "方向键无法选择历史项");
        Check(MacHotkey.KeyCode == 9 && MacHotkey.Modifiers == ((1u << 8) | (1u << 12)), "快捷键必须是 Control+Command+V");
        window.CaptureRenderedFrame()?.Save(Path.Combine(Repo, "out", "macos-history-preview.png"));
        window.SelectView("images"); await Task.Delay(100);
        Check(list.Items.Count == 1, "图片标签混入文字");
        window.CaptureRenderedFrame()?.Save(Path.Combine(Repo, "out", "macos-images-preview.png"));
        window.SelectView("history");
    }
    private static async Task PanelReopenAndPaste()
    {
        var app = (App)App.Current!;
        var window = (MainWindow)((ClassicDesktopStyleApplicationLifetime)app.ApplicationLifetime!).MainWindow!;
        for (int i = 0; i < 60; i++) App.History.Capture(new(Text: "滚动条目 " + i));
        window.SelectView("history"); window.Hide(); app.ShowPanel();
        await Task.Delay(100);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single();
        var viewer = list.FindDescendantOfType<ScrollViewer>()!;
        var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchBox");
        search.Text = "滚动条目 59";
        Check(list.ItemCount == 1 && list.SelectedIndex == 0, "搜索后没有选中第一条结果");
        search.Text = "滚动条目";
        list.SelectedIndex = 50; viewer.Offset = new Vector(0, 1500);
        await Task.Delay(100);
        Check(viewer.Offset.Y > 0, "测试未能把列表滚到下方");
        window.Hide(); app.ShowPanel();
        await Task.Delay(100);
        Check(search.Text == "" && list.ItemCount > 60, "重新打开后搜索没有清空");
        Check(viewer.Offset.Y == 0 && list.SelectedIndex == 0, "重新打开后没有回到顶部并选中第一条");

        var first = (ClipItem)list.Items[0]!;
        list.SelectedIndex = -1;
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Task.Delay(50);
        Check(app.LastPreviewCopy == (first.Id, true, false), "没有选中时回车没有粘贴第一条");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
        await Task.Delay(50);
        Check(app.LastPreviewCopy == (first.Id, true, true), "Shift+回车没有粘贴为纯文本");

        // 粘贴结束时面板会重新渲染列表。CI 的 Mac 上别的窗口偶尔抢走焦点，面板按设计失焦隐藏，隐藏的窗口不再布局，
        // 新的行就一直不生成；这一步测的是单击，不是失焦隐藏，所以重新显示面板。
        if (!window.IsVisible) { Console.WriteLine("  面板在测试中失焦隐藏，重新显示"); app.ShowPanel(); }
        for (int i = 0; i < 40 && list.GetVisualDescendants().OfType<ListBoxItem>().Count() < 3; i++) await Task.Delay(50);
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        Check(rows.Length > 2, $"粘贴后列表没有重新生成行：visible={window.IsVisible} active={window.IsActive} items={list.ItemCount} rows={rows.Length}");
        var row = rows[2];
        var target = (ClipItem)row.DataContext!;
        // 先点行的内边距（不在卡片内容上），再按住 Shift 点行中间。
        var padding = row.TranslatePoint(new Point(4, row.Bounds.Height / 2), window)!.Value;
        var center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        Check(window.InputHitTest(padding) is not Grid, "测试点没有落在行内边距上");
        window.MouseDown(padding, MouseButton.Left); window.MouseUp(padding, MouseButton.Left);
        await Task.Delay(50);
        Check(app.LastPreviewCopy == (target.Id, true, false), "单击条目没有直接粘贴");
        await Task.Delay(600); // 超过双击间隔，第二次按下才算单击
        window.MouseDown(center, MouseButton.Left, RawInputModifiers.Shift); window.MouseUp(center, MouseButton.Left, RawInputModifiers.Shift);
        await Task.Delay(50);
        Check(app.LastPreviewCopy == (target.Id, true, true), "Shift+单击没有粘贴为纯文本");
        foreach (var item in App.History.Items.Where(i => i.Text?.StartsWith("滚动条目") == true).ToArray()) App.History.Remove(item);
    }
    private static async Task RichTextItems()
    {
        var window = (MainWindow)((ClassicDesktopStyleApplicationLifetime)App.Current!.ApplicationLifetime!).MainWindow!;
        const string html = "<meta charset='utf-8'><p>步骤：</p><ol><li>打开面板</li><li>按 <b>回车</b></li></ol>";
        App.History.Capture(new(Text: "步骤：\n\n打开面板\n按 回车", Rich: RichText.Create(html, null)));
        var item = App.History.Items[0];
        Check(item.Text == "步骤：\n\n1. 打开面板\n2. 按 回车" && item.HasRichText, "Mac 没有从 HTML 重建编号或没有保存格式：" + item.Text);
        Check(App.Persistence.LoadRichBlob(item.RichBlobName)?.Html == html, "Mac 格式文件内容不对");
        window.SelectView("history"); window.Show();
        await Task.Delay(100);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single();
        var row = list.GetVisualDescendants().OfType<ListBoxItem>().First(r => r.DataContext == item);
        Check(row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "格式"), "带格式条目没有显示标记");
        var menu = row.GetVisualDescendants().OfType<Control>().Select(c => c.ContextMenu).First(m => m != null)!;
        Check(menu.Items.Count == 0, "右键菜单项应在第一次打开时才生成");
        MainWindow.EnsureMenuItems(menu); // 菜单项在打开时生成；测试不弹出真实菜单
        Check(menu.Items.OfType<MenuItem>().Any(m => m.Header as string == "粘贴为纯文本"), "右键菜单缺少纯文本粘贴");
        // 真实的右键路径：ContextRequested 先触发 Opening 生成菜单项再弹出（无头平台，不会出现真实窗口）。
        var other = list.GetVisualDescendants().OfType<ListBoxItem>().First(r => r.DataContext != item)
            .GetVisualDescendants().OfType<Control>().First(c => c.ContextMenu != null);
        other.RaiseEvent(new ContextRequestedEventArgs());
        Check(other.ContextMenu!.IsOpen && other.ContextMenu.Items.OfType<MenuItem>().Any(m => m.Header as string == "复制")
            && other.ContextMenu.Items.OfType<MenuItem>().Last().Header as string == "收藏到", "右键打开的菜单没有生成菜单项");
        other.ContextMenu.Close();
        App.History.Remove(item);
        Check(!File.Exists(App.Persistence.GetBlobPath(item.RichBlobName!)), "删除条目后格式文件未清理");
    }
    private static async Task SettingsDialogs(bool native)
    {
        var app = (App)App.Current!;
        var lifetime = (ClassicDesktopStyleApplicationLifetime)app.ApplicationLifetime!;
        var window = (MainWindow)lifetime.MainWindow!;
        window.Show(); window.Activate();
        await Task.Delay(100);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var settings = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "☀");
            settings.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            var dialog = window.OwnedWindows.Single(w => w.Title == "设置");
            try
            {
                Check(dialog.IsVisible && dialog.Topmost && !window.Topmost, "设置弹窗被置顶主面板压住");
                var done = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "完成");
                Check(done.IsEffectivelyEnabled && !window.GetVisualDescendants().OfType<ListBox>().Single().IsEffectivelyEnabled, "弹窗不可操作或主面板未进入等待状态");
                app.ShowPanel();
                Check(!window.Topmost && Dialogs.HasModal(window), "快捷键重新把主面板提升到了弹窗上方");
                if (native)
                {
                    using var pool = new MacNative.Pool();
                    var windows = MacNative.Call(MacNative.Call(MacNative.Class("NSApplication"), "sharedApplication"), "windows");
                    long Level(string title)
                    {
                        for (int i = 0; i < (int)MacNative.Call(windows, "count"); i++)
                        {
                            var w = MacNative.Call(windows, "objectAtIndex:", i);
                            if (MacNative.Text(MacNative.Call(w, "title")) == title) return (long)MacNative.Call(w, "level");
                        }
                        throw new InvalidOperationException("未找到原生窗口：" + title);
                    }
                    Check(Level("设置") > Level("ClipBoard"), "macOS 实际窗口层级仍然颠倒");
                }
                if (attempt == 0) done.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                else dialog.Close();
                await Task.Delay(100);
                Check(!Dialogs.HasModal(window) && window.Topmost && window.GetVisualDescendants().OfType<ListBox>().Single().IsEffectivelyEnabled,
                    "关闭设置后主面板没有恢复操作");
            }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }
        var form = Dialogs.Form(window, "测试输入", "测试关闭后恢复", ("文字", "", false));
        var input = window.OwnedWindows.Single(w => w.Title == "测试输入");
        Check(input.Topmost && !window.Topmost, "输入弹窗未应用层级修复");
        input.Close(); await form;
        try { await Dialogs.PickAsync<int>(window, () => Task.FromException<int>(new IOException("模拟选择器失败"))); }
        catch (IOException) { }
        Check(window.Topmost && !Dialogs.HasModal(window), "选择器失败后没有恢复窗口层级");
    }
    private static byte[] Png(int seed, int w = 160, int h = 160)
    {
        using var bitmap = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor((byte)(seed * 40), 150, 120), IsAntialias = true };
        canvas.DrawCircle(w / 2f, h / 2f, Math.Min(w, h) * .4f, paint);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static byte[] Tgs()
    {
        const string json = """{"v":"5.7.4","fr":30,"ip":0,"op":30,"w":512,"h":512,"nm":"Test","ddd":0,"assets":[],"layers":[{"ddd":0,"ind":1,"ty":1,"nm":"solid","sr":1,"ks":{"o":{"a":0,"k":100},"r":{"a":0,"k":0},"p":{"a":0,"k":[256,256,0]},"a":{"a":0,"k":[128,128,0]},"s":{"a":0,"k":[100,100,100]}},"sw":256,"sh":256,"sc":"#edaa66","ip":0,"op":30,"st":0,"bm":0}]}""";
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }
}
