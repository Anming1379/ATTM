using System.Windows;

namespace ATTM.Plugins;

/// <summary>插件装载状态。</summary>
public enum PluginState
{
    /// <summary>装载并初始化成功。</summary>
    Loaded,

    /// <summary>装载或初始化失败，原因见 <see cref="PluginEntry.Error"/>。</summary>
    Failed,

    /// <summary>被用户在插件管理器里禁用：对应 dll 直接跳过，不执行任何插件代码。</summary>
    Disabled,

    /// <summary>插件申请无沙箱权限但用户还没确认，宿主暂不初始化它。</summary>
    PendingApproval
}

/// <summary>插件的信任级别。</summary>
public enum PluginTrust
{
    /// <summary>默认：只能通过 <see cref="IPluginContext"/> 做事，且加载前会做 API 静态扫描。</summary>
    Sandboxed,

    /// <summary>用户确认过的完全信任：允许任意文件/网络/进程访问。</summary>
    Unrestricted
}

/// <summary>
/// 申请无沙箱权限的插件实现这个接口。宿主不会自动放行，
/// 而是弹窗让用户确认，并把结论按 "id@version" 记进配置。
/// </summary>
public interface ITrustRequestPlugin : IAttmPlugin
{
    /// <summary>给用户看的中文理由：为什么需要越过沙箱。</summary>
    string TrustJustification { get; }
}

/// <summary>插件可以往主程序的哪些位置添加控件。</summary>
public enum PluginUiSlot
{
    /// <summary>工具栏右侧。</summary>
    Toolbar,

    /// <summary>状态栏中部。</summary>
    StatusBar,

    /// <summary>设置窗口底部的插件页。</summary>
    SettingsPanel
}

/// <summary>一次导入的产物。</summary>
public sealed record PluginImportResult(
    string Text,
    string? SuggestedFileName = null,
    IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<string> WarningList => Warnings ?? Array.Empty<string>();
}

/// <summary>插件注册的一条命令；宿主把它变成工具栏上的一项。</summary>
/// <param name="Id">插件内唯一。</param>
/// <param name="Title">按钮文字 / 无障碍名称。</param>
/// <param name="ToolTip">悬停提示。</param>
/// <param name="IconGlyph">Material Symbols 码位字符，例如 "\uE87B"；为空则显示文字。</param>
/// <param name="Execute">执行体，已经在 UI 线程上。</param>
/// <param name="CanExecute">可选：返回 false 时按钮变灰。</param>
public sealed record PluginCommand(
    string Id,
    string Title,
    string? ToolTip,
    string? IconGlyph,
    Action<IPluginContext> Execute,
    Func<IPluginContext, bool>? CanExecute = null);

/// <summary>插件贡献的一个控件。</summary>
/// <param name="Slot">放到哪个位置。</param>
/// <param name="Order">同位置内排序，小的靠前。</param>
/// <param name="CreateView">创建控件；宿主在 UI 线程调用一次并缓存。</param>
public sealed record PluginUiItem(
    PluginUiSlot Slot,
    int Order,
    Func<IPluginContext, FrameworkElement> CreateView);

/// <summary>宿主提供给插件的服务与文档访问。</summary>
public interface IPluginContext
{
    /// <summary>ATTM.exe 所在目录（只读）。</summary>
    string ApplicationDirectory { get; }

    /// <summary>本插件专属可写目录，已创建。</summary>
    string DataDirectory { get; }

    /// <summary>写一行日志。</summary>
    void Log(string message);

    /// <summary>当前编辑器全文。</summary>
    string GetDocumentText();

    /// <summary>替换编辑器全文（会标记未保存）。</summary>
    void SetDocumentText(string text);

    /// <summary>在状态栏显示一条瞬时信息。</summary>
    void SetStatus(string message);

    /// <summary>弹一个提示框；<paramref name="isError"/> 为 true 时用警告图标。</summary>
    void ShowMessage(string message, bool isError = false);

    /// <summary>
    /// 读一个文件并返回字节。沙箱插件必须走这里而不是 <c>System.IO.File</c>，
    /// 宿主会做大小上限与存在性检查。
    /// </summary>
    byte[] ReadFileBytes(string path);
}

/// <summary>主窗口实现，供宿主转发文档操作。插件不直接拿到窗口。</summary>
public interface IPluginDocumentBridge
{
    string GetDocumentText();
    void SetDocumentText(string text);
    void SetStatus(string message);
    void ShowMessage(string message, bool isError);
}

/// <summary>所有插件的入口。</summary>
public interface IAttmPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }

    void Initialize(IPluginContext context);
    void Shutdown();
}

/// <summary>导入型插件：外部文件 → ATTM 文本。</summary>
public interface IImporterPlugin : IAttmPlugin
{
    IReadOnlyList<string> Extensions { get; }
    string FileFilter { get; }
    PluginImportResult Import(string path);
}

/// <summary>导出型插件：当前谱面 → 其它格式。</summary>
public interface IExporterPlugin : IAttmPlugin
{
    IReadOnlyList<string> Extensions { get; }
    string FileFilter { get; }

    /// <summary>把当前文档写到 <paramref name="path"/>；失败抛异常即可。</summary>
    void Export(IPluginContext context, string path);
}

/// <summary>命令型插件：往工具栏加按钮。</summary>
public interface ICommandPlugin : IAttmPlugin
{
    IReadOnlyList<PluginCommand> Commands { get; }
}

/// <summary>界面型插件：往工具栏 / 状态栏 / 设置页插入任意 WPF 控件。</summary>
public interface IUiPlugin : IAttmPlugin
{
    IReadOnlyList<PluginUiItem> UiItems { get; }
}

/// <summary>已扫描到的一条插件记录（成功、失败、禁用都留痕）。</summary>
public sealed class PluginEntry
{
    public PluginEntry(
        IAttmPlugin? plugin,
        string source,
        string sourcePath,
        PluginState state,
        string? error = null,
        IPluginContext? context = null,
        PluginTrust trust = PluginTrust.Sandboxed)
    {
        Plugin = plugin;
        Source = source;
        SourcePath = sourcePath;
        State = state;
        Error = error;
        Context = context;
        Trust = trust;
    }

    /// <summary>装载时采用的信任级别。</summary>
    public PluginTrust Trust { get; }

    /// <summary>被禁用的插件没有实例。</summary>
    public IAttmPlugin? Plugin { get; }

    /// <summary>dll 文件名，也是插件管理器的钥匙。</summary>
    public string Source { get; }

    public string SourcePath { get; }

    public PluginState State { get; }

    public string? Error { get; }

    public IPluginContext? Context { get; }

    public bool IsLoaded => State == PluginState.Loaded && Plugin is not null;

    public string DisplayName => Plugin?.Name ?? Source;
}
