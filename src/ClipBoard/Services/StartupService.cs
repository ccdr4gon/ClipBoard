using System.IO;
using Microsoft.Win32;

namespace ClipBoard.Services;

/// <summary>
/// 通过 HKCU\...\Run 管理开机自启动（无需管理员）。
///
/// 关键策略：自启动目标永远是“规范安装路径”（自包含安装版），
/// 绝不把 bin\Debug / bin\Release 等构建输出当作自启动目标。
/// 因此“构建并运行 Debug 版做测试”不会再把开机自启动改坏——
/// 即使从 Debug 版启动，它也只会把 Run 键维护成安装版的路径。
/// （框架依赖的 Debug 构建在 Windows 登录时经常静默拉不起来，所以只能让安装版自启。）
///
/// 除 Run 键外还必须维护 StartupApproved 的“已启用”标记：
/// 本机（Win11 25H2）的 explorer 登录时只启动带显式 02 标记的 HKCU Run 项，
/// 缺失标记的项会被静默跳过（历史行为本是“缺失=启用”，此为 24H2/25H2 的回归）。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "ClipBoard";

    // StartupApproved 的“已启用”标志：首字节 02，其余 11 字节清零
    //（任务管理器禁用时首字节改奇数，后 8 字节存放禁用时刻的 FILETIME）。
    private static readonly byte[] ApprovedEnabledBlob = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>规范安装路径：%LOCALAPPDATA%\Programs\ClipBoard\ClipBoard.exe</summary>
    public static string InstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "ClipBoard", "ClipBoard.exe");

    /// <summary>当前进程 exe 路径。</summary>
    public static string? ProcessPath => Environment.ProcessPath;

    /// <summary>是否已安装到规范位置。</summary>
    public static bool IsInstalled => File.Exists(InstallPath);

    private static bool IsBuildOutput(string path) =>
        path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 自启动应指向的 exe：
    /// ① 已安装 → 安装版路径；
    /// ② 否则当前是“非构建输出”的便携运行 → 当前路径；
    /// ③ 只有构建输出可用 → null（不注册，避免拿 bin\Debug 当自启目标）。
    /// </summary>
    public static string? AutostartTarget
    {
        get
        {
            if (IsInstalled) return InstallPath;
            if (ProcessPath is { Length: > 0 } p && !IsBuildOutput(p)) return p;
            return null;
        }
    }

    private static string? ExpectedValue =>
        AutostartTarget is { Length: > 0 } t ? $"\"{t}\"" : null;

    /// <summary>
    /// StartupApproved 三态：null=标记不存在，true=显式启用（首字节偶数），false=被任务管理器禁用（首字节奇数）。
    /// </summary>
    public static bool? ApprovedState
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
                return key?.GetValue(ValueName) is byte[] { Length: > 0 } b ? (b[0] & 0x01) == 0 : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>诊断 / 日志用的 StartupApproved 状态文本。</summary>
    public static string ApprovedStateText => ApprovedState switch
    {
        true => "enabled",
        false => "disabled(任务管理器)",
        null => "<missing>",
    };

    /// <summary>
    /// 写入 / 删除自启动项；写入后回读校验。
    /// 开启时始终写 <see cref="AutostartTarget"/>；若没有合适目标（只有构建输出可用）则保持现状不动。
    /// 同时确保 StartupApproved 启用标记：标记缺失时补写 02；
    /// 已被任务管理器禁用（奇数首字节）时只有用户在设置界面主动勾选（userInitiated）才覆盖回 02，
    /// 开机静默同步尊重用户在任务管理器里的选择，绝不悄悄翻回去。
    /// 关闭时两处一并删除。返回注册表最终状态是否符合期望。
    /// </summary>
    public static bool Apply(bool enable, bool userInitiated = false)
    {
        try
        {
            if (enable)
            {
                if (ExpectedValue is null) return false; // 无合适目标：不写、也不破坏现有项
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null) return false;
                key.SetValue(ValueName, ExpectedValue, RegistryValueKind.String);

                var approved = ApprovedState;
                if (approved is null || (approved == false && userInitiated))
                {
                    using var ak = Registry.CurrentUser.CreateSubKey(ApprovedKeyPath);
                    ak?.SetValue(ValueName, ApprovedEnabledBlob, RegistryValueKind.Binary);
                }
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                // 孤立的 StartupApproved 标记会残留在任务管理器的启动应用列表里，一并清掉。
                using var ak = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
                ak?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            return false;
        }

        return enable ? RunVerified : !IsEnabled();
    }

    /// <summary>
    /// 同时维护注册表 Run 项与任务计划登录任务，返回「下次开机是否真的会被拉起」。
    ///
    /// 之所以两条路都留：这台机器上 explorer 的 Run 枚举从未启动过本程序
    /// （见 <see cref="ScheduledTaskService"/> 的说明），任务计划是可靠的那条；
    /// Run 项保留作冗余，App 的单实例锁保证不会重复启动。
    /// </summary>
    public static bool ApplyAll(bool enable, bool userInitiated = false)
    {
        bool runOk = Apply(enable, userInitiated);

        bool taskOk;
        if (enable)
        {
            var target = AutostartTarget;
            if (target is null)
            {
                taskOk = false; // 只有构建输出可用：不注册，避免把 bin\Debug 当自启目标
            }
            else
            {
                var snap = ScheduledTaskService.Query(target);
                // Unavailable 也重试：注册是幂等的（CREATE_OR_UPDATE），
                // 任务计划服务在登录高峰偶发不可用，不该让自启动一直坏着。
                bool needRegister = snap.State is TaskStartupState.Missing
                                                or TaskStartupState.Mismatched
                                                or TaskStartupState.Unavailable
                                    || (snap.State == TaskStartupState.Disabled && userInitiated);
                taskOk = needRegister ? ScheduledTaskService.Register(target)
                                      : snap.State == TaskStartupState.Ok;
            }
        }
        else
        {
            taskOk = ScheduledTaskService.Unregister();
        }

        return enable ? (taskOk || runOk) : (taskOk && runOk);
    }

    /// <summary>登录任务的当前状态。</summary>
    public static TaskStartupState TaskState => ScheduledTaskService.Query(AutostartTarget).State;

    /// <summary>一行诊断文本：两条自启动路径的真实状态，写进日志用。</summary>
    public static string Describe()
    {
        var snap = ScheduledTaskService.Query(AutostartTarget);
        return $"installed={IsInstalled} runningFrom={ProcessPath} target={AutostartTarget ?? "<none>"} " +
               $"| run: value={CurrentValue() ?? "<none>"} approved={ApprovedStateText} verified={RunVerified} " +
               $"| task: state={snap.State} command={snap.Command ?? "<none>"}";
    }

    /// <summary>注册表里是否存在非空自启动项。</summary>
    public static bool IsEnabled() => !string.IsNullOrEmpty(CurrentValue());

    /// <summary>
    /// 注册表这条路是否完备：Run 键指向当前应有的自启动目标（安装版），
    /// 且 StartupApproved 为显式启用——缺失或被任务管理器禁用时 explorer 都不会启动。
    /// 注意：本机上即使这里为 true，explorer 实测仍可能不启动它，所以还有任务计划那条路。
    /// </summary>
    public static bool RunVerified
    {
        get
        {
            var cur = CurrentValue();
            return !string.IsNullOrEmpty(cur) && ExpectedValue != null
                   && string.Equals(cur, ExpectedValue, StringComparison.OrdinalIgnoreCase)
                   && ApprovedState == true;
        }
    }

    /// <summary>下次开机是否真的会被拉起：两条路径任意一条完备即可。</summary>
    public static bool IsVerified => TaskState == TaskStartupState.Ok || RunVerified;

    /// <summary>返回注册表里当前存的命令字符串（诊断/界面用），不存在则 null。</summary>
    public static string? CurrentValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
