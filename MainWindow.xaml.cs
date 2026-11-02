using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ATTM.Core.Import;
using ATTM.Core.Midi;
using ATTM.Core.Playback;
using ATTM.Core.Syntax;
using ATTM.Editing;
using ATTM.Services;
using ATTM.Views;
using Microsoft.Win32;

namespace ATTM;

public partial class MainWindow : Window
{
    private const string NewDocumentTemplate = """
# ATTM 新建文档 - 示例: 小星星
# 数字 1-7 是音符，0 是休止，逗号分段，空行分隔小节
# 播放会发送 MIDI 到设置里选择的输出设备

bpm = 120 # 歌曲速度
1 = C 4/4 # 这是调式和拍号, 可以改, 如 1 = #F 3/4
Vel = 90 +-0 # 力度90, 随机 +-0

+1,+1,+5,+5
(135)
# 可以用括号同时弹多个音 

$ 6,6,5,0 # 在前面用 $ 相当于给每个音符一个 + 号
% (6+1+3),(6+1+3)-6 # 在前面用 % 相当于给每个音符一个 - 号

+4,+4,+3,+3
(-4-61)

+2+2+10
(-5-72 ),(-5-72 )-5

$ legato(10223050) # legato() 表示连奏
15+1000+30
""";

    private readonly DispatcherTimer _parseTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };

    private readonly DispatcherTimer _playTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(10)
    };

    private readonly AppConfig _config;
    private readonly ScorePlayer _player;

    private ParseResult? _parse;

    /// <summary>乐句提前退场的提前量（秒）。</summary>
    private const double PhraseFadeLeadSeconds = 0.16;

    /// <summary>已经提前退场的小节号，避免同一小节反复退/又显示。</summary>
    private int _fadedMeasure = -1;
    private string? _filePath;
    private string _fileName = "未命名.attm";
    private string _deviceName = string.Empty;
    private bool _isDirty;
    private bool _suppressDirty;

    /// <summary>一次性状态栏提示（比如刚导入完 MIDI），用户一改动就消失。</summary>
    private string? _statusNotice;
    private int _lastSecond = -1;
    private bool _hasHighlight;

    public MainWindow(string? initialFile = null)
    {
        _config = AppConfig.Load();

        InitializeComponent();

        ThemeService.SetMode(_config.Mode);
        UpdateThemeChrome();
        RestoreWindowPlacement();

        _player = new ScorePlayer(new NullMidiOutput());
        _player.StateChanged += (_, _) => UpdatePlaybackUi();
        _player.PositionChanged += (_, _) => UpdatePlaybackPosition();
        _player.Finished += (_, _) => ClearHighlight();

        ThemeService.ThemeChanged += OnThemeChanged;

        SourceInitialized += (_, _) => WindowTheme.Apply(this, ThemeService.IsDark);

        _parseTimer.Tick += (_, _) =>
        {
            _parseTimer.Stop();
            Reparse();
        };

        _playTimer.Tick += (_, _) => _player.Advance();
        _playTimer.Start();

        Editor.BufferChanged += (_, _) =>
        {
            MarkDirty();
            ScheduleParse();
        };

        Editor.CaretChanged += (_, _) => UpdateStatus();
        Editor.HistoryChanged += (_, _) => UpdateButtons();

        _suppressDirty = true;
        Editor.Text = NewDocumentTemplate;
        _suppressDirty = false;

        Reparse();
        UpdateButtons();

        Loaded += (_, _) =>
        {
            Editor.EditorFontSize = _config.EditorFontSize;
            _deviceName = _config.MidiOutputDevice;
            ApplyMidiDevice(_config.MidiOutputDevice);

            var phraseMode = ParsePhraseMode(_config.PhrasePreviewModeName);
            PhraseButton.IsChecked = phraseMode != PhrasePreviewMode.Hidden;
            PhraseButton.ToolTip = PhraseTooltip(phraseMode);
            EnsurePhraseRoom(phraseMode);
            Editor.SetPhraseMode(phraseMode, animate: false);

            // 双击 .attm 打开时，命令行参数里带着文件路径
            if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
                LoadFile(initialFile);
            else
                Editor.FocusEditor();
        };

        Closing += (_, e) =>
        {
            if (!ConfirmDiscard())
            {
                e.Cancel = true;
                return;
            }

            SaveWindowPlacement();
            _playTimer.Stop();
            _player.Dispose();
        };
    }

    // ------------------------------------------------------------ 主题

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        WindowTheme.Apply(this, ThemeService.IsDark);
        Editor.ApplyTheme();
        UpdateThemeChrome();
        UpdateStatus();
    }

    private void UpdateThemeChrome()
    {
        var dark = ThemeService.IsDark;
        // 图标表示当前主题：深色=月亮，浅色=太阳
        ThemeButton.Content = dark ? "\uE51C" : "\uE518";

        var modeText = ThemeService.Mode switch
        {
            AppTheme.System => "跟随系统",
            AppTheme.Light => "浅色",
            _ => "深色"
        };

        ThemeButton.ToolTip = $"当前：{modeText} · 点击切换到{(dark ? "浅色" : "深色")}";
    }

    // ------------------------------------------------------------ 窗口位置

    private void RestoreWindowPlacement()
    {
        if (_config.WindowWidth is not { } width || _config.WindowHeight is not { } height) return;
        if (width < MinWidth || height < MinHeight) return;

        var left = _config.WindowLeft ?? Left;
        var top = _config.WindowTop ?? Top;
        var bounds = new Rect(left, top, width, height);

        // 上次的显示器可能已经拔掉，落在屏幕外就回退到居中
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (!bounds.IntersectsWith(virtualScreen)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        if (_config.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;

        _config.WindowLeft = (int)Math.Round(bounds.Left);
        _config.WindowTop = (int)Math.Round(bounds.Top);
        _config.WindowWidth = (int)Math.Round(bounds.Width);
        _config.WindowHeight = (int)Math.Round(bounds.Height);
        _config.WindowMaximized = WindowState == WindowState.Maximized;
        _config.Save();
    }

    // ------------------------------------------------------------ 解析 / 状态

    private void ScheduleParse()
    {
        _parseTimer.Stop();
        _parseTimer.Start();
    }

    private void Reparse()
    {
        _parse = AttmParser.Parse(Editor.Text);
        Editor.ApplyParse(_parse);

        if (_player.State != PlayerState.Stopped)
        {
            _player.Stop();
            ClearHighlight();
        }

        _player.SetScore(_parse.CanPlay ? _parse.Score : null);

        UpdateStatus();
        UpdateButtons();
    }

    private void MarkDirty()
    {
        if (_player.State != PlayerState.Stopped)
        {
            _player.Stop();
            ClearHighlight();
        }

        if (_suppressDirty || _isDirty) return;

        _isDirty = true;
        _statusNotice = null;
        UpdateStatus();
    }

    private void UpdateButtons()
    {
        UndoButton.IsEnabled = Editor.CanUndo;
        RedoButton.IsEnabled = Editor.CanRedo;
        PlayPauseButton.IsEnabled = _parse is { CanPlay: true };
        PlayFromCursorButton.IsEnabled = _parse is { CanPlay: true };
    }

    private void UpdatePlaybackUi()
    {
        var playing = _player.State == PlayerState.Playing;
        PlayPauseButton.Content = playing ? "\uE034" : "\uE037";
        PlayPauseButton.ToolTip = playing ? "暂停" : "播放";
        StopButton.IsEnabled = _player.State != PlayerState.Stopped;
        UpdateStatus();
    }

    private void UpdatePlaybackPosition()
    {
        if (_player.State == PlayerState.Stopped)
        {
            Editor.SetPlaybackSpans(null);
            Editor.HidePhrase();
            _hasHighlight = false;
            return;
        }

        var spans = _player.GetActiveSpans();
        Editor.SetPlaybackSpans(spans);
        _hasHighlight = spans.Count > 0;

        if (Editor.IsPhrasePreviewVisible)
        {
            var measure = GetPlayingMeasure();

            if (ShouldFadeOutPhrase(measure))
            {
                Editor.FadeOutPhrase();
                _fadedMeasure = measure;
            }

            if (measure != _fadedMeasure) Editor.ShowPhrase(measure);
        }

        if (_config.AutoScrollOnPlay && spans.Count > 0)
            Editor.ScrollOffsetIntoView(spans[0].Start);

        var second = (int)_player.Position.TotalSeconds;
        if (second == _lastSecond) return;

        _lastSecond = second;
        UpdateStatus();
    }

    private void ClearHighlight()
    {
        Editor.SetPlaybackSpans(null);
        Editor.HidePhrase();
        _hasHighlight = false;
        _lastSecond = -1;
        _fadedMeasure = -1;
        UpdateStatus();
    }

    /// <summary>当前播放位置落在第几个小节（0 起）。</summary>
    private int GetPlayingMeasure()
    {
        var score = _player.Score;
        if (score is null || score.MeasureCount <= 0) return 0;

        var ticks = _player.Position.TotalSeconds / score.SecondsPerTick;
        var index = (int)Math.Floor(ticks / Math.Max(1, score.MeasureTicks));
        return Math.Clamp(index, 0, score.MeasureCount - 1);
    }

    /// <summary>展开乐句预览时，如果窗口太窄就把窗口撑宽一点，免得正文被挤没。</summary>
    /// <summary>半屏侧边栏要留出宽度；全窗是盖住正文，不需要额外变宽。</summary>
    private void EnsurePhraseRoom(PhrasePreviewMode mode)
    {
        const double minWithPanel = 1180;
        var needsRoom = mode == PhrasePreviewMode.Panel;

        MinWidth = needsRoom ? minWithPanel : 720;

        if (needsRoom && WindowState == WindowState.Normal && Width < minWithPanel)
            Width = minWithPanel;
    }

    /// <summary>小节快结束前一点就让旧乐句开始渐隐，换句时屏幕上已经是干净的。</summary>
    private bool ShouldFadeOutPhrase(int measureIndex)
    {
        if (_player.Score is not { } score) return false;
        if (measureIndex == _fadedMeasure) return false;
        if (Editor.PhraseMeasureIndex != measureIndex) return false;

        var measureSeconds = score.MeasureTicks * score.SecondsPerTick;
        var remaining = (measureIndex + 1) * measureSeconds - _player.Position.TotalSeconds;

        return remaining > 0 && remaining <= PhraseFadeLeadSeconds;
    }

    private void FileStatusText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(_filePath)) RevealInExplorer(_filePath);
    }

    private PianoWindow? _pianoWindow;

    private void PianoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pianoWindow is { IsLoaded: true })
        {
            _pianoWindow.Activate();
            return;
        }

        _pianoWindow = new PianoWindow(() => _player.Output, _config) { Owner = this };
        _pianoWindow.Closed += (_, _) => _pianoWindow = null;
        _pianoWindow.Show();
    }

    /// <summary>乐句预览按钮：半屏 -> 全窗 -> 收起 -> 半屏，按一下走一步。</summary>
    private void PhraseButton_Click(object sender, RoutedEventArgs e)
    {
        var mode = Editor.TogglePhrasePreview();

        // 按钮只表示"开着还是关着"（半屏和全窗都算开着），具体在第几档看预览面板自己
        PhraseButton.IsChecked = mode != PhrasePreviewMode.Hidden;
        EnsurePhraseRoom(mode);

        _config.PhrasePreviewMode = mode.ToString();
        _config.PhrasePreviewVisible = mode != PhrasePreviewMode.Hidden;
        _config.Save();

        PhraseButton.ToolTip = PhraseTooltip(mode);

        if (mode == PhrasePreviewMode.Hidden) return;

        if (_player.State != PlayerState.Stopped)
            Editor.ShowPhrase(GetPlayingMeasure());
        else if (mode == PhrasePreviewMode.Full)
            Editor.ShowPhrase(GetCaretMeasure());
    }

    private static PhrasePreviewMode ParsePhraseMode(string name) => name switch
    {
        "Panel" => PhrasePreviewMode.Panel,
        "Full" => PhrasePreviewMode.Full,
        _ => PhrasePreviewMode.Hidden
    };

    private static string PhraseTooltip(PhrasePreviewMode mode) => mode switch
    {
        PhrasePreviewMode.Panel => "乐句预览：半屏（再按一下全窗）",
        PhrasePreviewMode.Full => "乐句预览：全窗（再按一下关闭）",
        _ => "乐句预览：关闭（再按一下半屏）"
    };

    private void UpdateStatus()
    {
        var (line, column) = Editor.CaretPosition;
        var score = _parse?.Score;
        var errors = _parse?.ErrorCount ?? 0;

        FileStatusText.Text = _filePath ?? _fileName;
        FileStatusText.ToolTip = string.IsNullOrEmpty(_filePath)
            ? "尚未保存到文件（Ctrl+S 保存）"
            : $"点击在资源管理器中显示 - {_filePath}";
        FileStatusText.Cursor = string.IsNullOrEmpty(_filePath) ? null : Cursors.Hand;
        DirtyStatusText.Text = _isDirty ? " · 未保存" : " · 已保存";
        Title = $"ATTM — {_fileName}{(_isDirty ? " *" : string.Empty)}";

        ErrorCountText.Text = $"错误 {errors}";
        ErrorCountText.Foreground = (Brush)FindResource(errors == 0 ? "Brush.Success" : "Brush.Error");

        if (_statusNotice is not null)
        {
            InfoText.Text = _statusNotice;
        }
        else if (score is null)
        {
            InfoText.Text = $"缺少 bpm / 调式声明 · 行 {line} 列 {column}";
        }
        else
        {
            InfoText.Text = $"BPM {score.Bpm:0.##} · {score.KeyName} · {score.TimeSignatureName} · " +
                            $"{score.Notes.Count} 音符 · {FormatDuration(score.Duration)} · 行 {line} 列 {column}";
        }

        PlaybackStatusText.Text = DescribePlayback();
    }

    private string DescribePlayback()
    {
        if (_player.State is PlayerState.Playing or PlayerState.Paused)
        {
            var state = _player.State == PlayerState.Playing ? "播放中" : "已暂停";
            var suffix = _player.IsSilent ? "（静音）" : string.Empty;
            return $"{state} {FormatDuration(_player.Position)} / {FormatDuration(_player.Duration)}{suffix}";
        }

        if (_parse is null) return "无法播放";
        if (_parse.ErrorCount > 0) return "存在语法错误";
        return _player.IsSilent ? "就绪（无 MIDI 设备）" : "就绪";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var total = Math.Max(0, (int)duration.TotalSeconds);
        return $"{total / 60}:{total % 60:00}";
    }

    // ------------------------------------------------------------ 设置 / MIDI 输出

    private void ApplyLiveSettings(AppConfig config)
    {
        var deviceChanged = !string.Equals(_deviceName, config.MidiOutputDevice, StringComparison.Ordinal);

        _config.Theme = config.Theme;
        _config.EditorFontSize = config.EditorFontSize;
        _config.MidiOutputDevice = config.MidiOutputDevice;
        _config.Save();

        ThemeService.SetMode(_config.Mode);
        Editor.EditorFontSize = _config.EditorFontSize;

        if (deviceChanged || !_player.Output.IsOpen)
            ApplyMidiDevice(_config.MidiOutputDevice);

        UpdateStatus();
    }

    private void ApplyMidiDevice(string deviceName)
    {
        _deviceName = deviceName;

        var devices = Win32MidiOutput.GetDevices();

        int? index = null;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = devices.FirstOrDefault(d => d.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                        ?? devices.FirstOrDefault(d => d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
            index = match?.Index;
        }

        if (index is null)
        {
            var preferred = devices.FirstOrDefault(d => d.Name.Contains("GS Wavetable", StringComparison.OrdinalIgnoreCase))
                            ?? devices.FirstOrDefault();
            index = preferred?.Index;
        }

        if (index is { } deviceIndex
            && Win32MidiOutput.TryOpen(deviceIndex, out var output, out _)
            && output is not null)
        {
            _player.SetOutput(output);
        }
        else
        {
            _player.SetOutput(new NullMidiOutput());
        }

        UpdatePlaybackUi();
    }

    // ------------------------------------------------------------ 文件

    private static bool IsScoreFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".attm", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private void NewDocument()
    {
        if (!ConfirmDiscard()) return;

        _suppressDirty = true;
        Editor.Text = NewDocumentTemplate;
        _suppressDirty = false;

        _filePath = null;
        _fileName = "未命名.attm";
        _isDirty = false;
        _statusNotice = null;
        _player.Stop();

        Reparse();
        Editor.FocusEditor();
    }

    /// <summary>打开谱库文件夹（音乐\ATTM），在里面双击 .attm 就会用本程序打开。</summary>
    private void OpenLibraryFolder()
    {
        // 谱库固定是"音乐\ATTM"，不跟随上次保存到哪
        var folder = AppConfig.LibraryFolder;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打不开谱库文件夹：{ex.Message}", "ATTM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>在资源管理器里定位到某个文件；文件不存在就打开它所在的目录。</summary>
    private void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) return;

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
            // 资源管理器打不开就算了，不打扰用户
        }
    }

    private bool LoadFile(string path)
    {
        string text;
        try
        {
            text = TextFile.Read(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开失败：{ex.Message}", "ATTM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _suppressDirty = true;
        Editor.Text = text;
        _suppressDirty = false;

        _filePath = path;
        _fileName = Path.GetFileName(path);
        _isDirty = false;

        _player.Stop();
        Reparse();
        Editor.FocusEditor();
        return true;
    }

    private bool SaveDocument(bool forceDialog)
    {
        if (_parse is null || _parse.HasErrors)
        {
            MessageBox.Show(this,
                "谱面还有语法错误，先修好再保存吧。\n错误位置在编辑器里用红色波浪线标出来了。",
                "ATTM",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var path = _filePath;
        if (forceDialog || string.IsNullOrEmpty(path))
        {
            var dialog = new SaveAsWindow(_filePath, _config, forceDialog) { Owner = this };
            if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.ResultPath))
                return false;

            path = dialog.ResultPath;
        }

        try
        {
            SaveTo(path!);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "ATTM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _filePath = path;
        _fileName = Path.GetFileName(path!);
        _isDirty = false;

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) _config.DefaultSaveFolder = folder;

        Reparse();
        return true;
    }

    /// <summary>同时写 .attm 文本和同名 .mid。</summary>
    private void SaveTo(string attmPath)
    {
        var directory = Path.GetDirectoryName(attmPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        TextFile.Write(attmPath, Editor.Text);

        var midiPath = Path.ChangeExtension(attmPath, ".mid");
        MidiFileWriter.Write(_parse!.Score!, midiPath);
    }

    /// <summary>有未保存改动时询问：保存 / 不保存 / 取消。</summary>
    private bool ConfirmDiscard()
    {
        if (!_isDirty) return true;

        var result = MessageBox.Show(this,
            $"\"{_fileName}\" 还没有保存，要保存吗？",
            "ATTM",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => SaveDocument(forceDialog: false),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (control && e.Key == Key.O)
        {
            OpenLibraryFolder();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.N)
        {
            NewDocument();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.S)
        {
            SaveDocument(forceDialog: shift);
            e.Handled = true;
            return;
        }

        // Ctrl+V 粘进来的是文件而不是文本：直接打开它
        if (control && !shift && e.Key == Key.V && Clipboard.ContainsFileDropList())
        {
            var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f => IsScoreFile(f) || IsMidiFile(f));
            if (file is not null)
            {
                if (IsMidiFile(file)) ImportMidi(file);
                else if (ConfirmDiscard()) LoadFile(file);

                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var file = TryGetDroppedFile(e);
        if (file is null) return;

        if (IsMidiFile(file)) ImportMidi(file);
        else if (ConfirmDiscard()) LoadFile(file);

        e.Handled = true;
    }

    private static string? TryGetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;

        return files.FirstOrDefault(file => IsScoreFile(file) || IsMidiFile(file));
    }

    public static bool IsMidiFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mid", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".midi", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ 工具栏

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_parse is not { CanPlay: true } parsed) return;

        switch (_player.State)
        {
            case PlayerState.Playing:
                _player.Pause();
                return;
            case PlayerState.Paused:
                _fadedMeasure = -1;
                _player.Play();
                return;
            default:
                _player.SetScore(parsed.Score);
                _fadedMeasure = -1;
                _player.Play();
                return;
        }
    }

    private void PlayFromCursorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_parse is not { CanPlay: true } parsed) return;

        _player.SetScore(parsed.Score);
        _fadedMeasure = -1;
        _player.Play(GetCaretPlaybackPosition());
    }

    /// <summary>光标现在落在第几小节（没有解析结果时是 0）。</summary>
    private int GetCaretMeasure()
    {
        var starts = _parse?.MeasureStartLines;
        if (starts is null || starts.Count == 0) return 0;

        var line = Editor.CaretPosition.Line - 1;
        var measure = 0;

        for (var i = 0; i < starts.Count; i++)
        {
            if (starts[i] <= line) measure = i;
            else break;
        }

        return measure;
    }

    /// <summary>光标所在小节对应的播放时刻。</summary>
    private TimeSpan GetCaretPlaybackPosition()
    {
        if (_parse?.Score is not { } score) return TimeSpan.Zero;

        var line = Editor.CaretPosition.Line - 1;
        var starts = _parse.MeasureStartLines;

        var measure = 0;
        for (var i = 0; i < starts.Count; i++)
        {
            if (starts[i] <= line) measure = i;
            else break;
        }

        var seconds = measure * score.MeasureTicks * score.SecondsPerTick;
        return TimeSpan.FromSeconds(seconds);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        ClearHighlight();
    }

    private void NewButton_Click(object sender, RoutedEventArgs e) => NewDocument();

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenLibraryFolder();

    // 工具栏的保存键始终要求重新选位置；Ctrl+S 才在保存过一次后直接覆盖保存
    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveDocument(forceDialog: true);

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Editor.Undo();

    private void RedoButton_Click(object sender, RoutedEventArgs e) => Editor.Redo();

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var next = ThemeService.IsDark ? AppTheme.Light : AppTheme.Dark;
        _config.Theme = ThemeService.ToConfigString(next);
        _config.Save();

        ThemeService.SetMode(next);
    }

    private void MidiImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 MIDI",
            Filter = "MIDI 文件 (*.mid;*.midi)|*.mid;*.midi|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = ResolveMidiFolder()
        };

        if (dialog.ShowDialog(this) != true) return;

        ImportMidi(dialog.FileName);
    }

    /// <summary>
    /// 把一个 MIDI 文件转成简谱，替换当前文档（会先问要不要保存旧内容）。
    /// 转换失败时只弹一句人话错误，不改动编辑器。
    /// </summary>
    public bool ImportMidi(string path, bool confirmDiscard = true)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"找不到文件：{path}", "ATTM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (confirmDiscard && !ConfirmDiscard()) return false;

        MidiImportResult result;

        try
        {
            result = MidiImporter.Convert(path);
        }
        catch (MidiFileException ex)
        {
            MessageBox.Show(this, ex.Message, "导不进这个 MIDI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败：{ex.Message}", "ATTM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _suppressDirty = true;
        Editor.Text = result.Text;
        _suppressDirty = false;

        _filePath = null;
        _fileName = Path.GetFileNameWithoutExtension(path) + ".attm";
        _isDirty = true;
        _player.Stop();

        Reparse();

        // 状态栏给一次性提示，细节（警告）放进 ToolTip
        var summary = result.Report.Summary;
        if (result.Report.Warnings.Count > 0)
        {
            InfoText.ToolTip = string.Join((char)10, result.Report.Warnings);
            summary += $" · {result.Report.Warnings.Count} 条提示（悬停查看）";
        }

        _statusNotice = summary;
        InfoText.Text = summary;
        Editor.FocusEditor();

        _config.LastMidiFolder = Path.GetDirectoryName(path) ?? string.Empty;
        _config.Save();
        return true;
    }

    private string ResolveMidiFolder()
    {
        if (!string.IsNullOrWhiteSpace(_config.LastMidiFolder) && Directory.Exists(_config.LastMidiFolder))
            return _config.LastMidiFolder;

        return AppConfig.LibraryFolder;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_config, ApplyLiveSettings) { Owner = this };
        dialog.ShowDialog();
    }
}
