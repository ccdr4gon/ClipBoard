using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using ClipBoard.Services;

namespace ClipBoard.Views;

public sealed class TelegramImportWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    public TelegramStickerSet? RemoteSet { get; private set; }
    public TelegramSticker[] SelectedStickers { get; private set; } = [];

    public TelegramImportWindow(TelegramConnection connection)
    {
        Title = "从 Telegram 导入贴纸包"; Width = 830; Height = 670; MinWidth = 650; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = StickerUi.Paper;
        var root = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(StickerUi.Label("粘贴 t.me/addstickers/… 链接，预览后选择要导入的贴纸。重复导入同一包会保留已有的本地修改。"));
        var link = StickerUi.Input(); top.Children.Add(link);
        var status = StickerUi.Label("");
        var list = new ListBox { SelectionMode = SelectionMode.Multiple, Background = System.Windows.Media.Brushes.Transparent };
        list.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Orientation="Horizontal" Margin="5">
                <CheckBox IsChecked="{Binding Selected, Mode=TwoWay}" VerticalAlignment="Center" Margin="0,0,12,0"/>
                <Image Source="{Binding Image}" Width="60" Height="60" Stretch="Uniform"/>
                <TextBlock Text="{Binding Label}" VerticalAlignment="Center" Margin="12,0,0,0"/>
              </StackPanel>
            </DataTemplate>
            """);
        var load = StickerUi.Button("读取贴纸包", async (sender, _) =>
        {
            var button = (Button)sender; button.IsEnabled = false;
            try
            {
                using var client = new TelegramStickerClient(connection.Token);
                RemoteSet = await client.GetSetAsync(TelegramStickerClient.ParseSetName(link.Text), _shutdown.Token);
                if (RemoteSet.StickerType != "regular") throw new InvalidOperationException("请选择普通贴纸包，不支持将自定义 emoji 或面具包作为普通贴纸导入。");
                var choices = RemoteSet.Stickers.Select((sticker, index) => new Choice(sticker, $"{index + 1:D3}  {sticker.Emoji}  {sticker.Format}")).ToArray();
                list.ItemsSource = choices;
                status.Text = $"{RemoteSet.Title} · {choices.Length} 张，正在读取缩略图…";
                using var limit = new SemaphoreSlim(4);
                await Task.WhenAll(choices.Select(async choice =>
                {
                    if (choice.Sticker.Thumbnail == null) return;
                    await limit.WaitAsync(_shutdown.Token);
                    try
                    {
                        var bytes = await client.DownloadAsync(choice.Sticker.Thumbnail.FileId, _shutdown.Token);
                        choice.Image = await Task.Run(() => StickerUi.DecodeImage(bytes), _shutdown.Token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* 单个缩略图缺失不阻断原件导入。 */ }
                    finally { limit.Release(); }
                }));
                status.Text = $"{RemoteSet.Title} · {choices.Length} 张。勾选要导入的素材。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status.Text = ex.Message; RemoteSet = null; }
            finally { button.IsEnabled = true; }
        });
        top.Children.Add(load); top.Children.Add(status);
        var bottom = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(StickerUi.Button("全选", (_, _) => { foreach (var item in list.Items.OfType<Choice>()) item.Selected = true; }));
        bottom.Children.Add(StickerUi.Button("全不选", (_, _) => { foreach (var item in list.Items.OfType<Choice>()) item.Selected = false; }));
        bottom.Children.Add(StickerUi.Button("导入所选", (_, _) =>
        {
            SelectedStickers = list.Items.OfType<Choice>().Where(c => c.Selected).Select(c => c.Sticker).ToArray();
            if (RemoteSet == null || SelectedStickers.Length == 0) { status.Text = "请先读取贴纸包并勾选至少一张。"; return; }
            DialogResult = true;
        }));
        root.Children.Add(list); Content = root;
        Closed += (_, _) => _shutdown.Cancel();
    }

    private sealed class Choice(TelegramSticker sticker, string label) : INotifyPropertyChanged
    {
        public TelegramSticker Sticker { get; } = sticker;
        public string Label { get; } = label;
        private bool _selected = true;
        public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        private BitmapSource? _image;
        public BitmapSource? Image { get => _image; set { _image = value; PropertyChanged?.Invoke(this, new(nameof(Image))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
