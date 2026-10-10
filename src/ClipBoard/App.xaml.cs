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

    // 退出标记和单实例锁在 Program.Main 里、WPF 启动之前就处理完了：走到这里的一定是抢到锁的那个实例。

    /// <summary>退出原因，供 OnExit 记录。默认是未知——能看出是被外力干掉还是走了正常路径。</summary>
    private static string _exitReason = "unknown";

    private static readonly string CrashLog = System.IO.Path.Combine(
        BenchMode.Root ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard"), "crash.log");

    private void OnStartup(object sender, StartupEventArgs e)
    {
        DiagLog.Write("startup", "OnStartup entered");
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
            Persistence = BenchMode.Enabled ? new PersistenceService(BenchMode.ProfileDir) : new PersistenceService();
            persisted = Persistence.Load();
            DiagLog.Write("startup", $"loaded history={persisted.History.Count} folders={persisted.Favorites?.Count ?? 0}");
        }

        // 缩略图解码（顶置、收藏、历史）立即在后台线程并行开始，与下面建集合、托盘和主窗口重叠进行；
        // 结果在 thumbs-join 按原顺序赋回，那时 OnStartup 还没返回，任何界面和第一帧都还没用到这些条目。
        var media = FavoritesStore.MediaPreload.Start(Persistence, persisted, includeHistory: true);

        Settings = persisted.Settings;
        Favorites = new FavoritesStore(Persistence, persisted, loadMedia: false);
        History = new HistoryStore(Favorites);
        History.SetPersistence(Persistence);
        Favorites.SetHistoryStore(History);

        using (DiagLog.Phase("blob-load"))
        {
            foreach (var item in persisted.History)
                History.Items.Add(item);
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

        if (!BenchMode.Enabled)
        {
            using (DiagLog.Phase("tray-icon"))
            {
                _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
                EnsureTrayIconVisible();
            }
        }

        using (DiagLog.Phase("main-window"))
        {
            _mainWindow = new MainWindow();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_mainWindow).EnsureHandle();
            _mainWindow.Hide();
            DiagLog.Write("startup", $"MainWindow hwnd={hwnd:X}");
        }

        using (DiagLog.Phase("thumbs-join"))
        {
            media.Complete(WaitWithoutPumping);
            DiagLog.Write("startup", $"blobs decoded: images={media.HistoryImages} gifs={media.HistoryGifs} | {media.Summary}");
        }

        if (BenchMode.Enabled)
        {
            DiagLog.Write("startup", $"=== startup complete in {DiagLog.ElapsedMs}ms (bench) ===");
            Dispatcher.BeginInvoke(new Action(async () => await BenchMode.RunAsync(_mainWindow)));
            return;
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
                History.AddText(e.Text, e.Rich);
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
        Program.WriteQuitMarker();
        DiagLog.Write("exit", "user chose 退出 from the tray menu; keep-alive suppressed until next logon");
        Shutdown();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
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
                ApplyNativeTrayTip();
                return;
            }
            CloseTrayPopups();
            _trayIcon.Visibility = Visibility.Collapsed;
            _trayIcon.Visibility = Visibility.Visible;
            DiagLog.Write("tray", $"icon visibility re-asserted at +{DiagLog.ElapsedMs}ms (created={_trayIcon.IsTaskbarIconCreated})");
            ApplyNativeTrayTip();
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
            // explorer 重启后 Hardcodet 会重新添加图标，不带系统 tooltip；顺带补上（只修改提示，不重建图标）。
            ApplyNativeTrayTip(log: false);
        };
        _tipWatchdog.Start();
    }

    // 悬停显示名称用系统自带的 tooltip（NIF_SHOWTIP），由 shell 自己显示和关闭，不会像 Hardcodet 的
    // WPF tooltip 那样因收不到 NIN_POPUPCLOSE 而变成孤儿贴条。Hardcodet 不暴露这个选项，
    // 所以取它内部的窗口句柄和图标 ID，自己发一次 NIM_MODIFY；ToolTipText 仍保持不设置。
    private const string TrayTipText = "ClipBoard — Ctrl+Alt+V";
    private void ApplyNativeTrayTip(bool log = true)
    {
        try
        {
            if (_trayIcon is not { IsTaskbarIconCreated: true }) return;
            var field = typeof(TaskbarIcon).GetField("iconData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var iconData = field?.GetValue(_trayIcon);
            if (iconData is null) { if (log) DiagLog.Write("tray", "native tooltip skipped: iconData not found"); return; }
            var type = iconData.GetType();
            var data = new NotifyIconDataW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NotifyIconDataW>(),
                hWnd = (IntPtr)type.GetField("WindowHandle")!.GetValue(iconData)!,
                uID = (uint)type.GetField("TaskbarIconId")!.GetValue(iconData)!,
                uFlags = NifTip | NifShowTip,
                szTip = TrayTipText,
            };
            bool ok = Shell_NotifyIconW(NimModify, ref data);
            if (log) DiagLog.Write("tray", $"native tooltip set: {ok}");
        }
        catch (Exception ex) when (ex is System.Reflection.TargetException or InvalidCastException or NullReferenceException)
        {
            if (log) DiagLog.Write("tray", "native tooltip failed: " + ex.Message);
        }
    }

    private const uint NimModify = 0x1, NifTip = 0x4, NifShowTip = 0x80;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct NotifyIconDataW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconDataW data);

    // UI 线程是 STA：普通的 Wait 在等待期间会派发别的线程 / 进程发来的消息，让托盘和窗口钩子在 OnStartup
    // 半途中被重入。启动阶段的等待因此直接走内核等待，不泵消息——和原来全在 UI 线程上顺序执行时一样。
    // 万一 15 秒还没等到（理论上只有某个解码器要回调 UI 线程才会如此），退回普通等待，宁可重入也不能卡死启动。
    private static void WaitWithoutPumping(WaitHandle handle)
    {
        if (WaitForSingleObject(handle.SafeWaitHandle, 15000) == WaitObject0) return;
        DiagLog.Write("startup", "non-pumping wait timed out after 15s, falling back to a pumping wait");
        handle.WaitOne();
    }

    private const uint WaitObject0 = 0;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeWaitHandle handle, uint milliseconds);

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
