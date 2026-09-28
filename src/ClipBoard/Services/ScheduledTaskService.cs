using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Xml.Linq;

namespace ClipBoard.Services;

/// <summary>任务的状态。</summary>
public enum TaskStartupState
{
    /// <summary>任务不存在。</summary>
    Missing,
    /// <summary>任务存在但被禁用。</summary>
    Disabled,
    /// <summary>任务存在且启用，但指向别的 exe（或该 exe 已不存在），或定义版本过旧需要更新。</summary>
    Mismatched,
    /// <summary>任务存在、启用、指向正确的安装版、且是当前版本的定义。</summary>
    Ok,
    /// <summary>任务计划服务暂时不可用（应重试，不代表任务不存在）。</summary>
    Unavailable,
}

/// <summary>Query 的一次性快照，避免为每个属性反复连任务计划服务。</summary>
public readonly record struct TaskSnapshot(TaskStartupState State, string? Command);

/// <summary>
/// 用「任务计划程序」实现自启动与掉线自愈（全程无需管理员）。
///
/// 为什么不用 HKCU\Run：这台机器上 explorer 的 Run 枚举从未启动过本程序
/// （Shell-Core 日志里多次登录都没有对应的 9707 事件，而同键其他项每次都正常启动，
/// 且 Run 值与 StartupApproved 标记都反复验证过是正确的）。原因未能查明，
/// 但任务计划走的是完全不同的路径，不经过 explorer 的启动项枚举。
/// Run 键仍作冗余保留（见 <see cref="StartupService"/>）。
///
/// 拆成两个任务，是为了区分「开机该启动」和「掉线该拉回来」这两件事：
///
/// ① ClipBoardAutostart —— LogonTrigger + 10 秒延迟，登录时启动，不带参数。
///    这条路径会清除退出标记：用户重新登录就意味着希望它回来。
///
/// ② ClipBoardKeepAlive —— TimeTrigger（StartBoundary 取过去时间）+ 每 15 分钟无限重复，
///    带 --keepalive 参数，负责把中途消失的程序拉回来。
///    带这个参数启动的进程会先看退出标记，用户主动退出过就安静地不启动。
///
/// 为什么必须拆成两个：一个任务的所有触发器共用同一组 Action，没法按触发来源传不同参数，
/// 也就无法区分「登录启动」和「定时重试」——而这个区分正是托盘「退出」还能不能用的关键。
/// 另外给 LogonTrigger 挂 Repetition 是无效的：它的重复窗口要等下次登录才开始计时，
/// 注册完当场 NextRunTime 是空的，恰恰覆盖不到「连续开机十几天中途掉线」这个最需要它的场景。
///
/// 通过后期绑定调用 Task Scheduler 2.0 COM（Schedule.Service），不引入任何 NuGet 依赖。
/// </summary>
public static class ScheduledTaskService
{
    private const string LogonTaskName = "ClipBoardAutostart";
    private const string KeepAliveTaskName = "ClipBoardKeepAlive";
    private const int CreateOrUpdate = 6;      // TASK_CREATE_OR_UPDATE
    private const int InteractiveToken = 3;    // TASK_LOGON_INTERACTIVE_TOKEN

    /// <summary>由保活任务启动时带的参数，App 据此判断要不要尊重退出标记。</summary>
    public const string KeepAliveArgument = "--keepalive";

    /// <summary>
    /// 任务定义的版本标记，以 [v4] 的形式写在 Description 里。
    /// 改动任务 XML（触发器、参数、重复间隔等）时必须同步改这个版本号，
    /// 否则已存在的旧任务会被判定为 Ok 而永远不会被更新。
    /// 带方括号比较，避免 v1 命中 v10 这类子串误判。
    /// </summary>
    private const string DefinitionVersion = "v4";
    private static string VersionMarker => $"[{DefinitionVersion}]";

    /// <summary>登录任务的状态（IsVerified 以它为准）。</summary>
    public static TaskSnapshot Query(string? expectedExe = null) => QueryTask(LogonTaskName, expectedExe);

    /// <summary>保活任务的状态。</summary>
    public static TaskSnapshot QueryKeepAlive(string? expectedExe = null) => QueryTask(KeepAliveTaskName, expectedExe);

    /// <summary>一次 COM 往返拿到某个任务的状态与目标命令。</summary>
    private static TaskSnapshot QueryTask(string taskName, string? expectedExe)
    {
        object? service = null;
        try
        {
            service = CreateService();
            if (service is null) return new TaskSnapshot(TaskStartupState.Unavailable, null);

            var folder = Call(service, "GetFolder", "\\");
            if (folder is null) return new TaskSnapshot(TaskStartupState.Unavailable, null);

            object? task;
            try
            {
                task = Call(folder, "GetTask", taskName);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is FileNotFoundException)
            {
                return new TaskSnapshot(TaskStartupState.Missing, null);
            }
            if (task is null) return new TaskSnapshot(TaskStartupState.Missing, null);

            string? xml = Prop(task, "Xml") as string;
            string? command = ExtractCommand(xml);
            bool enabled = Prop(task, "Enabled") is true;

            if (!enabled) return new TaskSnapshot(TaskStartupState.Disabled, command);

            // 旧版本的任务定义要当作「需要更新」处理。
            if (xml is null || !xml.Contains(VersionMarker, StringComparison.Ordinal))
                return new TaskSnapshot(TaskStartupState.Mismatched, command);

            // 指向的 exe 必须存在，否则「启用」只是假象——任务会启动失败。
            if (string.IsNullOrEmpty(command) || !File.Exists(command))
                return new TaskSnapshot(TaskStartupState.Mismatched, command);

            if (expectedExe is { Length: > 0 }
                && !string.Equals(command, expectedExe, StringComparison.OrdinalIgnoreCase))
                return new TaskSnapshot(TaskStartupState.Mismatched, command);

            return new TaskSnapshot(TaskStartupState.Ok, command);
        }
        catch (Exception ex)
        {
            DiagLog.Error("autostart", $"task query failed ({taskName})", ex);
            return new TaskSnapshot(TaskStartupState.Unavailable, null);
        }
        finally
        {
            Release(service);
        }
    }

