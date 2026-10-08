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
            Persistence = new(ProfileDirectory);
            var data = Persistence.Load();
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
                using var icon = AssetLoader.Open(new Uri("avares://ClipBoard.Mac/Assets/tray.ico"));
                _tray = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = "ClipBoard", Menu = menu, IsVisible = true };
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
            int pid = MacNative.ForegroundPid();
            if (pid > 0 && pid != Environment.ProcessId) PreviousApp = pid;
            long sequence = MacClipboard.Sequence;
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
        Diag($"paste result={result} ax={MacNative.IsAxTrusted} postEvent={MacNative.CanPostEvents} target={target} {MacNative.BundleId(target)} " +
            $"front={MacNative.ForegroundPid()} plain={plainText} macOS={Environment.OSVersion.Version}");
        if (result == MacNative.PasteResult.NotTrusted)
        {
            _window?.SetStatus("已复制，但没有自动粘贴：ClipBoard 还没有辅助功能权限。请到系统设置 → 隐私与安全性 → 辅助功能打开 ClipBoard；" +
                "更新应用后开关虽显示已打开却无效时，先用“−”移除 ClipBoard，再重新添加。");
            if (!_accessibilityPrompted) { _accessibilityPrompted = true; MacNative.RequestAccessibility(); }
        }
        else if (result != MacNative.PasteResult.Pasted)
            _window?.SetStatus("已复制，但没能切回原应用自动粘贴，请切回后按 ⌘V。");
    }
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
