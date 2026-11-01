using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ATTM.Core.Syntax;

namespace ATTM.Editing;

/// <summary>
/// 自绘文本编辑器：可见行虚拟化、语法高亮、选区、插入符、错误波浪线、
/// 撤销 200 步、鼠标/键盘/输入法。
/// </summary>
public sealed class EditorTextView : FrameworkElement
{
    public const double TextPaddingLeft = 12;
    public const double TextPaddingTop = 8;
    public const int TabSize = 4;

    private const int HistoryLimit = 200;
    private const double CaretWidth = 2;
    private const int LineCacheLimit = 400;

    private readonly TextBuffer _buffer = new();
    private readonly List<EditCommand> _undo = new();
    private readonly List<EditCommand> _redo = new();
    private readonly Dictionary<int, FormattedText> _lineCache = new();
    private readonly DispatcherTimer _blinkTimer;

    private Typeface _typeface;
    private EditorPalette _palette;
    private List<HighlightRun>[]? _runs;
    private List<Diagnostic>[] _diagnosticsByLine = Array.Empty<List<Diagnostic>>();

    private double _fontSize = 18;
    private double _lineHeight = 24;
    private double _charWidth = 8;
    private double _pixelsPerDip = 1;

    private int _caret;
    private int _anchor;
    private int _preferredColumn = -1;
    private bool _caretVisible = true;
    private bool _isDragging;
    private string? _compositionText;
    private ScrollViewer? _scroll;
    private IReadOnlyList<SourceSpan> _playbackSpans = Array.Empty<SourceSpan>();

    public EditorTextView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        _palette = EditorPalette.CreateFallback();
        _typeface = CreateTypeface();

