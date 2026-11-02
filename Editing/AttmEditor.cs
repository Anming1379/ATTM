using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ATTM.Core.Syntax;

namespace ATTM.Editing;

/// <summary>
/// 编辑器宿主：行号槽 + 可滚动的自绘文本区 + 最右缩略图。
/// 对外暴露文本、光标、撤销、解析结果等接口。
/// </summary>
public sealed class AttmEditor : Grid
{
    private readonly EditorMinimap _minimap = new();
    private readonly EditorTextView _view = new();
    private readonly EditorGutter _gutter = new();
    private readonly ScrollViewer _scroll = new();
    private readonly PhrasePreview _phrase = new();
    private readonly Border _phraseHost = new()
    {
        ClipToBounds = true,
        Width = 0,
        HorizontalAlignment = HorizontalAlignment.Right,
        Visibility = Visibility.Collapsed,
        Child = null
    };

    private EditorPalette _palette = EditorPalette.CreateFallback();
    private ParseResult? _parse;
    private List<HighlightRun>[]? _runs;
    private PhrasePreviewMode _phraseMode = PhrasePreviewMode.Hidden;
    private DispatcherTimer? _widthTimer;
    private long _widthStartedAt;
    private double _widthFrom;
    private double _widthTo;

    private const double PhraseSlideMs = 220;

    public AttmEditor()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EditorMinimap.PanelWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 导航交给右侧缩略图，正文区不再显示滚动条
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.CanContentScroll = false;
        _scroll.Focusable = false;
        _scroll.Content = _view;
        _scroll.ScrollChanged += (_, _) => SyncScroll();

        // 横向移动：Shift + 滚轮（竖向仍然直接滚轮）
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            var modifiers = Keyboard.Modifiers;
            if ((modifiers & ModifierKeys.Shift) == 0 || (modifiers & ModifierKeys.Control) != 0) return;

