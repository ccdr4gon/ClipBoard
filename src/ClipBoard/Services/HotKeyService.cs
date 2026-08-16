using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipBoard.Services;

public class HotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT   = 0x0004;
    private const uint MOD_WIN     = 0x0008;
    private const int HOTKEY_ID    = 0xC1B0;
    private const uint VK_V        = 0x56;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Window _window;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;
    private bool _disposed;

    public event EventHandler? HotKeyPressed;

    /// <summary>热键是否注册成功。失败通常是被别的程序占用了 Ctrl+Alt+V。</summary>
    public bool IsRegistered => _registered;

    public HotKeyService(Window window)
    {
        _window = window;
        new WindowInteropHelper(window).EnsureHandle();
        Attach();
    }

    private void Attach()
    {
        var helper = new WindowInteropHelper(_window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _registered = RegisterHotKey(_hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_V);
        // 这里绝不能弹模态框：Attach() 在 OnStartup 的启动路径上，
        // 开机时弹窗会把整个启动流程卡死在无人点击的对话框上（托盘图标也就永远出不来）。
        // 失败只记日志，设置窗口里再向用户展示。
        if (!_registered)
            DiagLog.Write("hotkey", $"RegisterHotKey(Ctrl+Alt+V) FAILED, win32Error={Marshal.GetLastWin32Error()} (可能被其他程序占用)");
        else
            DiagLog.Write("hotkey", $"RegisterHotKey(Ctrl+Alt+V) ok hwnd={_hwnd:X}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotKeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered && _hwnd != IntPtr.Zero)
            UnregisterHotKey(_hwnd, HOTKEY_ID);
        _source?.RemoveHook(WndProc);
    }
}
