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

public partial class MainWindow
{
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
        UpdateStatus();
    }

    private void UpdateButtons()
    {
        UndoButton.IsEnabled = Editor.CanUndo;
        RedoButton.IsEnabled = Editor.CanRedo;
        PlayPauseButton.IsEnabled = _parse is { CanPlay: true };
        PlayFromCursorButton.IsEnabled = _parse is { CanPlay: true };

        var importers = _plugins.Importers;
        ImportButton.IsEnabled = importers.Count > 0;
        ImportButton.ToolTip = importers.Count == 0
            ? "没有可用插件：把插件 dll 放到程序目录的 plugins 里"
            : "导入（" + string.Join(" / ", importers.Select(e => ((IImporterPlugin)e.Plugin!).Name)) + "）· Ctrl+I";
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
        => _player.Score?.GetMeasureIndex(_player.Position) ?? 0;

    /// <summary>展开乐句预览时，如果窗口太窄就把窗口撑宽一点，免得正文被挤没。</summary>
    private void EnsurePhraseRoom(bool visible)
    {
        MinWidth = visible ? UiTuning.MinWidthWithPhrasePanel : UiTuning.MinWidthWithoutPhrasePanel;

        if (visible && WindowState == WindowState.Normal && Width < UiTuning.MinWidthWithPhrasePanel)
            Width = UiTuning.MinWidthWithPhrasePanel;
    }

    /// <summary>小节快结束前一点就让旧乐句开始渐隐，换句时屏幕上已经是干净的。</summary>
    private bool ShouldFadeOutPhrase(int measureIndex)
    {
        if (_player.Score is not { } score) return false;
        if (measureIndex == _fadedMeasure) return false;
        if (Editor.PhraseMeasureIndex != measureIndex) return false;

        var remaining = score.GetMeasureStartTime(measureIndex + 1).TotalSeconds - _player.Position.TotalSeconds;

        return remaining > 0 && remaining <= UiTuning.PhraseFadeLeadSeconds;
    }

    private void FileStatusText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(_filePath)) RevealInExplorer(_filePath);
    }

    private void PhraseButton_Click(object sender, RoutedEventArgs e)
    {
        var visible = PhraseButton.IsChecked == true;

        Editor.SetPhrasePreviewVisible(visible);
        EnsurePhraseRoom(visible);
        _config.PhrasePreviewVisible = visible;
        _config.Save();

        if (visible && _player.State != PlayerState.Stopped)
            Editor.ShowPhrase(GetPlayingMeasure());
    }

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

        if (score is null)
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
}
