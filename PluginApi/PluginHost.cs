using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace ATTM.Plugins;

/// <summary>
/// 插件宿主：扫描目录 → 静态体检 → 每个 dll 一个（可回收的）AssemblyLoadContext →
/// 找到 IAttmPlugin 实现 → 实例化并 Initialize。
///
/// 三条硬规则：
/// 1. 主程序不引用任何具体插件，只认契约；
/// 2. ATTM 自身始终走默认上下文（否则接口类型身份不一致）；
/// 3. 任何插件失败都只留痕，不影响启动与其它插件。
/// </summary>
public sealed class PluginHost : IDisposable
{
    private const long MaxReadBytesForSandbox = 64L * 1024 * 1024;

    private readonly List<PluginEntry> _entries = new();
    private readonly List<PluginLoadContext> _contexts = new();
    private readonly IReadOnlyList<string> _directories;
    private readonly IReadOnlyCollection<string> _disabledFiles;
    private readonly HashSet<string> _approvals;
    private readonly string _dataRoot;

    public PluginHost(
        string applicationDirectory,
        string dataRoot,
        IEnumerable<string> pluginDirectories,
        IPluginDocumentBridge? bridge = null,
        IEnumerable<string>? disabledFiles = null,
        IEnumerable<string>? approvedPlugins = null)
    {
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
        _dataRoot = Path.GetFullPath(dataRoot);
        _directories = pluginDirectories.Select(Path.GetFullPath).ToList();
        _disabledFiles = ToFileNameSet(disabledFiles);
        _approvals = new HashSet<string>(approvedPlugins ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Bridge = bridge;
    }

    public string ApplicationDirectory { get; }

    /// <summary>扫描过的插件目录（插件管理器的「打开目录」用）。</summary>
    public IReadOnlyList<string> Directories => _directories;

    /// <summary>文档桥；没有界面时可为 null（对应操作变成空操作）。</summary>
    public IPluginDocumentBridge? Bridge { get; }

    /// <summary>全部条目（含失败、禁用、待授权）。</summary>
    public IReadOnlyList<PluginEntry> Entries => _entries;

    public IReadOnlyList<PluginEntry> Importers => WithCapability<IImporterPlugin>();

    public IReadOnlyList<PluginEntry> Exporters => WithCapability<IExporterPlugin>();

    /// <summary>等待用户确认无沙箱权限的插件。</summary>
    public IReadOnlyList<PluginEntry> PendingApprovals
        => _entries.Where(static e => e.State == PluginState.PendingApproval).ToList();

    /// <summary>命令：条目 + 命令本身。</summary>
    public IReadOnlyList<(PluginEntry Entry, PluginCommand Command)> Commands
        => _entries
            .Where(static e => e.IsLoaded && e.Plugin is ICommandPlugin)
            .SelectMany(static e => ((ICommandPlugin)e.Plugin!).Commands.Select(cmd => (e, cmd)))
            .ToList();

    /// <summary>界面贡献：条目 + 控件描述，按 Order 排序。</summary>
    public IReadOnlyList<(PluginEntry Entry, PluginUiItem Item)> UiItems
        => _entries
            .Where(static e => e.IsLoaded && e.Plugin is IUiPlugin)
            .SelectMany(static e => ((IUiPlugin)e.Plugin!).UiItems.Select(item => (e, item)))
            .OrderBy(static pair => pair.item.Order)
            .ToList();

    /// <summary>上一次 Reload 是否把所有插件上下文都真正卸载了。</summary>
    public bool LastUnloadClean { get; private set; } = true;

    /// <summary>扫描全部目录并装载；被禁用的 dll 直接跳过。</summary>
    public void LoadAll()
    {
        foreach (var directory in _directories)
        {
            if (!Directory.Exists(directory)) continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*.dll").OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(path);

                if (_disabledFiles.Contains(fileName))
                {
                    _entries.Add(new PluginEntry(null, fileName, path, PluginState.Disabled));
                    continue;
                }

                LoadAssembly(path, fileName);
            }
        }
    }

