# 10 · 结构审查与 API 规范

> 本文记录一次系统性的结构 / 接口审查：发现了什么、改了什么、为什么还有没改的。
> 审查基线：提交 `ce048ba`，代码约 9 700 行，两套测试 92 + 49 项。

---

## 1. 审查方法

| 维度 | 做法 |
| --- | --- |
| 文件级 | 统计行数与职责，判断单文件是否混装多种关切 |
| 调用点 | 对每个公开成员 grep 全部使用处，区分「真实调用」与「历史遗留」 |
| 重复 | 找同一语义在多处各写一遍的实现（例如小节与时间的换算） |
| 魔数 | 找同一常量在多个文件里各写一次的情况 |
| 可测性 | 看公开面是否为了测试而被迫放宽（可写属性、null-forgiving） |
| 契约 | 看公共方法的参数是否成了「一长串位置参数」，加参数是否要改所有调用点 |

---

## 2. 发现的问题与处理

### 2.1 结构问题

| 编号 | 问题 | 证据 | 严重度 | 处理 |
| --- | --- | --- | --- | --- |
| S1 | `MainWindow` 单文件 797 行，混装窗口位置、主题、解析调度、播放 UI、文件流程、设备选择、快捷键、工具栏 8 类职责 | `MainWindow.xaml.cs`（改前） | 高 | **已拆**为 5 个 partial 文件 |
| S2 | 魔数散落：解析防抖 180、播放节拍 10、乐句提前退场 0.16、窗口最小宽 1180/720 | 构造函数、`ShouldFadeOutPhrase`、`EnsurePhraseRoom` | 中 | **已集中**到 `UiTuning` |
| S3 | 语言常量散落：PPQ 480、最短时值 15、bpm 1–1000、力度 1–127、中央 C 60 分布在 parser / PitchMapper / MidiSequence / Score | 各文件 | 中 | **已集中**到 `AttmSpec` |
| S4 | 「小节 ↔ tick ↔ 时间」换算写了三遍：`MainWindow.GetPlayingMeasure`、`MainWindow.GetCaretPlaybackPosition`、`ParseResult.GetMeasureIndex` | 改前代码 | 中 | **已收敛**到 `Score.GetMeasureIndex` / `GetMeasureStartTime` + `ParseResult.GetMeasureIndex` |
| S5 | `ATTM.Core.Syntax` 同时装「语言实现」和「音乐领域模型」（Score / NoteEvent / PitchMapper / NoteMerger） | 目录结构 | 低 | **暂不改**（见 §4-P2-1） |
| S6 | `tests/ATTM.Tests/Program.cs` 单文件 978 行；`UiChecks/Program.cs` 1137 行 | — | 低 | **暂不改**（见 §4-P3-1） |
| S7 | 仓库没有 `.editorconfig`，编码/缩进/换行约定只写在文档里 | — | 中 | **已补**（含 `*.ps1 = utf-8-bom` 这条硬教训） |

### 2.2 API 问题

| 编号 | 问题 | 证据 | 处理 |
| --- | --- | --- | --- |
| A1 | `EditorMinimap.Update` 有 8 个位置参数，且其中 `topPadding` **完全没被方法体使用**（死参数） | `Editing/EditorMinimap.cs`（改前 88–137 行） | **已改**为 `Update(in MinimapState)`，并删掉死参数 |
| A2 | `EditorPalette.FromResources(FrameworkElement anchor)` 用 `null!` 强行传空来生成回退配色 | `CreateFallback() => FromResources(null!)` | **已改**为可空参数，去掉 null-forgiving |
| A3 | `EditorTextView.Text` 只读、另有 `SetText()` 做同样的写入，两套入口语义不清 | — | **已统一**为 `Text` 属性（setter 等价 `SetText`，会重置撤销栈） |
| A4 | `ThemeService` 同时有 `SetMode(AppTheme)` 与兼容重载 `SetTheme(bool)`，两个入口会走岔 | 3 处测试调用 | **已删**兼容重载，全部改 `SetMode` |
| A5 | `AttmEditor.Text` 的 setter 绕一层去调 `_view.SetText`，与 A3 重复 | `Editing/AttmEditor.cs:118` | **已改**为 `_view.Text = value` |
| A6 | `Score` 只有 `MeasureTicks` / `SecondsPerTick`，调用方各自做换算 | — | **已加** `GetMeasureStartTicks` / `GetMeasureStartTime` / `GetMeasureIndex(TimeSpan)` |
| A7 | 测试依赖系统 `%TEMP%`（受限环境下 9 项失败）与系统「音乐」目录 | `Path.GetTempPath()` 9 处 | **已改**为输出目录下 `tmp`；音乐目录断言改为契约断言（见 §3.3） |

