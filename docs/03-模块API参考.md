# 03 · 模块 API 参考

> 按命名空间组织，列出**公开成员**及其职责。签名与源码一致，可作为调用速查表。

目录：Core.Syntax · Core.Midi · Core.Playback · Editing · Services · Views · ATTM 主程序

---

## 1. `ATTM.Core.Syntax`

### 1.1 `AttmLexer`（静态类）

```csharp
public static List<Token> Lex(string text)
```

把源文本切成 token 序列。**不做语法判断**，只做字符级切分（规则见《02-语法参考》第 2 节）。null 视为空串。

### 1.2 `AttmParser`（静态类）

```csharp
public static ParseResult Parse(string text)
```

**纯函数**：输入文本，输出乐谱 + 诊断 + token + 行偏移。同样输入永远得到同样输出（力度抖动不在这里做，解析结果确定）。

### 1.3 `ParseResult`

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Score` | `Score?` | 仅当 bpm / 调式 / 拍号齐全时非空（可伴随其它错误） |
| `Diagnostics` | `IReadOnlyList<Diagnostic>` | 已按偏移与码值排序 |
| `Tokens` | `IReadOnlyList<Token>` | 供语法高亮复用，含空白与注释 |
| `LineStarts` | `IReadOnlyList<int>` | 每行首字符偏移（0 起） |
| `MeasureStartLines` | `IReadOnlyList<int>` | 每个小节首行行号（0 起） |
| `GetLineColumn(int offset)` | `(int Line, int Column)` | 二分查找，1 起 |
| `GetMeasureIndex(int offset)` | `int` | 偏移 → 小节序号（0 起） |
| `HasErrors` / `ErrorCount` / `CanPlay` | bool / int / bool | UI 状态 |

### 1.4 `Score`

| 成员 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `Bpm` | double | 120 | 每分钟四分音符数 |
| `KeyRoot` | char | 'C' | 调式主音（已大写） |
| `KeySharp` | bool | false | 调式是否带升号（1 = #C） |
| `BeatsPerMeasure` / `BeatUnit` | int | 4 / 4 | 分子 / 分母 |
| `VelocityBase` | int | 90 | 力度基准，构造时 clamp 到 1–127 |
| `VelocityJitter` | int | 0 | 力度随机抖动半径 |
| `TicksPerQuarter` | int | 480 | PPQ |
| `MeasureCount` | int | — | 小节总数 |
| `Notes` | `IReadOnlyList<NoteEvent>` | 空 | 已过 `NoteMerger` |
| `MeasureTicks` | int | 计算 | 480 * 4 * BeatsPerMeasure / BeatUnit |
| `TotalTicks` | long | 计算 | MeasureCount * MeasureTicks |
| `SecondsPerTick` | double | 计算 | 60 / (Bpm * 480) |
| `Duration` | TimeSpan | 计算 | 总时长 |
| `KeyName` | string | 计算 | 如 "C 大调"、"F# 大调" |
| `TimeSignatureName` | string | 计算 | 如 "4/4" |
| `TicksToMilliseconds(long)` | long | — | 四舍五入到毫秒 |
| `GetMeasureStartTicks(int)` | long | — | 第 N 小节起点的绝对 tick（越界 clamp） |
| `GetMeasureStartTime(int)` | TimeSpan | — | 第 N 小节起点的时间 |
| `GetMeasureIndex(TimeSpan)` | int | — | 播放位置 → 小节序号（越界 clamp） |

### 1.5 `NoteEvent`

```csharp
public sealed record NoteEvent(
    int Pitch,          // 0-127
    int Velocity,       // 1-127（解析期等于 VelocityBase）
    long StartTicks,    // 绝对 tick
    long DurationTicks,
    int MeasureIndex,   // 0 起
    int VoiceIndex,     // 小节内声部序号，0 起
    SourceSpan Span);   // 指回源码

