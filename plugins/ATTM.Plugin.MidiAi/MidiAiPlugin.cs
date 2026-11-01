using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ATTM.Core.Syntax;
using ATTM.Plugins;

namespace ATTM.Plugin.MidiAi;

/// <summary>
/// 智能 MIDI 整理插件。
///
/// 推理优先跑在插件进程内（LLamaSharp + 本地 GGUF）；没配本地模型时用用户自定义的 API。
/// 插件自带模型下载器（多线程分块），也允许用户自己加任意 OpenAI 兼容 / Ollama 接口。
/// </summary>
public sealed class MidiAiPlugin : IImporterPlugin, ITrustRequestPlugin, IUiPlugin
{
    private readonly LocalModel _local = new();

    private IPluginContext? _context;
    private ChatSettings _settings = ChatSettings.Default;

    public string Id => "attm.midi-ai";
    public string Name => "MIDI AI 整理";
    public string Version => "1.2.0";
    public string Description => "进程内小模型 + 自定义 API + 多线程模型下载，把 MIDI 整理成 ATTM 谱面";

    public string TrustJustification =>
        "需要读取你指定的 GGUF 模型、下载模型文件，或访问你自己填写的模型 API 地址";

    public IReadOnlyList<string> Extensions { get; } = new[] { ".mid", ".midi" };
    public string FileFilter => "MIDI（AI 整理，需本机模型）|*.mid;*.midi";