    /// <summary>
    /// 热重载：关停 → 卸载上下文 → 重新扫描装载。
    /// 返回是否「所有上下文都已真正回收」（false 表示部分插件仍被引用，建议重启）。
    /// </summary>
    public bool Reload()
    {
        ShutdownAll();
        _entries.Clear();

        var contexts = _contexts.ToList();
        _contexts.Clear();

        var weak = contexts.Select(static c => new WeakReference(c)).ToList();

        UnloadContexts(contexts);

        // 丢掉强引用，否则 GC 永远不会回收这些上下文
        contexts.Clear();

        // 收集必须发生在**另一个栈帧**里：本方法的局部变量（foreach 里的 context）
        // 会被保守栈扫描当成存活引用，直接在这里 GC 一定收不掉。
        LastUnloadClean = WaitForUnload(weak);

        if (!LastUnloadClean)
            Log("部分插件上下文未能卸载（可能被 WPF 对象持有引用），建议重启程序");

        LoadAll();
        return LastUnloadClean;
    }

    /// <summary>卸载一批上下文。同样放在独立帧：Unload 之后宿主不能再持有强引用。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void UnloadContexts(IEnumerable<AssemblyLoadContext> contexts)
    {
        foreach (var context in contexts)
        {
            try
            {
                context.Unload();
            }
            catch
            {
                // 卸载失败只能等重启
            }
        }
    }

    /// <summary>反复回收直到所有上下文都被释放；放在独立帧里避免栈引用拖住对象。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool WaitForUnload(IReadOnlyList<WeakReference> weak)
    {
        for (var round = 0; round < 10; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (weak.All(static w => !w.IsAlive)) return true;
        }

        return false;
    }

    /// <summary>手动登记一个插件实例（测试与内置场景用）。重复 Id 会被忽略。</summary>
    public PluginEntry Register(IAttmPlugin plugin, string source = "<in-memory>")
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var existing = _entries.FirstOrDefault(e => e.IsLoaded && e.Plugin!.Id == plugin.Id);
        if (existing is not null) return existing;

        if (plugin is ITrustRequestPlugin trust && !IsApproved(plugin.Id, plugin.Version))
        {
            var reason = string.IsNullOrWhiteSpace(trust.TrustJustification)
                ? "申请无沙箱权限（未说明理由）"
                : trust.TrustJustification;

            var pending = new PluginEntry(plugin, source, source, PluginState.PendingApproval, reason);
            _entries.Add(pending);
            return pending;
        }

