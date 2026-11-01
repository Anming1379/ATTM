using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ATTM.Core.Syntax;

namespace ATTM.Editing;

/// <summary>
/// 缩略图的一次状态快照。参数变多时只改这个类型，不必反复改 <see cref="EditorMinimap.Update"/> 的签名。
/// </summary>
public readonly record struct MinimapState(
    TextBuffer? Buffer,
    EditorPalette Palette,
    double LineHeight,
    double VerticalOffset,
    double ViewportHeight,
    int CaretLine,
    HashSet<int>? ErrorLines);

/// <summary>
/// 右侧缩略图：把整篇谱面按统一比例压成横条，再叠一个当前视野的方框。
/// 条、视野框、光标标记全部用同一套"文档行 -> 像素"的映射，保证上下对齐。
/// 点一下或拖动可以快速跳转。
/// </summary>
public sealed class EditorMinimap : FrameworkElement
{
    /// <summary>面板宽度。</summary>
    public const double PanelWidth = 80;

    /// <summary>顶部留白。</summary>
    private const double TopMargin = 3;

    /// <summary>短文档时每行最多占多少像素（避免把 20 行画成 600px 的大方块）。</summary>
    private const double MaxLinePixel = 6.0;

    /// <summary>文档特别长时每行的最小像素（只为避免除零，正常都会被 1 以下的值适配）。</summary>
    private const double MinLinePixel = 0.05;

    /// <summary>一个桶至少这么高，保证条与条之间有肉眼可见的空隙。</summary>
    private const double MinBucketPixel = 4.0;

    private const double BarInset = 9;
    private const double MinBarWidth = 6;

    private TextBuffer? _buffer;
    private EditorPalette _palette = EditorPalette.CreateFallback();
    private HashSet<int> _errorLines = new();

    private double _lineHeight = 25;
    private double _verticalOffset;
    private double _viewportHeight;
    private int _caretLine = -1;

    /// <summary>文档行 -> 缩略图像素的统一比例。</summary>
    private double _linePixel = MaxLinePixel;

    /// <summary>内容层缓存：只有文档、尺寸、配色变化时才重建。</summary>
    private readonly DrawingGroup _content = new();
    private bool _contentDirty = true;
    private int _bufferVersion = -1;
    private double _builtWidth;
    private double _builtHeight;

    private Brush _noteBrush = Brushes.Gray;
    private Brush _commentBrush = Brushes.Gray;
    private Brush _directiveBrush = Brushes.Gray;
    private Brush _errorBrush = Brushes.Red;
    private Brush _viewportFill = Brushes.Transparent;
    private Brush _hoverFill = Brushes.Transparent;
    private Pen _viewportPen = new(Brushes.Gray, 1);
    private Brush _caretBrush = Brushes.OrangeRed;

    private bool _dragging;
    private double _hoverY = -1;

