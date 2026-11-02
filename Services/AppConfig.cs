using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ATTM.Services;

/// <summary>用户配置，保存在 %APPDATA%\ATTM\config.json。</summary>
public sealed class AppConfig
{
    /// <summary>配置版本：2 = 默认字号 18，3 = 去掉自定义主题色、记住窗口位置，4 = 乐句预览三态。</summary>
    public int Version { get; set; }

    public string Theme { get; set; } = "Dark";

    /// <summary>MIDI 输出设备名；空字符串表示自动选择。</summary>
    public string MidiOutputDevice { get; set; } = string.Empty;

    /// <summary>上次保存到的目录；空字符串表示默认的 音乐\ATTM。</summary>
    public string DefaultSaveFolder { get; set; } = string.Empty;

    /// <summary>上次导入 MIDI 的目录。</summary>
    public string LastMidiFolder { get; set; } = string.Empty;

    public double EditorFontSize { get; set; } = 18;

    public bool AutoScrollOnPlay { get; set; } = true;

    /// <summary>钢琴键盘的最低八度（C4 = 4）。</summary>
    public int PianoBaseOctave { get; set; } = 4;

    /// <summary>乐句预览的状态：Hidden / Panel / Full。空字符串 = 还没写过（看旧字段）。</summary>
    public string PhrasePreviewMode { get; set; } = string.Empty;

    /// <summary>
    /// 旧版字段：true = 半屏侧边栏。留着是为了读得懂老配置（v3 之前只记了这一个 bool）。
    /// 新代码请用 <see cref="PhrasePreviewModeName"/>。
    /// </summary>
    public bool PhrasePreviewVisible { get; set; }

    // 窗口位置与大小（首次运行没有值时为空，用居中默认）
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public AppTheme Mode => ThemeService.ParseMode(Theme);

    /// <summary>乐句预览状态（兼容只有旧 bool 字段的老配置）。</summary>
    [JsonIgnore]
    public string PhrasePreviewModeName => string.IsNullOrWhiteSpace(PhrasePreviewMode)
        ? (PhrasePreviewVisible ? "Panel" : "Hidden")
        : PhrasePreviewMode;

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
            if (!File.Exists(ConfigPath)) return new AppConfig { Version = 4 };

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();

            if (config.Version < 2 && Math.Abs(config.EditorFontSize - 15) < 0.01)
            {
                // 旧版本默认字号 15 偏小，升级到 18
                config.EditorFontSize = 18;
            }

            if (config.Version < 4)
            {
                // 老配置只有"展开/收起"，先把它翻译成新字段，再让后续版本继续跑
                config.Version = 4;
                config.PhrasePreviewMode = config.PhrasePreviewModeName;
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 无写入权限时忽略
        }
    }
}
