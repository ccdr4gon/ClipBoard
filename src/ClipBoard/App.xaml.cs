using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using ClipBoard.Models;
using ClipBoard.Services;

namespace ClipBoard;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MainWindow? _mainWindow;

    public static HistoryStore History { get; private set; } = null!;
    public static FavoritesStore Favorites { get; private set; } = null!;
    public static PersistenceService Persistence { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;

    private ClipboardMonitor? _clipboardMonitor;
    private HotKeyService? _hotKey;

    private static readonly string CrashLog = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard", "crash.log");

    private void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandled", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogCrash("AppDomain", ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("TaskUnobserved", args.Exception);
            args.SetObserved();
        };

        Persistence = new PersistenceService();
        var persisted = Persistence.Load();

        Settings = persisted.Settings;
        Favorites = new FavoritesStore(Persistence, persisted);
        History = new HistoryStore(Favorites);
        History.SetPersistence(Persistence);
        Favorites.SetHistoryStore(History);

        foreach (var item in persisted.History)
        {
            if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName))
                item.Image = Persistence.LoadImageThumbnail(item.ImageBlobName); // 只载入缩略图，避免启动时把上百张大图全分辨率读进内存
            else if (item.Kind == ClipKind.Gif && !string.IsNullOrEmpty(item.GifBlobName))
                item.GifBytes = Persistence.LoadGifBlob(item.GifBlobName);
            History.Items.Add(item);
        }

        Settings.PropertyChanged += (_, args) =>
        {
            Favorites.Save();
            if (args.PropertyName == nameof(AppSettings.StartWithWindows))
            {
                bool ok = StartupService.Apply(Settings.StartWithWindows);
                LogStartup($"toggle -> {Settings.StartWithWindows}, verified={ok}");
            }
        };

        // 同步注册表：默认开启自启动；路径变更（如移动 exe）时也会刷新。
        // 写入后回读校验，并把“注册表真实状态”写入 startup.log，便于确认是否真的开机启动。
        bool applied = StartupService.Apply(Settings.StartWithWindows);
        LogStartup($"launched. desired={Settings.StartWithWindows} verified={applied} " +
                   $"enabled={StartupService.IsEnabled()} pointsToCurrentExe={StartupService.PointsToCurrentExe()} " +
                   $"regValue={StartupService.CurrentValue() ?? "<none>"} exe={StartupService.ExePath}");

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        EnsureTrayIconVisible();

        _mainWindow = new MainWindow();
        new System.Windows.Interop.WindowInteropHelper(_mainWindow).EnsureHandle();
        _mainWindow.Hide();

        _clipboardMonitor = new ClipboardMonitor(_mainWindow);
        _clipboardMonitor.ClipboardChanged += OnClipboardChanged;

        _hotKey = new HotKeyService(_mainWindow);
        _hotKey.HotKeyPressed += (_, _) => _mainWindow!.ShowPanel();
    }

    private void OnClipboardChanged(object? sender, ClipboardChangedEventArgs e)
    {
        if (History.IsSelfUpdate)
        {
            History.ClearSelfUpdate();
            return;
        }

        switch (e.Kind)
        {
            case ClipKind.Text when e.Text is { Length: > 0 }:
                History.AddText(e.Text);
                break;
            case ClipKind.Image when e.Image is not null:
                History.AddImage(e.Image);
                break;
            case ClipKind.Files when e.Files is { Length: > 0 }:
                History.AddFiles(e.Files);
                break;
            case ClipKind.Gif when e.GifBytes is { Length: > 0 }:
                History.AddGif(e.GifBytes);
                break;
        }
    }

    private void Tray_ShowPanel(object sender, RoutedEventArgs e) => _mainWindow?.ShowPanel();

    private void Tray_ShowSettings(object sender, RoutedEventArgs e) => _mainWindow?.OpenSettingsWindow();

    private void Tray_Exit(object sender, RoutedEventArgs e) => Shutdown();

    private void OnExit(object sender, ExitEventArgs e)
    {
        _hotKey?.Dispose();
        _clipboardMonitor?.Dispose();
        _trayIcon?.Dispose();
        Persistence?.FlushSync();
    }

    private static readonly string StartupLog = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard", "startup.log");

    private static void LogStartup(string message)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(StartupLog)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(StartupLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }

    // 登录时托盘程序常与 explorer 初始化通知区域竞态，导致图标不显示（进程其实已在跑）。
    // 在启动后延迟重新断言一次图标可见性，强制重新向通知区域注册。
    private void EnsureTrayIconVisible()
    {
        void Reassert()
        {
            if (_trayIcon is null) return;
            _trayIcon.Visibility = Visibility.Collapsed;
            _trayIcon.Visibility = Visibility.Visible;
        }

        foreach (var seconds in new[] { 3, 10 })
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds),
            };
            timer.Tick += (s, _) =>
            {
                ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                Reassert();
            };
            timer.Start();
        }
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(CrashLog)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(CrashLog,
                $"\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] ===\n{ex}\n");
        }
        catch { }
    }
}
