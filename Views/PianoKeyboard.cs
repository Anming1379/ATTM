using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ATTM.Views;

/// <summary>
/// 自绘钢琴键盘：三个八度，鼠标点按 / 按住拖动滑奏，按下有下沉 + 主题色点亮动画。
/// 只负责"画"和"报告按下/松开"，发声交给宿主窗口。
/// </summary>
public sealed class PianoKeyboard : FrameworkElement
{
    public const int OctavesVisible = 3;

    private static readonly int[] WhiteSemitones = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly int[] BlackSemitones = { 1, 3, 6, 8, 10 };
    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>按下动画时长（毫秒）。</summary>
    private const double PressInMs = 55;

    /// <summary>松开动画时长（毫秒）。</summary>
    private const double PressOutMs = 160;

    private const double SinkPixels = 3.5;

    private readonly HashSet<int> _down = new();
    private readonly Dictionary<int, double> _level = new();
    private readonly Typeface _labelTypeface;

    private readonly DispatcherTimer _animator;
    private long _lastTick;

    private Color _whiteColor = Color.FromRgb(0xDA, 0xDA, 0xE4);
    private Color _blackColor = Color.FromRgb(0x16, 0x16, 0x1B);
    private Color _whiteTextColor = Color.FromRgb(0x55, 0x55, 0x6A);
    private Color _blackTextColor = Color.FromRgb(0x8E, 0x8E, 0xA0);
    private Color _accentColor = Color.FromRgb(0xFF, 0x4E, 0x6A);
    private Color _borderColor = Color.FromRgb(0x2E, 0x2E, 0x36);

    /// <summary>换音域的滑动时长（毫秒）。</summary>
    private const double SlideMs = 170;

    private int _baseOctave = 4;
    private int? _mouseNote;
    private bool _pedalOn;

    private int _slideFrom;
    private int _slideDirection = 1;
    private double _slideT = 1;

    public PianoKeyboard()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = false;
        Cursor = Cursors.Hand;

        var uiFamily = Application.Current?.TryFindResource("Font.UI") as FontFamily ?? new FontFamily("Microsoft YaHei UI");
        _labelTypeface = new Typeface(uiFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        _animator = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animator.Tick += (_, _) => StepAnimation();

        ApplyTheme();
    }

    /// <summary>某键被按下（参数是 MIDI 音高）。</summary>
    public event Action<int>? NoteDown;

    /// <summary>某键被松开。</summary>
    public event Action<int>? NoteUp;

    /// <summary>最低那个八度的编号（C4 = 4）。</summary>
    public int BaseOctave => _baseOctave;

    /// <summary>最低那个八度的 C 的 MIDI 音高（C4 = 60）。</summary>
    public int BaseOctaveNote => (_baseOctave + 1) * 12;

    /// <summary>延音踏板是否踩下（键盘底部会亮一条主题色）。</summary>
    public bool PedalOn
    {
        get => _pedalOn;
        set
        {
            if (_pedalOn == value) return;

            _pedalOn = value;
            InvalidateVisual();
        }
    }

    /// <summary>换音域：旧键盘往一边滑走、新键盘从另一边滑进来。</summary>
    public void SetBaseOctave(int octave)
    {
        var clamped = Math.Clamp(octave, 0, 7);
        if (clamped == _baseOctave) return;

        // 上一段滑动还没结束就从当前画面接着滑，不会跳帧
        _slideFrom = _baseOctave;
        _slideDirection = clamped > _baseOctave ? 1 : -1;
        _baseOctave = clamped;
        _slideT = 0;

        StartAnimator();
    }

    /// <summary>换音域动画的进度：0 = 刚开始，1 = 结束（测试用）。</summary>
    public double SlideProgress => _slideT;

    /// <summary>正在滑动换音域（测试用）。</summary>
    public bool IsSliding => _slideT < 1;

    public void ApplyTheme()
    {
        _whiteColor = Get("Brush.Piano.White", "#FFDADAE4");
        _blackColor = Get("Brush.Piano.Black", "#FF16161B");
        _whiteTextColor = Get("Brush.Piano.WhiteText", "#FF55556A");
        _blackTextColor = Get("Brush.Piano.BlackText", "#FF8E8EA0");
        _accentColor = Get("Brush.Accent", "#FFFF4E6A");
        _borderColor = Get("Brush.Border", "#FF2E2E36");
        InvalidateVisual();
    }

    private static Color Get(string key, string fallback)
    {
        var brush = Application.Current?.TryFindResource(key) as SolidColorBrush;
        return brush?.Color ?? (Color)ColorConverter.ConvertFromString(fallback);
    }

    // ------------------------------------------------------------ 按下状态与动画

    public void SetPressed(int note, bool pressed)
    {
        if (pressed)
        {
            if (!_down.Add(note)) return;
        }
        else if (!_down.Remove(note)) return;

        StartAnimator();
    }

