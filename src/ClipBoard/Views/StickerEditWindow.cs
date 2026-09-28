using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClipBoard.Models;
using ClipBoard.Services;
using WpfAnimatedGif;

namespace ClipBoard.Views;

public sealed class StickerEditWindow : Window
{
    private readonly List<ClipItem> _generated = [];
    private CancellationTokenSource? _operation;
    public ClipItem? Result { get; private set; }

    public StickerEditWindow(ClipItem source, StickerMediaService media, PersistenceService persistence)
    {
        Title = "编辑贴纸 — " + source.TitleOrUntitled; Width = 960; Height = 740;
        MinWidth = 800; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = StickerUi.Paper;
        var root = new DockPanel { Margin = new Thickness(20) };
        var status = StickerUi.Label("原件会保留。裁剪按原图百分比填写；动画输出为 Telegram 视频贴纸。");
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var controls = new StackPanel { Width = 275, Margin = new Thickness(20, 0, 0, 0) };
        DockPanel.SetDock(controls, Dock.Right); root.Children.Add(new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Width = 300 });
        // DockPanel 的 Dock 需设置在直接子元素上。
        DockPanel.SetDock(root.Children[root.Children.Count - 1], Dock.Right);
        var preview = new Image { Source = source.Image, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(10) };
        root.Children.Add(new Border { Background = System.Windows.Media.Brushes.White, Child = preview, Padding = new Thickness(10) });
        controls.Children.Add(StickerUi.Label("裁剪区域（百分比）"));
        var fields = new Dictionary<string, TextBox>();
        foreach (var (name, value) in new[] { ("左边", "0"), ("顶部", "0"), ("宽度", "100"), ("高度", "100") })
        {
            controls.Children.Add(StickerUi.Label(name)); fields[name] = StickerUi.Input(value); controls.Children.Add(fields[name]);
        }
        controls.Children.Add(StickerUi.Label("叠加文字（最多 4 行）"));
        var caption = StickerUi.Input(); caption.AcceptsReturn = true; caption.Height = 75; controls.Children.Add(caption);
        bool animated = source.Sticker != null && StickerMediaService.IsAnimated(source.Sticker.Format);
        foreach (var (name, value) in new[] { ("起点（秒）", "0"), ("输出时长（秒）", Number(Math.Min(3, Math.Max(0.1, source.Sticker?.DurationSeconds ?? 3)))), ("播放速度", "1") })
        {
            controls.Children.Add(StickerUi.Label(name)); fields[name] = StickerUi.Input(value); fields[name].IsEnabled = animated; controls.Children.Add(fields[name]);
        }
        var save = StickerUi.Button("使用这版修改", (_, _) => { if (Result != null) DialogResult = true; }); save.IsEnabled = false;
        foreach (var field in fields.Values.Append(caption))
            field.TextChanged += (_, _) => save.IsEnabled = false;
        controls.Children.Add(StickerUi.Button("生成预览", async (sender, _) =>
        {
            if (_operation != null) return;
            var button = (Button)sender; button.IsEnabled = false; save.IsEnabled = false;
            foreach (var field in fields.Values.Append(caption)) field.IsEnabled = false;
            _operation = new CancellationTokenSource();
            try
            {
                double Read(string name) => double.Parse(fields[name].Text, CultureInfo.InvariantCulture);
                var edit = new StickerEditOptions(Read("左边") / 100, Read("顶部") / 100, Read("宽度") / 100, Read("高度") / 100,
                    caption.Text, animated ? Read("起点（秒）") : 0, animated ? Read("输出时长（秒）") : 3, animated ? Read("播放速度") : 1);
                status.Text = "正在生成修改版…";
                var result = await media.EditAsync(source, edit, _operation.Token);
                _generated.Add(result);
                Result = result;
                ImageBehavior.SetAnimatedSource(preview, null); preview.Source = result.Image;
                if (animated)
                {
                    var file = await media.CreatePreviewAsync(result, _operation.Token);
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.UriSource = new Uri(file); image.EndInit(); image.Freeze();
                    ImageBehavior.SetAnimatedSource(preview, image);
                }
                status.Text = "预览已生成。确认画面后点“使用这版修改”；原件不会被覆盖。";
                save.IsEnabled = true;
            }
            catch (OperationCanceledException) { status.Text = "已取消。"; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally
            {
                _operation.Dispose(); _operation = null; button.IsEnabled = true;
                foreach (var pair in fields)
                    pair.Value.IsEnabled = animated || pair.Key is "左边" or "顶部" or "宽度" or "高度";
                caption.IsEnabled = true;
            }
        }));
        controls.Children.Add(save);
        controls.Children.Add(StickerUi.Button("取消转换", (_, _) => _operation?.Cancel()));
        Content = root;
        Closing += (_, e) => { if (_operation != null) { e.Cancel = true; _operation.Cancel(); status.Text = "正在取消，请稍后关闭。"; } };
        Closed += (_, _) =>
        {
            ImageBehavior.SetAnimatedSource(preview, null);
            foreach (var item in _generated.Where(i => DialogResult != true || i != Result))
                foreach (var name in new[] { item.ImageBlobName, item.Sticker?.WorkingBlobName }.Where(n => n != null).Distinct())
                    try { File.Delete(persistence.GetBlobPath(name!)); } catch (IOException) { }
        };
    }
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
