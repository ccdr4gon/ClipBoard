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

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Window _window;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;
    private bool _disposed;

    public event EventHandler? HotKeyPressed;

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
        if (!_registered)
        {
            System.Windows.MessageBox.Show(
                "快捷键 Ctrl+Alt+V 注册失败（可能被其他程序占用）。\n可在托盘菜单退出后修改 HotKeyService.cs 选其他组合。",
                "ClipBoard", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
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
