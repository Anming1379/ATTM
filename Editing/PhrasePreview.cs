using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ATTM.Core.Syntax;

namespace ATTM.Editing;

/// <summary>
/// 侧边栏里的"乐句预览"：用超大字号显示当前播放到的那一小节（含并行声部的每一行），
/// 换小节时先让旧句淡出、再让新句逐行滑入，并实时框出正在发声的音。
///
/// 性能：整段排版（字号、每行 FormattedText、裁剪矩形、高亮矩形）只在内容/尺寸/主题变化时
/// 重建一次，动画帧里只做 PushTransform + DrawText，所以 60fps 下也很轻。
/// </summary>
public sealed class PhrasePreview : FrameworkElement
{
    public const double PanelWidth = 460;

    private const double SidePadding = 24;
    private const double HeaderHeight = 38;
    private const double MaxFontSize = 64;

    /// <summary>全窗模式下的字号上限（铺满整块编辑区，字可以放很大）。</summary>
    private const double MaxFontSizeFull = 180;
    private const double MinFontSize = 14;

    // 入场：滑入 + 淡入（缓出曲线，起步快、收尾稳）
    // 出场：只做渐隐，不位移，也不等待——新旧两段直接叠着交替，换句瞬间就有新内容。
    private const double InDurationMs = 240;
    private const double OutDurationMs = 85;

    /// <summary>出场起点透明度上限：旧的立刻压暗，新的读起来更清楚。</summary>
    private const double OutAlphaCap = 0.5;

    private const double InDelayMs = 0;
    private const double LineStaggerMs = 60;
    private const double SlideDistance = 40;   // 位移短一点，文字更快进入可读位置

    private readonly List<PhraseLine> _current = new();
    private readonly List<ActiveRange> _active = new();
    private readonly List<Mark> _marks = new();

    private OutgoingPhrase? _outgoing;
    private PhraseLayout? _layout;
    private int _contentRevision;

    private long _inStartedAt;
    private EditorPalette _palette = EditorPalette.CreateFallback();
    private Brush _background = Brushes.Black;
    private Typeface _typeface;
    private Typeface _uiTypeface;

    private bool _fullWindow;
    private bool _frameHooked;
    private DispatcherTimer? _watchdog;

