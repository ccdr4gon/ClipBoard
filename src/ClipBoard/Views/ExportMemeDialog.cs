using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClipBoard.Models;
using ClipBoard.Services;

namespace ClipBoard.Views;

public class ExportMemeDialog : Window
{
    private readonly FavoriteFolder _folder;
    private readonly PersistenceService _persistence;
    private readonly ComboBox _presetBox;
    private readonly TextBox _dirBox;
    private readonly TextBlock _statusLabel;

    public ExportMemeDialog(FavoriteFolder folder, PersistenceService persistence)
    {
        _folder = folder;
        _persistence = persistence;
        Title = $"导出表情包 — {folder.Name}";
        Width = 480; Height = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock { Text = "选择平台预设：", Margin = new Thickness(0, 0, 0, 4) });
        _presetBox = new ComboBox { Padding = new Thickness(4) };
        _presetBox.Items.Add(new ComboBoxItem { Content = "微信 — 240×240 PNG", Tag = ExportPreset.WeChat });
        _presetBox.Items.Add(new ComboBoxItem { Content = "Telegram — 512×512 PNG", Tag = ExportPreset.Telegram });
        _presetBox.Items.Add(new ComboBoxItem { Content = "QQ — 240×240 PNG", Tag = ExportPreset.QQ });
        _presetBox.Items.Add(new ComboBoxItem { Content = "WhatsApp — 512×512 WEBP ≤100KB", Tag = ExportPreset.WhatsApp });
        _presetBox.Items.Add(new ComboBoxItem { Content = "原始图片 — PNG", Tag = ExportPreset.Raw });
        _presetBox.SelectedIndex = 0;
        root.Children.Add(_presetBox);

        root.Children.Add(new TextBlock { Text = "输出目录：", Margin = new Thickness(0, 12, 0, 4) });
        var dirPanel = new Grid();
        dirPanel.ColumnDefinitions.Add(new ColumnDefinition());
        dirPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _dirBox = new TextBox { Padding = new Thickness(4) };
        Grid.SetColumn(_dirBox, 0);
        dirPanel.Children.Add(_dirBox);
        var browseBtn = new Button { Content = "浏览…", Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        browseBtn.Click += OnBrowse;
        Grid.SetColumn(browseBtn, 1);
        dirPanel.Children.Add(browseBtn);
        root.Children.Add(dirPanel);

        _statusLabel = new TextBlock { Margin = new Thickness(0, 12, 0, 0), Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11 };
        root.Children.Add(_statusLabel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var okBtn = new Button { Content = "导出", Width = 80, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        okBtn.Click += OnExport;
        var cancelBtn = new Button { Content = "关闭", Width = 80, IsCancel = true };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        root.Children.Add(buttons);

        Content = root;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择导出目录" };
        if (dlg.ShowDialog(this) == true)
            _dirBox.Text = dlg.FolderName;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_dirBox.Text) || !Directory.Exists(_dirBox.Text))
        {
            _statusLabel.Foreground = System.Windows.Media.Brushes.Red;
            _statusLabel.Text = "请选择有效的目录";
            return;
        }
        if (_presetBox.SelectedItem is not ComboBoxItem cbi || cbi.Tag is not ExportPreset preset) return;

        _statusLabel.Foreground = System.Windows.Media.Brushes.Gray;
        _statusLabel.Text = "导出中…";
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                int n = MemePackExporter.Export(_folder, preset, _dirBox.Text, _persistence);
                _statusLabel.Foreground = System.Windows.Media.Brushes.DarkGreen;
                _statusLabel.Text = $"已导出 {n} 张到 {_dirBox.Text}";
            }
            catch (Exception ex)
            {
                _statusLabel.Foreground = System.Windows.Media.Brushes.Red;
                _statusLabel.Text = "导出失败：" + ex.Message;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
