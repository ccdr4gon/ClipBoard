using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace ClipBoard.Services;

internal static class TrayMenuService
{
    public static void OpenAtCursor(ContextMenu menu)
    {
        // Hardcodet 1.1.0 的绝对坐标定位在屏幕缩放下会偏移。
        // 直接让 WPF 以鼠标为锚点，并自行处理屏幕边缘和显示器坐标。
        menu.IsOpen = false;
        menu.PlacementTarget = null;
        menu.Placement = PlacementMode.MousePoint;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
        menu.IsOpen = true;

        // 托盘菜单没有可见的父窗口，需要激活自身以支持 Esc 和点击外部关闭。
        if (PresentationSource.FromVisual(menu) is HwndSource source)
            SetForegroundWindow(source.Handle);
        menu.Focus();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
