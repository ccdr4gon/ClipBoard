using System.Runtime.InteropServices;
using System.Text;

namespace ClipBoard.Services;

internal static class MacNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    [DllImport(ObjC)] internal static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send0(nint obj, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send1(nint obj, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send2(nint obj, nint selector, nint a, nint b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte Bool1(nint obj, nint selector, nint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte Bool2(nint obj, nint selector, nint a, nint b);
    internal static nint Call(nint obj, string selector) => Send0(obj, sel_registerName(selector));
    internal static nint Call(nint obj, string selector, nint a) => Send1(obj, sel_registerName(selector), a);
    internal static nint Call(nint obj, string selector, nint a, nint b) => Send2(obj, sel_registerName(selector), a, b);
    internal static bool Bool(nint obj, string selector, nint a) => Bool1(obj, sel_registerName(selector), a) != 0;
    internal static bool Bool(nint obj, string selector, nint a, nint b) => Bool2(obj, sel_registerName(selector), a, b) != 0;
    internal static nint Class(string name) => objc_getClass(name);
    internal static nint String(string text)
    {
        nint bytes = Marshal.StringToCoTaskMemUTF8(text);
        try { return Call(Class("NSString"), "stringWithUTF8String:", bytes); }
        finally { Marshal.FreeCoTaskMem(bytes); }
    }
    internal static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Call(value, "UTF8String")) ?? "";
    internal static byte[]? Bytes(nint data)
    {
        if (data == 0) return null;
        long length = (long)Call(data, "length");
        if (length <= 0 || length > 64 * 1024 * 1024) return null;
        var bytes = new byte[(int)length];
        Marshal.Copy(Call(data, "bytes"), bytes, 0, bytes.Length);
        return bytes;
    }
    internal static nint Data(byte[] bytes)
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return Call(Class("NSData"), "dataWithBytes:length:", pinned.AddrOfPinnedObject(), bytes.Length); }
        finally { pinned.Free(); }
    }
    // 剪贴板每 500 ms 轮询一次：常用的类和选择器第一次用到时缓存，不再每次按字符串查找。
    // 选择器注册后永久有效；类只在找到后才缓存（框架加载前 objc_getClass 返回 0，见 MacLogin）。
    internal static nint Sel(ref nint cache, string name)
    {
        var value = cache;
        if (value == 0) cache = value = sel_registerName(name);
        return value;
    }
    internal static nint Cls(ref nint cache, string name)
    {
        var value = cache;
        if (value == 0 && (value = objc_getClass(name)) != 0) cache = value;
        return value;
    }
    internal static nint Send(nint obj, nint selector) => Send0(obj, selector);
    private static nint s_poolClass, s_new, s_drain, s_workspaceClass, s_sharedWorkspace, s_frontmostApplication, s_processIdentifier;
    internal sealed class Pool : IDisposable
    {
        private readonly nint _pool = Send0(Cls(ref s_poolClass, "NSAutoreleasePool"), Sel(ref s_new, "new"));
        public void Dispose() => Send0(_pool, Sel(ref s_drain, "drain"));
    }

    internal static int ForegroundPid()
    {
        using var pool = new Pool();
        return ForegroundPidInPool();
    }
    /// <summary>调用方须已持有自动释放池。</summary>
    internal static int ForegroundPidInPool() =>
        (int)Send0(Send0(Send0(Cls(ref s_workspaceClass, "NSWorkspace"), Sel(ref s_sharedWorkspace, "sharedWorkspace")),
            Sel(ref s_frontmostApplication, "frontmostApplication")), Sel(ref s_processIdentifier, "processIdentifier"));
    private static nint SharedApplication => Call(Class("NSApplication"), "sharedApplication");

    internal enum PasteResult { Pasted, NotTrusted, NoTarget, NotActivated }

    internal static bool IsAxTrusted => AXIsProcessTrusted() != 0;
    /// <summary>发送 ⌘V 由单独的“发送事件”授权管控，两项检查可能不一致：macOS 27 上出现过辅助功能已授权、发送事件仍报未授权。</summary>
    internal static bool CanPostEvents => CGPreflightPostEventAccess() != 0;
    /// <summary>任一项通过就尝试发送；即使按键被系统丢弃，内容也已在剪贴板里。</summary>
    internal static bool IsAccessibilityTrusted => IsAxTrusted || CanPostEvents;

    /// <summary>未授权时弹出系统的辅助功能授权提示。应用更新后临时签名会变化，旧授权条目随之失效。</summary>
    internal static void RequestAccessibility()
    {
        using var pool = new Pool();
        if (!IsAxTrusted)
        {
            var prompt = Call(Class("NSNumber"), "numberWithBool:", 1);
            AXIsProcessTrustedWithOptions(Call(Class("NSDictionary"), "dictionaryWithObject:forKey:", prompt, String("AXTrustedCheckOptionPrompt")));
        }
        else if (!CanPostEvents) CGRequestPostEventAccess();
    }

    internal static string BundleId(int pid)
    {
        using var pool = new Pool();
        var app = pid > 0 ? Call(Class("NSRunningApplication"), "runningApplicationWithProcessIdentifier:", pid) : 0;
        return app == 0 ? "" : Text(Call(app, "bundleIdentifier"));
    }

    /// <summary>粘贴时为交还焦点隐藏了整个应用，再次显示面板前取消隐藏。</summary>
    internal static void UnhideApplication()
    {
        using var pool = new Pool();
        Call(SharedApplication, "unhideWithoutActivation");
    }

    /// <summary>把焦点交还给打开面板前的应用，再发送 ⌘V。未授权或切换失败时内容仍在剪贴板里。</summary>
    internal static async Task<PasteResult> PasteAsync(int pid)
    {
        bool trusted = IsAccessibilityTrusted;
        bool hasTarget = pid > 0 && pid != Environment.ProcessId;
        bool focused = await ReturnFocusAsync(hasTarget ? pid : 0);
        if (!trusted) return PasteResult.NotTrusted;
        if (!hasTarget) return PasteResult.NoTarget;
        if (!focused) return PasteResult.NotActivated;
        // Shift+回车 / Shift+单击时等修饰键松开，免得目标应用把它当成 ⇧⌘V 等其他快捷键。
        for (int i = 0; i < 100 && (CGEventSourceFlagsState(HidSystemState) & HeldModifiers) != 0; i++) await Task.Delay(15);
        await Task.Delay(40); // 目标窗口刚激活，等它成为 key window
        if (ForegroundPid() != pid) return PasteResult.NotActivated;
        return PostCommandV() ? PasteResult.Pasted : PasteResult.NotActivated;
    }

    // macOS 14 起改为协作式激活：当前应用先让出，目标的激活请求才会生效；
    // 再隐藏自身，即使激活请求被忽略，系统也会把焦点交给面板下方的应用，也就是原应用。
    private static async Task<bool> ReturnFocusAsync(int pid)
    {
        using (new Pool())
        {
            var shared = SharedApplication;
            var app = pid > 0 ? Call(Class("NSRunningApplication"), "runningApplicationWithProcessIdentifier:", pid) : 0;
            if (app != 0 && Bool(shared, "respondsToSelector:", sel_registerName("yieldActivationToApplication:")))
                Call(shared, "yieldActivationToApplication:", app);
            Call(shared, "hide:", 0);
            if (app != 0) Call(app, "activateWithOptions:", 2); // NSApplicationActivateIgnoringOtherApps，macOS 13 需要
        }
        if (pid <= 0) return false;
        for (int i = 0; i < 40 && ForegroundPid() != pid; i++) await Task.Delay(25);
        return ForegroundPid() == pid;
    }

    private const int CombinedSessionState = 0, HidSystemState = 1;
    private const ulong HeldModifiers = (1UL << 17) | (1UL << 18) | (1UL << 19) | (1UL << 20); // Shift、Control、Option、Command
    private const ulong CommandFlags = (1UL << 20) | 0x8; // 加上左 Command 的设备位，部分应用只认设备位
    private const uint AnnotatedSessionTap = 2;
    private const uint PermitMouseAndSystemEvents = 0x1 | 0x4;

    // 与 Maccy、Clipy 的做法一致：合并会话状态的事件源发到会话层，并在这段时间里屏蔽本地键盘事件，
    // 免得还没松开的回车等按键混进 ⌘V。
    private static bool PostCommandV()
    {
        var source = CGEventSourceCreate(CombinedSessionState);
        var down = CGEventCreateKeyboardEvent(source, 9, 1);
        var up = CGEventCreateKeyboardEvent(source, 9, 0);
        try
        {
            if (down == 0 || up == 0) return false;
            if (source != 0) CGEventSourceSetLocalEventsFilterDuringSuppressionState(source, PermitMouseAndSystemEvents, 0);
            CGEventSetFlags(down, CommandFlags); CGEventSetFlags(up, CommandFlags);
            CGEventPost(AnnotatedSessionTap, down); CGEventPost(AnnotatedSessionTap, up);
            return true;
        }
        finally { if (down != 0) CFRelease(down); if (up != 0) CFRelease(up); if (source != 0) CFRelease(source); }
    }
    [DllImport(Core)] internal static extern void CFRelease(nint value);
    [DllImport(AppServices)] private static extern byte AXIsProcessTrusted();
    [DllImport(AppServices)] private static extern byte AXIsProcessTrustedWithOptions(nint options);
    [DllImport(AppServices)] private static extern byte CGPreflightPostEventAccess();
    [DllImport(AppServices)] private static extern byte CGRequestPostEventAccess();
    [DllImport(AppServices)] private static extern ulong CGEventSourceFlagsState(int state);
    [DllImport(AppServices)] private static extern nint CGEventSourceCreate(int state);
    [DllImport(AppServices)] private static extern void CGEventSourceSetLocalEventsFilterDuringSuppressionState(nint source, uint filter, uint state);
    [DllImport(AppServices)] private static extern nint CGEventCreateKeyboardEvent(nint source, ushort key, byte down);
    [DllImport(AppServices)] private static extern void CGEventSetFlags(nint ev, ulong flags);
    [DllImport(AppServices)] private static extern void CGEventPost(uint tap, nint ev);
}
