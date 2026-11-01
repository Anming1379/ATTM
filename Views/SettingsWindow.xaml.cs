using System.Windows;
using System.Windows.Controls;
using ATTM.Core.Playback;
using ATTM.Plugins;
using ATTM.Services;

namespace ATTM.Views;

/// <summary>
/// 设置窗口：所有改动即时生效并立即保存，没有保存按钮，关闭用标题栏的叉。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly List<MidiDeviceInfo> _devices = new();
    private readonly AppConfig _config;
    private readonly Action<AppConfig> _applySettings;

    private bool _loading;

    public SettingsWindow(AppConfig config, Action<AppConfig> applySettings, IReadOnlyList<PluginEntry>? plugins = null)
    {
        InitializeComponent();

        _config = config;
        _applySettings = applySettings;

        _loading = true;

        ThemeCombo.SelectedIndex = config.Mode switch
        {
            AppTheme.Light => 1,
            AppTheme.System => 2,
            _ => 0
        };

        ReloadDevices(config.MidiOutputDevice);
        FillPluginList(plugins);

        _loading = false;

        SourceInitialized += (_, _) => WindowTheme.Apply(this, ThemeService.IsDark);
        ThemeService.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => WindowTheme.Apply(this, ThemeService.IsDark);

    /// <summary>插件是只读展示：装载/失败情况一目了然，管理交给 plugins 目录。</summary>
    private void FillPluginList(IReadOnlyList<PluginEntry>? plugins)
    {
        if (plugins is null || plugins.Count == 0)
        {
            PluginText.Text = "未装载任何插件。把插件 dll 放到程序目录的 plugins\\ 子目录，重启后生效。";
            return;
        }

        var lines = new List<string>(plugins.Count);
        foreach (var entry in plugins)
        {
            if (entry.Plugin is null)
            {
                lines.Add($"✗ {entry.Source}：{entry.Error}");
                continue;
            }

            var suffix = entry.IsLoaded ? string.Empty : $"（{entry.Error}）";
            var kind = entry.Plugin is IImporterPlugin ? "导入" : "扩展";
            lines.Add($"· {entry.Plugin.Name} v{entry.Plugin.Version} — {kind}{suffix}");
        }

        PluginText.Text = string.Join(Environment.NewLine, lines);

        FillPluginSettings(plugins);
    }

    /// <summary>把插件的设置页控件放进来的唯一入口。</summary>
    private void FillPluginSettings(IReadOnlyList<PluginEntry>? plugins)
    {
        PluginSettingsHost.Children.Clear();
        if (plugins is null) return;

        foreach (var entry in plugins)
        {
            if (!entry.IsLoaded || entry.Plugin is not IUiPlugin ui || entry.Context is null) continue;

            foreach (var item in ui.UiItems.Where(i => i.Slot == PluginUiSlot.SettingsPanel).OrderBy(i => i.Order))
            {
                try
                {
                    PluginSettingsHost.Children.Add(item.CreateView(entry.Context));
                }
                catch
                {
                    // 单个控件挂不上不影响整页
                }
            }
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _config.Theme = ThemeCombo.SelectedIndex switch
        {
            1 => "Light",
            2 => "System",
            _ => "Dark"
        };

        _applySettings(_config);
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        _config.MidiOutputDevice = DeviceCombo.SelectedIndex > 0 && DeviceCombo.SelectedIndex < _devices.Count
            ? _devices[DeviceCombo.SelectedIndex].Name
            : string.Empty;

        _applySettings(_config);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var name = DeviceCombo.SelectedIndex > 0 && DeviceCombo.SelectedIndex < _devices.Count
            ? _devices[DeviceCombo.SelectedIndex].Name
            : string.Empty;

        _loading = true;
        ReloadDevices(name);
        _loading = false;
    }

    private void ReloadDevices(string selectedName)
    {
        _devices.Clear();
        DeviceCombo.Items.Clear();

        _devices.Add(new MidiDeviceInfo(-1, string.Empty));
        DeviceCombo.Items.Add("（自动：优先 Microsoft GS Wavetable）");

        foreach (var device in Win32MidiOutput.GetDevices())
        {
            _devices.Add(device);
            DeviceCombo.Items.Add(device.Name);
        }

        var selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            var found = _devices.FindIndex(d => d.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
            if (found < 0)
                found = _devices.FindIndex(d => d.Name.Contains(selectedName, StringComparison.OrdinalIgnoreCase));

            if (found >= 0) selectedIndex = found;
        }

        DeviceCombo.SelectedIndex = selectedIndex;
    }
}