public long EndTicks => StartTicks + DurationTicks;
```

### 1.6 `PitchMapper`（静态类）

```csharp
public static bool TryGetRootOffset(char key, out int offset);   // C=0 D=2 E=4 F=5 G=7 A=9 B=11
public static bool TryMap(char keyRoot, int degree, int octave, bool sharp, out int pitch);
public static bool TryMap(char keyRoot, bool keySharp, int degree, int octave, bool sharp, out int pitch);
public static string DescribeKey(char keyRoot, bool keySharp);   // "C" / "F#"
```

映射公式见《02-语法参考》9.1。degree 不在 1–7、根音非法、结果超出 0–127 时返回 false。

### 1.7 `NoteMerger`（静态类）

```csharp
public static IReadOnlyList<NoteEvent> Apply(IEnumerable<NoteEvent> notes);
```

同音高重叠覆盖：后音截断前音；起点相同时移除前音。丢弃 DurationTicks <= 0 的音。输出按 (StartTicks, Pitch) 排序。

### 1.8 结构体与小类型

| 类型 | 定义 | 说明 |
| --- | --- | --- |
| `SourceSpan` | readonly record struct (int Start, int Length) | `End`、`Empty`、`FromBounds(start,end)` |
| `Token` | readonly record struct (TokenKind Kind, string Text, int Line, int Column, SourceSpan Span) | `IsTrivia` = 空白/换行/注释 |
| `TokenKind` | Whitespace, NewLine, Comment, Number, Dot, Plus, Minus, Sharp, Comma, LParen, RParen, Slash, Equals, Identifier, LineOctaveUp, LineOctaveDown, Unknown | — |
| `Diagnostic` | sealed record (Code, Severity, Message, Line, Column, Span) | `CodeNumber` = (int)Code |
| `DiagnosticCode` | enum（1–5、101–124） | 全表见《02-语法参考》第 13 节 |
| `DiagnosticSeverity` | Warning, Error | 当前只产出 Error |

### 1.9 `AttmSpec`（静态类）

语法规格常量的唯一来源。改它等于改语言：必须同步 `docs/02-语法参考.md` 与 `tests/ATTM.Tests`。

```csharp
public const int TicksPerQuarter = 480;
public const int MinUnitTicks = TicksPerQuarter / 32;   // 15
public const int MiddleC = 60;
public const double MinBpm = 1, MaxBpm = 1000, DefaultBpm = 120;
public const int MinVelocity = 1, MaxVelocity = 127, DefaultVelocity = 90, MaxVelocityJitter = 127;
public const int MinPitch = 0, MaxPitch = 127;
public const int DefaultBeatsPerMeasure = 4, DefaultBeatUnit = 4;
```

---

## 2. `ATTM.Core.Midi`

### 2.1 `MidiSequence`（静态类）

```csharp
public const int Channel = 0;

public static List<MidiNoteMessage> BuildNoteMessages(Score score, Random? random = null);
public static int ApplyJitter(int baseVelocity, int jitter, Random? random);
```

- 每个音产出两条消息：NoteOn(StartTicks, velocity) 与 NoteOff(EndTicks, 0)。
- 排序：先 tick，后 Kind（NoteOff = 0 排在 NoteOn = 1 **之前**，避免同 tick 同音高被吃掉）。
- ApplyJitter：jitter <= 0 或 random == null 返回 clamp 后的基准力度；否则 random.Next(-jitter, jitter+1) 后 clamp 到 1–127。
- **播放器与导出器共用它**，两者时间轴与力度完全一致。

### 2.2 `MidiNoteMessage`

```csharp
public readonly record struct MidiNoteMessage(long Tick, MidiEventKind Kind, int Pitch, int Velocity);
public enum MidiEventKind { NoteOff = 0, NoteOn = 1 }
```

### 2.3 `MidiFileWriter`（静态类）

```csharp
public static void Write(Score score, string path, Random? random = null);
public static byte[] BuildTrackData(Score score, Random? random = null);
```

写出 **SMF format 0，单轨**：

| 位置 | 内容 |
| --- | --- |
| Header | "MThd" + 长度 6 + format 0 + 1 轨 + division = TicksPerQuarter(480) |
| 第一段 | Tempo meta FF 51 03，值 = round(60 000 000 / Bpm)，clamp 到 0xFFFFFF |
| 第二段 | Time Signature meta FF 58 04，[分子][分母的 2 次幂][24][8] |
| 第三段 | Program Change C0 00（通道 0，大钢琴） |
| 主体 | 逐消息：delta 变长整数 + 0x90 / 0x80 短消息 |
| 结尾 | 在 TotalTicks 处写 FF 2F 00（End of Track） |

Write 自动创建目录；path 为空抛 ArgumentException。

### 2.4 `SmfReader`（静态类，MIDI 导入）

```csharp
public static SmfDocument Read(string path);
public static SmfDocument Read(byte[] data);     // 结构非法时抛 SmfFormatException
```

支持 format 0 / 1 / 2、running status、tempo / 拍号 / 调号 meta；多轨合并到同一条时间轴，只保留音符。SMPTE 时间基准不支持。

```csharp
public sealed record SmfNote(long StartTicks, long DurationTicks, int Pitch, int Velocity);
public sealed record SmfDocument(int Format, int TrackCount, int TicksPerQuarter, double Bpm,
    int BeatsPerMeasure, int BeatUnit, int? KeyFifths, bool KeyMinor, IReadOnlyList<SmfNote> Notes);
