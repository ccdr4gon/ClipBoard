using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Xml.Linq;

namespace ClipBoard.Services;

/// <summary>登录任务的状态。</summary>
public enum TaskStartupState
{
    /// <summary>任务不存在。</summary>
    Missing,
    /// <summary>任务存在但被禁用。</summary>
    Disabled,
    /// <summary>任务存在且启用，但指向别的 exe（或该 exe 已不存在）。</summary>
    Mismatched,
    /// <summary>任务存在、启用、指向正确的安装版。</summary>
    Ok,
    /// <summary>任务计划服务暂时不可用（应重试，不代表任务不存在）。</summary>
    Unavailable,
}

/// <summary>Query 的一次性快照，避免为每个属性反复连任务计划服务。</summary>
public readonly record struct TaskSnapshot(TaskStartupState State, string? Command);

/// <summary>
/// 用「任务计划程序」的登录触发器实现开机自启动（无需管理员）。
///
/// 为什么不只用 HKCU\Run：这台机器上 explorer 的 Run 枚举**从未**启动过本程序
/// （Shell-Core 日志里 7 次登录都没有对应的 9707 事件，而同键其他项每次都正常启动，
/// 且 Run 值与 StartupApproved 标记都反复验证过是正确的）。原因未能查明，
/// 但任务计划程序走的是完全不同的路径，不经过 explorer 的启动项枚举。
///
/// Run 键仍然保留（见 <see cref="StartupService"/>）：两条路径同时存在也安全，
/// 因为 App 有单实例互斥锁，先到的赢、后到的直接退出。
///
/// 通过后期绑定调用 Task Scheduler 2.0 COM（Schedule.Service），不引入任何 NuGet 依赖。
/// </summary>
public static class ScheduledTaskService
{
    private const string TaskName = "ClipBoardAutostart";
    private const int CreateOrUpdate = 6;      // TASK_CREATE_OR_UPDATE
    private const int InteractiveToken = 3;    // TASK_LOGON_INTERACTIVE_TOKEN

    /// <summary>一次 COM 往返拿到状态与目标命令。</summary>
    public static TaskSnapshot Query(string? expectedExe = null)
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
                task = Call(folder, "GetTask", TaskName);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is FileNotFoundException)
            {
                return new TaskSnapshot(TaskStartupState.Missing, null);
            }
            if (task is null) return new TaskSnapshot(TaskStartupState.Missing, null);

            string? command = ExtractCommand(Prop(task, "Xml") as string);
            bool enabled = Prop(task, "Enabled") is true;

            if (!enabled) return new TaskSnapshot(TaskStartupState.Disabled, command);

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
            DiagLog.Error("autostart", "task query failed", ex);
            return new TaskSnapshot(TaskStartupState.Unavailable, null);
        }
        finally
        {
            Release(service);
        }
    }

    /// <summary>创建 / 更新登录任务，指向 <paramref name="exePath"/>。</summary>
    public static bool Register(string exePath)
    {
        object? service = null;
        try
        {
            service = CreateService();
            if (service is null) return false;

            var folder = Call(service, "GetFolder", "\\");
            if (folder is null) return false;

            Call(folder, "RegisterTask",
                TaskName, BuildXml(exePath), CreateOrUpdate,
                Type.Missing, Type.Missing, InteractiveToken, Type.Missing);

            DiagLog.Write("autostart", $"scheduled task registered -> {exePath}");
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Error("autostart", "task register failed", ex);
            return false;
        }
        finally
        {
            Release(service);
        }
    }

    /// <summary>删除登录任务；任务本来就不存在时也算成功。</summary>
    public static bool Unregister()
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
                Call(folder, "DeleteTask", TaskName, 0);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is FileNotFoundException)
            {
                return true; // 本来就没有
            }

            DiagLog.Write("autostart", "scheduled task removed");
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Error("autostart", "task unregister failed", ex);
            return false;
        }
        finally
        {
            Release(service);
        }
    }

    /// <summary>从任务 XML 里取出 &lt;Command&gt;。用 XDocument 解析，避免手写反转义把 &amp;amp; 解错。</summary>
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
    /// 登录触发器任务定义。
    /// 延迟 10 秒是为了避开登录高峰（此时通知区域常常还没就绪，托盘图标容易注册不上）。
    /// ExecutionTimeLimit=PT0S 表示不限时长——常驻托盘程序绝不能被任务计划掐掉。
    /// </summary>
    private static string BuildXml(string exePath)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        string exe = SecurityElement.Escape(exePath) ?? exePath;
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? string.Empty) ?? string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>ClipBoard 剪贴板管理器开机自启动</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{sid}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
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
                  <Command>{exe}</Command>
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
