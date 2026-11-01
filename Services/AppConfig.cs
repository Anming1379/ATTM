using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ATTM.Services;

/// <summary>用户配置，保存在 %APPDATA%\ATTM\config.json。</summary>
public sealed class AppConfig
{
    /// <summary>配置版本：2 = 默认字号 18，3 = 去掉自定义主题色、记住窗口位置。</summary>
    public int Version { get; set; }

    public string Theme { get; set; } = "Dark";

    /// <summary>MIDI 输出设备名；空字符串表示自动选择。</summary>
    public string MidiOutputDevice { get; set; } = string.Empty;

    /// <summary>上次保存到的目录；空字符串表示默认的 音乐\ATTM。</summary>
    public string DefaultSaveFolder { get; set; } = string.Empty;

    public double EditorFontSize { get; set; } = 18;

    public bool AutoScrollOnPlay { get; set; } = true;

    /// <summary>被禁用的插件 dll 文件名（插件管理器写入，重启后生效）。</summary>
    public List<string> DisabledPlugins { get; set; } = new();

    /// <summary>用户确认过无沙箱权限的插件，格式 "id@version"。</summary>
    public List<string> ApprovedPlugins { get; set; } = new();

    /// <summary>右侧乐句预览侧边栏是否展开。</summary>
    public bool PhrasePreviewVisible { get; set; }

    // 窗口位置与大小（首次运行没有值时为空，用居中默认）
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public AppTheme Mode => ThemeService.ParseMode(Theme);

    [JsonIgnore]
    public bool IsDark => Mode switch
    {
        AppTheme.Light => false,
        AppTheme.System => !ThemeService.IsSystemLightTheme(),
        _ => true
    };

    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATTM");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>主路径不可写时的退路：放在程序目录，跟着 exe 走。</summary>
    public static string PortableConfigPath => Path.Combine(AppContext.BaseDirectory, "ATTM.config.json");

    /// <summary>最近一次实际读写的配置文件路径。</summary>
    public static string ActiveConfigPath { get; private set; } = ConfigPath;

    /// <summary>最近一次保存失败的原因；保存成功时为 null。</summary>
    public static string? LastSaveError { get; private set; }

    /// <summary>候选配置路径，按优先级排列。</summary>
    private static IEnumerable<string> CandidatePaths()
    {
        yield return ConfigPath;
        yield return PortableConfigPath;
    }

    /// <summary>
    /// 谱库目录：固定是"音乐\ATTM"（系统默认音乐文件夹），不存在就建出来。
    /// 与"上次保存到哪"无关——打开谱库永远回到这里。
    /// </summary>
    public static string LibraryFolder
    {
        get
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(music))
                music = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var folder = Path.Combine(music, "ATTM");

            try { Directory.CreateDirectory(folder); }
            catch { /* 建不出来就交给调用方报错 */ }

            return folder;
        }
    }

    /// <summary>保存目录：上次用过的目录，否则 音乐\ATTM（不存在就建出来）。</summary>
    public static string ResolveSaveFolder(AppConfig config)
    {
        var folder = string.IsNullOrWhiteSpace(config.DefaultSaveFolder)
            ? LibraryFolder
            : config.DefaultSaveFolder;

        try { Directory.CreateDirectory(folder); }
        catch { /* 建不出来就让对话框报错 */ }

        return folder;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig Load()
    {
        try
        {
            var source = CandidatePaths().FirstOrDefault(File.Exists);
            if (source is null) return new AppConfig { Version = 3 };

            var json = File.ReadAllText(source);
            ActiveConfigPath = source;
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();

            if (config.Version < 2 && Math.Abs(config.EditorFontSize - 15) < 0.01)
            {
                // 旧版本默认字号 15 偏小，升级到 18
                config.EditorFontSize = 18;
            }

            if (config.Version < 3)
            {
                config.Version = 3;
                config.Save();
            }

            return config;
        }
        catch
        {
            // 配置损坏时静默回退
            return new AppConfig();
        }
    }

    /// <summary>
    /// 保存配置：先写 %APPDATA%，不行就写到程序目录（便携模式）。
    /// 返回 false 表示两个位置都写不了，原因见 <see cref="LastSaveError"/>。
    /// </summary>
    public bool Save()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        LastSaveError = null;

        foreach (var path in CandidatePaths())
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(path, json);
                ActiveConfigPath = path;
                return true;
            }
            catch (Exception ex)
            {
                LastSaveError = $"{path}：{ex.Message}";
            }
        }

        return false;
    }
}