    public PhrasePreview()
    {
        _typeface = CreateTypeface();
        _uiTypeface = CreateUiTypeface();

        ClipToBounds = true;
        SnapsToDevicePixels = true;
        SizeChanged += (_, _) =>
        {
            _layout = null;
            InvalidateVisual();
        };

        Width = PanelWidth;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>
    /// 切到"全窗"（铺满整个编辑区，盖住正文）或回到半屏侧边栏。
    /// 全窗时宽度交给外层容器决定，字号上限也放大。
    /// </summary>
    public void SetFullWindow(bool full)
    {
        if (_fullWindow == full) return;

        _fullWindow = full;
        HorizontalAlignment = full ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Width = full ? double.NaN : PanelWidth;

        _layout = null;
        InvalidateVisual();
    }

    public bool IsFullWindow => _fullWindow;

    /// <summary>当前乐句（按行）。</summary>
    public IReadOnlyList<PhraseLine> Lines => _current;

    public int MeasureIndex { get; private set; } = -1;

    /// <summary>总小节数，用于顶部计数显示。</summary>
    public int MeasureCount { get; private set; }

    public bool HasContent => _current.Count > 0;

    /// <summary>是否正在显示"播放时在这里显示当前乐句"的空状态提示。</summary>
    public bool IsShowingIdleHint => MeasureIndex < 0 && _current.Count == 0 && _outgoing is null;

    /// <summary>当前正在发声的音（相对每个乐句行的列区间）。</summary>
    public IReadOnlyList<ActiveRange> ActiveRanges => _active;

    /// <summary>最近一次排版用的字号（供测试/诊断）。</summary>
    public double LastFontSize => _layout?.FontSize ?? 0;

    /// <summary>最近一次排版里最宽一行的宽度（供测试/诊断）。</summary>
    public double LastTextWidth => _layout?.MaxTextWidth ?? 0;

    /// <summary>排版缓存重建次数（动画期间不应该增长）。</summary>
    public int LayoutBuildCount { get; private set; }

    /// <summary>OnRender 调用次数与累计耗时（供性能检查）。</summary>
    public int RenderCount { get; private set; }

    public double RenderMilliseconds { get; private set; }

    public bool IsAnimating
    {
        get
        {
            if (_outgoing is not null) return true;
            if (_current.Count == 0) return false;
            return Now() < _inStartedAt || RevealProgress < 1;
        }
    }

    /// <summary>最后一行的揭示进度（0 = 还没出现，1 = 完全就位），供测试采样动画。</summary>
    public double RevealProgress
    {
        get
        {
            if (_current.Count == 0) return 0;
            return EnterProgress(_current.Count - 1, Now() - _inStartedAt);
        }
    }

    public string CurrentText => string.Join("\n", _current.Select(line => line.Text));

    public void SetPalette(EditorPalette palette)
    {
        _palette = palette;
        _typeface = CreateTypeface();
        _uiTypeface = CreateUiTypeface();
        _background = palette.GutterBackground;

        _layout = null;
        _marks.Clear();
        InvalidateVisual();
    }

    // ------------------------------------------------------------ 内容

    /// <summary>显示某一小节的乐句；与当前相同时不重复播放动画。</summary>
    public void Show(int measureIndex, IReadOnlyList<PhraseLine> lines, int measureCount = 0)
    {
        if (measureIndex == MeasureIndex && SameLines(lines)) return;

        if (_current.Count > 0)
            _outgoing = new OutgoingPhrase(new List<PhraseLine>(_current), _layout, AlphaOfCurrent(), Now());

        _current.Clear();
        _current.AddRange(lines);
        _active.Clear();
        _marks.Clear();
        _layout = null;
        _contentRevision++;
        MeasureIndex = measureIndex;
        MeasureCount = measureCount;

        // 新旧直接叠着交替：新句立刻开始入场，旧句在它下面渐隐
        _inStartedAt = (long)(Now() + InDelayMs);

        StartFrames();
        InvalidateVisual();
    }

    /// <summary>
    /// 淡出并清空。
    /// </summary>
    /// <param name="keepMeasure">
    /// true = 只是本小节提前退场，保留小节号（这样顶部的"第 N / M 小节"不会闪，
    /// 也不会露出"播放时在这里显示当前乐句"的提示）。
    /// </param>
    public void Hide(bool keepMeasure = false)
    {
        if (_current.Count == 0 && _outgoing is null)
        {
            if (!keepMeasure) MeasureIndex = -1;
            return;
        }

        if (_current.Count > 0)
            _outgoing = new OutgoingPhrase(new List<PhraseLine>(_current), _layout, AlphaOfCurrent(), Now());

        _current.Clear();
        _active.Clear();
        _marks.Clear();
        _layout = null;
        _contentRevision++;
        if (!keepMeasure) MeasureIndex = -1;

        StartFrames();
        InvalidateVisual();
    }

    /// <summary>更新"正在发声"的音，实时把当前音符框出来（不触发入场动画）。</summary>
    public void SetActiveSpans(IReadOnlyList<SourceSpan>? spans)
    {
        var next = new List<ActiveRange>();

        if (spans is not null && spans.Count > 0)
        {
            for (var i = 0; i < _current.Count; i++)
            {
                var line = _current[i];
                var lineStart = line.Offset;
                var lineEnd = lineStart + line.Text.Length;

                foreach (var span in spans)
                {
                    var start = Math.Max(span.Start, lineStart);
                    var end = Math.Min(span.Start + span.Length, lineEnd);
                    if (end > start) next.Add(new ActiveRange(i, start - lineStart, end - start));
                }
            }
        }

        if (SameActive(next)) return;

        _active.Clear();
        _active.AddRange(next);
        EnsureLayout();
        UpdateMarks();
        InvalidateVisual();
    }

    private bool SameLines(IReadOnlyList<PhraseLine> lines)
    {
        if (lines.Count != _current.Count) return false;

        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(lines[i].Text, _current[i].Text, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private bool SameActive(List<ActiveRange> next)
    {
        if (next.Count != _active.Count) return false;

        for (var i = 0; i < next.Count; i++)
        {
            if (next[i] != _active[i]) return false;
        }

        return true;
    }

    // ------------------------------------------------------------ 动画时钟

    private static long Now() => Environment.TickCount64;

    /// <summary>入场曲线：缓出（1-(1-t)^3），起步快、收尾稳。</summary>
    public static double EnterCurve(double t)
    {
        var clamped = Math.Clamp(t, 0.0, 1.0);
        return 1 - Math.Pow(1 - clamped, 3);
    }

    /// <summary>出场曲线：线性淡出（纯渐隐，不做位移与缓动）。</summary>
    public static double ExitCurve(double t) => 1 - Math.Clamp(t, 0.0, 1.0);

    private static double EnterProgress(int index, double elapsedMs)
        => EnterCurve((elapsedMs - index * LineStaggerMs) / InDurationMs);

    private static double ExitProgress(long elapsedMs)
        => ExitCurve(elapsedMs / OutDurationMs);

    private double AlphaOfCurrent()
    {
        if (_current.Count == 0) return 0;
        return EnterProgress(_current.Count - 1, Now() - _inStartedAt);
    }

    private void StartFrames()
    {
        if (!_frameHooked)
        {
            CompositionTarget.Rendering += OnFrame;
            _frameHooked = true;
        }

        // 兜底：离屏/无渲染循环时也能推进并收尾
        if (_watchdog is not null) return;

        _watchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };

        _watchdog.Tick += (_, _) =>
        {
            InvalidateVisual();
            StopFramesIfIdle();
        };

        _watchdog.Start();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        InvalidateVisual();
        StopFramesIfIdle();
    }

    private void StopFramesIfIdle()
    {
        if (IsAnimating) return;

        if (_frameHooked)
        {
            CompositionTarget.Rendering -= OnFrame;
            _frameHooked = false;
        }

        _watchdog?.Stop();
        _watchdog = null;
    }

    // ------------------------------------------------------------ 排版缓存

    private void EnsureLayout()
    {
        var body = BodyRect();
        if (body.Height < 8 || body.Width < 8)
        {
            _layout = null;
            return;
        }

        if (_layout is not null
            && Math.Abs(_layout.Width - body.Width) < 0.5
            && Math.Abs(_layout.Height - body.Height) < 0.5)
        {
            return;
        }

        _layout = PhraseLayout.Build(
            _current, body, _palette, _typeface, VisualTreeHelper.GetDpi(this).PixelsPerDip,
            _fullWindow ? MaxFontSizeFull : MaxFontSize);
        LayoutBuildCount++;
        UpdateMarks();
    }

    private void UpdateMarks()
    {
        _marks.Clear();
        if (_layout is null || _active.Count == 0) return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var range in _active)
        {
            if (range.Line < 0 || range.Line >= _layout.Lines.Count) continue;

            var text = _layout.Lines[range.Line].Text;
            var start = Math.Clamp(range.Start, 0, text.Length);
            var length = Math.Clamp(range.Length, 0, text.Length - start);
            if (length <= 0) continue;

            var prefixWidth = start == 0
                ? 0
                : LayoutHelper.Measure(text[..start], _layout.FontSize, _typeface, pixelsPerDip);
            var pieceWidth = LayoutHelper.Measure(text.Substring(start, length), _layout.FontSize, _typeface, pixelsPerDip);

            var textTop = _layout.Tops[range.Line];
            var textHeight = _layout.Lines[range.Line].Height;
            var left = _layout.Lefts[range.Line] + prefixWidth;

            // 高亮框只罩住字形带（Lekton 的行高比字形高，按比例收一点更贴身）
            _marks.Add(new Mark(
                range.Line,
                new Rect(left - 2, textTop + textHeight * 0.12, pieceWidth + 4, textHeight * 0.8)));
        }
    }

    private Rect BodyRect() => new(0, HeaderHeight, ActualWidth, Math.Max(0, ActualHeight - HeaderHeight));

    // ------------------------------------------------------------ 绘制

    protected override void OnRender(DrawingContext dc)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            RenderCore(dc);
        }
        finally
        {
            RenderCount++;
            RenderMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt)
                                  * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private void RenderCore(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        dc.DrawRectangle(_background, null, new Rect(0, 0, width, height));
        dc.DrawRectangle(_palette.Border, null, new Rect(0, 0, 1, height));

        DrawHeader(dc, width);

        var body = BodyRect();
        if (body.Height < 8) return;

        if (_outgoing is not null)
        {
            var outgoing = _outgoing;
            var elapsed = Now() - outgoing.StartedAt;
            var lastVisible = ExitProgress(elapsed);

            if (lastVisible <= 0.002) _outgoing = null;
            else DrawPhrase(dc, outgoing.Lines, outgoing.Layout, outgoing.StartedAt, Math.Min(outgoing.StartAlpha, OutAlphaCap), true);
        }

        if (_current.Count == 0)
        {
            // 播放中小节之间会有空场，这时候不要露出空状态提示（会闪一下）
            if (IsShowingIdleHint) DrawEmptyHint(dc, body);
            return;
        }

        EnsureLayout();
        if (_layout is null) return;

        DrawPhrase(dc, _current, _layout, _inStartedAt, 1.0, false);
    }

