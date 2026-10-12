using System.Diagnostics;

namespace ClipBoard.Services;

/// <summary>
/// 更新后自动清掉系统里本应用失效的辅助功能 / 发送按键授权，用户不用再自己去终端运行 tccutil。
///
/// 2.1.1 及以前的版本是临时签名：系统按每个版本的 cdhash 记授权，更新后列表里旧条目的开关仍显示打开，
/// 新版本却得不到授权，而且因为条目还在，系统也不再弹授权提示。从 2.1.2 起发布版用固定证书签名，
/// 授权按证书记、更新后照样有效；这里处理从旧版本升级的那一次，以及以后证书万一更换的情况。
///
/// 只在两项授权都没有时才清：这时本来就不能自动粘贴，清掉不会损失什么；清完以后第一次粘贴时照常弹出系统提示。
/// 每份程序只清一次，用户主动关掉开关时不会每次启动都被重置。
/// </summary>
internal static class MacAccessibilityRepair
{
    private const string BundleId = "io.github.ccdr4gon.clipboard";

    /// <summary>后台执行，不拖慢启动。返回写入诊断日志的一行；什么都没做时返回 null。</summary>
    internal static Task<string?> RunOnceAsync(string dataDirectory) => Task.Run(() =>
    {
        string marker = Path.Combine(dataDirectory, "accessibility-reset.txt");
        string build = BuildStamp();
        if (!ShouldReset(MacNative.IsAccessibilityTrusted, ReadMarker(marker), build)) return null;
        bool accessibility = Reset("Accessibility"), postEvent = Reset("PostEvent");
        try { File.WriteAllText(marker, build); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return $"accessibility reset (stale grant cleared) accessibility={accessibility} postEvent={postEvent} build={build}";
    });

    /// <summary>两项授权都没有，且这份程序还没清过时才清。</summary>
    internal static bool ShouldReset(bool trusted, string? lastResetBuild, string build) => !trusted && lastResetBuild != build;

    /// <summary>区分不同版本的程序：路径、大小和修改时间（解压出的文件保留打包时的时间），不读取签名。</summary>
    internal static string BuildStamp()
    {
        var path = Environment.ProcessPath ?? "";
        var file = new FileInfo(path);
        return file.Exists ? $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : path;
    }

    private static string? ReadMarker(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool Reset(string service)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/tccutil")
            {
                ArgumentList = { "reset", service, BundleId },
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (process == null) return false;
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch (InvalidOperationException) { } return false; }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
