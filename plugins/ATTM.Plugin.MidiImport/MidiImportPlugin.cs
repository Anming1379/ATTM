using System.IO;
using ATTM.Plugins;

namespace ATTM.Plugin.MidiImport;

/// <summary>
/// MIDI 导入插件：把 .mid / .midi 转成 ATTM 简谱文本（近似谱）。
/// 转换细节见 <see cref="MidiToAttm"/> 与 docs/11-MIDI导入.md。
/// </summary>
public sealed class MidiImportPlugin : IImporterPlugin
{
    private IPluginContext? _context;

    public string Id => "attm.midi-import";

    public string Name => "MIDI 导入";

    public string Version => "1.0.0";

    public string Description => "把标准 MIDI 文件自动转换成 ATTM 简谱文本（节奏会量化，多声部会合并）";

    public IReadOnlyList<string> Extensions { get; } = new[] { ".mid", ".midi" };

    public string FileFilter => "MIDI 文件 (*.mid;*.midi)|*.mid;*.midi";

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.Log($"{Name} 已就绪，数据目录 {context.DataDirectory}");
    }

    public void Shutdown() => _context = null;

    public PluginImportResult Import(string path)
    {
        // 沙箱插件不碰 System.IO.File：字节由宿主读好递进来
        if (_context is null) throw new InvalidOperationException("插件尚未初始化");

        var document = SmfReader.Read(_context.ReadFileBytes(path));
        var result = MidiToAttm.Convert(document, Path.GetFileName(path));

        return new PluginImportResult(
            result.Text,
            Path.GetFileNameWithoutExtension(path) + ".attm",
            result.Warnings);
    }
}