### 2.3 记录但本轮不改

| 编号 | 现象 | 结论 |
| --- | --- | --- |
| Q1 | `DiagnosticCode.EmptyMeasure(116)`、`LegatoRest(123)` 已定义但解析器从不产出 | 保留：数值是公开契约，删了会破坏文档与外部引用；文档已注明「未使用」 |
| Q2 | `EditorGutter` / `PhrasePreview` 有大量 public 可写属性 | 是为无头测试准备的观测/注入点，值得保留；若将来要收紧，用 `internal` + `InternalsVisibleTo` |
| Q3 | `MainWindow` 直接调静态 `ThemeService` | 引入 `IThemeService` 会让 3 个窗口都改构造签名，收益仅限「可替换」，本轮不动 |
| Q4 | `AttmParser` 内部 `Parser` 964 行，`ParseVoiceLine` 约 140 行 | 逻辑本身线性可读，拆分需要引入中间状态对象；列为 P2 |

---

## 3. 本轮实施明细

### 3.1 `MainWindow` 拆分为部分类

| 文件 | 行数 | 职责 |
| --- | --- | --- |
| `MainWindow.xaml.cs` | 146 | 文档模板常量、字段、构造函数、事件接线 |
| `MainWindow.Appearance.cs` | 90 | 主题切换与标题栏、窗口位置记忆 |
| `MainWindow.Playback.cs` | 279 | 解析调度、状态栏、播放 UI、乐句预览驱动、MIDI 设备 |
| `MainWindow.Files.cs` | 254 | 新建 / 打开 / 保存 / 脏标记保护 / 快捷键 / 拖放 |
| `MainWindow.Commands.cs` | 101 | 工具栏按钮回调、从光标处播放 |

拆分是**纯机械移动**：类型仍是同一个 `public partial class MainWindow`，字段留在主文件，行为零变化（两套测试全绿即为证）。

### 3.2 常量集中

`Core/Syntax/AttmSpec.cs` —— 语法规格：

```csharp
public const int TicksPerQuarter = 480;
public const int MinUnitTicks = TicksPerQuarter / 32;   // 15
public const int MiddleC = 60;
public const double MinBpm = 1;
public const double MaxBpm = 1000;
public const double DefaultBpm = 120;
public const int MinVelocity = 1;
public const int MaxVelocity = 127;
public const int DefaultVelocity = 90;
public const int MaxVelocityJitter = 127;
public const int MinPitch = 0;
public const int MaxPitch = 127;
public const int DefaultBeatsPerMeasure = 4;
public const int DefaultBeatUnit = 4;
```

`Services/UiTuning.cs` —— 界面时序与布局：

```csharp
public const int ParseDebounceMilliseconds = 180;
public const int PlaybackTickMilliseconds = 10;
public const double PhraseFadeLeadSeconds = 0.16;
public const double MinWidthWithPhrasePanel = 1180;
public const double MinWidthWithoutPhrasePanel = 720;
```

使用方：`AttmParser`（PPQ / 最短时值 / bpm / 力度范围）、`Score`（默认值）、`PitchMapper`（中央 C / 音高范围）、`MidiSequence`（力度 clamp）、`MainWindow*`（时序）。

### 3.3 测试健壮性

改前统计：`ATTM.Tests` **84 通过 / 8 失败**，`ATTM.UiChecks` **48 / 1**。9 项失败**全部**是环境问题，不是逻辑错误：

```text
UnauthorizedAccessException: Access to the path 'C:\Users\<user>\AppData\Local\Temp\attm-test-1.mid' is denied.
```

改法：

1. 两套 harness 各加一个 `TempRoot` 属性 —— 指向 `AppContext.BaseDirectory/tmp`（仓库内、可写、跑完不污染系统临时目录）。
2. 「音乐目录」相关的两条断言改成**契约断言**：只校验路径等于 `音乐\ATTM`（拿不到音乐目录时按 `AppConfig` 同样的规则退回家目录），目录存在性仅在「本进程能创建目录」时才要求。

改后：**92 / 92** 与 **49 / 49**。

---

## 4. 后续可做（按性价比排序）

### P2

