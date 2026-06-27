using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ClipBoard.Services;

namespace ClipBoard.Views;

public class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        Title = "设置 — ClipBoard";
        Width = 420; Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Brushes.White;
        DataContext = settings;

        var root = new StackPanel { Margin = new Thickness(20) };

        var generalTitle = new TextBlock
        {
            Text = "通用",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        root.Children.Add(generalTitle);

        var cbStartup = new CheckBox
        {
            Content = "开机时自动启动",
            Margin = new Thickness(0, 4, 0, 4),
        };
        cbStartup.SetBinding(ToggleButton_IsCheckedProp(), new Binding(nameof(AppSettings.StartWithWindows))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        root.Children.Add(cbStartup);

        var startupHint = new TextBlock
        {
            Text = "通过当前用户注册表 Run 项实现，无需管理员权限。",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(24, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(startupHint);

        // 实时显示注册表真实状态，确认“是否真的开机启动”。
        var startupStatus = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(24, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };
        void RefreshStatus()
        {
            if (StartupService.IsVerified)
            {
                startupStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x3A));
                startupStatus.Text = "✓ 注册表已确认：开机将启动（指向安装版）\n" + StartupService.CurrentValue();
            }
            else if (!StartupService.IsInstalled)
            {
                startupStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0x6A, 0x00));
                startupStatus.Text = "⚠ 未检测到安装版，开机自启动需要安装版（框架依赖的开发构建在开机时常拉不起来）。\n"
                                     + "请把自包含单文件放到：" + StartupService.InstallPath;
            }
            else if (StartupService.IsEnabled())
            {
                startupStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0x6A, 0x00));
                startupStatus.Text = "⚠ 自启动项存在但未指向安装版：\n" + StartupService.CurrentValue();
            }
            else
            {
                startupStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                startupStatus.Text = "✗ 未设置开机自启动";
            }
        }
        RefreshStatus();
        root.Children.Add(startupStatus);

        // 勾选状态变化后回读注册表刷新显示；窗口关闭时退订，避免句柄泄漏。
        void OnSettingsChanged(object? _, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(AppSettings.StartWithWindows))
                Dispatcher.BeginInvoke(new Action(RefreshStatus));
        }
        settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) => settings.PropertyChanged -= OnSettingsChanged;

        var title = new TextBlock
        {
            Text = "显示选项",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 10)
        };
        root.Children.Add(title);

        var cbInvisible = new CheckBox
        {
            Content = "显示不可见字符编码（ZWSP / BOM / 控制码 等）",
            Margin = new Thickness(0, 4, 0, 4),
        };
        cbInvisible.SetBinding(ToggleButton_IsCheckedProp(), new Binding(nameof(AppSettings.ShowInvisibleChars))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        root.Children.Add(cbInvisible);

        var hint = new TextBlock
        {
            Text = "开启后，文本条目中出现的 Unicode 格式 / 控制字符会以浅红小字显示。\n例如 ZWSP 显示为 <U+200B>，ESC 显示为 ␛。\n中日韩普通文字不受影响。",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(24, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(hint);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var close = new Button { Content = "关闭", Width = 80, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        root.Children.Add(footer);

        Content = root;
    }

    private static DependencyProperty ToggleButton_IsCheckedProp() => CheckBox.IsCheckedProperty;
}
