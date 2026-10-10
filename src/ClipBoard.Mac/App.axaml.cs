using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ClipBoard.Services;

namespace ClipBoard;

public partial class App : Application
{
    public static bool Preview { get; set; }
    public static string? ProfileDirectory { get; set; }
    public static PersistenceService Persistence { get; private set; } = null!;
    public static FavoritesStore Favorites { get; private set; } = null!;
    public static HistoryStore History { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = new();
    /// <summary>Program.Main 在 Avalonia 平台初始化、加载主题的同时于后台读取 data.json。</summary>
    internal static Task<(PersistenceService Service, Models.PersistedData Data)>? PreloadedData { get; set; }
    private MacHotkey? _hotkey;
    private DispatcherTimer? _poll;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private long _sequence;
    internal int PreviousApp { get; private set; }
    internal bool Quitting { get; private set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Models.PersistedData data;
            if (PreloadedData is { } preload) { (Persistence, data) = preload.GetAwaiter().GetResult(); PreloadedData = null; }
            else { Persistence = new(ProfileDirectory); data = Persistence.Load(); } // 测试等不经过 Program.Main 的宿主
            Settings = data.Settings;
            Favorites = new(Persistence, data);
            History = new(Favorites);
            History.SetPersistence(Persistence);
            Favorites.SetHistoryStore(History);
            History.Load(data.History);
            Favorites.EnsureDefaultMemeFolder();
            _window = new MainWindow();
            desktop.MainWindow = _window;
            if (!Preview && OperatingSystem.IsMacOS())
            {
                _sequence = MacClipboard.Sequence;
                try { _hotkey = new(ShowPanel); }
                catch (Exception ex) { _window.SetStatus(ex.Message); }
                _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _poll.Tick += (_, _) => Poll();
                _poll.Start();
                var menu = new NativeMenu();
                var show = new NativeMenuItem("显示面板  ⌃⌘V"); show.Click += (_, _) => ShowPanel();
                var quit = new NativeMenuItem("退出 ClipBoard"); quit.Click += (_, _) => Quit();
                menu.Items.Add(show); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quit);
                using var icon = AssetLoader.Open(new Uri("avares://ClipBoard.Mac/Assets/menubar.png"));
                _tray = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = "ClipBoard", Menu = menu, IsVisible = true };
                MacOSProperties.SetIsTemplateIcon(_tray, true); // 模板图：深色菜单栏显示为白色，浅色菜单栏显示为黑色
                TrayIcon.SetIcons(this, new TrayIcons { _tray });
            }
            desktop.Exit += (_, _) => { Favorites.Save(); Persistence.FlushSync(); _poll?.Stop(); _hotkey?.Dispose(); _tray?.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
    private void Poll()
    {
        try
        {
            long sequence;
            using (new MacNative.Pool()) // 两次查询共用一个自动释放池
            {
                int pid = MacNative.ForegroundPidInPool();
                if (pid > 0 && pid != Environment.ProcessId) PreviousApp = pid;
                sequence = MacClipboard.SequenceInPool;
            }
            if (sequence == _sequence) return;
            _sequence = sequence;
            var content = MacClipboard.Read();
            if (content != null && MacClipboard.Sequence == sequence) History.Capture(content);
        }
        catch (Exception ex) { _window?.SetStatus("读取剪贴板失败：" + ex.Message); }
    }
    public void ShowPanel()
    {
        if (_window == null || Views.Dialogs.ActivateModal(_window)) return;
        if (!Preview && OperatingSystem.IsMacOS())
        {
            int pid = MacNative.ForegroundPid();
            if (pid > 0 && pid != Environment.ProcessId) PreviousApp = pid;
            MacNative.UnhideApplication();
        }
        // 重新打开时清空搜索并回到第一条；已经显示（例如固定窗口）时只激活，不打断正在进行的搜索。
        if (!_window.IsVisible) _window.ResetView();
        _window.Show(); _window.Activate(); _window.FocusSearch();
    }
    // 界面预览和自动测试不碰系统剪贴板，只记录最后一次请求。
    internal (Guid Item, bool Paste, bool PlainText)? LastPreviewCopy { get; private set; }
    private bool _accessibilityPrompted;
    public async Task CopyAsync(Models.ClipItem item, bool paste, bool plainText = false)
    {
        if (Preview) { LastPreviewCopy = (item.Id, paste, plainText); _window?.SetStatus("界面预览模式不读写系统剪贴板。"); return; }
        if (!MacClipboard.Write(item, Persistence, plainText)) throw new IOException("剪贴板写入失败。");
        _sequence = MacClipboard.Sequence;
        if (!paste) { _window?.SetStatus(plainText ? "已复制为纯文本。" : "已复制。"); return; }
        _window?.SetStatus("");
        _window?.Hide();
        // 无论能否自动粘贴都不再弹回面板：内容已在剪贴板，焦点已交还原应用，可直接按 ⌘V。原因留到下次打开面板时显示。
        int target = PreviousApp;
        var result = await MacNative.PasteAsync(target);
        bool ax = MacNative.IsAxTrusted, post = MacNative.CanPostEvents;
        Diag($"paste result={result} ax={ax} postEvent={post} target={target} {MacNative.BundleId(target)} " +
            $"front={MacNative.ForegroundPid()} plain={plainText} macOS={Environment.OSVersion.Version} app={Environment.ProcessPath}");
        // 缺哪项授权就申请哪项，每次启动只弹一次；申请会把当前运行的这份程序登记到系统授权里。
        if (!(ax && post) && !_accessibilityPrompted) { _accessibilityPrompted = true; MacNative.RequestAccessibility(); }
        if (result == MacNative.PasteResult.NotTrusted)
            _window?.SetStatus("已复制，但没有自动粘贴：ClipBoard 还没有辅助功能权限。请到系统设置 → 隐私与安全性 → " + AccessibilityPane + "打开 ClipBoard；" +
                "更新应用后开关虽显示已打开却无效时，先用“−”移除 ClipBoard 再重新添加，然后退出并重新打开 ClipBoard。");
        else if (result != MacNative.PasteResult.Pasted)
            _window?.SetStatus("已复制，但没能切回原应用自动粘贴，请切回后按 ⌘V。");
        else if (!post)
            _window?.SetStatus("已发送 ⌘V。如果没有粘贴进去，请在系统弹出的提示中允许 ClipBoard 控制电脑，或到 " + AccessibilityPane + "重新打开 ClipBoard 的开关后重试。");
    }
    /// <summary>macOS 27 把隐私设置里的“辅助功能”改名为“设备控制与数据访问”。</summary>
    internal static string AccessibilityPane => OperatingSystem.IsMacOSVersionAtLeast(27) ? "设备控制与数据访问（Device Control and Data Access）" : "辅助功能";
    // 每次自动粘贴记一行权限和焦点状态，排查“面板消失但没粘贴”。不记录剪贴板内容。
    private static void Diag(string line)
    {
        try
        {
            var path = Path.Combine(Persistence.RootDirectory, "diag.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void Quit()
    {
        Quitting = true;
        _window?.CancelWork();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
