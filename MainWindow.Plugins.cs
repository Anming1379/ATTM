using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ATTM.Plugins;
using ATTM.Views;

namespace ATTM;

/// <summary>
/// 插件相关的界面部分：把插件声明的命令与控件挂进工具栏 / 状态栏，
/// 并提供 <see cref="IPluginDocumentBridge"/> 让插件能读改文档而不接触窗口本身。
/// </summary>
public partial class MainWindow : IPluginDocumentBridge
{
    private readonly List<FrameworkElement> _pluginViews = new();
    private readonly List<(Button Button, PluginCommand Command, IPluginContext Context)> _pluginButtons = new();

    private DispatcherTimer? _pluginMessageTimer;

    // ------------------------------------------------------------ 装配

    /// <summary>把插件贡献挂到界面上（启动时一次）。</summary>
    private void ApplyPluginContributions()
    {
        PluginToolbarHost.Children.Clear();
        PluginStatusHost.Children.Clear();
        _pluginViews.Clear();
        _pluginButtons.Clear();

        foreach (var (entry, command) in _plugins.Commands)
        {
            if (entry.Context is null) continue;

            var button = CreatePluginCommandButton(entry, command);
            _pluginButtons.Add((button, command, entry.Context));
            PluginToolbarHost.Children.Add(button);
        }

        foreach (var (entry, item) in _plugins.UiItems)
        {
            var host = item.Slot switch
            {
                PluginUiSlot.Toolbar => PluginToolbarHost,
                PluginUiSlot.StatusBar => PluginStatusHost,
                _ => null
            };

            if (host is null) continue;

            var view = CreatePluginView(entry, item);
            if (view is null) continue;

            host.Children.Add(view);
            _pluginViews.Add(view);
        }

        UpdatePluginCommandStates();
    }

    private Button CreatePluginCommandButton(PluginEntry entry, PluginCommand command)
    {
        var button = new Button
        {
            Style = TryFindResource("IconButton") as Style,
            ToolTip = command.ToolTip ?? command.Title
        };

        if (!string.IsNullOrEmpty(command.IconGlyph))
        {
            button.Content = command.IconGlyph;
        }
        else
        {
            button.Content = command.Title;
            button.MinWidth = 64;
            button.Padding = new Thickness(8, 0, 8, 0);
        }

        button.Click += (_, _) =>
        {
            if (entry.Context is null) return;

            try
            {
                command.Execute(entry.Context);
            }
            catch (Exception ex)
            {
                _plugins.Log($"命令 {command.Id} 执行失败：{ex.Message}");
                ShowStatusMessage($"{entry.DisplayName}：{ex.Message}", isError: true);
            }
        };

        return button;
    }

    private FrameworkElement? CreatePluginView(PluginEntry entry, PluginUiItem item)
    {
        if (entry.Context is null) return null;

        try
        {
            return item.CreateView(entry.Context);
        }
        catch (Exception ex)
        {
            _plugins.Log($"{entry.DisplayName} 的控件创建失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>按 CanExecute 刷新插件按钮。</summary>
    private void UpdatePluginCommandStates()
    {
        foreach (var (button, command, context) in _pluginButtons)
        {
            try
            {
                button.IsEnabled = command.CanExecute?.Invoke(context) ?? true;
            }
            catch
            {
                button.IsEnabled = false;
            }
        }
    }

    // ------------------------------------------------------------ 插件管理器

    private void ShowPluginManager() => SafeRun("打开插件管理器", () =>
    {
        var dialog = new PluginManagerWindow(_plugins, _config, ReloadPlugins) { Owner = this };
        dialog.ShowDialog();

        if (!dialog.ToggledAny) return;

        ShowStatusMessage("插件开关已保存，重启 ATTM 后生效");
    });

    /// <summary>热重载：拆掉插件控件 → 卸载上下文 → 重新扫描 → 重新装配。</summary>
    private void ReloadPlugins()
    {
        PluginToolbarHost.Children.Clear();
        PluginStatusHost.Children.Clear();
        _pluginViews.Clear();
        _pluginButtons.Clear();

        var clean = _plugins.Reload();

        ApplyPluginContributions();
        UpdateButtons();

        ShowStatusMessage(
            clean ? "插件已重新加载" : "插件已重新加载（部分上下文未能卸载，建议重启）",
            isError: !clean);
    }

    /// <summary>
    /// 启动时处理「申请无沙箱」的插件：逐个弹窗确认，同意的记进配置并立即重载。
    /// 用户拒绝的保持 PendingApproval，可在插件管理器里看到原因。
    /// </summary>
    private void HandlePendingTrustRequests()
    {
        var pending = _plugins.PendingApprovals;
        if (pending.Count == 0) return;

        var approved = 0;

        foreach (var entry in pending)
        {
            if (entry.Plugin is null) continue;

            var answer = MessageBox.Show(this,
                $"插件「{entry.Plugin.Name}」v{entry.Plugin.Version} 申请无沙箱权限。\n\n" +
                $"理由：{entry.Error}\n\n" +
                "允许后它可以读写任意文件、访问网络、启动进程。只在你信任这个插件时才选择「是」。",
                "插件权限申请",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) continue;

            _config.ApprovedPlugins.Add(entry.Plugin.Id + "@" + entry.Plugin.Version);

            // 关键：宿主的授权集合是构造时快照的，必须同步更新，否则 Reload 后仍是 PendingApproval
            _plugins.Approve(entry.Plugin.Id, entry.Plugin.Version);
            approved++;
        }

        if (approved == 0) return;

        _config.Save();
        ReloadPlugins();
    }

    /// <summary>
    /// 统一包一层：任何界面操作失败都变成「可读的错误提示 + 日志」，
    /// 而不是按钮点了没反应。
    /// </summary>
    private void SafeRun(string action, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            var path = App.WriteLog(ex);
            _plugins.Log($"{action} 失败：{ex}");

            MessageBox.Show(this, $"{action}失败：{ex.Message}\n\n详细信息已写入：\n{path}", "ATTM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------ 文档桥

    string IPluginDocumentBridge.GetDocumentText() => Editor.Text;

    void IPluginDocumentBridge.SetDocumentText(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ((IPluginDocumentBridge)this).SetDocumentText(text)));
            return;
        }

        _suppressDirty = true;
        Editor.Text = text;
        _suppressDirty = false;

        _isDirty = true;
        _player.Stop();
        Reparse();
    }

    void IPluginDocumentBridge.SetStatus(string message) => ShowStatusMessage(message);

    void IPluginDocumentBridge.ShowMessage(string message, bool isError)
        => MessageBox.Show(this, message, "插件",
            MessageBoxButton.OK,
            isError ? MessageBoxImage.Warning : MessageBoxImage.Information);

    // ------------------------------------------------------------ 状态栏消息

    /// <summary>状态栏上的一条短期消息，几秒后自动消失。</summary>
    private void ShowStatusMessage(string message, bool isError = false)
    {
        PluginMessageText.Text = message;
        PluginMessageText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "Brush.Error" : "Brush.TextSecondary");

        _pluginMessageTimer?.Stop();
        _pluginMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 12 : 6) };
        _pluginMessageTimer.Tick += (_, _) =>
        {
            _pluginMessageTimer?.Stop();
            PluginMessageText.Text = string.Empty;
        };
        _pluginMessageTimer.Start();
    }
}
