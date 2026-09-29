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
        if (!Preview && OperatingSystem.IsMacOS())
        {
            int pid = MacNative.ForegroundPid();
            if (pid > 0 && pid != Environment.ProcessId) PreviousApp = pid;
        }
        _window?.Show(); _window?.Activate(); _window?.FocusSearch();
    }
    public async Task CopyAsync(Models.ClipItem item, bool paste)
    {
        if (Preview) { _window?.SetStatus("界面预览模式不读写系统剪贴板。"); return; }
        if (!MacClipboard.Write(item, Persistence)) throw new IOException("剪贴板写入失败。");
        _sequence = MacClipboard.Sequence;
        if (!paste) { _window?.SetStatus("已复制。"); return; }
        _window?.Hide();
        if (!await MacNative.PasteAsync(PreviousApp))
        {
            ShowPanel();
            _window?.SetStatus("已复制。自动粘贴需要在系统设置 → 隐私与安全性 → 辅助功能中允许 ClipBoard；也可以切回原应用按 ⌘V。");
        }
    }
    public void Quit()
    {
        Quitting = true;
        _window?.CancelWork();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
