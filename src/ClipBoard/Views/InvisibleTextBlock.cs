using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClipBoard.Views;

public static class InvisibleTextBlock
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(InvisibleTextBlock),
        new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, string? v) => d.SetValue(SourceProperty, v);

    public static readonly DependencyProperty ShowInvisibleProperty = DependencyProperty.RegisterAttached(
        "ShowInvisible", typeof(bool), typeof(InvisibleTextBlock),
        new PropertyMetadata(false, OnSourceChanged));

    public static bool GetShowInvisible(DependencyObject d) => (bool)d.GetValue(ShowInvisibleProperty);
    public static void SetShowInvisible(DependencyObject d, bool v) => d.SetValue(ShowInvisibleProperty, v);

    private static readonly SolidColorBrush MarkerBrush = new(Color.FromRgb(0xD1, 0x14, 0x14));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var src = GetSource(tb) ?? "";
        var show = GetShowInvisible(tb);
        tb.Inlines.Clear();
        if (!show)
        {
            tb.Inlines.Add(new Run(src.Replace("\r", "").Replace("\n", " ⏎ ")));
            return;
        }
        Render(tb, src);
    }

    private static void Render(TextBlock tb, string s)
    {
        var buf = new System.Text.StringBuilder();
        double baseSize = tb.FontSize > 0 ? tb.FontSize : 13;

        void FlushNormal()
        {
            if (buf.Length == 0) return;
            tb.Inlines.Add(new Run(buf.ToString().Replace("\r", "").Replace("\n", " ⏎ ")));
            buf.Clear();
        }

        foreach (var ch in s)
        {
            if (IsInvisible(ch))
            {
                FlushNormal();
                string marker = ch switch
                {
                    <= '\u001F' => ((char)(0x2400 + ch)).ToString(),
                    '\u007F' => "\u2421",
                    _ => $"<U+{(int)ch:X4}>",
                };
                var run = new Run(marker)
                {
                    Foreground = MarkerBrush,
                    FontSize = Math.Max(baseSize - 2, 8),
                };
                tb.Inlines.Add(run);
            }
            else
            {
                buf.Append(ch);
            }
        }
        FlushNormal();
    }

    private static bool IsInvisible(char c)
    {
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r') return false;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat switch
        {
            UnicodeCategory.Control => true,
            UnicodeCategory.Format => true,
            UnicodeCategory.LineSeparator => true,
            UnicodeCategory.ParagraphSeparator => true,
            UnicodeCategory.SpaceSeparator when c != ' ' => true,
            _ => false,
        };
    }
}