public sealed class SmfFormatException : Exception;
```

---

## 2B. `ATTM.Plugins`（插件契约）

> 完整说明见 [12-插件系统](12-插件系统.md)。MIDI 转换代码在 `plugins/ATTM.Plugin.MidiImport/`。

```csharp
public interface IPluginContext        // 插件能碰到的一切
{
    string ApplicationDirectory { get; }
    string DataDirectory { get; }
    void Log(string message);
    string GetDocumentText();
    void SetDocumentText(string text);
    void SetStatus(string message);
    void ShowMessage(string message, bool isError = false);
}

public interface IAttmPlugin { string Id/Name/Version/Description; void Initialize(IPluginContext); void Shutdown(); }
public interface IImporterPlugin : IAttmPlugin { Extensions; FileFilter; PluginImportResult Import(string path); }
public interface IExporterPlugin : IAttmPlugin { Extensions; FileFilter; void Export(IPluginContext, string path); }
public interface ICommandPlugin  : IAttmPlugin { IReadOnlyList<PluginCommand> Commands; }
public interface IUiPlugin       : IAttmPlugin { IReadOnlyList<PluginUiItem> UiItems; }

public sealed record PluginCommand(string Id, string Title, string? ToolTip, string? IconGlyph,
    Action<IPluginContext> Execute, Func<IPluginContext, bool>? CanExecute = null);

public sealed record PluginUiItem(PluginUiSlot Slot, int Order, Func<IPluginContext, FrameworkElement> CreateView);
public enum PluginUiSlot { Toolbar, StatusBar, SettingsPanel }
public enum PluginState { Loaded, Failed, Disabled }
public sealed class PluginEntry { Plugin; Source; SourcePath; State; Error; Context; IsLoaded; }
```

|| 类型 | 说明 |
| --- | --- |
| `PluginHost` | 扫描 `plugins` 目录、每个 dll 一个 `AssemblyLoadContext`、支持禁用表；聚合出 `Importers` / `Exporters` / `Commands` / `UiItems` |
| `PluginLoadContext` | 内部类；对 `ATTM` 返回 null 以复用宿主程序集，保证接口类型身份一致 |
| `IPluginDocumentBridge` | 主窗口实现，`PluginHost` 借它把文档读写转发给插件 |

---

## 3. `ATTM.Core.Playback`

### 3.1 `IMidiOutput`

```csharp
public interface IMidiOutput : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    void NoteOn(int channel, int pitch, int velocity);
    void NoteOff(int channel, int pitch);
    void ControlChange(int channel, int controller, int value);
    void AllNotesOff(int channel);
    void ResetControllers(int channel);   // 松三种踏板 + 关所有残留音
}
```

| 实现 | 用途 |
| --- | --- |
| `Win32MidiOutput` | 真实设备，winmm P/Invoke |
| `NullMidiOutput` | 没有可用设备时的静音替身；时间轴与高亮照常，IsOpen == false |

### 3.2 `Win32MidiOutput`

```csharp
public static IReadOnlyList<MidiDeviceInfo> GetDevices();
public static bool TryOpen(int? deviceIndex, out Win32MidiOutput? output, out string error);
```

- deviceIndex == null → 打开 MIDI Mapper（0xFFFFFFFF，系统默认路由）。
- 失败时返回 winmm 错误码文本，调用方应回退 NullMidiOutput。
- 短消息打包：0x90|ch | pitch<<8 | velocity<<16；NoteOff 为 0x80|ch | pitch<<8。
- AllNotesOff：CC123（All Notes Off）+ CC120（All Sound Off）。
- ResetControllers：AllNotesOff + CC64/66/67 置 0（延音/持音/弱音踏板）。
- Dispose：AllNotesOff + ResetControllers + midiOutReset，再 midiOutClose；设备拔出时异常被吞掉。

### 3.3 `IPlaybackClock` / `StopwatchPlaybackClock`

```csharp
public interface IPlaybackClock { TimeSpan Elapsed { get; } void Restart(); void Stop(); void Reset(); }
```

测试里可替换成手动推进的假时钟（tests/ATTM.Tests/PlaybackChecks.cs）。

### 3.4 `ScorePlayer`

```csharp
public sealed class ScorePlayer : IDisposable
{
    public ScorePlayer(IMidiOutput output, IPlaybackClock? clock = null);

    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler? Finished;

