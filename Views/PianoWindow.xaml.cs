using System.Windows;
using System.Windows.Input;
using ATTM.Core.Playback;
using ATTM.Services;

namespace ATTM.Views;

/// <summary>
/// 钢琴键盘窗口：鼠标点键、电脑键盘弹奏，声音直接发给当前 MIDI 输出设备。
/// 和播放共用同一个输出端口（通道 0），所以播放时也能跟着弹。
/// </summary>
public partial class PianoWindow : Window
{
    private const int Channel = 0;
    private const int Velocity = 100;
    private const int SustainController = 64;

    private readonly Func<IMidiOutput> _output;
    private readonly AppConfig _config;
    private readonly HashSet<int> _down = new();

    private bool _shiftDown;
    private bool _spaceDown;
    private bool _pedalLatched;
    private bool _pedalSent;

    public PianoWindow(Func<IMidiOutput> output, AppConfig config)
    {
        InitializeComponent();

        _output = output;
        _config = config;

        Keys.SetBaseOctave(Math.Clamp(_config.PianoBaseOctave, 0, 7));
        Keys.NoteDown += Press;
        Keys.NoteUp += Release;

        ThemeService.ThemeChanged += OnThemeChanged;

        SourceInitialized += (_, _) => WindowTheme.Apply(this, ThemeService.IsDark);

        Loaded += (_, _) =>
        {
            WindowTheme.Apply(this, ThemeService.IsDark);

            UpdateRangeText();
            UpdateDeviceIcon();

            // 打开时先复位踏板，避免上一段延音残留
            Output?.ControlChange(Channel, SustainController, 0);

            Focus();
            Keyboard.Focus(this);
        };
    }

    private IMidiOutput? Output => _output();

    /// <summary>当前最低八度的编号（C4 = 4）。</summary>
    public int BaseOctave => Keys.BaseOctave;

    /// <summary>正在发声的音高数量（测试用）。</summary>
    public int VoicesDown => _down.Count;

    /// <summary>延音踏板是否踩下（测试用）。</summary>
    public bool PedalEngaged => _pedalSent;

    // ------------------------------------------------------------ 发声

    public void Press(int note)
    {
        if (note is < 0 or > 127) return;

        Keys.SetPressed(note, true);
        if (!_down.Add(note)) return;

        Output?.NoteOn(Channel, note, Velocity);
    }

    public void Release(int note)
    {
        Keys.SetPressed(note, false);
        if (!_down.Remove(note)) return;

        Output?.NoteOff(Channel, note);
    }

    /// <summary>只松开所有声音，不动踏板（换音域时用）。</summary>
    private void ReleaseNotes()
    {
        if (_down.Count == 0) return;

        foreach (var note in _down.ToArray())
        {
            Keys.SetPressed(note, false);
            Output?.NoteOff(Channel, note);
        }

        _down.Clear();
    }

    /// <summary>松音 + 放掉踏板（失焦、关窗口、Esc 时用）。</summary>
    public void ReleaseAll()
    {
        ReleaseNotes();

        _shiftDown = false;
        _spaceDown = false;
        _pedalLatched = false;
        PedalButton.IsChecked = false;
        SetPedal(false);
    }

    private void SetPedal(bool on)
    {
        Keys.PedalOn = on;

        if (on == _pedalSent) return;

        _pedalSent = on;
        Output?.ControlChange(Channel, SustainController, on ? 127 : 0);
    }

    // ------------------------------------------------------------ 键盘

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var modifiers = Keyboard.Modifiers;
        var control = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;

        if (HandleKey(e.Key, true, control, shift, e.IsRepeat)) e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        var modifiers = Keyboard.Modifiers;
        var control = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;

        if (HandleKey(e.Key, false, control, shift)) e.Handled = true;
    }

    /// <summary>
    /// 处理一次按键（键盘事件和测试都走这里）。
    /// 返回 true 表示这个键被钢琴窗口消化掉了。
    /// </summary>
    public bool HandleKey(Key key, bool isDown, bool control = false, bool shift = false, bool isRepeat = false)
    {
        if (isRepeat) return true;

        // Shift 和空格都当延音踏板（Shift + ←/→ 换音域时踏板保持踩下）
        if (key is Key.LeftShift or Key.RightShift)
        {
            _shiftDown = isDown;
            UpdatePedalLock();
            return true;
        }

        if (key == Key.Space)
        {
            _spaceDown = isDown;
            UpdatePedalLock();
            return true;
        }

        // Shift + ←/→ 快速换音域（保留了 Ctrl + ←/→ 的老习惯）
        if (key is Key.Left or Key.Right)
        {
            if (!control && !shift) return false;

            if (isDown) ChangeOctave(key == Key.Left ? -1 : +1);
            return true;
        }

        if (control && key is Key.Up or Key.Down)
        {
            if (isDown) ChangeOctave(key == Key.Up ? +1 : -1);
            return true;
        }

        if (key == Key.Escape)
        {
            if (!isDown) return true;

            ReleaseAll();
            Close();
            return true;
        }

        if (!PianoKeyMap.TryGetOffset(key, out var offset)) return false;

        if (isDown) Press(Keys.BaseOctaveNote + offset);
        else Release(Keys.BaseOctaveNote + offset);

        return true;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        ChangeOctave(e.Delta > 0 ? +1 : -1);
        e.Handled = true;
    }

    // ------------------------------------------------------------ 八度 / 踏板

    private void ChangeOctave(int delta)
    {
        var next = Math.Clamp(Keys.BaseOctave + delta, 0, 7);
        if (next == Keys.BaseOctave) return;

        // 换八度前先把正在响的音松开，否则会留下"幽灵音"；
        // 踏板状态保留，Shift 按着换音域时延音不会被踩断
        ReleaseNotes();
        Keys.SetBaseOctave(next);
        UpdateRangeText();
    }

    private void UpdatePedalLock()
    {
        var on = _shiftDown || _spaceDown || _pedalLatched;
        SetPedal(on);
    }

    private void OctaveDownButton_Click(object sender, RoutedEventArgs e) => ChangeOctave(-1);

    private void OctaveUpButton_Click(object sender, RoutedEventArgs e) => ChangeOctave(+1);

    private void PedalButton_Click(object sender, RoutedEventArgs e)
    {
        _pedalLatched = PedalButton.IsChecked == true;
        UpdatePedalLock();
    }

    // ------------------------------------------------------------ 界面

    private void UpdateRangeText()
    {
        var low = Keys.BaseOctaveNote;
        var high = Keys.BaseOctaveNote + PianoKeyMap.Span - 1;
        RangeText.Text = $"{NoteName(low)} - {NoteName(high)}";
    }

    private void UpdateDeviceIcon()
    {
        var output = Output;
        NoDeviceIcon.Visibility = output is null || !output.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string NoteName(int note)
    {
        string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        var octave = note / 12 - 1;
        return names[((note % 12) + 12) % 12] + octave;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyTheme));
            return;
        }

        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Keys.ApplyTheme();
        WindowTheme.Apply(this, ThemeService.IsDark);
    }

    private void Window_Deactivated(object sender, EventArgs e) => ReleaseAll();

    private void Window_Closed(object sender, EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;

        ReleaseAll();

        _config.PianoBaseOctave = Keys.BaseOctave;
        try { _config.Save(); } catch { /* 存不下来也无所谓 */ }
    }
}
