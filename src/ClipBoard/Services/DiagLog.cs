using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipBoard.Services;

/// <summary>
/// 统一诊断日志（%APPDATA%\ClipBoard\diag.log）。
///
/// 核心目的：让「下一次开机」能自证——只看这一个文件就能回答
/// 「进程到底有没有被创建？是谁创建的？卡在或崩在哪一步？」
///
/// 关键设计：第一行在模块初始化时写出，早于 WPF 的任何初始化、
/// 早于 OnStartup、早于持久化加载。因此
///   「日志里没有本次开机的记录」== 「进程从未被创建」，
/// 不再与「启动了但早期就崩了」混淆——之前排查正是卡在这个二义性上。
///
/// 隐私：本程序是剪贴板管理器，日志只记长度 / 类型 / 数量 / 耗时，
/// 永远不写剪贴板内容本身。新增日志点时请遵守这条。
/// </summary>
public static class DiagLog
{
    private const long MaxBytes = 1024 * 1024; // 单文件上限，超出后滚动到 .1
    private static readonly object Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard");

    private static string LogPath => Path.Combine(Dir, "diag.log");
    private static string RolledPath => Path.Combine(Dir, "diag.1.log");

    /// <summary>本次进程的会话标识，用于在日志里区分同一天的多次启动。</summary>
    public static string SessionId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>进程启动至今的毫秒数（单调，不受开机时系统对时跳变影响）。</summary>
    public static long ElapsedMs => Uptime.ElapsedMilliseconds;

    /// <summary>父进程描述，例如 "explorer.exe(1234)" / "svchost.exe(999)" / "&lt;none&gt;"。</summary>
    public static string ParentDescription { get; private set; } = "<pending>";

    /// <summary>
    /// 模块初始化：CLR 加载本程序集时立即执行，是本进程能写日志的最早时刻。
    /// 整体 try/catch —— 诊断日志绝不能成为启动失败的原因。
    ///
    /// 这里只写一行「见证日志」：任务计划每 15 分钟就会重试拉起一次，
    /// 绝大多数进程会因为单实例锁立刻退出，若每次都写一大段就会把真正有用的记录冲掉。
    /// 完整环境信息交给 <see cref="LogEnvironment"/>，只有真正启动成功的那个进程才写。
    /// </summary>
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            ParentDescription = DescribeParent();
            Write("boot", $"=== process created === pid={Environment.ProcessId} parent={ParentDescription} " +
                          $"exe={Environment.ProcessPath ?? "<null>"} tickSinceBoot={Environment.TickCount64}ms");
        }
        catch { }
    }

    /// <summary>完整环境信息，由抢到单实例锁的进程调用（每次真正启动只记一次）。</summary>
    public static void LogEnvironment()
    {
        try
        {
            Write("boot", $"cmdline={Environment.CommandLine}");
            Write("boot", $"user={Environment.UserName} winSession={Process.GetCurrentProcess().SessionId} " +
                          $"os={Environment.OSVersion.Version} clr={Environment.Version} " +
                          $"utcOffset={TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)}");
        }
        catch { }
    }

    /// <summary>写一行日志。category 用于分类（boot/startup/tray/hotkey/autostart/exit...）。</summary>
    public static void Write(string category, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                Roll();
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} +{Uptime.ElapsedMilliseconds,-6} [{SessionId}] [{category}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { }
    }

    /// <summary>记录异常（带来源标签），堆栈完整写入。</summary>
    public static void Error(string category, string message, Exception ex)
        => Write(category, $"ERROR {message} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}");

    /// <summary>
    /// 标记一个启动阶段，using 包裹即可自动记录开始 / 结束与耗时。
    /// 若进程在阶段中途死掉，日志里只有 begin 没有 end——正好定位到卡死的那一步。
    /// </summary>
    public static IDisposable Phase(string name)
    {
        Write("phase", $"begin {name}");
        return new PhaseScope(name);
    }

    private sealed class PhaseScope(string name) : IDisposable
    {
        private readonly long _start = Uptime.ElapsedMilliseconds;
        public void Dispose() => Write("phase", $"end   {name} ({Uptime.ElapsedMilliseconds - _start}ms)");
    }

    /// <summary>超过上限时把当前日志滚动为 .1（只保留两代，避免无限增长）。</summary>
    private static void Roll()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            if (File.Exists(RolledPath)) File.Delete(RolledPath);
            File.Move(LogPath, RolledPath);
        }
        catch { }
    }

    // ---- 父进程识别：用来证明「到底是不是 explorer 拉起的我们」----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int infoClass, ref ProcessBasicInformation info, int infoLength, out int returnLength);

    /// <summary>
    /// 解析父进程。pid 会被系统回收复用，所以额外校验父进程的启动时间必须早于本进程，
    /// 否则只报 &lt;recycled&gt; 而不是给出一个自信但错误的名字。
    /// </summary>
    private static string DescribeParent()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(self.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0)
                return "<query-failed>";

            int parentPid = pbi.InheritedFromUniqueProcessId.ToInt32();
            if (parentPid <= 0) return "<none>";

            try
            {
                var parent = Process.GetProcessById(parentPid);
                // 父进程比自己还晚启动 => pid 已被复用，拿到的是别的进程
                if (parent.StartTime > self.StartTime) return $"<recycled>({parentPid})";
                return $"{parent.ProcessName}.exe({parentPid})";
            }
            catch
            {
                return $"<exited>({parentPid})"; // 父进程已退出（例如 explorer 拉起后立刻不再持有）
            }
        }
        catch
        {
            return "<error>";
        }
    }
}