            _scroll.ScrollToHorizontalOffset(Math.Max(0, _scroll.HorizontalOffset - e.Delta / 3.0));
            e.Handled = true;
        };

        _phraseHost.Child = _phrase;

        Grid.SetColumn(_gutter, 0);
        Grid.SetColumn(_scroll, 1);
        Grid.SetColumn(_minimap, 2);
        Grid.SetColumn(_phraseHost, 3);

        _minimap.ScrollRequested = offset => _scroll.ScrollToVerticalOffset(Math.Max(0, offset));

        Children.Add(_minimap);
        Children.Add(_gutter);
        Children.Add(_scroll);
        Children.Add(_phraseHost);

        _view.BufferChanged += (_, _) =>
        {
            UpdateGutter();
            SyncMinimap();
            BufferChanged?.Invoke(this, EventArgs.Empty);
        };

        _view.CaretChanged += (_, _) =>
        {
            UpdateGutter();
            CaretChanged?.Invoke(this, EventArgs.Empty);
        };

        _view.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);

        _view.LineMetricsChanged += (_, _) =>
        {
            SyncMetrics();
            SyncMinimap();
        };

        SizeChanged += (_, _) =>
        {
            if (_phraseMode != PhrasePreviewMode.Full) return;
            if (_widthTimer is not null) return;

            _phraseHost.Width = PhraseTargetWidth(_phraseMode);
        };

        Loaded += (_, _) =>
        {
            ApplyTheme();
            SyncMetrics();
            SyncScroll();
            _view.FocusEditor();
        };
    }

    public event EventHandler? BufferChanged;
    public event EventHandler? CaretChanged;
    public event EventHandler? HistoryChanged;

    public TextBuffer Buffer => _view.Buffer;

    public string Text
    {
        get => _view.Text;
        set => _view.SetText(value);
    }

    public int CaretOffset => _view.CaretOffset;

    public (int Line, int Column) CaretPosition => _view.CaretPosition;

    public bool CanUndo => _view.CanUndo;

    public bool CanRedo => _view.CanRedo;

    public bool HasSelection => _view.SelectionLength > 0;

    public bool HasPlaybackHighlight => _view.HasPlaybackHighlight;

    public double EditorLineHeight => _view.LineHeight;

    /// <summary>当前纵向滚动偏移。</summary>
    public double VerticalOffset => _scroll.VerticalOffset;

    /// <summary>当前可视区域高度。</summary>
    public double ViewportHeight => _scroll.ViewportHeight;

    /// <summary>滚动到指定纵向偏移。</summary>
    public void ScrollToVerticalOffset(double offset) => _scroll.ScrollToVerticalOffset(Math.Max(0, offset));

    /// <summary>播放跟随时把某一行的位置滚进视野。</summary>
    public void ScrollOffsetIntoView(int offset) => _view.ScrollOffsetIntoView(offset);

    public double EditorFontSize
    {
        get => _view.EditorFontSize;
        set
        {
            _view.SetFontSize(value);
            SyncMetrics();
        }
    }

    public void ApplyParse(ParseResult? result)
    {
        _parse = result;
        _runs = result is null ? null : EditorHighlighter.Build(result).ToArray();
        _view.SetParse(result);

        _gutter.ErrorLines = result is null
            ? new HashSet<int>()
            : result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Line - 1)
                .ToHashSet();

        _gutter.InvalidateVisual();
        SyncMinimap();
    }

    public void SetPlaybackSpans(IReadOnlyList<SourceSpan>? spans)
    {
        _view.SetPlaybackSpans(spans);
        _phrase.SetActiveSpans(spans);
    }

    public void SetCaret(int offset) => _view.SetSelection(offset, offset);

    public void InsertText(string text) => _view.InsertText(text);

    public void Undo() => _view.Undo();

    public void Redo() => _view.Redo();

    public void SelectAll() => _view.SelectAll();

    public void FocusEditor() => _view.FocusEditor();

    public void ApplyTheme()
    {
        var palette = EditorPalette.FromResources(this);
        _palette = palette;
        Background = palette.Background;
        _view.SetPalette(palette);
        _phrase.SetPalette(palette);
        _gutter.Palette = palette;
        _gutter.InvalidateVisual();
        SyncMinimap();
    }

    // ------------------------------------------------------------ 乐句预览

    /// <summary>右侧的乐句预览面板（缩略图的右边）。</summary>
    public PhrasePreview PhrasePreview => _phrase;

    public bool IsPhrasePreviewVisible => _phraseMode != PhrasePreviewMode.Hidden;

    /// <summary>当前是收起 / 半屏侧边栏 / 全窗。</summary>
    public PhrasePreviewMode PhraseMode => _phraseMode;

    /// <summary>侧边栏当前占用的宽度（收起时为 0）。</summary>
    public double PhrasePanelWidth => _phraseHost.ActualWidth;

    /// <summary>半屏 -> 全窗 -> 收起 -> 半屏，循环。返回切换后的状态。</summary>
    public PhrasePreviewMode TogglePhrasePreview()
    {
        var next = _phraseMode switch
        {
            PhrasePreviewMode.Hidden => PhrasePreviewMode.Panel,
            PhrasePreviewMode.Panel => PhrasePreviewMode.Full,
            _ => PhrasePreviewMode.Hidden
        };

        SetPhraseMode(next);
        return next;
    }

    /// <summary>旧接口：true = 半屏侧边栏，false = 收起。</summary>
    public void SetPhrasePreviewVisible(bool visible, bool animate = true)
        => SetPhraseMode(visible ? PhrasePreviewMode.Panel : PhrasePreviewMode.Hidden, animate);

    public void SetPhraseMode(PhrasePreviewMode mode, bool animate = true)
    {
        _phraseMode = mode;

        if (mode == PhrasePreviewMode.Hidden)
        {
            AnimatePhraseWidth(0, animate);
            return;
        }

        var full = mode == PhrasePreviewMode.Full;

        // 全窗：跨掉行号槽 / 正文 / 缩略图四列，从右往左盖；半屏：回到最右边那一列
        Grid.SetColumn(_phraseHost, full ? 0 : 3);
        Grid.SetColumnSpan(_phraseHost, full ? 4 : 1);
        _phrase.SetFullWindow(full);

        _phraseHost.Visibility = Visibility.Visible;
        if (_phrase.Lines.Count == 0) _phrase.InvalidateVisual();

        AnimatePhraseWidth(PhraseTargetWidth(mode), animate);
    }

    /// <summary>这一状态下预览面板应该多宽。</summary>
    private double PhraseTargetWidth(PhrasePreviewMode mode)
    {
        if (mode == PhrasePreviewMode.Panel) return PhrasePreview.PanelWidth;
        if (mode != PhrasePreviewMode.Full) return 0;

        // 全窗：铺满整个编辑区（窗口还没量出来时先退回半屏宽度）
        var available = ActualWidth;
        return available < 8 ? PhrasePreview.PanelWidth : available;
    }

    private void AnimatePhraseWidth(double target, bool animate)
    {
        _widthTimer?.Stop();
        _widthTimer = null;

        if (!animate)
        {
            ApplyPhraseWidth(target);
            return;
        }

        _widthFrom = _phraseHost.ActualWidth;
        _widthTo = target;
        _widthStartedAt = Environment.TickCount64;

        // 自己驱动：不依赖 WPF 动画时钟（离屏/虚拟显示环境下动画时钟可能不推进）
        _widthTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _widthTimer.Tick += (_, _) =>
        {
            var t = Math.Clamp((Environment.TickCount64 - _widthStartedAt) / PhraseSlideMs, 0.0, 1.0);
            var eased = 1 - Math.Pow(1 - t, 3);
            _phraseHost.Width = _widthFrom + (_widthTo - _widthFrom) * eased;

            if (t < 1) return;

            _widthTimer?.Stop();
            _widthTimer = null;

            // 全窗要贴住编辑区现在的宽度（动画途中窗口可能被拖过）
            ApplyPhraseWidth(_phraseMode == PhrasePreviewMode.Full ? PhraseTargetWidth(_phraseMode) : _widthTo);
        };

        _widthTimer.Start();
    }

    private void ApplyPhraseWidth(double target)
    {
        _phraseHost.Width = target;

        if (target > 0) return;

        _phraseHost.Visibility = Visibility.Collapsed;
        _phrase.Hide();
    }

    /// <summary>显示某一小节对应的乐句（并行声部的每一行都算在内）。</summary>
    public void ShowPhrase(int measureIndex)
        => _phrase.Show(measureIndex, BuildPhraseLines(measureIndex), _parse?.MeasureStartLines.Count ?? 0);

    public void HidePhrase() => _phrase.Hide();

    /// <summary>当前乐句对应的小节号（-1 = 没有内容）。</summary>
    public int PhraseMeasureIndex => _phrase.MeasureIndex;

    /// <summary>让当前乐句马上开始渐隐（在小节结束前一点提前退场，保留小节号）。</summary>
    public void FadeOutPhrase() => _phrase.Hide(keepMeasure: true);

    private IReadOnlyList<PhraseLine> BuildPhraseLines(int measureIndex)
    {
        var starts = _parse?.MeasureStartLines;
        if (starts is null || starts.Count == 0 || measureIndex < 0) return Array.Empty<PhraseLine>();

        var index = Math.Clamp(measureIndex, 0, starts.Count - 1);
        var first = starts[index];
        var last = index + 1 < starts.Count ? starts[index + 1] - 1 : _view.Buffer.LineCount - 1;

        var result = new List<PhraseLine>(2);
        for (var line = first; line <= last && line < _view.Buffer.LineCount; line++)
        {
            if (line < 0) continue;
            var text = _view.Buffer.GetLine(line);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.TrimStart().StartsWith('#')) continue;

            // 去掉行首缩进，同时把高亮区间整体左移
            var indent = text.Length - text.TrimStart().Length;
            var trimmed = indent > 0 ? text[indent..] : text;

            var runs = _runs is not null && line < _runs.Length
                ? _runs[line]
                    .Select(run => run with { Start = run.Start - indent })
                    .Where(run => run.Start + run.Length > 0)
                    .Select(run => run.Start < 0 ? run with { Length = run.Length + run.Start, Start = 0 } : run)
                    .Where(run => run.Length > 0)
                    .ToList()
                : new List<HighlightRun>();

            result.Add(new PhraseLine(trimmed, runs, _view.Buffer.GetLineStart(line) + indent));
        }

        return result;
    }

    private void SyncMetrics()
    {
        _gutter.LineHeight = _view.LineHeight;
        _gutter.TopPadding = _view.TopPadding;
        _gutter.Typeface = _view.EditorTypeface;
        _gutter.FontSize = Math.Max(10, _view.EditorFontSize - 3);
        _gutter.LineCount = _view.Buffer.LineCount;
        _gutter.InvalidateVisual();
    }

    private void SyncScroll()
    {
        _gutter.VerticalOffset = _scroll.VerticalOffset;
        _gutter.InvalidateVisual();
        _view.InvalidateVisual();
        SyncMinimap();

        // 让文本区至少填满视口，点击空白处也能定位到最后一行
        var viewportHeight = _scroll.ViewportHeight;
        if (viewportHeight > 0 && Math.Abs(_view.MinHeight - viewportHeight) > 0.5)
            _view.MinHeight = viewportHeight;
    }

    private void UpdateGutter()
    {
        _gutter.ActiveLine = _view.CaretLine;
        _gutter.LineCount = _view.Buffer.LineCount;
        _gutter.InvalidateVisual();
        SyncMinimap();
    }

    private void SyncMinimap()
    {
        _minimap.Update(
            _view.Buffer,
            _palette,
            _view.LineHeight,
            _view.TopPadding,
            _scroll.VerticalOffset,
            _scroll.ViewportHeight,
            _view.CaretLine,
            _gutter.ErrorLines);
    }
}
