using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ATTM.Core.Midi;
using ATTM.Plugins;
using ATTM.Core.Playback;
using ATTM.Core.Syntax;
using ATTM.Services;
using ATTM.Views;
using Microsoft.Win32;

namespace ATTM;

public partial class MainWindow
{
    // ------------------------------------------------------------ 文件

    private static bool IsScoreFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".attm", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>找出能处理该文件的导入插件（按扩展名匹配）。</summary>
    private IImporterPlugin? FindImporter(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return null;

        var candidates = new List<PluginEntry>();

        foreach (var entry in _plugins.Importers)
        {
            if (entry.Plugin is not IImporterPlugin importer) continue;
            if (importer.Extensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase))) candidates.Add(entry);
        }

        if (candidates.Count == 0) return null;

        // 多个插件认领同一扩展名时：拖放/命令行/粘贴走「沙箱插件」这个安全默认值，
        // 想用需要联网或模型的插件，请走导入对话框（按过滤器选择）。
        var preferred = candidates.FirstOrDefault(e => e.Trust == PluginTrust.Sandboxed) ?? candidates[0];
        return (IImporterPlugin)preferred.Plugin!;
    }

    /// <summary>能直接打开的文件：谱面文本，或任一导入插件认得的格式。</summary>
    private bool IsOpenableFile(string path) => IsScoreFile(path) || FindImporter(path) is not null;

    /// <summary>按扩展名分流：有插件认领就导入，否则按文本打开。</summary>
    private bool OpenPath(string path)
    {
        var importer = FindImporter(path);
        return importer is null ? LoadFile(path) : ImportWith(importer, path);
    }

    /// <summary>
    /// 用插件把外部文件转换成 ATTM 文本。
    /// 结果不绑定原文件路径——保存时重新选位置，避免和源文件同名互相覆盖。
    /// </summary>
    private bool ImportWith(IImporterPlugin importer, string path)
    {
        PluginImportResult result;

        try
        {
            result = importer.Import(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{importer.Name} 导入失败：{ex.Message}", "ATTM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            MessageBox.Show(this, $"{importer.Name} 没有产出任何内容。", "ATTM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _suppressDirty = true;
        Editor.Text = result.Text;
        _suppressDirty = false;

        _filePath = null;
        _fileName = result.SuggestedFileName ?? Path.GetFileNameWithoutExtension(path) + ".attm";
        _isDirty = true;

        _player.Stop();
        Reparse();
        Editor.FocusEditor();

        if (result.WarningList.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", result.WarningList), importer.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        return true;
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
        _player.Stop();

        Reparse();
        Editor.FocusEditor();

        // 内容与模板相同的时候界面看不出变化，给一条明确反馈
        ShowStatusMessage("已新建文档");
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
            SafeRun("打开谱库", OpenLibraryFolder);
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.N)
        {
            SafeRun("新建", NewDocument);
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.S)
        {
            SaveDocument(forceDialog: shift);
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.I)
        {
            PromptImportMidi();
            e.Handled = true;
            return;
        }

        // Ctrl+V 粘进来的是文件而不是文本：直接打开它
        if (control && !shift && e.Key == Key.V && Clipboard.ContainsFileDropList())
        {
            var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsOpenableFile);
            if (file is not null)
            {
                if (ConfirmDiscard()) OpenPath(file);
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

        if (ConfirmDiscard())
            OpenPath(file);

        e.Handled = true;
    }

    private string? TryGetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;

        return files.FirstOrDefault(IsOpenableFile);
    }
}
