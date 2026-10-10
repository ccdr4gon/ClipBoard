using System.IO;
using System.Runtime.CompilerServices;
using ClipBoard.Services;

namespace ClipBoard;

/// <summary>
/// 程序入口（csproj 的 StartupObject）。退出标记和单实例锁在 WPF 启动之前处理：
/// 保活任务每 15 分钟拉起一次进程，绝大多数会因为已有实例在跑而立刻退出，
/// 放在这里判断，这些进程就不必加载 WPF、解析 App.xaml、启动 Dispatcher。
///
/// 注意：Main 和它在 <see cref="RunApp"/> 之前调用的代码都不能引用 App 或任何 WPF 类型，
/// 否则编译 Main 时就会提前加载 WPF 程序集，省下的又回来了。
/// </summary>
internal static class Program
{
    // 单实例保护：自启动 + 手动启动会跑出两个实例（双托盘图标、历史重复、持久化互相覆盖）。
    // 必须是静态字段：局部变量在最后一次使用后就可能被回收，锁随之释放。进程退出时由系统释放。
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    private static void Main()
    {
        // 保活任务每 15 分钟拉一次，它分不清「程序挂了」和「用户主动退出」。
        // 所以用户点过退出就留个标记，保活启动时看到标记就安静地不启动；
        // 而登录 / 手动启动会清掉标记——重新登录或亲手打开都意味着希望它回来。
        bool byKeepAlive = false;
        var args = Environment.GetCommandLineArgs(); // [0] 是程序路径，WPF 的 e.Args 同样不含它
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], ScheduledTaskService.KeepAliveArgument, StringComparison.OrdinalIgnoreCase))
            {
                byKeepAlive = true;
                break;
            }
        }
        if (byKeepAlive && File.Exists(QuitMarker))
        {
            DiagLog.Write("startup", "keep-alive launch, but user quit deliberately — staying out of the way");
            return;
        }
        // 基准模式与正在使用的实例并存：不碰退出标记，单实例锁用独立的名字。
        if (!byKeepAlive && !BenchMode.Enabled) ClearQuitMarker();

        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            BenchMode.Enabled ? $@"Local\ClipBoard.Bench.{Environment.ProcessId}" : @"Local\ClipBoard.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 任务计划每 15 分钟重试一次，绝大多数会走到这里。保持安静：只留一行，
            // 且不写 startup.log / OnExit，避免把真正有价值的启动记录冲掉。
            DiagLog.Write("startup", "duplicate instance (app already running), exiting quietly");
            return;
        }
        DiagLog.Write("startup", "single-instance mutex acquired");
        RunApp();
    }

    // 与 WPF 生成的 App.Main 相同的三步。单独成方法且禁止内联：只有走到这里才加载 WPF。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>用户主动退出的标记文件，让「退出」的意图能跨进程存活。</summary>
    private static string QuitMarker => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard", "user-quit.marker");

    internal static void WriteQuitMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QuitMarker)!);
            File.WriteAllText(QuitMarker, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 用户从托盘菜单退出\n");
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
            if (!File.Exists(QuitMarker)) return;
            File.Delete(QuitMarker);
            DiagLog.Write("startup", "cleared previous quit marker (user-initiated or logon launch)");
        }
        catch (Exception ex)
        {
            DiagLog.Error("startup", "failed to clear quit marker", ex);
        }
    }
}
