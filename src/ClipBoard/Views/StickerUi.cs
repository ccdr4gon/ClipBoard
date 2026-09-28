using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace ClipBoard.Views;

internal static class StickerUi
{
    public static readonly Brush Paper = new SolidColorBrush(Color.FromRgb(0xF8, 0xF5, 0xEE));
    public static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x2C, 0x24, 0x18));
    public static Button Button(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 7, 7), MinHeight = 30 };
        button.Click += click;
        return button;
    }
    public static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 5), TextWrapping = TextWrapping.Wrap, Foreground = Ink };
    public static TextBox Input(string value = "") => new() { Text = value, Padding = new Thickness(7), MinHeight = 32 };
    public static BitmapSource DecodeImage(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes) ?? throw new IOException("预览图片无法读取。");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        var result = new BitmapImage();
        result.BeginInit(); result.CacheOption = BitmapCacheOption.OnLoad; result.StreamSource = stream; result.EndInit(); result.Freeze();
        return result;
    }
}