    public PlayerState State { get; }   // Stopped | Playing | Paused
    public IMidiOutput Output { get; }
    public Score? Score { get; }
    public TimeSpan Duration { get; }
    public TimeSpan Position { get; }
    public bool IsSilent { get; }       // = !Output.IsOpen

    public void SetOutput(IMidiOutput output);
    public void SetScore(Score? score);
    public void Play(TimeSpan? startAt = null);
    public void Pause();
    public void Stop();
    public void Advance();                              // 由 UI 定时器周期调用
    public IReadOnlyList<SourceSpan> GetActiveSpans();  // 播放高亮
    public void Dispose();
}
```

| 操作 | 行为 |
| --- | --- |
| SetOutput | 先 Stop，再替换并释放旧输出 |
| SetScore | 先 Stop，清空事件表 |
| Play(null) 且处于 Paused | **续播**：把暂停时仍在时值内的音重新 NoteOn，从暂停位置继续（FindNextEventIndex 二分定位） |
| Play(startAt) | **起播**：重建事件表 → ResetControllers → 若 startAt > 0 则补发跨过该位置的音（听感不缺拍） |
| Pause | 记录位置、停表、AllNotesOff、保存仍在时值内的音 |
| Stop | AllNotesOff、清空位置与事件游标 |
| Advance | 发送 Position + 12 ms 前瞻窗口内所有到期消息；到达 Duration 触发 Complete（AllNotesOff + Finished） |
| GetActiveSpans | 线性扫描 Score.Notes（按 StartTicks 升序，start > 当前位置即 break），返回正在发声的源码区间 |

关键常量：Lookahead = 12 ms。

状态机：Stopped --Play--> Playing --Pause--> Paused --Play--> Playing；Playing/Paused --Stop--> Stopped；Playing 到达 Duration → Finished → Stopped。

---

## 4. `ATTM.Editing`

### 4.1 `AttmEditor : Grid`（编辑器宿主）

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| BufferChanged / CaretChanged / HistoryChanged | event EventHandler | 转发自 EditorTextView |
| `Buffer` | TextBuffer | 只读暴露 |
| `Text` | string（读写） | 设置时重置撤销栈与光标 |
| `CaretOffset` / `CaretPosition` | int / (int,int) | — |
| CanUndo / CanRedo / HasSelection / HasPlaybackHighlight | bool | 工具栏按钮绑定 |
| `EditorLineHeight` / `EditorFontSize` | double | 字号范围 9–40 |
| VerticalOffset / ViewportHeight / ScrollToVerticalOffset(double) | — | 缩略图导航 |
| `ScrollOffsetIntoView(int offset)` | — | 播放跟随滚动 |
| `ApplyParse(ParseResult?)` | — | 更新高亮、诊断、错误行、缩略图 |
| `SetPlaybackSpans(IReadOnlyList<SourceSpan>?)` | — | 播放高亮 + 乐句预览高亮 |
| SetCaret / InsertText / Undo / Redo / SelectAll / FocusEditor | — | 命令 |
| `ApplyTheme()` | — | 重新解析配色并下发到子控件 |
| `PhrasePreview` | PhrasePreview | 乐句预览控件 |
| IsPhrasePreviewVisible / TogglePhrasePreview / SetPhrasePreviewVisible(bool, bool animate = true) | — | 侧边栏开合（220 ms 宽度动画） |
| ShowPhrase(int) / HidePhrase() / FadeOutPhrase() / PhraseMeasureIndex | — | 驱动预览内容 |

布局：4 列 = 行号槽(auto) | ScrollViewer(*) | 缩略图(80) | 乐句预览(auto)。滚动条隐藏，导航交给缩略图。

### 4.2 `TextBuffer`

```csharp
public sealed class TextBuffer
{
    public TextBuffer(string? text = null);
    public string Text { get; }
    public int Length { get; }
    public int LineCount { get; }
    public int MaxLineLength { get; }
    public int Version { get; }        // 每次内容变化自增

