using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipBoard.Models;

namespace ClipBoard;

public class FolderNameDialog : Window
{
    private readonly TextBox _input;
    private readonly RadioButton? _normalRadio;
    private readonly RadioButton? _memeRadio;
    public string FolderName => _input.Text;
    public FolderKind SelectedKind => (_memeRadio?.IsChecked == true) ? FolderKind.Meme : FolderKind.Normal;

    public FolderNameDialog(string title, string initial, bool showKindPicker = true, FolderKind initialKind = FolderKind.Normal, string? labelText = null)
    {
        Title = title;
        Width = 360; Height = showKindPicker ? 200 : 140;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock { Text = labelText ?? "收藏夹名称：", Margin = new Thickness(0, 0, 0, 4) });
        _input = new TextBox { Text = initial, Padding = new Thickness(4) };
        root.Children.Add(_input);

        if (showKindPicker)
        {
            root.Children.Add(new TextBlock { Text = "类型：", Margin = new Thickness(0, 12, 0, 4) });
            var kindPanel = new StackPanel { Orientation = Orientation.Horizontal };
            _normalRadio = new RadioButton { Content = "普通（条形列表）", IsChecked = initialKind == FolderKind.Normal, Margin = new Thickness(0, 0, 16, 0), GroupName = "kind" };
            _memeRadio = new RadioButton { Content = "表情包（方形网格）", IsChecked = initialKind == FolderKind.Meme, GroupName = "kind" };
            kindPanel.Children.Add(_normalRadio);
            kindPanel.Children.Add(_memeRadio);
            root.Children.Add(kindPanel);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "取消", Width = 70, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;

        Loaded += (_, _) =>
        {
            Activate();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _input.Focus();
                Keyboard.Focus(_input);
                _input.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
    }
}