        var entry = Initialize(plugin, source, source,
            plugin is ITrustRequestPlugin ? PluginTrust.Unrestricted : PluginTrust.Sandboxed);
        _entries.Add(entry);
        return entry;
    }

    public void ShutdownAll()
    {
        foreach (var entry in _entries)
        {
            if (entry.Plugin is null) continue;

            try
            {
                entry.Plugin.Shutdown();
            }
            catch (Exception ex)
            {
                Log($"关闭插件 {entry.Plugin.Id} 时出错：{ex.Message}");
            }
        }
    }

    public void Log(string message) => Debug.WriteLine("[plugin] " + message);

    public void Dispose() => ShutdownAll();

    // ---------------------------------------------------------------- 内部

    private static IReadOnlyCollection<string> ToFileNameSet(IEnumerable<string>? names)
        => (names ?? Array.Empty<string>())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<PluginEntry> WithCapability<T>() where T : class
        => _entries.Where(static e => e.IsLoaded && e.Plugin is T).ToList();

    /// <summary>运行时追加一条授权（用户刚点了「是」）；随后调用 <see cref="Reload"/> 即可生效。</summary>
    public void Approve(string pluginId, string version) => _approvals.Add(pluginId + "@" + version);

    private bool IsApproved(string id, string version) => _approvals.Contains(id + "@" + version);

    private string DataDirectoryFor(string pluginId)
    {
        var path = Path.Combine(_dataRoot, pluginId);

        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // 建不出来就把原路径给插件
        }

        return path;
    }

    private void LoadAssembly(string path, string fileName)
    {
        IReadOnlyList<string> violations;

        try
        {
            violations = PluginSecurityScanner.Scan(path);
        }
        catch (Exception ex)
        {
            _entries.Add(new PluginEntry(null, fileName, path, PluginState.Failed, "安全扫描失败：" + ex.Message));
            return;
        }

        try
        {
            var context = new PluginLoadContext(path);
            _contexts.Add(context);

            var assembly = context.LoadFromAssemblyPath(path);

            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || !typeof(IAttmPlugin).IsAssignableFrom(type)) continue;

                IAttmPlugin? instance;
                try
                {
                    instance = (IAttmPlugin?)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    _entries.Add(new PluginEntry(null, fileName, path, PluginState.Failed, $"创建 {type.Name} 失败：{ex.Message}"));
                    continue;
                }

                if (instance is null) continue;

                var needsTrust = instance is ITrustRequestPlugin;

                if (needsTrust && !IsApproved(instance.Id, instance.Version))
                {
                    var reason = ((ITrustRequestPlugin)instance).TrustJustification;
                    _entries.Add(new PluginEntry(instance, fileName, path, PluginState.PendingApproval,
                        string.IsNullOrWhiteSpace(reason) ? "申请无沙箱权限（未说明理由）" : reason));
                    continue;
                }

                if (!needsTrust && violations.Count > 0)
                {
                    _entries.Add(new PluginEntry(instance, fileName, path, PluginState.Failed,
                        "沙箱禁止的 API：" + PluginSecurityScanner.Describe(violations)));
                    continue;
                }

                var trust = needsTrust ? PluginTrust.Unrestricted : PluginTrust.Sandboxed;
                _entries.Add(Initialize(instance, fileName, path, trust));
            }
        }
        catch (BadImageFormatException)
        {
            // 插件目录里混着原生 dll，跳过
        }
        catch (Exception ex)
        {
            _entries.Add(new PluginEntry(null, fileName, path, PluginState.Failed, $"装载失败：{ex.Message}"));
        }
    }

    private PluginEntry Initialize(IAttmPlugin plugin, string source, string sourcePath, PluginTrust trust)
    {
        var context = new PluginContext(this, plugin, trust);

        try
        {
            plugin.Initialize(context);
            Log($"已装载 {plugin.Id} v{plugin.Version}（{source}，{trust}）");
            return new PluginEntry(plugin, source, sourcePath, PluginState.Loaded, null, context, trust);
        }
        catch (Exception ex)
        {
            Log($"初始化 {plugin.Id} 失败：{ex.Message}");
            return new PluginEntry(plugin, source, sourcePath, PluginState.Failed, $"初始化失败：{ex.Message}", context, trust);
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static t => t is not null)!;
        }
    }

    /// <summary>单个插件看到的世界：数据目录隔离、日志带前缀、文档操作转发给主窗口。</summary>
    private sealed class PluginContext : IPluginContext
    {
        private readonly PluginHost _host;
        private readonly IAttmPlugin _plugin;
        private readonly PluginTrust _trust;

        public PluginContext(PluginHost host, IAttmPlugin plugin, PluginTrust trust)
        {
            _host = host;
            _plugin = plugin;
            _trust = trust;
        }

        public string ApplicationDirectory => _host.ApplicationDirectory;

        public string DataDirectory => _host.DataDirectoryFor(_plugin.Id);

        public void Log(string message) => _host.Log($"[{_plugin.Id}] {message}");

        public string GetDocumentText() => _host.Bridge?.GetDocumentText() ?? string.Empty;

        public void SetDocumentText(string text) => _host.Bridge?.SetDocumentText(text);

        public void SetStatus(string message) => _host.Bridge?.SetStatus(message);

        public void ShowMessage(string message, bool isError = false) => _host.Bridge?.ShowMessage(message, isError);

        public byte[] ReadFileBytes(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("文件不存在：" + path);
            if (info.Length > MaxReadBytesForSandbox) throw new IOException("文件超过沙箱上限 64 MB");

            Log($"读取 {path}（{info.Length} 字节，{_trust}）");
            return File.ReadAllBytes(path);
        }
    }

    /// <summary>插件专用装载上下文：ATTM 自身走宿主，其余依赖从插件目录解析；可回收以支持热重载。</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath) : base(isCollectible: true)
            => _resolver = new AssemblyDependencyResolver(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 返回 null = 回落到默认上下文，即复用宿主已加载的 ATTM（保住接口类型身份）
            if (string.Equals(assemblyName.Name, "ATTM", StringComparison.OrdinalIgnoreCase)) return null;

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