    public EditorMinimap()
    {
        Width = PanelWidth;
        Focusable = false;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>请求把编辑器滚到某个纵向偏移。</summary>
    public Action<double>? ScrollRequested { get; set; }

    /// <summary>一行文档对应多少像素（供对齐测试与调试使用）。</summary>
    public double LinePixel => _linePixel;

    /// <summary>把文档行号换算成缩略图里的纵坐标（同一个映射，供测试使用）。</summary>
    public double YForLine(double lineIndex) => TopMargin + lineIndex * _linePixel;

    /// <summary>当前视野方框的顶边（像素）。</summary>
    public double ViewportTop => TopMargin + (_verticalOffset / Math.Max(1, _lineHeight)) * _linePixel;

    /// <summary>当前视野方框的高度（像素）。</summary>
    public double ViewportHeightPx => (_viewportHeight / Math.Max(1, _lineHeight)) * _linePixel;

    public void Update(in MinimapState state)
    {
        var buffer = state.Buffer;
        var palette = state.Palette;
        var lineHeight = state.LineHeight;
        var verticalOffset = state.VerticalOffset;
        var viewportHeight = state.ViewportHeight;
        var caretLine = state.CaretLine;
        var errorLines = state.ErrorLines;

        // TextBuffer 是原地修改的，引用不变不代表内容不变，必须看版本号
        var version = buffer?.Version ?? -1;

        if (!ReferenceEquals(_palette, palette))
        {
            _palette = palette;
            // 低透明度 + 降饱和：只是"淡淡的文字影子"，不抢正文的注意力
            _noteBrush = Soft(palette.Foreground, 0x34, 0.25);
            _commentBrush = Soft(palette.Comment, 0x20, 0.20);
            _directiveBrush = Soft(palette.Directive, 0x3A, 0.35);
            _errorBrush = Soft(palette.Error, 0x7C, 0.25);
            _viewportFill = WithAlpha(palette.Accent, 0x16);
            _hoverFill = WithAlpha(palette.Border, 0x55);
            _viewportPen = new Pen(WithAlpha(palette.Border, 0xB0), 1);
            _caretBrush = palette.Caret;
            _contentDirty = true;
        }

        if (!ReferenceEquals(_buffer, buffer)
            || version != _bufferVersion
            || Math.Abs(_lineHeight - lineHeight) > 0.01)
        {
            _contentDirty = true;
        }

        _bufferVersion = version;
        _buffer = buffer;
        _lineHeight = Math.Max(1, lineHeight);
        _verticalOffset = Math.Max(0, verticalOffset);
        _viewportHeight = Math.Max(0, viewportHeight);
        _caretLine = caretLine;

        if (errorLines is not null && !errorLines.SetEquals(_errorLines))
        {
            _errorLines = errorLines;
            _contentDirty = true;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        dc.DrawRectangle(_palette.GutterBackground, null, new Rect(0, 0, width, height));
        dc.DrawLine(new Pen(_palette.Border, 1), new Point(0.5, 0), new Point(0.5, height));

        if (_buffer is null || _buffer.LineCount == 0) return;

        UpdateScale(height);

        if (_contentDirty
            || Math.Abs(_builtWidth - width) > 0.5
            || Math.Abs(_builtHeight - height) > 0.5)
        {
            RebuildContent(width, height);
            _contentDirty = false;
            _builtWidth = width;
            _builtHeight = height;
        }

        dc.DrawDrawing(_content);

        // 悬停提示条
        if (_hoverY >= 0 && !_dragging)
            dc.DrawRectangle(_hoverFill, null, new Rect(0, _hoverY - 1, width, 2));

        // 当前视野
        var viewY = ViewportTop;
        var viewH = Math.Max(8, ViewportHeightPx);
        viewH = Math.Min(viewH, Math.Max(0, height - viewY - 1));

        if (viewH > 0)
            dc.DrawRectangle(_viewportFill, _viewportPen, new Rect(1.5, viewY, width - 3, viewH));

        // 光标行：右侧一小段主题色
        if (_caretLine >= 0 && _caretLine < _buffer.LineCount)
        {
            var y = YForLine(_caretLine);
            dc.DrawRectangle(_caretBrush, null,
                new Rect(width - 3, y, 2, Math.Max(2, _linePixel * 0.9)));
        }
    }

    /// <summary>统一比例：文档整体在面板里的缩放系数，条和视野框都用它。</summary>
    private void UpdateScale(double height)
    {
        var lines = Math.Max(1, _buffer?.LineCount ?? 1);
        var available = Math.Max(8, height - TopMargin * 2);
        var fit = available / lines;

        _linePixel = Math.Clamp(fit, MinLinePixel, MaxLinePixel);
    }

    /// <summary>把整篇文档压成若干个桶，每个桶画一条柔和的横条，空白行留空。</summary>
    private void RebuildContent(double width, double height)
    {
        using var dc = _content.Open();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var buffer = _buffer;
        if (buffer is null || buffer.LineCount == 0) return;

        // 行太密时一个桶覆盖多行，保证条与条之间仍有空隙
        var linesPerBucket = Math.Max(1, (int)Math.Ceiling(MinBucketPixel / _linePixel));
        var bucketPixel = linesPerBucket * _linePixel;
        var barHeight = Math.Max(1.2, bucketPixel * 0.58);

        var usableWidth = Math.Max(MinBarWidth, width - BarInset - 9);
        var maxLength = Math.Max(8, buffer.MaxLineLength);

        for (var start = 0; start < buffer.LineCount; start += linesPerBucket)
        {
            var end = Math.Min(buffer.LineCount, start + linesPerBucket);

            var nonEmpty = 0;
            var totalLength = 0;
            var hasError = false;
            var hasDirective = false;
            var commentOnly = true;

            for (var line = start; line < end; line++)
            {
                var text = buffer.GetLine(line).Trim();
                if (text.Length == 0) continue;

                nonEmpty++;
                totalLength += text.Length;

                if (_errorLines.Contains(line)) hasError = true;
                if (text[0] != '#') commentOnly = false;
                if (text.Contains('=', StringComparison.Ordinal)) hasDirective = true;
            }

            if (nonEmpty == 0) continue;

            var brush = hasError ? _errorBrush
                : hasDirective ? _directiveBrush
                : commentOnly ? _commentBrush
                : _noteBrush;

            var averageLength = totalLength / (double)nonEmpty;
            var barWidth = Math.Clamp(usableWidth * (averageLength / maxLength), MinBarWidth, usableWidth);

            // 关键：y 用统一映射算，和视野框、光标标记完全对齐
            var y = YForLine(start);

            dc.DrawRectangle(brush, null, new Rect(BarInset, y, barWidth, barHeight));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        ScrollToPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        _hoverY = e.GetPosition(this).Y;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var y = e.GetPosition(this).Y;

        if (_dragging)
        {
            ScrollToPoint(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (Math.Abs(y - _hoverY) > 0.5)
        {
            _hoverY = y;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverY < 0 && !_dragging) return;

        _hoverY = -1;
        InvalidateVisual();
    }

    private void ScrollToPoint(Point point)
    {
        if (_buffer is null || _buffer.LineCount == 0) return;

        var visibleLines = _viewportHeight / Math.Max(1, _lineHeight);
        var targetLine = (point.Y - TopMargin) / Math.Max(0.01, _linePixel) - visibleLines / 2;
        var target = targetLine * _lineHeight;

        ScrollRequested?.Invoke(Math.Max(0, target));
    }

    /// <summary>降低饱和度并压低透明度，得到柔和的横条颜色。</summary>
    private static Brush Soft(Brush source, byte alpha, double desaturate)
    {
        if (source is not SolidColorBrush solid) return source;

        var color = solid.Color;
        var gray = (byte)(color.R * 0.299 + color.G * 0.587 + color.B * 0.114);

        var r = (byte)(color.R + (gray - color.R) * desaturate);
        var g = (byte)(color.G + (gray - color.G) * desaturate);
        var b = (byte)(color.B + (gray - color.B) * desaturate);

        var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush WithAlpha(Brush brush, byte alpha)
    {
        if (brush is not SolidColorBrush solid) return brush;

        var copy = new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
        copy.Freeze();
        return copy;
    }
}
