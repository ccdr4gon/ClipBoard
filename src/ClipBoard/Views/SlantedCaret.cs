using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClipBoard.Views;

/// <summary>
/// 斜体搜索框的光标。WPF 的光标是竖直的，画在最后一个字形的步进宽度处，而斜体字形的上半部会伸出步进宽度，
/// 所以光标会压在字母（例如 t）的右上角，看起来太靠左。这里隐藏原生光标，按字体真实的倾斜角画一条斜的光标，
/// 与笔画平行、在基线处穿过插入点（Word、macOS 对斜体文字也是这样画光标）。闪烁节奏跟系统设置一致。
/// </summary>
internal sealed class SlantedCaret
{
    private const double FallbackSlantDegrees = 13; // Georgia Italic 的笔画倾角
    private readonly TextBox _box;
    private readonly Line _line;
    private readonly DispatcherTimer? _blink;
    private double _tan = double.NaN, _baseline;

    /// <summary>离屏截图用：窗口不激活、搜索框拿不到键盘焦点时也按有焦点画出光标，且不闪烁。</summary>
    internal bool AssumeFocused { get; set; }

    public SlantedCaret(TextBox box, Line line)
    {
        _box = box;
        _line = line;
        _box.CaretBrush = Brushes.Transparent;
        _line.StrokeThickness = SystemParameters.CaretWidth;
        uint blinkMs = GetCaretBlinkTime();
        if (blinkMs is > 0 and < uint.MaxValue)
        {
            _blink = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(blinkMs) };
            _blink.Tick += (_, _) => _line.Visibility = _line.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        }
        _box.SelectionChanged += (_, _) => Update();
        _box.TextChanged += (_, _) => Update();
        _box.SizeChanged += (_, _) => Update();
        _box.IsKeyboardFocusedChanged += (_, _) => Update();
        _box.IsVisibleChanged += (_, _) => Update();
        _box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Update()));
    }

    /// <summary>移动或输入后光标立即显示并重新开始闪烁，与原生光标一致。</summary>
    private void Update()
    {
        // 排版在 Render 前完成，此时取到的字符位置才是最新的。
        _box.Dispatcher.BeginInvoke(Place, DispatcherPriority.Render);
    }

    private void Place()
    {
        _blink?.Stop();
        // 原生光标在没有键盘焦点或选中了文字时不显示。
        if (!(_box.IsKeyboardFocused || AssumeFocused) || _box.SelectionLength > 0 || !_box.IsVisible || _line.Parent is not UIElement parent)
        {
            _line.Visibility = Visibility.Collapsed;
            return;
        }
        var rect = _box.GetRectFromCharacterIndex(_box.CaretIndex);
        if (rect.IsEmpty || double.IsInfinity(rect.Height))
        {
            _line.Visibility = Visibility.Collapsed;
            return;
        }
        if (double.IsNaN(_tan)) MeasureFont();
        var top = _box.TranslatePoint(rect.TopLeft, parent);
        double baseline = top.Y + Math.Min(_baseline * _box.FontSize, rect.Height);
        double bottom = top.Y + rect.Height;
        // 斜体字母的收笔（例如 t 的尾巴）正好落在插入点上：后面没有字时往右让出约 1 点，光标不贴着字；
        // 两个字之间不挪，否则会压到后一个字。
        string text = _box.Text;
        bool openRight = _box.CaretIndex >= text.Length || char.IsWhiteSpace(text[_box.CaretIndex]);
        double x = top.X + (openRight ? 0.07 * _box.FontSize : 0);
        _line.X1 = x - (bottom - baseline) * _tan;
        _line.Y1 = bottom;
        _line.X2 = x + (baseline - top.Y) * _tan;
        _line.Y2 = top.Y;
        _line.Visibility = Visibility.Visible;
        if (!AssumeFocused) _blink?.Start();
    }

    internal void Refresh() => Update();

    // 取搜索框实际用上的斜体字体（字体列表里第一个已安装的），从 l 的竖笔量出倾角。
    private void MeasureFont()
    {
        _tan = Math.Tan(FallbackSlantDegrees * Math.PI / 180);
        _baseline = 0.9;
        foreach (var name in _box.FontFamily.Source.Split(','))
        {
            var typeface = new Typeface(new FontFamily(name.Trim()), _box.FontStyle, _box.FontWeight, _box.FontStretch);
            if (!typeface.TryGetGlyphTypeface(out var glyphs)) continue;
            _baseline = glyphs.Baseline;
            if (glyphs.CharacterToGlyphMap.TryGetValue('l', out ushort index) && StemSlope(glyphs.GetGlyphOutline(index, 1000, 1000)) is { } slope)
                _tan = slope;
            return;
        }
    }

    /// <summary>最长的近竖直边的斜率（水平位移 / 垂直位移）。</summary>
    private static double? StemSlope(Geometry outline)
    {
        double best = 0, slope = 0;
        foreach (var figure in outline.GetFlattenedPathGeometry(0.1, ToleranceType.Absolute).Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly) points.AddRange(poly.Points);
                else if (segment is LineSegment single) points.Add(single.Point);
            }
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                double dx = a.X - b.X, dy = a.Y - b.Y, length = Math.Sqrt(dx * dx + dy * dy);
                if (Math.Abs(dy) > 0.8 * length && length > best) { best = length; slope = Math.Abs(dx / dy); }
            }
        }
        return best > 100 && slope < 0.5 ? slope : null; // 竖笔至少 0.1 em；直立字体返回 0
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();
}
