using System.Runtime.InteropServices;

namespace ClipBoard.Services;

internal sealed class MacHotkey : IDisposable
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    [StructLayout(LayoutKind.Sequential)] private struct EventType { public uint Class, Kind; }
    [StructLayout(LayoutKind.Sequential)] private struct HotkeyId { public uint Signature, Id; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Handler(nint next, nint ev, nint data);
    private readonly Handler _handler;
    private nint _hotkey, _registration;
    public MacHotkey(Action show)
    {
        _handler = (_, ev, _) =>
        {
            if (GetEventParameter(ev, 0x2D2D2D2D, 0x686B6964, 0, 8, 0, out var id) != 0 || id.Signature != 0x434C4950 || id.Id != 1) return -9874;
            Avalonia.Threading.Dispatcher.UIThread.Post(show);
            return 0;
        };
        var type = new EventType { Class = 0x6B657962, Kind = 6 };
        int status = InstallEventHandler(GetApplicationEventTarget(), _handler, 1, ref type, 0, out _registration);
        if (status == 0) status = RegisterEventHotKey(9, (1 << 8) | (1 << 11), new HotkeyId { Signature = 0x434C4950, Id = 1 }, GetApplicationEventTarget(), 0, out _hotkey);
        if (status != 0) { Dispose(); throw new IOException($"无法注册 ⌘⌥V 快捷键（{status}），可从菜单栏打开。"); }
    }
    public void Dispose()
    {
        if (_hotkey != 0) { UnregisterEventHotKey(_hotkey); _hotkey = 0; }
        if (_registration != 0) { RemoveEventHandler(_registration); _registration = 0; }
        GC.KeepAlive(_handler);
    }
    [DllImport(Carbon)] private static extern nint GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(nint target, Handler handler, uint count, ref EventType type, nint data, out nint registration);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint key, uint modifiers, HotkeyId id, nint target, uint options, out nint hotkey);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(nint hotkey);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(nint registration);
    [DllImport(Carbon)] private static extern int GetEventParameter(nint ev, uint name, uint type, nint actualType, uint size, nint actualSize, out HotkeyId data);
}
