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

    // 单实例保护：自启动 + 手动启动会跑出两个实例（双托盘图标、历史重复、持久化互相覆盖）。
    // 持有字段引用防止被 GC；进程退出时由系统释放。
    private static Mutex? _singleInstanceMutex;

    /// <summary>本进程是否因为「已有实例在跑」而退出（任务计划定时重试的常态），用于让日志保持安静。</summary>
    private static bool _isDuplicateInstance;

    /// <summary>退出原因，供 OnExit 记录。默认是未知——能看出是被外力干掉还是走了正常路径。</summary>
    private static string _exitReason = "unknown";

    private static readonly string CrashLog = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard", "crash.log");

    private void OnStartup(object sender, StartupEventArgs e)
    {
        DiagLog.Write("startup", "OnStartup entered");

        // 保活任务每 15 分钟拉一次，它分不清「程序挂了」和「用户主动退出」。
        // 所以用户点过退出就留个标记，保活启动时看到标记就安静地不启动；
        // 而登录 / 手动启动会清掉标记——重新登录或亲手打开都意味着希望它回来。
        bool byKeepAlive = e.Args.Contains(ScheduledTaskService.KeepAliveArgument, StringComparer.OrdinalIgnoreCase);
        if (byKeepAlive && System.IO.File.Exists(QuitMarker))
        {
            DiagLog.Write("startup", "keep-alive launch, but user quit deliberately — staying out of the way");
            _isDuplicateInstance = true; // 复用安静退出路径
            Shutdown();
            return;
        }
        if (!byKeepAlive) ClearQuitMarker();

        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\ClipBoard.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 任务计划每 15 分钟重试一次，绝大多数会走到这里。保持安静：只留一行，
            // 且不写 startup.log / OnExit，避免把真正有价值的启动记录冲掉。
            _isDuplicateInstance = true;
            DiagLog.Write("startup", "duplicate instance (app already running), exiting quietly");
            Shutdown();
            return;
        }
        DiagLog.Write("startup", "single-instance mutex acquired");
        DiagLog.LogEnvironment();

        DispatcherUnhandledException += (_, args) =>
        {
            DiagLog.Error("crash", "DispatcherUnhandled", args.Exception);
            LogCrash("DispatcherUnhandled", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                DiagLog.Error("crash", $"AppDomain terminating={args.IsTerminating}", ex);
                LogCrash("AppDomain", ex);
            }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagLog.Error("crash", "TaskUnobserved", args.Exception);
            LogCrash("TaskUnobserved", args.Exception);
            args.SetObserved();
        };
        // 系统要求结束会话（关机 / 注销 / 安装程序请求重启）时 WPF 会直接关掉应用。
        // 之前 8/19 那次「干净退出」就分不清是用户点了退出还是系统要求的——这里记下来。
        SessionEnding += (_, args) =>
        {
            _exitReason = $"SessionEnding({args.ReasonSessionEnding})";
            DiagLog.Write("exit", $"system session ending: {args.ReasonSessionEnding}, app will be shut down by WPF");
        };
        DiagLog.Write("startup", "exception handlers installed");

        PersistedData persisted;
        using (DiagLog.Phase("persistence-load"))
        {
            Persistence = new PersistenceService();
            persisted = Persistence.Load();
            DiagLog.Write("startup", $"loaded history={persisted.History.Count} folders={persisted.Favorites?.Count ?? 0}");
        }

        Settings = persisted.Settings;
        Favorites = new FavoritesStore(Persistence, persisted);
        History = new HistoryStore(Favorites);
        History.SetPersistence(Persistence);
        Favorites.SetHistoryStore(History);

        using (DiagLog.Phase("blob-load"))
        {
            int images = 0, gifs = 0;
            foreach (var item in persisted.History)
            {
                if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName))
                {
                    item.Image = Persistence.LoadImageThumbnail(item.ImageBlobName); // 只载入缩略图，避免启动时把上百张大图全分辨率读进内存
                    images++;
                }
                else if (item.Kind == ClipKind.Gif && !string.IsNullOrEmpty(item.GifBlobName))
                {
                    item.GifBytes = Persistence.LoadGifBlob(item.GifBlobName);
                    gifs++;
                }
                History.Items.Add(item);
            }
            DiagLog.Write("startup", $"blobs decoded: images={images} gifs={gifs}");
        }

        Settings.PropertyChanged += (_, args) =>
        {
            Favorites.Save();
            if (args.PropertyName == nameof(AppSettings.StartWithWindows))
            {
                // 用户在设置界面主动勾选：应用内设置是唯一权威，允许覆盖任务管理器里的禁用标记。
                bool ok = StartupService.ApplyAll(Settings.StartWithWindows, userInitiated: true);
                DiagLog.Write("autostart", $"user toggled -> {Settings.StartWithWindows}, verified={ok} | {StartupService.Describe()}");
                LogStartup($"toggle -> {Settings.StartWithWindows}, verified={ok}");
            }
        };

        using (DiagLog.Phase("tray-icon"))
        {
            _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
            EnsureTrayIconVisible();
        }

        using (DiagLog.Phase("main-window"))
        {
            _mainWindow = new MainWindow();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_mainWindow).EnsureHandle();
            _mainWindow.Hide();
            DiagLog.Write("startup", $"MainWindow hwnd={hwnd:X}");
        }

        using (DiagLog.Phase("clipboard-monitor"))
        {
            _clipboardMonitor = new ClipboardMonitor(_mainWindow!, History.IsSelfUpdate);
            _clipboardMonitor.ClipboardChanged += OnClipboardChanged;
        }

        using (DiagLog.Phase("hotkey"))
        {
            _hotKey = new HotKeyService(_mainWindow!);
            _hotKey.HotKeyPressed += (_, _) => _mainWindow!.ShowPanel();
        }

        DiagLog.Write("startup", $"=== startup complete in {DiagLog.ElapsedMs}ms ===");

        // 自启动同步放到最后、且在后台线程：它要连任务计划服务（跨进程 COM），
        // 登录高峰时可能慢。绝不能让它挡在托盘图标前面——那正是「进程在跑但托盘没图标」的成因。
        SyncAutostartInBackground();
        var importArgument = e.Args.FirstOrDefault(a => a.StartsWith("--import-stickers=", StringComparison.OrdinalIgnoreCase));
        if (e.Args.Contains("--stickers", StringComparer.OrdinalIgnoreCase) || importArgument != null)
            Dispatcher.BeginInvoke(new Action(async () => await _mainWindow!.OpenStickerTabAsync(importArgument?["--import-stickers=".Length..])));
    }

    /// <summary>后台同步两条自启动路径（注册表 Run 项 + 任务计划登录任务），并把真实状态写进日志。</summary>
    private static void SyncAutostartInBackground() => System.Threading.Tasks.Task.Run(() =>
    {
        try
        {
            using (DiagLog.Phase("autostart-sync"))
            {
                // 静默同步：不覆盖用户在任务管理器里的禁用选择（userInitiated 默认 false）。
                bool applied = StartupService.ApplyAll(Settings.StartWithWindows);
                DiagLog.Write("autostart", $"desired={Settings.StartWithWindows} verified={applied} | {StartupService.Describe()}");
                LogStartup($"launched. desired={Settings.StartWithWindows} verified={applied} " +
                           $"parent={DiagLog.ParentDescription} | {StartupService.Describe()}");
            }
        }
        catch (Exception ex)
        {
            DiagLog.Error("autostart", "background sync failed", ex);
        }
    });

    private void OnClipboardChanged(object? sender, ClipboardChangedEventArgs e)
    {
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

    private void Tray_PreviewContextMenuOpen(object sender, RoutedEventArgs e)
    {
        if (sender is not TaskbarIcon { ContextMenu: { } menu }) return;
        // 必须在预览事件中接管；库随后会覆盖 Placement 和两个 Offset。
        e.Handled = true;
        TrayMenuService.OpenAtCursor(menu);
    }

    private void Tray_Exit(object sender, RoutedEventArgs e)
    {
        _exitReason = "user clicked tray menu 退出";
        // 留下退出标记，否则 15 分钟后保活任务会把它又拉起来，
        // 「退出」就成了摆设——对剪贴板管理器来说尤其糟：
        // 用户明确退出后它又回来继续记录剪贴板、抢走 Ctrl+Alt+V。
        // 标记会在下次登录或手动启动时清除。
        WriteQuitMarker();
        DiagLog.Write("exit", "user chose 退出 from the tray menu; keep-alive suppressed until next logon");
        Shutdown();
    }

    /// <summary>用户主动退出的标记文件，让「退出」的意图能跨进程存活。</summary>
    private static string QuitMarker => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard", "user-quit.marker");

    private static void WriteQuitMarker()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(QuitMarker)!);
            System.IO.File.WriteAllText(QuitMarker, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 用户从托盘菜单退出\n");
        }
        catch (Exception ex)
        {
            DiagLog.Error("exit", "failed to write quit marker", ex);
        }
    }

    private static void ClearQuitMarker()
    {
        try
        {
            if (!System.IO.File.Exists(QuitMarker)) return;
            System.IO.File.Delete(QuitMarker);
            DiagLog.Write("startup", "cleared previous quit marker (user-initiated or logon launch)");
        }
        catch (Exception ex)
        {
            DiagLog.Error("startup", "failed to clear quit marker", ex);
        }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        if (_isDuplicateInstance) return; // 定时重试的重复实例：不写日志、也没有资源要清理

        // 有这行才说明是正常退出；日志里只有启动没有这行 = 进程被杀或崩溃。
        // reason 能区分「用户点了退出」和「系统要求结束会话」——这正是之前查不出的那一点。
        DiagLog.Write("exit", $"OnExit code={e.ApplicationExitCode} reason={_exitReason} aliveFor={DiagLog.ElapsedMs}ms");
        _hotKey?.Dispose();
        _clipboardMonitor?.Dispose();
        _trayIcon?.Dispose();
        Persistence?.FlushSync();
        DiagLog.Write("exit", "=== clean shutdown ===");
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
    // 在启动后延迟重新断言图标可见性，强制重新向通知区域注册。
    // 仅在原生图标确实没注册成功时才重建：无谓的删除/重建会刺激 shell 发出
    // 无悬停的 NIN_POPUPOPEN（正是「贴条无故出现」的诱因），也会让图标闪烁。
    private void EnsureTrayIconVisible()
    {
        void Reassert()
        {
            if (_trayIcon is null) return;
            if (_trayIcon.IsTaskbarIconCreated)
            {
                DiagLog.Write("tray", $"icon already registered at +{DiagLog.ElapsedMs}ms, skip re-assert");
                return;
            }
            CloseTrayPopups();
            _trayIcon.Visibility = Visibility.Collapsed;
            _trayIcon.Visibility = Visibility.Visible;
            DiagLog.Write("tray", $"icon visibility re-asserted at +{DiagLog.ElapsedMs}ms (created={_trayIcon.IsTaskbarIconCreated})");
        }

        foreach (var seconds in new[] { 3, 10, 30 })
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

        StartTrayTooltipWatchdog();
    }

    private void CloseTrayPopups()
    {
        if (_trayIcon?.TrayToolTipResolved is System.Windows.Controls.ToolTip tip && tip.IsOpen)
        {
            tip.IsOpen = false;
            DiagLog.Write("tray", "closed open tray tooltip before icon re-create");
        }
        if (_trayIcon?.ContextMenu is { IsOpen: true } menu)
            menu.IsOpen = false;
    }

    // 兜底看门狗：XAML 已不再设置 ToolTipText，TrayToolTipResolved 应恒为 null、贴条不可能出现。
    // 但仍每 15 秒轮询一次（每次都取当前实例，不依赖事件挂接），发现任何打开超过 ~30 秒的
    // 托盘 tooltip 一律强制关闭并记日志——若日后有人重新加回 ToolTipText，这层保护依然有效。
    private System.Windows.Threading.DispatcherTimer? _tipWatchdog;
    private DateTime _tipOpenSince = DateTime.MinValue;
    private void StartTrayTooltipWatchdog()
    {
        _tipWatchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _tipWatchdog.Tick += (_, _) =>
        {
            if (_trayIcon?.TrayToolTipResolved is System.Windows.Controls.ToolTip tip && tip.IsOpen)
            {
                if (_tipOpenSince == DateTime.MinValue)
                {
                    _tipOpenSince = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - _tipOpenSince).TotalSeconds >= 30)
                {
                    tip.IsOpen = false;
                    _tipOpenSince = DateTime.MinValue;
                    DiagLog.Write("tray", "watchdog force-closed a stuck tray tooltip");
                }
            }
            else
            {
                _tipOpenSince = DateTime.MinValue;
            }
        };
        _tipWatchdog.Start();
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