1. **拆 `Core.Syntax` 的两个关切**：把 `Score` / `NoteEvent` / `PitchMapper` / `NoteMerger` 移到 `ATTM.Core.Score`（`Core/Score/` 目录），`AttmParser` 保留在 `ATTM.Core.Syntax`。影响面：约 20 个文件的 `using`、两套测试、以及 `docs/03` 的章节标题。收益是「语言」与「音乐模型」解耦，风险是中等的机械改动。
2. **收紧测试专用公开面**：`EditorGutter` / `PhrasePreview` 的观测属性改 `internal` + `[assembly: InternalsVisibleTo("ATTM.UiChecks")]`。
3. **`Parser` 拆分**：抽出 `MeasureTiming`（段/单元 → tick 的纯计算），把 `ParseVoiceLine` 从 140 行降到 ~60 行。

### P3

1. 测试工程按域拆文件（`LexerTests.cs` / `ParserTests.cs` / `MidiTests.cs` / `PlaybackTests.cs`），harness 抽成共享 `TestRunner`。
2. harness 增强：支持「期望抛异常」「跳过（环境不满足）」两种语义，替代现在靠返回 `true` 变通。
3. 加 CI（`dotnet build` + 两套测试），失败即红。

---

## 5. API 设计规范（本仓库自此遵守）

### 5.1 命名与形状

| 场景 | 规范 | 反例（已改） |
| --- | --- | --- |
| 状态 | 用**属性**（`get` 必有，`set` 表示可整体替换） | `SetText()` + 只读 `Text` 并存 |
| 注入 | 用 `SetXxx(...)`，表示「外部推一份状态进来」 | — |
| 周期性刷新 | 参数超过 3 个就抽**值对象**，签名写 `Update(in State)` | `Update(a,b,c,d,e,f,g,h)` |
| 只读查询 | `GetXxx(...)`，不产生副作用 | — |
| 兼容重载 | 不留。要改就改所有调用点 | `SetTheme(bool)` 与 `SetMode(AppTheme)` 并存 |

### 5.2 可见性与可空性

- 参数确实可以为空，就把类型写成可空（`FrameworkElement?`），**不允许**用 `null!` 骗过编译器。
- 只给测试用的成员：优先 `internal` + `InternalsVisibleTo`；本轮保留 public 的属性见 §2.3-Q2。

### 5.3 常量

- 一个数值同时出现在两处以上 → 必须抽成常量。
- 语法规格常量放 `AttmSpec`，界面时序常量放 `UiTuning`，控件内部度量留在控件里（`TextPaddingLeft` 等）。
- 常量改动的连带责任：`docs/02` 的诊断码与规则表、`ATTM 语法教程.md` 的示例、`tests/ATTM.Tests` 的断言。

### 5.4 注释

- 公开类型与公开成员写 `<summary>`；一句话说清「它是什么 / 什么时候用」。
- 有非显然取舍的地方写 `<remarks>` 或行内注释说明**为什么**（例如「NoteOff 排在 NoteOn 前面」）。
- 不写「设置 xx」这类复述签名的话。

### 5.5 文件组织

- 一个文件一个主类型；配套的小值对象（`HighlightRun`、`MinimapState`、`PhraseLine`）允许同文件。
- 单一类超过 ~400 行先问「是不是混了两个关切」，是就拆（`MainWindow` 就是这么拆的）。

---

## 6. 回归验证

```bash
dotnet build                          # 0 警告 0 错误
dotnet run --project tests/ATTM.Tests      # 通过 92，失败 0
dotnet run --project tests/ATTM.UiChecks   # 通过 49，失败 0
```

判读退出码时注意：`dotnet run ... | head` 拿到的是**管道最后一条命令**的退出码，会掩盖失败。要么 `set -o pipefail`，要么不加管道。

---

## 7. 改动清单（便于 review）

| 类型 | 文件 |
| --- | --- |
| 新增 | `Core/Syntax/AttmSpec.cs`、`Services/UiTuning.cs`、`.editorconfig`、本文件 |
| 新增（拆分） | `MainWindow.Appearance.cs`、`MainWindow.Playback.cs`、`MainWindow.Files.cs`、`MainWindow.Commands.cs` |
| 修改 | `MainWindow.xaml.cs`（瘦身）、`Core/Syntax/{AttmParser,Score,PitchMapper}.cs`、`Core/Midi/MidiSequence.cs`、`Editing/{AttmEditor,EditorMinimap,EditorPalette,EditorTextView}.cs`、`Services/ThemeService.cs`、`tests/**/Program.cs` |
| 文档 | `docs/03`、`docs/04`、`docs/08`、`docs/README.md`、`README.md` |