    public IReadOnlyList<PluginUiItem> UiItems { get; } = new[]
    {
        new PluginUiItem(PluginUiSlot.SettingsPanel, 50, static context => CreateSettingsView(context))
    };

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _settings = LoadSettings(context);
        LocalModel.PrepareNative(context.ApplicationDirectory);
        context.Log("AI 整理就绪：" + DescribeModel(_settings));
    }

    public void Shutdown()
    {
        _local.Dispose();
        _context = null;
    }

    public PluginImportResult Import(string path)
    {
        if (_context is null) throw new InvalidOperationException("插件尚未初始化");

        var bytes = _context.ReadFileBytes(path);
        var score = MiniSmf.Parse(bytes);
        var prompt = Prompt.Build(score);

        var warnings = new List<string>();
        var text = AskModel(prompt, score, warnings);

        if (score.BeatsPerMeasure != 4 || score.BeatUnit != 4)
            warnings.Add("原曲拍号 " + score.BeatsPerMeasure + "/" + score.BeatUnit + "，ATTM 只支持 4/4 与 3/4");

        return new PluginImportResult(text, Path.GetFileNameWithoutExtension(path) + ".attm", warnings);
    }

    // ---------------------------------------------------------------- 模型调用

    private string AskModel(string prompt, MiniScore score, List<string> warnings)
    {
        var run = CreateRunner();
        var error = string.Empty;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var user = attempt == 0 ? prompt
                : prompt + "\n\n上一次的输出无法被解析，错误如下，请修正后重新输出：\n" + error;

            var text = EnsureDirectives(CleanResponse(run(user)), score);
            var parsed = AttmParser.Parse(text);

            if (parsed.CanPlay && HasBody(text))
            {
                if (attempt > 0) warnings.Add("模型第一次输出不合法，已自动重试一次");
                return text;
            }

            error = parsed.CanPlay
                ? "模型只输出了指令，没有写出任何小节。"
                : string.Join("\n", parsed.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(5)
                    .Select(d => "第 " + d.Line + " 行第 " + d.Column + " 列 [E" + d.CodeNumber + "] " + d.Message));
        }

        throw new InvalidOperationException("模型连续两次都没写出合法谱面：\n" + error);
    }

    private Func<string, string> CreateRunner()
    {
        var settings = _settings;

        if (!string.IsNullOrWhiteSpace(settings.ModelPath))
        {
            if (!File.Exists(settings.ModelPath))
                throw new FileNotFoundException("找不到模型文件：" + settings.ModelPath);

            try
            {
                _local.EnsureLoaded(settings.ModelPath, 4096);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("加载本地模型失败：" + ex.Message, ex);
            }

            return user => _local.Complete(Prompt.System, user, settings.MaxTokens, CancellationToken.None);
        }

        var profile = settings.Active
            ?? throw new InvalidOperationException("还没有配置模型：请指定 .gguf 文件，或添加一条 API。");

        return user => ChatClient.Complete(settings, profile, Prompt.System, user, CancellationToken.None);
    }

    private static string DescribeModel(ChatSettings settings)
        => !string.IsNullOrWhiteSpace(settings.ModelPath)
            ? Path.GetFileName(settings.ModelPath)
            : (settings.Active?.Display ?? "(未配置)");

    // ---------------------------------------------------------------- 清洗

    private static string CleanResponse(string raw)
    {
        var text = raw.Trim();

        if (text.StartsWith("\u0060\u0060\u0060", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            if (firstBreak > 0) text = text[(firstBreak + 1)..];

            var end = text.LastIndexOf("\u0060\u0060\u0060", StringComparison.Ordinal);
            if (end >= 0) text = text[..end];
        }

        var kept = text.Split('\n').Where(IsScoreLine).ToList();
        return string.Join("\n", kept).Trim();
    }

    private static bool HasBody(string text)
        => text.Split('\n').Any(line =>
        {
            var t = line.Trim();
            return t.Length > 0
                   && !t.StartsWith("#", StringComparison.Ordinal)
                   && !System.Text.RegularExpressions.Regex.IsMatch(t, @"^(bpm|Vel|1)\s*=");
        });

    private static bool IsScoreLine(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return true;
        if (t.StartsWith("#", StringComparison.Ordinal)) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(bpm|Vel|1)\s*=")) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(t, @"^[0-9+\-#(),$% ]+$");
    }

    private static string EnsureDirectives(string text, MiniScore score)
    {
        var builder = new StringBuilder();
        var beats = score.BeatsPerMeasure is 3 or 4 && score.BeatUnit == 4 ? score.BeatsPerMeasure : 4;

        if (!text.Contains("bpm", StringComparison.OrdinalIgnoreCase))
            builder.Append("bpm = ").Append(Math.Clamp(Math.Round(score.Bpm, 2), 1, 1000)
                .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');

        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"(?m)^\s*1\s*="))
            builder.Append("1 = ").Append(KeyName(score)).Append(' ').Append(beats).Append("/4\n");

        if (!text.Contains("Vel", StringComparison.OrdinalIgnoreCase))
            builder.Append("Vel = 90 +-4\n");

        if (builder.Length > 0) builder.Append('\n');
        return builder.Append(text).ToString();
    }

    private static string KeyName(MiniScore score)
    {
        string[] majors = { "C", "G", "D", "A", "E", "B", "F#", "C#" };
        string[] sharps = { "C", "F", "A#", "D#", "G#", "C#", "F#", "B" };

        if (score.KeyFifths is not { } fifths) return "C";
        var index = Math.Abs(fifths);
        if (index > 7) return "C";
        return fifths >= 0 ? majors[index] : sharps[index];
    }

    // ---------------------------------------------------------------- 设置存取

    private static ChatSettings LoadSettings(IPluginContext context)
    {
        foreach (var path in SettingsPaths(context))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var loaded = JsonSerializer.Deserialize<ChatSettings>(File.ReadAllText(path));
                if (loaded is not null) return loaded.Normalize();
            }
            catch (Exception ex)
            {
                context.Log("读取设置失败：" + ex.Message);
            }
        }

        return ChatSettings.Default;
    }

    private static bool SaveSettings(IPluginContext context, ChatSettings settings, out string? error)
    {
        error = null;

        foreach (var path in SettingsPaths(context))
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonSerializer.Serialize(settings));
                return true;
            }
            catch (Exception ex)
            {
                error = path + "：" + ex.Message;
            }
        }

        return false;
    }

    private static IEnumerable<string> SettingsPaths(IPluginContext context)
    {
        yield return Path.Combine(context.DataDirectory, "ai-settings.json");
        yield return Path.Combine(context.ApplicationDirectory, "ATTM.Plugin.MidiAi.settings.json");
    }

    // ---------------------------------------------------------------- 设置界面

    private static FrameworkElement CreateSettingsView(IPluginContext context)
    {
        var current = LoadSettings(context);
        var root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        // ---- 本地模型 ----
        root.Children.Add(Header("1. 本地模型（进程内推理）"));

        var modelPath = new TextBox { Text = current.ModelPath };
        var browse = new Button { Content = "浏览…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };

        var pathRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(modelPath);

        root.Children.Add(Caption("模型文件（.gguf，留空则用下面的 API）"));
        root.Children.Add(pathRow);

        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 GGUF 模型",
                Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true) modelPath.Text = dialog.FileName;
        };

        // ---- 下载 ----
        root.Children.Add(Header("2. 下载模型（多线程分块）"));

        var url = new TextBox { Text = "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf" };
        var concurrency = new TextBox { Text = current.DownloadConcurrency.ToString(), Width = 60 };
        var download = new Button { Content = "开始下载", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
        var bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 8, 0, 0), Minimum = 0, Maximum = 100 };
        var downloadStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.8, FontSize = 11.5 };

        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(concurrency, Dock.Right);
        DockPanel.SetDock(download, Dock.Right);
        row.Children.Add(concurrency);
        row.Children.Add(download);
        row.Children.Add(url);

        root.Children.Add(Caption("GGUF 直链（HuggingFace / 镜像 / 任意 HTTP），右侧是并发数"));
        root.Children.Add(row);
        root.Children.Add(bar);
        root.Children.Add(downloadStatus);

        download.Click += async (_, _) =>
        {
            var target = modelPath.Text.Trim();

            if (string.IsNullOrWhiteSpace(target) || !target.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(context.ApplicationDirectory, "models", FileNameOf(url.Text));

            download.IsEnabled = false;

            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    bar.Value = p.Percent;
                    downloadStatus.Text = p.Percent.ToString("0.0") + "%  " +
                        (p.Received / 1048576.0).ToString("0.0") + " / " + (p.Total / 1048576.0).ToString("0.0") + " MB";
                });

                await ModelDownloader.DownloadAsync(
                    url.Text.Trim(),
                    target,
                    int.TryParse(concurrency.Text.Trim(), out var c) ? c : current.DownloadConcurrency,
                    progress,
                    CancellationToken.None);

                bar.Value = 100;
                modelPath.Text = target;
                downloadStatus.Text = "下载完成：" + target;
            }
            catch (Exception ex)
            {
                downloadStatus.Text = "下载失败：" + ex.Message;
            }
            finally
            {
                download.IsEnabled = true;
            }
        };

        // ---- API ----
        root.Children.Add(Header("3. 自定义 API（备用通道）"));

        var profiles = new List<ApiProfile>(current.Profiles);
        var selector = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var profile in profiles) selector.Items.Add(profile.Display);
        selector.SelectedIndex = Math.Max(0, profiles.FindIndex(p => p.Name == current.ActiveProfile));

        var name = new TextBox();
        var endpoint = new TextBox();
        var model = new TextBox();
        var apiKey = new System.Windows.Controls.PasswordBox();

        void SyncFromSelector()
        {
            var index = selector.SelectedIndex;
            if (index < 0 || index >= profiles.Count) return;

            var p = profiles[index];
            name.Text = p.Name;
            endpoint.Text = p.Endpoint;
            model.Text = p.Model;
            apiKey.Password = p.ApiKey;
        }

        selector.SelectionChanged += (_, _) => SyncFromSelector();
        SyncFromSelector();

        root.Children.Add(Caption("已有接口"));
        root.Children.Add(selector);
        root.Children.Add(Field("名称", name));
        root.Children.Add(Field("地址（Ollama /api/chat 或 OpenAI /v1/chat/completions）", endpoint));
        root.Children.Add(Field("模型名", model));
        root.Children.Add(Field("API Key（可空，填了会加 Authorization: Bearer）", apiKey));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var add = new Button { Content = "新增", Padding = new Thickness(10, 3, 10, 3) };
        var update = new Button { Content = "更新当前", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        var remove = new Button { Content = "删除当前", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(update);
        buttons.Children.Add(remove);
        root.Children.Add(buttons);

        var timeout = new TextBox { Text = current.TimeoutSeconds.ToString() };
        var maxTokens = new TextBox { Text = current.MaxTokens.ToString() };

        root.Children.Add(Field("超时秒数", timeout));
        root.Children.Add(Field("单次最大生成 token", maxTokens));

        var status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        var save = new Button
        {
            Content = "保存设置",
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        ApiProfile ReadEditor() => new(
            string.IsNullOrWhiteSpace(name.Text) ? "自定义" : name.Text.Trim(),
            endpoint.Text.Trim(),
            model.Text.Trim(),
            apiKey.Password.Trim());

        void RefreshSelector(string? selectName = null)
        {
            selector.Items.Clear();
            foreach (var p in profiles) selector.Items.Add(p.Display);

            var index = selectName is null ? 0 : profiles.FindIndex(p => p.Name == selectName);
            selector.SelectedIndex = Math.Max(0, index);
            SyncFromSelector();
        }

        add.Click += (_, _) =>
        {
            var profile = ReadEditor();
            if (string.IsNullOrWhiteSpace(profile.Endpoint)) { status.Text = "地址不能为空"; return; }

            profiles.Add(profile);
            RefreshSelector(profile.Name);
            status.Text = "已加入列表（记得点保存设置）";
        };

        update.Click += (_, _) =>
        {
            var index = selector.SelectedIndex;
            if (index < 0) { status.Text = "先选一条接口"; return; }

            profiles[index] = ReadEditor();
            RefreshSelector(profiles[index].Name);
            status.Text = "已更新（记得点保存设置）";
        };

        remove.Click += (_, _) =>
        {
            var index = selector.SelectedIndex;
            if (index < 0 || profiles.Count <= 1) { status.Text = "至少保留一条接口"; return; }

            profiles.RemoveAt(index);
            RefreshSelector();
            status.Text = "已删除（记得点保存设置）";
        };

        save.Click += (_, _) =>
        {
            var active = selector.SelectedIndex >= 0 && selector.SelectedIndex < profiles.Count
                ? profiles[selector.SelectedIndex].Name
                : profiles[0].Name;

            var settings = new ChatSettings(
                modelPath.Text.Trim(),
                int.TryParse(timeout.Text.Trim(), out var seconds) ? seconds : 180,
                int.TryParse(maxTokens.Text.Trim(), out var tokens) ? tokens : 512,
                int.TryParse(concurrency.Text.Trim(), out var conc) ? conc : 8,
                profiles,
                active).Normalize();

            if (string.IsNullOrWhiteSpace(settings.ModelPath) && settings.Profiles.All(p => string.IsNullOrWhiteSpace(p.Endpoint)))
            {
                status.Text = "模型文件与 API 至少配一个。";
                return;
            }

            status.Text = SaveSettings(context, settings, out var error)
                ? "已保存：模型=" + DescribeModel(settings) + "，导入时生效。"
                : "保存失败：" + error;
        };

        root.Children.Add(save);
        root.Children.Add(status);

        return root;
    }

    private static string FileNameOf(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? "model.gguf" : name;
        }
        catch
        {
            return "model.gguf";
        }
    }

    private static FrameworkElement Header(string text)
        => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) };

    private static FrameworkElement Caption(string text)
        => new TextBlock { Text = text, Opacity = 0.75, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private static FrameworkElement Field(string label, FrameworkElement input)
    {
        switch (input)
        {
            case TextBox box:
                box.Margin = new Thickness(0, 4, 0, 0);
                box.Padding = new Thickness(6, 4, 6, 4);
                break;
            case System.Windows.Controls.PasswordBox password:
                password.Margin = new Thickness(0, 4, 0, 0);
                password.Padding = new Thickness(6, 4, 6, 4);
                break;
        }

        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(new TextBlock { Text = label, Opacity = 0.75, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(input);
        return stack;
    }
}
