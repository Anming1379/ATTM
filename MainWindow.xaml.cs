using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ATTM.Core.Midi;
using ATTM.Core.Playback;
using ATTM.Core.Syntax;
using ATTM.Plugins;
using ATTM.Services;
using ATTM.Views;
using Microsoft.Win32;

namespace ATTM;

public partial class MainWindow : Window, IPluginDocumentBridge
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
        Interval = TimeSpan.FromMilliseconds(UiTuning.ParseDebounceMilliseconds)
    };

    private readonly DispatcherTimer _playTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(UiTuning.PlaybackTickMilliseconds)
    };

    private readonly AppConfig _config;
    private readonly ScorePlayer _player;
    private readonly PluginHost _plugins;

    /// <summary>窗口所在目录下的插件目录。</summary>
    private static string BundledPluginDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");

    private ParseResult? _parse;

    /// <summary>已经提前退场的小节号，避免同一小节反复退/又显示。</summary>
    private int _fadedMeasure = -1;
    private string? _filePath;
    private string _fileName = "未命名.attm";
    private string _deviceName = string.Empty;
    private bool _isDirty;
    private bool _suppressDirty;
    private int _lastSecond = -1;
    private bool _hasHighlight;

    public MainWindow(string? initialFile = null)
    {
        _config = AppConfig.Load();

        // 插件：先装随程序分发的，再装用户目录里的；任一插件出错都不影响启动
        _plugins = new PluginHost(
            AppContext.BaseDirectory,
            AppConfig.ConfigDirectory,
            new[] { BundledPluginDirectory, Path.Combine(AppConfig.ConfigDirectory, "plugins") },
            this,
            _config.DisabledPlugins,
            _config.ApprovedPlugins);
        _plugins.LoadAll();

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
        ApplyPluginContributions();
        HandlePendingTrustRequests();

        Loaded += (_, _) =>
        {
            Editor.EditorFontSize = _config.EditorFontSize;
            _deviceName = _config.MidiOutputDevice;
            ApplyMidiDevice(_config.MidiOutputDevice);

            PhraseButton.IsChecked = _config.PhrasePreviewVisible;
            Editor.SetPhrasePreviewVisible(_config.PhrasePreviewVisible, animate: false);

            // 双击 .attm 打开时，命令行参数里带着文件路径
            if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
                OpenPath(initialFile);
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
            _plugins.ShutdownAll();
        };
    }
}
