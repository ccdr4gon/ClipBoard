using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClipBoard.Views;

/// <summary>
/// 斜体搜索框的光标（与 Windows 版 Views/SlantedCaret 相同的做法）。竖直光标画在最后一个字形的步进宽度处，
/// 斜体字形的上半部会伸出去，光标就压在字母（例如 t）的右上角。这里隐藏原生光标，按 Georgia Italic 的笔画倾角
/// 画一条斜的光标，在基线处穿过插入点；后面没有字时再往右让出约 1 点。
/// </summary>
internal sealed class SlantedCaret
{
    private static readonly double Slant = Math.Tan(13 * Math.PI / 180); // Georgia Italic 的笔画倾角
    private readonly TextBox _box;
    private readonly Line _line;
    private readonly DispatcherTimer _blink = new();
    private TextPresenter? _presenter;
    private bool _queued;

    public SlantedCaret(TextBox box, Line line)
    {
        _box = box;
        _line = line;
        _box.CaretBrush = Brushes.Transparent;
        _blink.Tick += (_, _) => _line.Opacity = _line.Opacity > 0 ? 0 : 1;
        _box.TemplateApplied += (_, e) =>
        {
            if (_presenter != null) _presenter.CaretBoundsChanged -= OnCaretBoundsChanged;
            _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
            if (_presenter != null) _presenter.CaretBoundsChanged += OnCaretBoundsChanged;
            Update();
        };
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.TextProperty
                || e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty
                || e.Property == Avalonia.Input.InputElement.IsFocusedProperty || e.Property == Visual.IsVisibleProperty
                || e.Property == Visual.BoundsProperty)
                Update();
        };
    }

    /// <summary>离屏截图和测试用：没有焦点时也按有焦点画出光标，且不闪烁。</summary>
    internal bool AssumeFocused { get; set; }

    private void OnCaretBoundsChanged(object? sender, EventArgs e) => Update();

    // 等排版完成再取字符位置；移动或输入后光标立即显示并重新开始闪烁，与原生光标一致。
    internal void Update()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Place(); }, DispatcherPriority.Background);
    }

    private void Place()
    {
        _blink.Stop();
        _line.Opacity = 1;
        if (!(_box.IsFocused || AssumeFocused) || _box.SelectionStart != _box.SelectionEnd || !_box.IsEffectivelyVisible
            || _presenter is not { } presenter || _line.Parent is not Visual parent)
        {
            _line.IsVisible = false;
            return;
        }
        var layout = presenter.TextLayout;
        var rect = layout.HitTestTextPosition(_box.CaretIndex);
        if (presenter.TranslatePoint(rect.TopLeft, parent) is not { } top || rect.Height <= 0)
        {
            _line.IsVisible = false;
            return;
        }
        double baseline = top.Y + (layout.TextLines.Count > 0 ? layout.TextLines[0].Baseline : rect.Height * 0.8);
        double bottom = top.Y + rect.Height;
        string text = _box.Text ?? "";
        bool openRight = _box.CaretIndex >= text.Length || char.IsWhiteSpace(text[_box.CaretIndex]);
        double x = top.X + (openRight ? 0.07 * _box.FontSize : 0);
        _line.StartPoint = new Point(x - (bottom - baseline) * Slant, bottom);
        _line.EndPoint = new Point(x + (baseline - top.Y) * Slant, top.Y);
        _line.IsVisible = true;
        var interval = presenter.CaretBlinkInterval;
        if (!AssumeFocused && interval > TimeSpan.Zero) { _blink.Interval = interval; _blink.Start(); }
    }
}