    public static string Normalize(string text);   // CRLF/CR -> LF
    public void Reset(string text);
    public string GetLine(int index);              // 越界自动 clamp
    public int GetLineStart(int index);
    public int GetLineLength(int index);
    public void Replace(int offset, int removeLength, string insertText);
    public string GetText(int offset, int length);
    public (int Line, int Column) GetLineColumn(int offset);   // 1 起
    public int GetOffset(int line, int column);                // 0 起行列 -> 偏移
}
```

设计：单个 string + 行索引数组，每次 Replace 全量重建索引。几千行规模下比 rope 更快也更不易出错。

### 4.3 `EditorTextView : FrameworkElement`

| 公开成员 | 说明 |
| --- | --- |
| TextPaddingLeft = 12 / TextPaddingTop = 8 / TabSize = 4 | 常量 |
| Buffer / CaretOffset / SelectionStart / SelectionLength / CaretPosition / CaretLine | 只读状态 |
| `Text` | **读写属性**；setter 等价于 `SetText()`，会清空撤销栈并把光标复位 |
| CanUndo / CanRedo / LineHeight / TopPadding / EditorTypeface / EditorFontSize | 度量 |
| SetText / SetPalette / SetFontSize / SetParse / SetPlaybackSpans | 状态注入 |
| SelectAll / ClearSelection / SetSelection(anchor, caret) / SelectLine(line) | 选区 |
| Copy / Cut / Paste / InsertText / Undo / Redo | 编辑命令 |
| `ScrollOffsetIntoView(int)` | 播放跟随 |
| `HasPlaybackHighlight` | 状态判断 |

撤销上限 HistoryLimit = 200；行缓存 LineCacheLimit = 400；光标闪烁 530 ms。

### 4.4 `EditorHighlighter`（静态类）

```csharp
public static List<HighlightRun>[] Build(ParseResult result);