    public void ClearPressed()
    {
        if (_down.Count == 0 && _level.Count == 0) return;

        _down.Clear();
        StartAnimator();
    }

    public bool IsPressed(int note) => _down.Contains(note);

    public int PressedCount => _down.Count;

    /// <summary>某个键的按下进度：0 = 完全弹起，1 = 完全按下（测试用）。</summary>
    public double PressLevel(int note) => _level.TryGetValue(note, out var level) ? level : 0;

    /// <summary>是否还有动画在跑（测试用）。</summary>
    public bool IsAnimating => _animator.IsEnabled;

    private void StartAnimator()
    {
        _lastTick = Environment.TickCount64;

        if (!_animator.IsEnabled) _animator.Start();
        InvalidateVisual();
    }

    private void StepAnimation()
    {
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - _lastTick, 0, 60);
        _lastTick = now;

        var upStep = elapsed / PressInMs;
        var downStep = elapsed / PressOutMs;
        var busy = false;

        if (_slideT < 1)
        {
            _slideT = Math.Min(1, _slideT + elapsed / SlideMs);
            if (_slideT < 1) busy = true;
        }

        foreach (var note in _down.ToArray())
        {
            var level = _level.TryGetValue(note, out var current) ? current : 0;
            level = Math.Min(1, level + upStep);
            _level[note] = level;
            if (level < 1) busy = true;
        }

        foreach (var note in _level.Keys.ToArray())
        {
            if (_down.Contains(note)) continue;

            var level = _level[note] - downStep;
            if (level <= 0)
            {
                _level.Remove(note);
                continue;
            }

            _level[note] = level;
            busy = true;
        }

        InvalidateVisual();

