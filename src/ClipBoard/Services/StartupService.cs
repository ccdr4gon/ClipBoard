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
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipBoard";

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
    /// 写入 / 删除自启动项；写入后回读校验。
    /// 开启时始终写 <see cref="AutostartTarget"/>；若没有合适目标（只有构建输出可用）则保持现状不动。
    /// 关闭时删除。返回注册表最终状态是否符合期望。
    /// </summary>
    public static bool Apply(bool enable)
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
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            return false;
        }

        return enable ? IsVerified : !IsEnabled();
    }

    /// <summary>注册表里是否存在非空自启动项。</summary>
    public static bool IsEnabled() => !string.IsNullOrEmpty(CurrentValue());

    /// <summary>Run 键是否确实指向当前应有的自启动目标（安装版）。</summary>
    public static bool IsVerified
    {
        get
        {
            var cur = CurrentValue();
            return !string.IsNullOrEmpty(cur) && ExpectedValue != null
                   && string.Equals(cur, ExpectedValue, StringComparison.OrdinalIgnoreCase);
        }
    }

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
