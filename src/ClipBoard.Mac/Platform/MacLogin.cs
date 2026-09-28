using System.Runtime.InteropServices;
using static ClipBoard.Services.MacNative;

namespace ClipBoard.Services;

internal static class MacLogin
{
    // Explicit initializer: Objective-C classes must be registered before looking them up.
    static MacLogin() => NativeLibrary.Load("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement");
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte WithError(nint obj, nint selector, out nint error);
    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(string selector);
    private static nint Service => Call(Class("SMAppService"), "mainAppService");
    public static bool Enabled { get { using var pool = new Pool(); return Call(Service, "status") == 1; } }
    public static string SetEnabled(bool enabled)
    {
        using var pool = new Pool();
        var bundle = Text(Call(Call(Class("NSBundle"), "mainBundle"), "bundlePath"));
        if (!bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请先将 ClipBoard.app 放到应用程序文件夹并启动，再设置登录启动。");
        var service = Service;
        if (WithError(service, sel_registerName(enabled ? "registerAndReturnError:" : "unregisterAndReturnError:"), out var error) == 0)
            throw new IOException("设置登录启动失败：" + Text(Call(error, "localizedDescription")));
        return Call(service, "status") == 2 ? "请在系统设置 → 通用 → 登录项中允许 ClipBoard。" : enabled ? "已启用登录启动。" : "已关闭登录启动。";
    }
}