    private void DrawHeader(DrawingContext dc, double width)
    {
        var label = new FormattedText(
            "乐句预览",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            _uiTypeface,
            12,
            _palette.GutterText,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(label, new Point(SidePadding - 4, (HeaderHeight - label.Height) / 2));

        if (MeasureIndex >= 0 && MeasureCount > 0)
        {
            var counter = new FormattedText(
                $"第 {MeasureIndex + 1} / {MeasureCount} 小节",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _uiTypeface,
                12,
                _palette.GutterText,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(counter, new Point(Math.Max(0, width - SidePadding - counter.Width + 4), (HeaderHeight - counter.Height) / 2));
        }

        dc.DrawRectangle(_palette.Border, null, new Rect(SidePadding - 4, HeaderHeight - 1, Math.Max(0, width - SidePadding * 2 + 8), 1));
    }

    private void DrawEmptyHint(DrawingContext dc, Rect body)
    {
        var hint = new FormattedText(
            "播放时在这里显示当前乐句",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            _uiTypeface,
            13,
            _palette.GutterText,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        hint.MaxTextWidth = Math.Max(40, body.Width - SidePadding * 2);
        hint.TextAlignment = TextAlignment.Center;
        dc.DrawText(hint, new Point(0, body.Top + (body.Height - hint.Height) / 2));
    }

    private void DrawPhrase(
        DrawingContext dc,
        IReadOnlyList<PhraseLine> lines,
        PhraseLayout? layout,
        long startedAt,
        double batchAlpha,
        bool leaving)
    {
        if (layout is null || lines.Count == 0 || batchAlpha <= 0.002) return;

        var now = Now();

        for (var i = 0; i < lines.Count && i < layout.Lines.Count; i++)
        {
            var progress = leaving ? ExitProgress(now - startedAt) : EnterProgress(i, now - startedAt);
            if (progress <= 0.002) continue;

            // 偶数行从左滑入，奇数行从右滑入；出场只淡出，不位移
            var direction = i % 2 == 0 ? -1.0 : 1.0;
            var dx = leaving ? 0 : direction * SlideDistance * (1 - progress);
            var alpha = batchAlpha * (leaving ? progress : Math.Clamp(progress * 1.4, 0, 1));

            dc.PushOpacity(Math.Clamp(alpha, 0, 1));
            dc.PushClip(layout.Clips[i]);
            dc.PushTransform(new TranslateTransform(dx, 0));

            if (!leaving) DrawMarks(dc, i);
            dc.DrawText(layout.Lines[i], new Point(layout.Lefts[i], layout.Tops[i]));

            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
    }

    private void DrawMarks(DrawingContext dc, int lineIndex)
    {
        foreach (var mark in _marks)
        {
            if (mark.Line != lineIndex) continue;

            dc.DrawRectangle(_palette.PlayHighlight, null, mark.Rect);
            dc.DrawRectangle(_palette.Accent, null, new Rect(mark.Rect.Left, mark.Rect.Bottom - 2, mark.Rect.Width, 2));
        }
    }

    private static Typeface CreateTypeface()
    {
        var family = Application.Current?.TryFindResource("Font.Editor") as FontFamily
                     ?? new FontFamily("Consolas");

        return new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    private static Typeface CreateUiTypeface()
    {
        var family = Application.Current?.TryFindResource("Font.UI") as FontFamily
                     ?? new FontFamily("Microsoft YaHei UI");

        return new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    private readonly record struct Mark(int Line, Rect Rect);

    private sealed record OutgoingPhrase(
        IReadOnlyList<PhraseLine> Lines,
        PhraseLayout? Layout,
        double StartAlpha,
        long StartedAt);
}

/// <summary>一段乐句的排版结果：每行的 FormattedText、位置、裁剪矩形都预先算好。</summary>
internal sealed class PhraseLayout
{
    private PhraseLayout(
        IReadOnlyList<PhraseLine> source,
        IReadOnlyList<FormattedText> lines,
        double[] lefts,
        double[] tops,
        RectangleGeometry[] clips,
        double fontSize,
        double lineHeight,
        double width,
        double height,
        double maxTextWidth)
    {
        Source = source;
        Lines = lines;
        Lefts = lefts;
        Tops = tops;
        Clips = clips;
        FontSize = fontSize;
        LineHeight = lineHeight;
        Width = width;
        Height = height;
        MaxTextWidth = maxTextWidth;
    }

    public IReadOnlyList<PhraseLine> Source { get; }
    public IReadOnlyList<FormattedText> Lines { get; }
    public double[] Lefts { get; }
    public double[] Tops { get; }
    public RectangleGeometry[] Clips { get; }
    public double FontSize { get; }
    public double LineHeight { get; }
    public double Width { get; }
    public double Height { get; }
    public double MaxTextWidth { get; }

    public static PhraseLayout Build(
        IReadOnlyList<PhraseLine> source,
        Rect body,
        EditorPalette palette,
        Typeface typeface,
        double pixelsPerDip,
        double maxFontSize = 64)
    {
        var available = Math.Max(60, body.Width - 48);

        // 字号：按最宽的一行缩放到可用宽度
        var widest = 1.0;
        foreach (var line in source)
        {
            if (line.Text.Length == 0) continue;
            widest = Math.Max(widest, LayoutHelper.Measure(line.Text, 64, typeface, pixelsPerDip));
        }

        // 也要装得下高度：行数多、窗口矮的时候别把字撑出去
        var byHeight = source.Count == 0
            ? maxFontSize
            : Math.Max(14, body.Height / (source.Count * 1.5) * 0.85);

        var fontSize = Math.Clamp(Math.Min(64 * (available / widest), byHeight), 14, maxFontSize);
        var lineHeight = fontSize * 1.5;
        var totalHeight = lineHeight * source.Count;
        var top = body.Top + Math.Max(12, (body.Height - totalHeight) / 2);

        var texts = new List<FormattedText>(source.Count);
        var lefts = new double[source.Count];
        var tops = new double[source.Count];
        var clips = new RectangleGeometry[source.Count];

        for (var i = 0; i < source.Count; i++)
        {
            var line = source[i];
            var text = LayoutHelper.Create(line.Text, fontSize, palette.Foreground, typeface, pixelsPerDip);

            // 整行只排一次，再按 token 上色：字距、连字交给 WPF 统一处理
            foreach (var run in line.Runs)
            {
                var start = Math.Clamp(run.Start, 0, line.Text.Length);
                var length = Math.Clamp(run.Length, 0, line.Text.Length - start);
                if (length > 0) text.SetForegroundBrush(palette.BrushFor(run.Class), start, length);
            }

            var lineTop = top + i * lineHeight;
            texts.Add(text);
            lefts[i] = Math.Max(0, (body.Width - text.WidthIncludingTrailingWhitespace) / 2);
            tops[i] = lineTop + (lineHeight - text.Height) / 2;
            clips[i] = new RectangleGeometry(new Rect(2, lineTop, Math.Max(0, body.Width - 4), lineHeight));
        }

        return new PhraseLayout(
            source,
            texts,
            lefts,
            tops,
            clips,
            fontSize,
            lineHeight,
            body.Width,
            body.Height,
            widest * fontSize / 64);
    }
}

internal static class LayoutHelper
{
    public static FormattedText Create(string text, double size, Brush brush, Typeface typeface, double pixelsPerDip) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        typeface,
        size,
        brush,
        pixelsPerDip);

    public static double Measure(string text, double size, Typeface typeface, double pixelsPerDip)
        => Create(text, size, Brushes.Black, typeface, pixelsPerDip).WidthIncludingTrailingWhitespace;
}

/// <summary>乐句里的一行：原文 + 该行的语法高亮区间 + 在缓冲区里的绝对起点。</summary>
public sealed record PhraseLine(string Text, IReadOnlyList<HighlightRun> Runs, int Offset);

/// <summary>乐句里正在发声的一段（行号 + 行内列区间）。</summary>
public readonly record struct ActiveRange(int Line, int Start, int Length);
