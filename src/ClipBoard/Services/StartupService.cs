using Microsoft.Win32;

namespace ClipBoard.Services;

/// <summary>
/// 通过 HKCU\...\Run 注册表项管理开机自启动（无需管理员权限），
/// 写入后回读校验，并提供诊断接口供界面/日志确认“是否真的开机启动”。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipBoard";

    /// <summary>当前进程 exe 的完整路径。</summary>
    public static string? ExePath => Environment.ProcessPath;

    /// <summary>期望写入注册表的命令字符串（带引号）。</summary>
    public static string ExpectedValue =>
        ExePath is { Length: > 0 } p ? $"\"{p}\"" : "";

    /// <summary>
    /// 写入或删除自启动项；写入后回读校验。
    /// 返回注册表最终状态是否等于期望（true=已确认按期望生效）。
    /// </summary>
    public static bool Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (enable)
            {
                if (ExePath is not { Length: > 0 }) return false;
                key.SetValue(ValueName, ExpectedValue, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表不可写时忽略，不影响应用运行。
            return false;
        }

        // 回读校验：开启时必须指向“当前 exe”，关闭时必须不存在。
        return enable ? PointsToCurrentExe() : !IsEnabled();
    }

    /// <summary>注册表里是否存在非空自启动项。</summary>
    public static bool IsEnabled() => !string.IsNullOrEmpty(CurrentValue());

    /// <summary>注册表里的自启动项是否确实指向当前 exe（路径未失效）。</summary>
    public static bool PointsToCurrentExe()
    {
        var cur = CurrentValue();
        return !string.IsNullOrEmpty(cur)
               && string.Equals(cur, ExpectedValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>返回注册表里当前存的命令字符串（用于诊断/界面显示），不存在则 null。</summary>
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
