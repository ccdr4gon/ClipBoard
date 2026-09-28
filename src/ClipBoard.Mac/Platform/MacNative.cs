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
    internal sealed class Pool : IDisposable
    {
        private readonly nint _pool = Call(Class("NSAutoreleasePool"), "new");
        public void Dispose() => Call(_pool, "drain");
    }

    internal static int ForegroundPid()
    {
        using var pool = new Pool();
        return (int)Call(Call(Call(Class("NSWorkspace"), "sharedWorkspace"), "frontmostApplication"), "processIdentifier");
    }
    internal static async Task<bool> PasteAsync(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId || AXIsProcessTrusted() == 0) return false;
        using (var pool = new Pool())
        {
            var app = Call(Class("NSRunningApplication"), "runningApplicationWithProcessIdentifier:", pid);
            if (app == 0 || !Bool(app, "activateWithOptions:", 2)) return false;
        }
        for (int i = 0; i < 10 && ForegroundPid() != pid; i++) await Task.Delay(50);
        if (ForegroundPid() != pid) return false;
        var down = CGEventCreateKeyboardEvent(0, 9, 1);
        var up = CGEventCreateKeyboardEvent(0, 9, 0);
        try
        {
            if (down == 0 || up == 0) return false;
            CGEventSetFlags(down, 1UL << 20); CGEventSetFlags(up, 1UL << 20);
            CGEventPost(0, down); CGEventPost(0, up);
            return true;
        }
        finally { if (down != 0) CFRelease(down); if (up != 0) CFRelease(up); }
    }
    [DllImport(Core)] internal static extern void CFRelease(nint value);
    [DllImport(AppServices)] private static extern byte AXIsProcessTrusted();
    [DllImport(AppServices)] private static extern nint CGEventCreateKeyboardEvent(nint source, ushort key, byte down);
    [DllImport(AppServices)] private static extern void CGEventSetFlags(nint ev, ulong flags);
    [DllImport(AppServices)] private static extern void CGEventPost(uint tap, nint ev);
}