public readonly record struct HighlightRun(int Start, int Length, EditorTokenClass Class);
public enum EditorTokenClass { Note, Rest, Octave, OctaveDown, Sharp, Punctuation, Comment, Directive, Keyword, Value, Unknown }
```

按行输出配色区间。**指令行判定**：该行含 Equals token。于是 bpm / Vel 在指令行显示为 Directive；谱面行里等于 legato 的 Identifier 显示为 Keyword。

### 4.5 `EditorPalette`

22 个 required Brush + `BrushFor(EditorTokenClass)`。`FromResources(FrameworkElement?)` 从主题字典取色（每键带硬编码回退值，anchor 为 null 时只查 Application 资源），`CreateFallback()` 供构造期使用。

### 4.6 `EditorGutter : FrameworkElement`

属性：LineHeight、TopPadding、VerticalOffset、LineCount、ActiveLine、ErrorLines、Palette、Typeface、FontSize、GutterWidth（默认 54）。只画可见行；错误行在左侧画半径 2.6 的红点。

### 4.7 `EditorMinimap : FrameworkElement`

| 成员 | 说明 |
| --- | --- |
| `PanelWidth = 80` | 面板宽度 |
| `ScrollRequested` | Action<double>，请求滚动到某纵向偏移 |
| LinePixel / YForLine(double) / ViewportTop / ViewportHeightPx | **统一映射**（条、视野框、光标标记共用），也是对齐测试取样口 |
| `Update(in MinimapState state)` | 宿主在滚动/编辑时调用；参数封装成值对象，避免长位置参数列表 |
| `MinimapState(Buffer, Palette, LineHeight, VerticalOffset, ViewportHeight, CaretLine, ErrorLines)` | 一次状态快照（record struct） |

### 4.8 `PhrasePreview : FrameworkElement`

| 成员 | 说明 |
| --- | --- |
| `PanelWidth = 460` | 侧边栏宽度 |
| Lines / CurrentText / MeasureIndex / MeasureCount / HasContent / IsShowingIdleHint | 内容状态 |
| `ActiveRanges` | 正在发声的音（相对每行的列区间） |
| `SetPalette` | 换主题时重建排版 |
| Show(measureIndex, lines, measureCount = 0) / Hide(bool keepMeasure = false) / SetActiveSpans | 驱动 |
| IsAnimating / RevealProgress / LastFontSize / LastTextWidth / LayoutBuildCount / RenderCount / RenderMilliseconds | 动画与性能观测口（测试依赖） |
| EnterCurve(double) / ExitCurve(double) | 缓动函数（静态，可单测） |
| PhraseLine(string Text, IReadOnlyList<HighlightRun> Runs, int Offset) | 一行乐句 |
| ActiveRange(int Line, int Start, int Length) | 高亮区间 |

动画参数：入场 240 ms（滑入 40 px + 淡入，逐行错开 60 ms）、出场 85 ms（纯渐隐，起点透明度上限 0.5）、字号自适应 14–64。

---

## 5. `ATTM.Services`

### 5.1 `AppConfig`

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| Version | int | 3 | 配置版本，用于迁移 |
| Theme | string | "Dark" | Dark / Light / System |
| MidiOutputDevice | string | "" | 空 = 自动（优先 GS Wavetable） |
| DefaultSaveFolder | string | "" | 空 = 音乐\ATTM |
| EditorFontSize | double | 18 | 9–40 |
| AutoScrollOnPlay | bool | true | 播放时自动滚动 |
| PhrasePreviewVisible | bool | false | 乐句预览是否展开 |
| WindowLeft/Top/Width/Height | int? | null | 窗口位置记忆 |
| WindowMaximized | bool | false | — |

只读派生：Mode（AppTheme）、IsDark。

| 静态成员 | 说明 |
| --- | --- |
| ConfigDirectory | %APPDATA%\ATTM |
| ConfigPath | %APPDATA%\ATTM\config.json |
| LibraryFolder | 音乐\ATTM（不存在则创建） |
| ResolveSaveFolder(AppConfig) | 上次目录，否则 LibraryFolder |
| Load() / Save() | 异常全部静默吞掉；Version < 2 且字号为 15 时升级到 18；Version < 3 立即回写 |

### 5.2 `TextFile`（静态类）

```csharp
public static string Read(string path);
public static void Write(string path, string text);
```

- 读：UTF-8 BOM → UTF-16 LE/BE BOM → 严格 UTF-8 → GB18030（代码页 54936）→ 宽松 UTF-8。
- 写：统一 LF + UTF-8 无 BOM。

### 5.3 `ThemeService`（静态类）

```csharp
public enum AppTheme { Dark, Light, System }

public static AppTheme Mode { get; }
public static bool IsDark { get; }
public static event EventHandler? ThemeChanged;

