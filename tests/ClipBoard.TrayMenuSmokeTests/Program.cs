using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipBoard.Services;
using Hardcodet.Wpf.TaskbarNotification;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 独立运行，不加载 ClipBoard.App，不监听剪贴板，也不访问用户的历史和自启动设置。
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "托盘菜单定位验证" });
        menu.Items.Add(new MenuItem { Header = "测试结束后自动关闭" });
        using var tray = new TaskbarIcon { Visibility = Visibility.Collapsed, ContextMenu = menu };
        // 对齐应用的启动顺序：先创建托盘，再创建并隐藏主窗口。
        var owner = new Window { ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true };
        new WindowInteropHelper(owner).EnsureHandle();
        owner.Hide();
        GetCursorPos(out var originalCursor);
        IntPtr originalForeground = GetForegroundWindow();
        bool useFix = !args.Contains("--legacy");
        Point openingCursor = default;
        tray.PreviewTrayContextMenuOpen += (_, e) =>
        {
            GetCursorPos(out openingCursor);
            if (!useFix) return;
            e.Handled = true;
            TrayMenuService.OpenAtCursor(menu);
        };
        var onMouse = typeof(TaskbarIcon).GetMethod("OnMouseEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var rightUp = Enum.Parse(onMouse.GetParameters()[0].ParameterType, "IconRightMouseUp");
        int failures = 0, cases = 0, legacyOffsets = 0;
        try
        {
            var monitors = new List<(IntPtr Handle, Rect Bounds)>();
            MonitorEnum callback = (IntPtr handle, IntPtr dc, ref Rect bounds, IntPtr data) =>
            {
                monitors.Add((handle, bounds));
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            foreach (var (handle, bounds) in monitors)
            {
                GetScaleFactorForMonitor(handle, out int scale);
                Console.WriteLine($"Monitor [{bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}], scale={scale}%");
                Point[] points =
                [
                    new() { X = bounds.Left + (bounds.Right - bounds.Left) / 2, Y = bounds.Top + (bounds.Bottom - bounds.Top) / 2 },
                    new() { X = bounds.Right - 120, Y = bounds.Bottom - 100 },
                ];
                foreach (var point in points)
                {
                    // 每次运行仅测一种方式，保证修复分支的第一次打开未经旧菜单预热。
                    foreach (bool fixedMode in new[] { useFix })
                    {
                        menu.IsOpen = false;
                        Pump();
                        useFix = fixedMode;
                        SetCursorPos(point.X, point.Y);
                        onMouse.Invoke(tray, [rightUp]);
                        Pump();
                        if (!menu.IsOpen || PresentationSource.FromVisual(menu) is not HwndSource source)
                            throw new InvalidOperationException("菜单未打开");
                        // 菜单固定在打开时的位置；用户之后移动鼠标不属于定位错误。
                        var cursor = openingCursor;
                        GetWindowRect(source.Handle, out var popup);
                        int dx = Math.Max(0, Math.Max(popup.Left - cursor.X, cursor.X - popup.Right));
                        int dy = Math.Max(0, Math.Max(popup.Top - cursor.Y, cursor.Y - popup.Bottom));
                        bool nearby = dx <= 8 && dy <= 8;
                        Console.WriteLine($"{(fixedMode ? "FIXED" : "LEGACY")} cursor=({cursor.X},{cursor.Y}) menu=[{popup.Left},{popup.Top},{popup.Right},{popup.Bottom}] gap=({dx},{dy}) windowDpi={GetDpiForWindow(source.Handle)}");
                        if (fixedMode)
                        {
                            cases++;
                            if (!nearby) failures++;
                            // 验证菜单能正常接收 Esc，而不只验证定位属性值。
                            menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                            {
                                RoutedEvent = Keyboard.KeyDownEvent,
                            });
                            Pump();
                            if (menu.IsOpen) { failures++; Console.WriteLine("FAIL Esc 未关闭菜单"); }
                        }
                        else if (!nearby) legacyOffsets++;
                    }
                }
            }
            GC.KeepAlive(callback);
            Console.WriteLine($"Fixed cases={cases}, failures={failures}, reproduced legacy offsets={legacyOffsets}");
            return failures == 0 && (cases > 0 || !useFix) ? 0 : 1;
        }
        finally
        {
            menu.IsOpen = false;
            tray.Dispose();
            owner.Close();
            SetCursorPos(originalCursor.X, originalCursor.Y);
            if (originalForeground != IntPtr.Zero) SetForegroundWindow(originalForeground);
            app.Shutdown();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
}