        if (!busy) _animator.Stop();
    }

    // ------------------------------------------------------------ 布局与绘制

    public int NoteAt(double x, double y)
    {
        if (ActualWidth <= 1 || ActualHeight <= 1) return -1;

        var whiteWidth = ActualWidth / (WhiteSemitones.Length * OctavesVisible);
        var blackWidth = whiteWidth * 0.62;
        var blackHeight = ActualHeight * 0.62;

        // 黑键压在白键上面，先判黑键
        if (y <= blackHeight)
        {
            for (var octave = 0; octave < OctavesVisible; octave++)
            {
                foreach (var semitone in BlackSemitones)
                {
                    var whiteIndex = WhiteIndexOf(semitone);
                    var center = (octave * 7 + whiteIndex + 1) * whiteWidth;
                    var left = center - blackWidth / 2;

                    if (x < left || x > left + blackWidth) continue;
                    return NoteFor(octave, semitone);
                }
            }
        }

        var white = (int)(x / whiteWidth);
        if (white < 0) white = 0;
        var maxWhite = WhiteSemitones.Length * OctavesVisible - 1;
        if (white > maxWhite) white = maxWhite;

        return NoteFor(white / 7, WhiteSemitones[white % 7]);
    }

    /// <summary>键盘上某个 MIDI 音高对应的矩形（未做按下位移）。</summary>
    public Rect BoundsOf(int note)
    {
        var offset = note - (_baseOctave + 1) * 12;
        if (offset < 0 || offset >= OctavesVisible * 12) return Rect.Empty;

        var octave = offset / 12;
        var semitone = offset % 12;
        var whiteWidth = ActualWidth / (WhiteSemitones.Length * OctavesVisible);
        var blackWidth = whiteWidth * 0.62;
        var blackHeight = ActualHeight * 0.62;

        if (BlackSemitones.Contains(semitone))
        {
            var whiteIndex = WhiteIndexOf(semitone);
            var center = (octave * 7 + whiteIndex + 1) * whiteWidth;
            return new Rect(center - blackWidth / 2, 0, blackWidth, blackHeight);
        }

        var white = octave * 7 + Array.IndexOf(WhiteSemitones, semitone);
        return new Rect(white * whiteWidth, 0, whiteWidth, ActualHeight);
    }

    private static int WhiteIndexOf(int semitone) => semitone switch
    {
        1 => 0,
        3 => 1,
        6 => 3,
        8 => 4,
        10 => 5,
        _ => 0
    };

    private int NoteFor(int octave, int semitone) => (_baseOctave + octave + 1) * 12 + semitone;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_slideT < 1)
        {
            // 换音域：旧键盘往一边滑出去，新键盘从另一边滑进来，接缝处刚好对齐
            var eased = EaseOut(_slideT);
            var shift = width * eased;
            var outOffset = -_slideDirection * shift;
            var inOffset = _slideDirection * (width - shift);

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));

            dc.PushOpacity(1 - 0.55 * eased);
            DrawOctave(dc, _slideFrom, outOffset, width, height, dpi);
            dc.Pop();

            dc.PushOpacity(0.45 + 0.55 * eased);
            DrawOctave(dc, _baseOctave, inOffset, width, height, dpi);
            dc.Pop();

            dc.Pop();
        }
        else
        {
            DrawOctave(dc, _baseOctave, 0, width, height, dpi);
        }

        // 踏板踩下：键盘底部亮一条主题色，状态一眼可见
        if (_pedalOn)
        {
            var accentBrush = new SolidColorBrush(_accentColor);
            accentBrush.Freeze();
            dc.DrawRectangle(accentBrush, null, new Rect(0, height - 3, width, 3));
        }
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private void DrawOctave(DrawingContext dc, int baseOctave, double offsetX, double width, double height, double dpi)
    {
        var whiteWidth = width / (WhiteSemitones.Length * OctavesVisible);
        var blackWidth = whiteWidth * 0.62;
        var blackHeight = height * 0.62;

        var borderPen = new Pen(new SolidColorBrush(_borderColor), 1);
        borderPen.Freeze();

        // 白键
        for (var octave = 0; octave < OctavesVisible; octave++)
        {
            for (var i = 0; i < WhiteSemitones.Length; i++)
            {
                var semitone = WhiteSemitones[i];
                var note = (baseOctave + octave + 1) * 12 + semitone;
                var level = PressLevel(note);
                var rect = new Rect((octave * 7 + i) * whiteWidth + offsetX, 0, whiteWidth, height);

                if (level > 0) rect = Sink(rect, level);

                var fill = level > 0 ? Blend(_whiteColor, _accentColor, level) : _whiteColor;
                var fillBrush = new SolidColorBrush(fill);
                fillBrush.Freeze();

                dc.DrawRectangle(fillBrush, null, rect);
                dc.DrawRectangle(null, borderPen, rect);

                // 按下去的那一瞬，靠近底部亮一小截，像琴键被压亮
                if (level > 0.02)
                {
                    var glowBrush = new SolidColorBrush(Darken(_accentColor, 0.18)) { Opacity = level };
                    glowBrush.Freeze();
                    dc.DrawRectangle(glowBrush, null,
                        new Rect(rect.Left + 1, rect.Bottom - 7, Math.Max(0, rect.Width - 2), 5));
                }

                if (semitone == 0) DrawNoteName(dc, rect, baseOctave + octave, level, dpi);
            }
        }

        // 黑键（后画，压在白键上）
        for (var octave = 0; octave < OctavesVisible; octave++)
        {
            foreach (var semitone in BlackSemitones)
            {
                var note = (baseOctave + octave + 1) * 12 + semitone;
                var level = PressLevel(note);
                var whiteIndex = WhiteIndexOf(semitone);
                var center = (octave * 7 + whiteIndex + 1) * whiteWidth + offsetX;
                var rect = new Rect(center - blackWidth / 2, 0, blackWidth, blackHeight);

                if (level > 0) rect = Sink(rect, level);

                var fill = level > 0 ? Blend(_blackColor, _accentColor, level) : _blackColor;
                var fillBrush = new SolidColorBrush(fill);
                fillBrush.Freeze();

                dc.DrawRectangle(fillBrush, null, rect);

                if (level > 0.02)
                {
                    var glowBrush = new SolidColorBrush(Darken(_accentColor, 0.18)) { Opacity = level };
                    glowBrush.Freeze();
                    dc.DrawRectangle(glowBrush, null,
                        new Rect(rect.Left + 1, rect.Bottom - 5, Math.Max(0, rect.Width - 2), 3));
                }
            }
        }
    }

    private static Rect Sink(Rect rect, double level)
    {
        var offset = SinkPixels * level;
        return new Rect(rect.Left, rect.Top + offset, rect.Width, Math.Max(0, rect.Height - offset));
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount));
    }

    private static Color Darken(Color color, double amount)
    {
        var factor = 1 - Math.Clamp(amount, 0, 1);
        return Color.FromRgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));
    }

    private void DrawNoteName(DrawingContext dc, Rect rect, int octave, double level, double dpi)
    {
        var text = new FormattedText(
            "C" + octave,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _labelTypeface,
            Math.Clamp(rect.Width * 0.26, 9, 13),
            new SolidColorBrush(Blend(_whiteTextColor, Colors.White, level)),
            dpi);

        dc.DrawText(text, new Point(
            rect.Left + (rect.Width - text.Width) / 2,
            rect.Top + 7));
    }

    // ------------------------------------------------------------ 鼠标

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var point = e.GetPosition(this);
        var note = NoteAt(point.X, point.Y);
        if (note < 0) return;

        CaptureMouse();
        _mouseNote = note;
        NoteDown?.Invoke(note);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_mouseNote is null || e.LeftButton != MouseButtonState.Pressed) return;

        var point = e.GetPosition(this);
        var note = NoteAt(point.X, point.Y);
        if (note < 0 || note == _mouseNote) return;

        NoteUp?.Invoke(_mouseNote.Value);
        _mouseNote = note;
        NoteDown?.Invoke(note);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_mouseNote is { } note) NoteUp?.Invoke(note);

        _mouseNote = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }
}