public static AppTheme ParseMode(string? value);
public static string ToConfigString(AppTheme mode);
public static void SetMode(AppTheme mode);
public static void RefreshSystemTheme();
public static bool IsSystemLightTheme();     // 读 HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme
```

ApplyPalette 替换 MergedDictionaries 中 Source 含 "Palette." 的那一项并触发 ThemeChanged。首次 SetMode 会挂 SystemEvents.UserPreferenceChanged 监听（General / Color），并切回 UI 线程重绘。

### 5.4 `WindowTheme`（internal static）

```csharp
public static void Apply(Window window, bool dark);
```

DwmSetWindowAttribute(hwnd, 20, 1|0)，失败回退属性 19（Win10 20H1 之前的编号）。

### 5.5 `UiTuning`（静态类）

界面时序与布局常量的唯一来源。

```csharp
public const int ParseDebounceMilliseconds = 180;   // 编辑后重解析防抖
public const int PlaybackTickMilliseconds = 10;     // 播放推进定时器
public const double PhraseFadeLeadSeconds = 0.16;   // 乐句提前退场
public const double MinWidthWithPhrasePanel = 1180; // 展开乐句预览时窗口最小宽
public const double MinWidthWithoutPhrasePanel = 720;
```

---

## 6. `ATTM.Views`

### 6.1 `SettingsWindow`

```csharp
public SettingsWindow(AppConfig config, Action<AppConfig> applySettings);
```

- 主题下拉（深色/浅色/跟随系统）+ MIDI 设备下拉 + 刷新按钮。
- **没有保存按钮**：任何改动立即通过回调落到主窗口并写盘（ApplyLiveSettings）。
- 设备列表第一项固定是「（自动：优先 Microsoft GS Wavetable）」。

### 6.2 `SaveAsWindow`

```csharp
public SaveAsWindow(string? currentPath, AppConfig config, bool isSaveAs);
public string? ResultPath { get; }
```

- 文件名 + 目录，实时预览将产出的 .attm 与 .mid 两个路径。
- 文件名清洗：去掉非法字符与控制字符、去掉用户手打的 .attm/.mid 后缀、去掉结尾的点。
- 目录不存在 / 文件名为空 → 红色错误提示并阻止提交。
- 目标已存在 → 二次确认覆盖。
- 目录选择用 Microsoft.Win32.OpenFolderDialog。

---

## 7. `ATTM`（主程序）

### 7.1 `App`

```csharp
protected override void OnStartup(StartupEventArgs e);
```

从命令行参数里找第一个**存在**的非 '-' 开头参数作为初始文件，交给 MainWindow(string?)。这就是「双击 .attm 直接打开」的实现。

### 7.2 `MainWindow`

```csharp
public MainWindow(string? initialFile = null);
```

| 常量 | 值 | 用途 |
| --- | --- | --- |
| NewDocumentTemplate | 内嵌《小星星》示例 | 新建文档内容 |
| PhraseFadeLeadSeconds | 0.16 | 小节结束前 160 ms 让旧乐句提前退场 |

| 关键字段 | 说明 |
| --- | --- |
| _parseTimer | 180 ms 解析防抖 |
| _playTimer | 10 ms，DispatcherPriority.Render，驱动 ScorePlayer.Advance |
| _parse | 当前 ParseResult |
| _config / _player | 配置与播放器 |
| _filePath / _fileName / _isDirty | 文件状态 |
| _fadedMeasure | 已提前退场的乐句小节（防重复触发） |
| _deviceName | 当前 MIDI 设备名，用于判断是否需要重开端口 |

| 主要方法 | 说明 |
| --- | --- |
| Reparse() | 全量解析 + 下发到编辑器 + 更新播放器乐谱 + 刷新状态/按钮 |
| ScheduleParse() | 重启 180 ms 防抖定时器 |
| UpdateStatus() | 状态栏文本（文件名、脏标记、错误数、BPM/调式/拍号/音符数/时长/行列、播放状态） |
| UpdateButtons() | 撤销/重做/播放按钮启用 |
| UpdatePlaybackPosition() | 播放高亮、乐句预览、自动滚动、秒级状态刷新 |
| GetPlayingMeasure() | 当前播放位置 → 小节序号 |
| ShouldFadeOutPhrase(measure) | 是否该让旧乐句提前退场 |
| EnsurePhraseRoom(bool) | 展开预览时把 MinWidth 提到 1180 并适当撑宽窗口 |
| ApplyMidiDevice(string) | 按名字匹配设备（先精确再包含），失败回退 NullMidiOutput |
| ApplyLiveSettings(AppConfig) | 设置窗口回调：立即生效并保存 |
| LoadFile / SaveDocument / SaveTo / NewDocument / ConfirmDiscard | 文件流程 |
| GetCaretPlaybackPosition() | 光标行 → 小节起点的时间 |
| RestoreWindowPlacement / SaveWindowPlacement | 窗口位置记忆（带虚拟屏幕可见性校验） |

**事件接线**

```text
Editor.BufferChanged    -> MarkDirty + ScheduleParse
Editor.CaretChanged     -> UpdateStatus
Editor.HistoryChanged   -> UpdateButtons
_player.StateChanged    -> UpdatePlaybackUi
_player.PositionChanged -> UpdatePlaybackPosition
_player.Finished        -> ClearHighlight
ThemeService.ThemeChanged -> 重应用标题栏 + 编辑器配色 + 状态栏
```
