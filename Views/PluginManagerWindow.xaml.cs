using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ATTM.Plugins;
using ATTM.Services;

namespace ATTM.Views;

/// <summary>
/// 插件管理器：列出所有扫描到的插件，控制启用状态，打开插件目录。
/// 这里是唯一知道「插件有哪些」的界面；主程序其它地方只按能力取用。
/// </summary>
public partial class PluginManagerWindow : Window
{
    private readonly PluginHost _host;
    private readonly AppConfig _config;
    private readonly List<(PluginEntry Entry, CheckBox Toggle, TextBlock State)> _rows = new();

    private readonly Action? _onReload;

    public PluginManagerWindow(PluginHost host, AppConfig config, Action? onReload = null)
    {
        InitializeComponent();

        _host = host;
        _config = config;
        _onReload = onReload;

        SourceInitialized += (_, _) => WindowTheme.Apply(this, ThemeService.IsDark);

        BuildList();
    }

    /// <summary>本次是否改动过启用状态（调用方据此提示重启）。</summary>
    public bool ToggledAny { get; private set; }

    private void BuildList()
    {
        PluginList.Items.Clear();
        _rows.Clear();

        var loaded = _host.Entries.Count(e => e.IsLoaded);
        var failed = _host.Entries.Count(e => e.State == PluginState.Failed);
        var disabled = _host.Entries.Count(e => e.State == PluginState.Disabled);
        var pending = _host.Entries.Count(e => e.State == PluginState.PendingApproval);

        SummaryText.Text =
            $"共 {_host.Entries.Count} 个：已启用 {loaded} · 已禁用 {disabled} · 待授权 {pending} · 失败 {failed}"
            + $"\n配置文件：{AppConfig.ActiveConfigPath}";

        if (_host.Entries.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;

        foreach (var entry in _host.Entries)
        {
            PluginList.Items.Add(BuildRow(entry));
        }
    }

    private FrameworkElement BuildRow(PluginEntry entry)
    {
        var grid = new Grid { Margin = new Thickness(14, 8, 14, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var disabledInConfig = _config.DisabledPlugins.Any(name =>
            string.Equals(name, entry.Source, StringComparison.OrdinalIgnoreCase));

        var toggle = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            IsChecked = !disabledInConfig,
            ToolTip = "取消勾选后重启即不再装载",
            IsEnabled = entry.SourcePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && entry.State != PluginState.PendingApproval
        };

        var stateText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        // 关键：切换后就地刷新这一行，**不要重建整个列表**
        // ——重建会把正在处理 Click 的 CheckBox 从树上摘掉，看起来就像「点了没反应」。
        toggle.Click += (_, _) =>
        {
            SetEnabled(entry, toggle.IsChecked == true);
            ApplyRowState(stateText, entry, toggle.IsChecked == true);
        };

        grid.Children.Add(toggle);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var title = entry.Plugin is null
            ? entry.DisplayName
            : $"{entry.Plugin.Name} v{entry.Plugin.Version}";

        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });

        if (entry.Plugin is not null && !string.IsNullOrWhiteSpace(entry.Plugin.Description))
        {
            text.Children.Add(new TextBlock
            {
                Text = entry.Plugin.Description,
                Foreground = (Brush)FindResource("Brush.TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        var trust = entry.Trust == PluginTrust.Unrestricted ? "无沙箱" : "沙箱";
        var detail = entry.Error is null
            ? $"{entry.Source} · {trust} · {DescribeCapabilities(entry)}"
            : $"{entry.Source} · {trust} · {entry.Error}";

        text.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = (Brush)FindResource(entry.Error is null ? "Brush.TextSecondary" : "Brush.Error"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 11.5
        });

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        ApplyRowState(stateText, entry, toggle.IsChecked == true);

        Grid.SetColumn(stateText, 2);
        grid.Children.Add(stateText);
        _rows.Add((entry, toggle, stateText));

        return new Border
        {
            BorderBrush = (Brush)FindResource("Brush.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    /// <summary>按「配置里的启用状态」刷新一行，未重启前也能看到改动结果。</summary>
    private void ApplyRowState(TextBlock stateText, PluginEntry entry, bool enabled)
    {
        if (entry.State == PluginState.PendingApproval)
        {
            stateText.Text = "待授权";
            stateText.Foreground = (Brush)FindResource("Brush.Accent");
            return;
        }

        if (entry.State == PluginState.Failed && enabled)
        {
            stateText.Text = "失败";
            stateText.Foreground = (Brush)FindResource("Brush.Error");
            return;
        }

        stateText.Text = enabled ? "已启用" : "已禁用（重启生效）";
        stateText.Foreground = (Brush)FindResource(enabled ? "Brush.Success" : "Brush.Accent");
    }

    private static string DescribeCapabilities(PluginEntry entry)
    {
        if (entry.Plugin is null) return "无";

        var parts = new List<string>(4);
        if (entry.Plugin is IImporterPlugin) parts.Add("导入");
        if (entry.Plugin is IExporterPlugin) parts.Add("导出");
        if (entry.Plugin is ICommandPlugin) parts.Add("命令");
        if (entry.Plugin is IUiPlugin) parts.Add("界面");

        return parts.Count == 0 ? "纯逻辑" : string.Join(" / ", parts);
    }

    private void SetEnabled(PluginEntry entry, bool enabled)
    {
        if (!entry.SourcePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;

        var list = _config.DisabledPlugins;

        if (enabled)
        {
            list.RemoveAll(name => string.Equals(name, entry.Source, StringComparison.OrdinalIgnoreCase));
        }
        else if (!list.Any(name => string.Equals(name, entry.Source, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(entry.Source);
        }

        if (!_config.Save())
        {
            MessageBox.Show(this,
                "设置没能保存，重启后这项改动会丢失：\n\n" + (AppConfig.LastSaveError ?? "未知原因"),
                "ATTM",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ToggledAny = true;
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _onReload?.Invoke();
        BuildList();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var directory in _host.Directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            }
            catch
            {
                // 打不开就算了，不打扰用户
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