        _blinkTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(530)
        };
        _blinkTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };

        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnImeStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, OnImeUpdate);

        Loaded += OnLoaded;
        Unloaded += (_, _) => _blinkTimer.Stop();
        SizeChanged += (_, _) => UpdateMetrics();
    }

    // ------------------------------------------------------------ 事件 / 属性

    public event EventHandler? BufferChanged;
    public event EventHandler? CaretChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? LineMetricsChanged;

    public TextBuffer Buffer => _buffer;
    /// <summary>全文。赋值等价于 <see cref="SetText"/>（会清空撤销栈并把光标复位）。</summary>
    public string Text
    {
        get => _buffer.Text;
        set => SetText(value);
    }
    public int CaretOffset => _caret;
    public int SelectionStart => Math.Min(_caret, _anchor);
    public int SelectionLength => Math.Abs(_caret - _anchor);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public double LineHeight => _lineHeight;

    public bool HasPlaybackHighlight => _playbackSpans.Count > 0;
    public double TopPadding => TextPaddingTop;
    public Typeface EditorTypeface => _typeface;
    public double EditorFontSize => _fontSize;

    public (int Line, int Column) CaretPosition => _buffer.GetLineColumn(_caret);

    public int CaretLine => _buffer.GetLineColumn(_caret).Line - 1;

    // ------------------------------------------------------------ 公开操作

    public void SetText(string text)
    {
        _buffer.Reset(TextBuffer.Normalize(text));
        _undo.Clear();
        _redo.Clear();
        _lineCache.Clear();
        _caret = 0;
        _anchor = 0;
        _preferredColumn = -1;

        InvalidateMeasure();
        InvalidateVisual();
        BufferChanged?.Invoke(this, EventArgs.Empty);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        CaretChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPalette(EditorPalette palette)
    {
        _palette = palette;
        _lineCache.Clear();
        InvalidateVisual();
    }

    public void SetFontSize(double size)
    {
        size = Math.Clamp(size, 9, 40);
        if (Math.Abs(size - _fontSize) < 0.01) return;

        _fontSize = size;
        UpdateMetrics();
        ScrollCaretIntoView();
    }

    public void SetParse(ParseResult? result)
    {
        _runs = result is null ? null : EditorHighlighter.Build(result);

        var lineCount = Math.Max(1, result?.LineStarts.Count ?? 1);
        _diagnosticsByLine = new List<Diagnostic>[lineCount];
        for (var i = 0; i < lineCount; i++) _diagnosticsByLine[i] = new List<Diagnostic>(0);

        if (result is not null)
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                var line = Math.Clamp(diagnostic.Line - 1, 0, lineCount - 1);
                _diagnosticsByLine[line].Add(diagnostic);
            }
        }

        _lineCache.Clear();
        InvalidateVisual();
    }

    public void SetPlaybackSpans(IReadOnlyList<SourceSpan>? spans)
    {
        var next = spans is null || spans.Count == 0 ? Array.Empty<SourceSpan>() : spans;

        if (_playbackSpans.Count == next.Count)
        {
            var same = true;
            for (var i = 0; i < next.Count; i++)
            {
                if (_playbackSpans[i] != next[i]) { same = false; break; }
            }

            if (same) return;
        }

        _playbackSpans = next;
        InvalidateVisual();
    }

    public void SelectAll() => SetSelection(0, _buffer.Length);

    public void ClearSelection()
    {
        _anchor = _caret;
        InvalidateVisual();
        CaretChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FocusEditor() => Focus();

    public void Copy()
    {
        var (start, length) = SelectionRange();
        if (length <= 0) return;

        try
        {
            Clipboard.SetText(_buffer.GetText(start, length));
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    public void Cut()
    {
        if (SelectionLength <= 0) return;
        Copy();
        DeleteSelection();
    }

    public void Paste()
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(text)) return;
        InsertText(text);
    }

    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        text = TextBuffer.Normalize(text);
        var (start, length) = SelectionRange();

        ApplyEdit(start, length, text);
        _anchor = _caret = start + text.Length;
        _preferredColumn = -1;
        AfterEdit();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        ApplyEdit(command.Offset, command.Inserted.Length, command.Removed, record: false);
        _redo.Add(command);

        _anchor = command.Offset;
        _caret = command.Offset + command.Removed.Length;
        _preferredColumn = -1;

        AfterEdit();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        ApplyEdit(command.Offset, command.Removed.Length, command.Inserted, record: false);
        _undo.Add(command);

        _anchor = command.Offset;
        _caret = command.Offset + command.Inserted.Length;
        _preferredColumn = -1;

        AfterEdit();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSelection(int anchor, int caret)
    {
        _anchor = Math.Clamp(anchor, 0, _buffer.Length);
        _caret = Math.Clamp(caret, 0, _buffer.Length);
        _preferredColumn = -1;
        AfterCaretMoved();
    }

    public void SelectLine(int line)
    {
        line = Math.Clamp(line, 0, _buffer.LineCount - 1);
        var start = _buffer.GetLineStart(line);
        var end = start + _buffer.GetLineLength(line);
        SetSelection(start, end);
    }

    // ------------------------------------------------------------ 编辑

    private void ApplyEdit(int offset, int removeLength, string insertText, bool record = true)
    {
        var removed = _buffer.GetText(offset, removeLength);
        if (removed.Length == 0 && insertText.Length == 0) return;

        _buffer.Replace(offset, removeLength, insertText);

        if (record) PushUndo(new EditCommand(offset, insertText, removed));

        _lineCache.Clear();
        InvalidateMeasure();
        InvalidateVisual();
        BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PushUndo(EditCommand command)
    {
        _redo.Clear();

        if (_undo.Count > 0)
        {
            var last = _undo[^1];

            // 连续输入合并成一步
            if (command.Removed.Length == 0
                && last.Removed.Length == 0
                && command.Offset == last.Offset + last.Inserted.Length
                && !command.Inserted.Contains('\n')
                && !last.Inserted.Contains('\n')
                && last.Inserted.Length + command.Inserted.Length <= 120)
            {
                _undo[^1] = last with { Inserted = last.Inserted + command.Inserted };
                HistoryChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // 连续退格合并成一步
            if (command.Inserted.Length == 0
                && last.Inserted.Length == 0
                && last.Offset == command.Offset + command.Removed.Length
                && !command.Removed.Contains('\n')
                && !last.Removed.Contains('\n')
                && last.Removed.Length + command.Removed.Length <= 120)
            {
                _undo[^1] = last with { Offset = command.Offset, Removed = command.Removed + last.Removed };
                HistoryChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        _undo.Add(command);
        if (_undo.Count > HistoryLimit) _undo.RemoveAt(0);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteSelection()
    {
        var (start, length) = SelectionRange();
        if (length <= 0) return;

        ApplyEdit(start, length, "");
        _anchor = _caret = start;
        _preferredColumn = -1;
        AfterEdit();
    }

    private void Backspace()
    {
        if (SelectionLength > 0)
        {
            DeleteSelection();
            return;
        }

        if (_caret <= 0) return;

        var count = 1;
        if (_caret >= 2
            && char.IsLowSurrogate(_buffer.Text[_caret - 1])
            && char.IsHighSurrogate(_buffer.Text[_caret - 2]))
        {
            count = 2;
        }

        var offset = _caret - count;
        ApplyEdit(offset, count, "");
        _anchor = _caret = offset;
        _preferredColumn = -1;
        AfterEdit();
    }

    private void DeleteForward()
    {
        if (SelectionLength > 0)
        {
            DeleteSelection();
            return;
        }

        if (_caret >= _buffer.Length) return;

        var count = 1;
        if (_caret + 2 <= _buffer.Length
            && char.IsHighSurrogate(_buffer.Text[_caret])
            && char.IsLowSurrogate(_buffer.Text[_caret + 1]))
        {
            count = 2;
        }

        ApplyEdit(_caret, count, "");
        _anchor = _caret;
        _preferredColumn = -1;
        AfterEdit();
    }

    // ------------------------------------------------------------ 光标移动

    private (int Start, int Length) SelectionRange()
    {
        var start = Math.Min(_caret, _anchor);
        var end = Math.Max(_caret, _anchor);
        return (start, end - start);
    }

    private void AfterCaretMoved()
    {
        _caretVisible = true;
        if (IsKeyboardFocusWithin)
        {
            _blinkTimer.Stop();
            _blinkTimer.Start();
        }

        ScrollCaretIntoView();
        InvalidateVisual();
        CaretChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AfterEdit()
    {
        _caretVisible = true;
        if (IsKeyboardFocusWithin)
        {
            _blinkTimer.Stop();
            _blinkTimer.Start();
        }

        ScrollCaretIntoView();
        InvalidateVisual();
        CaretChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveTo(int offset, bool extend)
    {
        offset = Math.Clamp(offset, 0, _buffer.Length);

        if (extend)
        {
            _caret = offset;
        }
        else
        {
            _anchor = offset;
            _caret = offset;
            _preferredColumn = -1;
        }

        AfterCaretMoved();
    }

    private void MoveHorizontal(int delta, bool extend)
    {
        MoveTo(_caret + delta, extend);
        _preferredColumn = -1;
    }

    private void MoveVertical(int delta, bool extend)
    {
        var (line, column) = _buffer.GetLineColumn(_caret);
        line--;
        column--;

        if (_preferredColumn < 0) _preferredColumn = column;

        var targetLine = Math.Clamp(line + delta, 0, _buffer.LineCount - 1);
        var targetColumn = Math.Min(_preferredColumn, _buffer.GetLineLength(targetLine));

        MoveTo(_buffer.GetOffset(targetLine, targetColumn), extend);
    }

    private void MoveLineEdge(bool toEnd, bool extend)
    {
        var (line, _) = _buffer.GetLineColumn(_caret);
        line--;

        var offset = toEnd
            ? _buffer.GetLineStart(line) + _buffer.GetLineLength(line)
            : _buffer.GetLineStart(line);

        MoveTo(offset, extend);
        _preferredColumn = -1;
    }

    private void MovePage(int direction, bool extend)
    {
        var rows = Math.Max(1, (int)((_scroll?.ViewportHeight ?? ActualHeight) / _lineHeight) - 1);
        MoveVertical(direction * rows, extend);
    }

    // ------------------------------------------------------------ 输入

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (control)
        {
            switch (e.Key)
            {
                case Key.Z:
                    Undo();
                    e.Handled = true;
                    return;
                case Key.Y:
                case Key.R:
                    Redo();
                    e.Handled = true;
                    return;
                case Key.A:
                    SelectAll();
                    e.Handled = true;
                    return;
                case Key.C:
                    Copy();
                    e.Handled = true;
                    return;
                case Key.X:
                    Cut();
                    e.Handled = true;
                    return;
                case Key.V:
                    Paste();
                    e.Handled = true;
                    return;
                case Key.Home:
                    MoveTo(0, shift);
                    e.Handled = true;
                    return;
                case Key.End:
                    MoveTo(_buffer.Length, shift);
                    e.Handled = true;
                    return;
                default:
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Back:
                Backspace();
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteForward();
                e.Handled = true;
                break;
            case Key.Enter:
                InsertText("\n");
                e.Handled = true;
                break;
            case Key.Tab:
                InsertText(new string(' ', TabSize));
                e.Handled = true;
                break;
            case Key.Left:
                MoveHorizontal(-1, shift);
                e.Handled = true;
                break;
            case Key.Right:
                MoveHorizontal(1, shift);
                e.Handled = true;
                break;
            case Key.Up:
                MoveVertical(-1, shift);
                e.Handled = true;
                break;
            case Key.Down:
                MoveVertical(1, shift);
                e.Handled = true;
                break;
            case Key.Home:
                MoveLineEdge(false, shift);
                e.Handled = true;
                break;
            case Key.End:
                MoveLineEdge(true, shift);
                e.Handled = true;
                break;
            case Key.PageUp:
                MovePage(-1, shift);
                e.Handled = true;
                break;
            case Key.PageDown:
                MovePage(1, shift);
                e.Handled = true;
                break;
            case Key.Escape:
                ClearSelection();
                e.Handled = true;
                break;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);

        _compositionText = null;

        var text = e.Text;
        if (string.IsNullOrEmpty(text)) return;
        if (text is "\r" or "\n" or "\u001b") return;

        InsertText(text);
        e.Handled = true;
    }

    private void OnImeStart(object sender, TextCompositionEventArgs e)
    {
        _compositionText = e.TextComposition.Text;
        InvalidateVisual();
    }

    private void OnImeUpdate(object sender, TextCompositionEventArgs e)
    {
        _compositionText = e.TextComposition.Text;
        InvalidateVisual();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _caretVisible = true;
        _blinkTimer.Stop();
        _blinkTimer.Start();
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _blinkTimer.Stop();
        _caretVisible = false;
        _compositionText = null;
        InvalidateVisual();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        SetFontSize(_fontSize + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    // ------------------------------------------------------------ 鼠标

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        Focus();
        var hit = HitTestPosition(e.GetPosition(this));
        var offset = _buffer.GetOffset(hit.Line, hit.Column);

        if (e.ClickCount >= 3)
        {
            SelectLine(hit.Line);
        }
        else if (e.ClickCount == 2)
        {
            SelectWord(offset);
        }
        else
        {
            SetSelection(offset, offset);
            _isDragging = true;
            CaptureMouse();
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var hit = HitTestPosition(e.GetPosition(this));
        _caret = _buffer.GetOffset(hit.Line, hit.Column);
        _preferredColumn = -1;
        AfterCaretMoved();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isDragging) return;

        _isDragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SelectWord(int offset)
    {
        var text = _buffer.Text;
        var start = offset;
        var end = offset;

        while (start > 0 && IsWordChar(text[start - 1])) start--;
        while (end < text.Length && IsWordChar(text[end])) end++;

        SetSelection(start, end);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '#' or '+' or '-' or '_';

    // ------------------------------------------------------------ 布局 / 绘制

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scroll = FindScrollViewer(this);
        UpdateMetrics();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null && current is not ScrollViewer)
            current = VisualTreeHelper.GetParent(current);

        return current as ScrollViewer;
    }

    private void UpdateMetrics()
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var sample = new FormattedText("M", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brushes.Black, _pixelsPerDip);

        _charWidth = sample.WidthIncludingTrailingWhitespace;
        _lineHeight = Math.Round(_fontSize * 1.38);

        _lineCache.Clear();
        InvalidateMeasure();
        InvalidateVisual();
        LineMetricsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = TextPaddingLeft * 2 + _buffer.MaxLineLength * _charWidth + 12;
        var height = TextPaddingTop * 2 + _buffer.LineCount * _lineHeight;
        return new Size(Math.Max(width, 10), Math.Max(height, 10));
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_palette.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_buffer.LineCount <= 0) return;

        var (first, last) = VisibleLines();
        if (last < first) return;

        DrawCurrentLine(dc, first, last);
        DrawSelection(dc, first, last);
        DrawPlayback(dc, first, last);

        for (var line = first; line <= last; line++)
        {
            var formatted = GetLineText(line);
            dc.DrawText(formatted, new Point(TextPaddingLeft, LineTop(line)));
        }

        DrawDiagnostics(dc, first, last);
        DrawCaret(dc);
    }

    private (int First, int Last) VisibleLines()
    {
        var verticalOffset = _scroll?.VerticalOffset ?? 0;
        var viewportHeight = _scroll?.ViewportHeight ?? ActualHeight;

        var first = Math.Max(0, (int)Math.Floor((verticalOffset - TextPaddingTop) / _lineHeight));
        var last = Math.Min(_buffer.LineCount - 1,
            (int)Math.Floor((verticalOffset + viewportHeight - TextPaddingTop) / _lineHeight) + 1);

        return (first, last);
    }

    private double LineTop(int line) => TextPaddingTop + line * _lineHeight;

    private FormattedText GetLineText(int line)
    {
        if (_lineCache.TryGetValue(line, out var cached)) return cached;

        var raw = _buffer.GetLine(line);
        var text = raw.Length == 0 ? " " : raw;

        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, _palette.Foreground, _pixelsPerDip);

        if (_runs is not null && line < _runs.Length)
        {
            foreach (var run in _runs[line])
            {
                var start = Math.Clamp(run.Start, 0, raw.Length);
                var length = Math.Clamp(run.Length, 0, raw.Length - start);
                if (length > 0)
                    formatted.SetForegroundBrush(_palette.BrushFor(run.Class), start, length);
            }
        }

        if (_lineCache.Count > LineCacheLimit) _lineCache.Clear();
        _lineCache[line] = formatted;
        return formatted;
    }

    private void DrawCurrentLine(DrawingContext dc, int first, int last)
    {
        var active = CaretLine;
        if (active < first || active > last) return;

        dc.DrawRectangle(_palette.CurrentLine, null,
            new Rect(0, LineTop(active), Math.Max(ActualWidth, 1), _lineHeight));
    }

    private void DrawSelection(DrawingContext dc, int first, int last)
    {
        var (selectionStart, selectionLength) = SelectionRange();
        if (selectionLength <= 0) return;

        var selectionEnd = selectionStart + selectionLength;

        for (var line = first; line <= last; line++)
        {
            var lineStart = _buffer.GetLineStart(line);
            var lineLength = _buffer.GetLineLength(line);
            var lineEnd = lineStart + lineLength;

            if (selectionEnd <= lineStart) break;
            if (selectionStart > lineEnd) continue;

            var fromColumn = Math.Clamp(selectionStart - lineStart, 0, lineLength);
            var toColumn = Math.Clamp(selectionEnd - lineStart, 0, lineLength);

            var x1 = TextPaddingLeft + MeasureColumn(line, fromColumn);
            var x2 = TextPaddingLeft + MeasureColumn(line, toColumn);

            if (x2 - x1 < 1)
            {
                if (selectionStart <= lineStart && selectionEnd > lineEnd) x2 = x1 + _charWidth * 0.5;
                else continue;
            }

            dc.DrawRectangle(_palette.Selection, null,
                new Rect(x1, LineTop(line), Math.Max(1.5, x2 - x1), _lineHeight));
        }
    }

    private void DrawPlayback(DrawingContext dc, int first, int last)
    {
        if (_playbackSpans.Count == 0) return;

        foreach (var span in _playbackSpans)
        {
            if (span.Length <= 0) continue;

            var start = _buffer.GetLineColumn(span.Start);
            var end = _buffer.GetLineColumn(span.Start + span.Length);
            var startLine = start.Line - 1;
            var endLine = end.Line - 1;

            for (var line = Math.Max(first, startLine); line <= Math.Min(last, endLine); line++)
            {
                var lineStart = _buffer.GetLineStart(line);
                var lineLength = _buffer.GetLineLength(line);

                var fromColumn = line == startLine ? Math.Clamp(start.Column - 1, 0, lineLength) : 0;
                var toColumn = line == endLine ? Math.Clamp(end.Column - 1, 0, lineLength) : lineLength;

                var x1 = TextPaddingLeft + MeasureColumn(line, fromColumn);
                var x2 = TextPaddingLeft + MeasureColumn(line, toColumn);
                if (x2 - x1 < 2) x2 = x1 + _charWidth;

                dc.DrawRectangle(_palette.PlayHighlight, null,
                    new Rect(x1 - 1, LineTop(line), Math.Max(3, x2 - x1 + 2), _lineHeight));
            }
        }
    }

    private void DrawDiagnostics(DrawingContext dc, int first, int last)
    {
        if (_diagnosticsByLine.Length == 0) return;

        var pen = new Pen(_palette.Error, 1.2);
        pen.Freeze();

        for (var line = first; line <= last && line < _diagnosticsByLine.Length; line++)
        {
            foreach (var diagnostic in _diagnosticsByLine[line])
            {
                var lineStart = _buffer.GetLineStart(line);
                var lineLength = _buffer.GetLineLength(line);

                var startColumn = Math.Clamp(diagnostic.Span.Start - lineStart, 0, lineLength);
                var length = Math.Max(1, Math.Min(diagnostic.Span.Length, lineLength - startColumn));

                var x1 = TextPaddingLeft + MeasureColumn(line, startColumn);
                var x2 = TextPaddingLeft + MeasureColumn(line, startColumn + length);
                var y = LineTop(line) + _lineHeight - 3;

                DrawWavy(dc, pen, x1, Math.Max(x2, x1 + 8), y);
            }
        }
    }

    private static void DrawWavy(DrawingContext dc, Pen pen, double x1, double x2, double y)
    {
        const double step = 3;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(x1, y), false, false);
            var up = true;
            for (var x = x1; x < x2; x += step)
            {
                var next = Math.Min(x + step, x2);
                context.LineTo(new Point(next, up ? y - 1.8 : y + 1.8), true, false);
                up = !up;
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawCaret(DrawingContext dc)
    {
        if (!IsKeyboardFocusWithin) return;

        var (line, column) = _buffer.GetLineColumn(_caret);
        line--;
        column--;

        var top = LineTop(line);

        if (!string.IsNullOrEmpty(_compositionText))
        {
            var formatted = new FormattedText(_compositionText, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, _typeface, _fontSize, _palette.Foreground, _pixelsPerDip);

            var x = TextPaddingLeft + MeasureColumn(line, column);
            dc.DrawText(formatted, new Point(x, top));

            var pen = new Pen(_palette.Caret, 1);
            dc.DrawLine(pen,
                new Point(x, top + _lineHeight - 2),
                new Point(x + formatted.WidthIncludingTrailingWhitespace, top + _lineHeight - 2));
            return;
        }

        if (!_caretVisible) return;

        var caretX = TextPaddingLeft + MeasureColumn(line, column);
        dc.DrawRectangle(_palette.Caret, null,
            new Rect(caretX, top + 2, CaretWidth, _lineHeight - 4));
    }

    private double MeasureColumn(int line, int column)
    {
        var text = _buffer.GetLine(line);
        column = Math.Clamp(column, 0, text.Length);
        if (column <= 0) return 0;

        var formatted = new FormattedText(text[..column], CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.Black, _pixelsPerDip);

        return formatted.WidthIncludingTrailingWhitespace;
    }

    // ------------------------------------------------------------ 命中 / 滚动

    private (int Line, int Column) HitTestPosition(Point point)
    {
        var line = (int)Math.Floor((point.Y - TextPaddingTop) / _lineHeight);
        line = Math.Clamp(line, 0, _buffer.LineCount - 1);

        var text = _buffer.GetLine(line);
        if (text.Length == 0) return (line, 0);

        var column = HitTestColumn(line, point.X - TextPaddingLeft);
        return (line, Math.Clamp(column, 0, text.Length));
    }

    private int HitTestColumn(int line, double localX)
    {
        var text = _buffer.GetLine(line);
        if (text.Length == 0 || localX <= 0) return 0;

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (MeasureColumn(line, mid) <= localX) low = mid;
            else high = mid - 1;
        }

        if (low < text.Length)
        {
            var before = MeasureColumn(line, low);
            var after = MeasureColumn(line, low + 1);
            if (localX - before > (after - before) / 2) low++;
        }

        return low;
    }

    /// <summary>播放跟随：把指定偏移所在的行滚进视野（不动插入符）。</summary>
    public void ScrollOffsetIntoView(int offset)
    {
        if (_scroll is null) return;

        var (line, _) = _buffer.GetLineColumn(offset);
        line--;

        var top = LineTop(line);
        var bottom = top + _lineHeight;
        var verticalOffset = _scroll.VerticalOffset;
        var viewportHeight = _scroll.ViewportHeight;

        if (top < verticalOffset)
            _scroll.ScrollToVerticalOffset(Math.Max(0, top - TextPaddingTop));
        else if (bottom > verticalOffset + viewportHeight)
            _scroll.ScrollToVerticalOffset(bottom - viewportHeight + TextPaddingTop);
    }

    private void ScrollCaretIntoView()
    {
        if (_scroll is null) return;

        var (line, column) = _buffer.GetLineColumn(_caret);
        line--;
        column--;

        var top = LineTop(line);
        var bottom = top + _lineHeight;
        var verticalOffset = _scroll.VerticalOffset;
        var viewportHeight = _scroll.ViewportHeight;

        if (top < verticalOffset)
            _scroll.ScrollToVerticalOffset(Math.Max(0, top - TextPaddingTop));
        else if (bottom > verticalOffset + viewportHeight)
            _scroll.ScrollToVerticalOffset(bottom - viewportHeight + TextPaddingTop);

        var x = TextPaddingLeft + MeasureColumn(line, column);
        var horizontalOffset = _scroll.HorizontalOffset;
        var viewportWidth = _scroll.ViewportWidth;
        const double margin = 48;

        if (x - margin < horizontalOffset)
            _scroll.ScrollToHorizontalOffset(Math.Max(0, x - margin));
        else if (x + margin > horizontalOffset + viewportWidth)
            _scroll.ScrollToHorizontalOffset(x + margin - viewportWidth);
    }

    private static Typeface CreateTypeface()
    {
        var family = Application.Current?.TryFindResource("Font.Editor") as FontFamily
                     ?? new FontFamily("Consolas");

        return new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    private readonly record struct EditCommand(int Offset, string Inserted, string Removed);
}