    /// <summary>创建 / 更新两个任务，都指向 <paramref name="exePath"/>。</summary>
    public static bool Register(string exePath)
    {
        bool logon = RegisterTask(LogonTaskName, BuildLogonXml(exePath));
        bool keepAlive = RegisterTask(KeepAliveTaskName, BuildKeepAliveXml(exePath));
        return logon && keepAlive;
    }

    /// <summary>删除两个任务；本来就不存在时也算成功。</summary>
    public static bool Unregister()
    {
        bool logon = UnregisterTask(LogonTaskName);
        bool keepAlive = UnregisterTask(KeepAliveTaskName);
        return logon && keepAlive;
    }

    private static bool RegisterTask(string taskName, string xml)
    {
        object? service = null;
        try
        {
            service = CreateService();
            if (service is null) return false;

            var folder = Call(service, "GetFolder", "\\");
            if (folder is null) return false;

            Call(folder, "RegisterTask",
                taskName, xml, CreateOrUpdate,
                Type.Missing, Type.Missing, InteractiveToken, Type.Missing);

            DiagLog.Write("autostart", $"scheduled task registered: {taskName}");
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Error("autostart", $"task register failed ({taskName})", ex);
            return false;
        }
        finally
        {
            Release(service);
        }
    }

    private static bool UnregisterTask(string taskName)
    {
        object? service = null;
        try
        {
            service = CreateService();
            if (service is null) return false;

            var folder = Call(service, "GetFolder", "\\");
            if (folder is null) return false;

            try
            {
                Call(folder, "DeleteTask", taskName, 0);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is FileNotFoundException)
            {
                return true; // 本来就没有
            }

            DiagLog.Write("autostart", $"scheduled task removed: {taskName}");
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Error("autostart", $"task unregister failed ({taskName})", ex);
            return false;
        }
        finally
        {
            Release(service);
        }
    }

    /// <summary>从任务 XML 里取出 Command 节点。用 XDocument 解析，避免手写反转义把实体解错。</summary>
    private static string? ExtractCommand(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        try
        {
            return XDocument.Parse(xml)
                .Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 登录任务：登录后延迟 10 秒启动。
    /// 延迟是为了避开登录高峰——那时通知区域常常还没就绪，托盘图标容易注册不上。
    /// </summary>
    private static string BuildLogonXml(string exePath) => BuildXml(
        exePath,
        arguments: null,
        description: $"ClipBoard 剪贴板管理器开机自启动（登录后 10 秒）{VersionMarker}",
        triggers: $"""
            <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Sid}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
        """,
        startWhenAvailable: true);

    /// <summary>
    /// 保活任务：每 15 分钟检查一次，程序不在就拉回来。
    /// StartBoundary 取一个过去的时间点，注册完立即生效，不必等下一次登录。
    /// StartWhenAvailable=false：错过的那次不补跑，反正下一个 15 分钟就到，避免唤醒后补跑一串。
    /// </summary>
    private static string BuildKeepAliveXml(string exePath) => BuildXml(
        exePath,
        arguments: KeepAliveArgument,
        description: $"ClipBoard 掉线自愈（每 15 分钟；用户主动退出后不打扰）{VersionMarker}",
        triggers: """
            <TimeTrigger>
                  <Enabled>true</Enabled>
                  <StartBoundary>2020-01-01T00:00:00</StartBoundary>
                  <Repetition>
                    <Interval>PT15M</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                </TimeTrigger>
        """,
        startWhenAvailable: false);

    private static string Sid => WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;

    /// <summary>
    /// 任务定义骨架。
    /// ExecutionTimeLimit=PT0S 表示不限时长——常驻托盘程序绝不能被任务计划掐掉。
    /// MultipleInstancesPolicy=IgnoreNew：程序还在跑时任务实例算「运行中」，
    /// 后续触发会被直接跳过，因此既不会起重复进程、也不会刷日志。
    /// RunLevel=LeastPrivilege + InteractiveToken：不提权，未登录时不运行。
    /// </summary>
    private static string BuildXml(string exePath, string? arguments, string description, string triggers, bool startWhenAvailable)
    {
        string exe = SecurityElement.Escape(exePath) ?? exePath;
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? string.Empty) ?? string.Empty;
        string desc = SecurityElement.Escape(description) ?? description;
        string argLine = arguments is { Length: > 0 }
            ? $"\n      <Arguments>{SecurityElement.Escape(arguments)}</Arguments>"
            : string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{desc}</Description>
              </RegistrationInfo>
              <Triggers>
                {triggers}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>{(startWhenAvailable ? "true" : "false")}</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>{argLine}
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    // ---- COM 后期绑定辅助 ----------------------------------------------------

    private static object? CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return null;
        var service = Activator.CreateInstance(type);
        if (service is null) return null;
        Call(service, "Connect", Type.Missing, Type.Missing, Type.Missing, Type.Missing);
        return service;
    }

    private static object? Call(object target, string method, params object?[] args)
        => target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

    private static object? Prop(object target, string name)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static void Release(object? comObject)
    {
        try
        {
            if (comObject is not null && System.Runtime.InteropServices.Marshal.IsComObject(comObject))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(comObject);
        }
        catch { }
    }
}
