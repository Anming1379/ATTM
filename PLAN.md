# ATTM 开发计划

ALambda Text To Midi — 纯文本转 MIDI 的 Windows 桌面应用（.NET 10 + WPF）

> 本文件是开发前的规格与排期约定。语法歧义处已与桉确认，结论见第 14 节。

---

## 1. 项目理解

ATTM 是一个"用文本写谱"的编辑器：用户用数字简谱语法写 `.attm` 纯文本，程序实时解析成音符时间轴，可以播放（发 MIDI 到系统合成器）、可以导出标准 `.mid`。

核心功能清单：

- 文本编辑器：行号、等宽字体、语法高亮、错误标记、撤销重做（200 步）
- 实时解析：ATTM 语法 → 音符时间轴，错误定位到行列
- 播放：播放/暂停、停止回到开头、播放位置高亮到具体音符
- 文件：新建 / 打开（.attm .txt）/ 拖入 / 粘贴 / 保存（.attm + .mid 双文件）/ 快速覆盖保存
- 主题：深色 + 浅色双主题，主题色默认 `#FF4E6A`，可在设置中改
- 设置：主题、主题色、默认保存目录（音乐\ATTM）、编辑器字号、MIDI 输出设备等
- 打包：自包含发布 + Inno Setup 安装包（含 .NET 检测/安装脚本）

非目标（本期不做）：多轨/多音色/打击乐、MIDI 导入、钢琴卷帘可视化、音频合成。

---

## 2. 环境事实（已实测）

| 项 | 结论 |
| --- | --- |
| .NET SDK | 10.0.403（另有 9.0.317）→ `net10.0-windows` 可用 |
| NuGet | 镜像 `cn-mirror` 可用；`api.nuget.org` 直连 SSL EOF |
| AvalonEdit | 镜像上只有 `4.3.0-alpha`（.NET Framework 时代版本），不适合 net10 → **不用第三方编辑器控件** |
| 字体 Lekton | family = `Lekton`（Regular/Bold，静态字体，非可变） |
| 图标字体 | family = `Material Symbols Sharp`（静态，4383 字形，含 Material 全部常用图标） |
| 图标 png | 1080x1080 RGBA → 需生成多尺寸 `icon.ico` |
| 未安装 | Pillow、ImageMagick；可用 ffmpeg + 手写 ICO 结构生成多尺寸图标 |

已确认可用的图标码点（用于工具栏/状态栏）：

| 用途 | 图标名 | 码点 |
| --- | --- | --- |
| 播放 | play_arrow | U+E037 |
| 暂停 | pause | U+E034 |
| 停止（回到开头） | stop | U+E047 |
| 重播 | replay | U+E042 |
| 撤销 | undo | U+E166 |
| 重做 | redo | U+E15A |
| 打开 | folder_open | U+E2C8 |
| 新建 | note_add | U+E89C |
| 保存 | save | U+E161 |
| 另存为 | save_as | U+EB60 |
| 设置 | settings | U+E8B8 |
| 深色 | dark_mode | U+E51C |
| 浅色 | light_mode | U+E518 |
| 音乐 | music_note | U+E405 |

---

## 3. 技术选型

### 3.1 零第三方运行时依赖

- **MIDI 导出**：手写 SMF（Standard MIDI File）写入器，约 200 行。PPQ 480、format 0、单轨、Tempo/TimeSignature/ProgramChange/Note/EndOfTrack。
  理由：需求固定（单轨、单一 tempo、无控制器曲线），手写完全可控，能精确实现"同音高重叠覆盖"，且避免包体积与 NuGet 风险。
- **MIDI 播放**：`winmm.dll` 的 `midiOut*` P/Invoke + `Stopwatch` 调度。约 200 行，暂停/继续/停止/进度全部自控。
  理由：播放需要与编辑器高亮严格对齐，自己调度时间轴最直接；也避免外部依赖。无可用输出端口时降级为"静音播放"（仅走时间轴与高亮）。
- **文本编辑器**：自绘控件（`FrameworkElement` + `DrawingContext`，按可见行虚拟化）。见第 8 节。
  理由：AvalonEdit 镜像不可用；WinRT/RichTextBox 方案在高亮、撤销、播放高亮上都要绕路。自研约 1200 行，完全可控，且是项目的核心亮点。
- **测试**：优先 xUnit（镜像有 2.9.3）；若 restore 失败，退回零依赖控制台断言 harness（`tests/ATTM.Checks`，`dotnet run` 返回非零退出码）。

### 3.2 架构

单项目 + 目录分层，解析器/导出器/播放器为纯逻辑（可被测试项目直接引用），UI 层 MVVM + 一个自绘控件。

- `Core\` 纯逻辑，不引用任何 UI 类型（除无）
- `Editing\` 自绘编辑器（WPF 控件 + 文本缓冲）
- `ViewModels\` `Views\` `Themes\` `Services\` UI 层
- `tests\` 语法/导出/时间轴断言

---

## 4. 目录结构

```
D:\Awork\Pi\ATTM\
  ATTM.sln
  ATTM.csproj                   WinExe, net10.0-windows, AssemblyName=ATTM
  app.manifest                  PerMonitorV2 DPI + Windows 10/11 兼容
  App.xaml / App.xaml.cs        资源合并、主题初始化、全局异常处理
  MainWindow.xaml / .cs         主窗口骨架（编辑器 + 工具栏 + 状态栏）
  assets\
    icon.png / icon.ico
    fonts\Lekton-Regular.ttf
    fonts\Lekton-Bold.ttf
    fonts\MSS-default.ttf
    fonts\OFL.txt
    install-dotnet.ps1 / install-dotnet.cmd
  Core\
    Syntax\
      Token.cs TokenKind.cs SourceSpan.cs
      AttmLexer.cs
      AttmParser.cs
      PitchMapper.cs            调式+数字 -> MIDI 音高
      Score.cs                  Score / Measure / Voice / NoteEvent
      Diagnostic.cs DiagnosticCode.cs
    Midi\
      MidiFileWriter.cs
      NoteMerger.cs             同音高重叠覆盖、排序
    Playback\
      MidiOutDevice.cs          winmm 封装 + 端口枚举
      ScorePlayer.cs            时间轴调度、暂停/继续/停止
  Editing\
    TextBuffer.cs               行模型、编辑命令、撤销栈
    AttmEditor.cs               自绘控件：渲染、插入符、选区、输入、IME、滚动
    HighlightTheme.cs           语法配色令牌
    EditorLineCache.cs          每行布局与 FormattedText 缓存
  ViewModels\
    MainViewModel.cs
    SettingsViewModel.cs
  Views\
    SettingsWindow.xaml / .cs
    SaveAsDialog.xaml / .cs
  Services\
    AppConfig.cs                JSON 配置（%APPDATA%\ATTM\config.json）
    ThemeService.cs             双主题 + 主题色注入
    FileService.cs              打开/保存/最近文件/编码探测
    DialogService.cs
  Themes\
    Palette.Dark.xaml
    Palette.Light.xaml
    Controls.xaml
    Typography.xaml
  Utils\
    ObservableObject.cs RelayCommand.cs Debouncer.cs
  packaging\
    build-portable.ps1
    build-installer.ps1
    installer.iss
  tools\
    make-icon.py                png -> 多尺寸 ico（纯 stdlib）
  tests\
    ATTM.Tests\                 语法与导出断言
  samples\
    欢乐颂.attm / 和弦示例.attm / 语法速查.attm
  README.md LICENSING.txt THIRD-PARTY-NOTICES.txt AI-GENERATED-NOTICE.txt
  PLAN.md
```

---

## 5. ATTM 语法规格（精确化）

### 5.1 行与词法

- 文件按 `\r\n` / `\n` / `\r` 分行，行号从 1 开始。
- 空行（只含空白）：小节分组分隔符，无其他语义。
- 注释：`#` 后面是行尾或空白 → 该行从 `#` 起到行尾是注释。
  - `# 注释` ✓ ；行尾 `1,2 # 注释` ✓ ；`#4` 是升号音符，不是注释。
- 指令行、注释行不参与小节；也不会打断并行小节组（连续的非空内容行属于同一组）。
- 数字与 `+` `-` `#` `(` `)` 必须连写，中间不能有空格；逗号两侧允许空白。
- 数字只允许 `0`-`7`（`8` `9` 报错）。

### 5.2 指令（必须在第一个小节之前）

```
bpm = 120          # 允许小数，1 <= bpm <= 1000
1 = C 4/4          # 调式 A-G（不区分大小写）+ 拍号 4/4 或 3/4
Vel = 100 +-20     # 可选；基准 1..127，抖动 0..127；省略时 = 90 +-0
```

- 指令名不区分大小写；等号右侧必须有至少一个空白（`bpm=120` 报错）。
- 三条指令各最多出现一次；`bpm` 与 `1 = ...` 必须存在；`Vel` 可省略。
- 指令行也允许带行尾注释。

### 5.3 小节与分段

- 每个"并行组"（连续非空内容行）= 1 个小节，时值 = 拍号分子 拍。
- 行内用 `,` 分段：段数 `S = 逗号数 + 1`，每段 = `1/S` 小节。
- 允许的 `S`：
  - 4/4：`S ∈ {1, 2, 4}`（逗号数 0 / 1 / 3；2 个逗号报错）
  - 3/4：`S ∈ {1, 2, 3, 4}`（逗号数 0 / 1 / 2 / 3）
- 段内是连续的"音符单元"：单个数字，或一个和弦括号组 `(...)`。
- 段内单元个数 `n` 必须是 2 的幂（1, 2, 4, 8, ...），否则报错。
- 每个单元时值 = `小节ticks / (S * n)`；要求 >= 15 ticks（1/32 拍），否则报错。

### 5.4 音符与和弦

单个音符：`[+|-] 数字 | [#]` 的组合，即：

```
1   +1   -1   #4   +#4   -#4   （+/- 在最前，# 紧跟数字）
```

- `+` / `-`：升高 / 降低一个八度（±12 半音）。
- `#`：升高一个半音。
- 和弦：`(46+1)`、`(357)`、`(6+1+3)` —— 括号内多个音符，整体视为一个单元，同时发声。
  - 括号内 `+` / `-` 既是分隔也是八度标记（`+1` 之后出现 `3` 自动视为新音符，因为数字紧接 `+` 后即一个完整音符）。
  - 括号内不允许 `0`；不允许空括号；不允许嵌套。
- 音高映射：`1 = X` 表示主音 X 位于中央 C 所在八度（C4 = MIDI 60）。
  - `MIDI = base(X) + MajorScale[digit-1] + 12*octave + sharp`，`MajorScale = {0,2,4,5,7,9,11}`
  - `1 = D` → 1 = D4 = 62；`1 = B` → 1 = B4 = 71。
  - 计算结果必须在 0..127，否则报错。

### 5.5 时值与力度

- 每个单元（音符、和弦、休止 `0`）都占据一个固定位置，单元时长 = 段时长 / 段内单元个数。
- **连奏**：发声音符的时值 = 本行下一个单元的起点 - 本音起点；若本行本小节后面没有单元，则到小节末尾。
- **休止会截断**：`0` 是一个占位单元，前一个音在 `0` 的起点处停止。
  - `1` → 全音符（后面没有单元，响到小节末）
  - `1,0` → 半音符（2 段各 2 拍，`1` 在第二个 `0` 处停止）
  - `1,0,0,0` → 四分音符（4 段各 1 拍，`1` 在第一个 `0` 处停止）
- 和弦内所有音共享同一时值（和弦整体 = 一个单元），停止点同样取"下一个单元的起点"。
- 并行组内每行独立计算时值，互不影响（不同行的音符在同一小节内叠加）。
- 力度：`Velocity = clamp(Vel基准 ± 随机抖动, 1, 127)`，每次播放/导出重新随机。
- **同音高重叠覆盖**：合并所有轨后按音高分组的相邻音，若 `prev.End > next.Start`，则 `prev.End = next.Start`；若 `prev.Start >= next.Start`，丢弃 `prev`。

### 5.6 诊断码

| 码 | 含义 |
| --- | --- |
| E0001 | 缺少 bpm 声明 |
| E0002 | 缺少调式/拍号声明 |
| E0003 | 指令格式错误（等号右侧必须有空格） |
| E0004 | 指令重复出现 |
| E0005 | 指令不得出现在小节之后 |
| E0101 | 非法数字（只允许 0-7） |
| E0102 | 非法字符 |
| E0103 | # 后必须紧跟数字 |
| E0104 | 括号未闭合 |
| E0105 | 空括号 |
| E0106 | 括号内出现 0 或嵌套 |
| E0107 | 段内单元个数不是 2 的幂 |
| E0108 | 逗号数 / 段数不合法 |
| E0109 | 力度超范围 |
| E0110 | 调式必须是 A-G |
| E0111 | 拍号仅支持 4/4、3/4 |
| E0112 | bpm 超出范围 |
| E0113 | 时值过小（< 1/32 拍） |
| E0114 | 空段（连续逗号、行首/行尾逗号） |
| E0115 | 音高超出 MIDI 范围 0..127 |
| E0116 | 空小节行（只有逗号） |

诊断结构：`Code / Severity / Message(中文) / Line / Column / Offset / Length`。状态栏显示数量，编辑器按行标红 + 波浪线，ToolTip 显示消息。

### 5.7 计划书示例的期望结果（用于单测锁定）

以 `1 = C 4/4`、PPQ=480（1 拍=480 ticks）为例：

| 输入行 | 期望 |
| --- | --- |
| `1` | C4(60) @0 长 1920（全音符） |
| `1,0` | C4 @0 长 960（半音符，被第二个 `0` 截断） |
| `1,0,0,0` | C4 @0 长 480（四分音符，被第一个 `0` 截断） |
| `3,3,4,5` | E4@0/480、E4@480/480、F4@960/480、G4@1440/480 |
| `11,12,3,05` | C4@0/240、C4@240/240、C4@480/240、D4@720/240、E4@960/480、G4@1680/240 |
| `1134,5,12,0057` | 1@0/120、1@120/120、3@240/120、4@360/120、5@480/480、1@960/240、2@1200/240、5@1680/120、7@1800/120 |
| `3,02,2,0` | E4@0/480、D4@720/240、D4@960/480 |
| `(46+1),(57+2),(357),(6+1+3)` | 每组起点 0/480/960/1440，各长 480；组内音同时 |
| `0(46+1),0(57+2),(357),(6+1+3)` | 前两组和弦起点 240/720、长 240（被下一段的 `0` 截断）；后两组起点 960/1440、长 480 |
| `1 = D` 时，单独一行 `1` / `+1` / `-1` | 62 / 74 / 50 |

---

## 6. MIDI 导出规格

- Header：`MThd`，format 0，ntrks 1，division 480
- Track 0 起始：Tempo（`FF 51 03`，`round(60000000 / bpm)` 微秒/四分音符）、TimeSignature（`FF 58 04 nn dd 18 08`，4/4 → `04 02`，3/4 → `03 02`）、ProgramChange（`C0 00`，大钢琴）
- 音符：NoteOn (`90 | ch`, pitch, velocity) / NoteOff (`80 | ch`, pitch, 0)，通道 0
- 事件排序：按 tick；同 tick 时 NoteOff 先于 NoteOn
- 结尾 EndOfTrack (`FF 2F 00`)
- 文本编码：`MidiFileWriter.Write(Score, path)`；导出前必须 `Diagnostics.Count == 0`

---

## 7. 播放引擎规格

- 生成 `(timeTicks, kind, pitch, velocity)` 事件序列，时间 = ticks × 60 / (bpm × 480) 秒
- `Stopwatch` 计时 + `DispatcherTimer`（8 ms）批量发送到期事件；发送前 5 ms 提前量降低抖动
- 暂停：对活动音发 NoteOff + 记录暂停位置（CC 64/66/67 归零，CC 123 AllNotesOff）
- 继续：重发仍在时值内的音的 NoteOn（从暂停点剩余时值），继续调度
- 停止：AllNotesOff + 位置归零 + 高亮清除
- 播完自动停止，位置回到开头
- 无可用 MIDI 端口：静音模式，仅时间轴与高亮工作（状态栏提示）
- 端口枚举：`midiOutGetNumDevs` / `midiOutGetDevCaps`；设备名存配置

---

## 8. 编辑器规格（自绘）

### 8.1 文本缓冲 `TextBuffer`

- 行列表 `List<Line>`（`Start` 偏移 + 文本），整篇 `string` 拼接缓存
- 编辑原语：`Insert(offset, text)` / `Delete(offset, length)` / `Replace`，自动维护行索引
- 撤销/重做：命令栈（`InsertCommand` / `DeleteCommand`，连续单字符输入合并），上限 **200 步**
- 选区模型：`Anchor` / `Caret` 两个偏移；支持 Shift+方向键、鼠标拖选、双击选词、三击选行、Ctrl+A

### 8.2 渲染

- 固定行高（`FontSize * 1.45`），可见行虚拟化，只为可见行创建/缓存 `FormattedText`
- 语法高亮：解析器输出的 token 区间 + 扫描得到的注释/指令区间，映射为配色 run（多次 `DrawText`，按 run 切分）
- 播放高亮：当前音符 token 背景色块（主题色 α=0.28）+ 可选自动滚动跟随
- 错误：行首错误标记点 + 错误区间红色波浪线（`Pen` 正弦路径）+ ToolTip
- 行号槽：右侧竖线分隔，当前行号高亮
- 当前行背景微高亮、选区底色、插入符（闪烁 530 ms，主题色）
- 水平/垂直滚动：自绘滚动条或标准 `ScrollBar` 覆盖层；Ctrl+滚轮缩放字号

### 8.3 输入

- 键盘输入（`TextInput`）、Backspace/Delete、方向键/Home/End/PageUp/PageDown、Tab（插入 4 空格）
- **IME**：通过 `TextCompositionManager` 事件接管组合串，组合期间在插入符处绘制候选下划线并提交整串；M3 第一天做 spike 验证（中文输入法必测）
- 剪贴板：Ctrl+C/X/V（纯文本）；粘贴文件路径 → 打开文件；拖入 `.attm`/`.txt` → 打开
- 只读预览？不做，编辑器始终可编辑

### 8.4 性能目标

- 5000 行文件：输入延迟 < 30 ms，滚动 60 fps
- 解析防抖 200 ms，解析失败保留上一次成功的时间轴（播放按钮禁用）

---

## 9. UI / 交互规格

### 9.1 布局

```
┌────────────────────────────────────────────────────────────┐
│ [播放][停止] │ [撤销][重做] │ [新建][打开][保存] │  ... [设置] │  44px
├──────┬─────────────────────────────────────────────────────┤
│ 行号 │ 编辑区（Lekton 等宽，语法高亮，播放高亮，错误标记）      │
├──────┴─────────────────────────────────────────────────────┤
│ 未命名.attm ·未保存   BPM 120 · C · 4/4 · 32音符 · 0:16 · 3:5 │ 错误 0 │ 就绪 │ 26px
└────────────────────────────────────────────────────────────┘
```

- 标准窗口边框（保证拖拽/贴边/最大化行为正确），标题栏文字随主题
- 工具栏按钮 34x34，图标字体，hover 背景 = 主题色 12% 叠加，按下 20%
- 快捷键提示放在 ToolTip

### 9.2 快捷键

| 操作 | 快捷键 |
| --- | --- |
| 撤销 | Ctrl+Z |
| 重做 | Ctrl+R / Ctrl+Y |
| 打开 | Ctrl+O |
| 新建 | Ctrl+N |
| 保存 | Ctrl+S |
| 另存为 | Ctrl+Shift+S |
| 设置 | Ctrl+, |

播放 / 暂停 / 停止回到开头**不设快捷键**，只通过工具栏按钮，避免与文本输入、输入法全半角切换冲突。编辑器内所有按键（含空格）都用于文本编辑。

### 9.3 主题令牌（`DynamicResource`）

```
Brush.Window / Brush.Surface / Brush.SurfaceAlt / Brush.Border
Brush.Text / Brush.TextSecondary / Brush.TextDisabled
Brush.Accent / Brush.AccentHover / Brush.AccentSoft
Brush.Selection / Brush.PlayHighlight / Brush.Caret
Brush.Error / Brush.ErrorSoft / Brush.Success
Brush.Gutter / Brush.GutterText / Brush.CurrentLine
```

深色：底 `#141417`，面 `#1C1C21`，文字 `#E8E8ED`；浅色：底 `#FAFAFB`，面 `#FFFFFF`，文字 `#1B1B1F`。
主题色默认 `#FF4E6A`，设置里提供预设 + 自定义取色，切换即时生效（替换 Palette 字典 + 通知）。

### 9.4 语法配色

| Token | 深色 | 浅色 |
| --- | --- | --- |
| 注释 | `#6A6A78` 斜体 | `#9198A6` 斜体 |
| 指令名（bpm/Vel/1 =） | `#7FB4FF` | `#2B6CB0` |
| 指令值（数字/调式/拍号） | `#FFB86C` | `#B45309` |
| 音符数字 | `#E8E8ED` | `#1B1B1F` |
| 八度 `+`/`-` | `#C792EA` | `#7C3AED` |
| 升号 `#` | `#FF7B72` | `#D93025` |
| 逗号/括号 | `#5D6373` | `#7A8194` |
| 休止 `0` | `#565B6B` 斜体 | `#9AA0AE` 斜体 |
| 播放中音符 | 主题色底 + 加粗 | 同 |

---

## 10. 设置与持久化

配置文件：`%APPDATA%\ATTM\config.json`（UTF-8，缩进，中文不转义）

```jsonc
{
  "theme": "Dark",              // Dark / Light / System
  "accentColor": "#FF4E6A",
  "defaultSaveFolder": "",      // 空 = 音乐\ATTM
  "editorFontSize": 15,
  "tabSize": 4,
  "autoScrollOnPlay": true,
  "midiOutputDevice": "",
  "windowWidth": 1100, "windowHeight": 720,
  "windowLeft": null, "windowTop": null, "windowMaximized": false,
  "showLineNumbers": true,
  "playVelocityJitter": true
}
```

设置窗口：左侧分类（外观 / 编辑器 / 播放与输出 / 文件），右侧表单，底部"恢复默认 / 取消 / 应用"。即时预览主题与主题色。

---

## 11. 文件与保存流程

- 新建：空文档 + 模板注释（`# ATTM ... bpm = 120` / `1 = C 4/4` 等可播放示例），标题"未命名.attm"
- 打开：`.attm` / `.txt`（编码探测：BOM → UTF-8 → GB18030 回退），Ctrl+O 或拖入或粘贴文件；打开时若无法解析则只显示错误不阻止打开
- 保存：首次弹自定义"保存"窗口（文件名输入 + 目录选择 + 两个产物路径预览 + 覆盖确认）；确定后写 `.attm` 与 `.mid`（同名同目录）
- 快速保存：已有路径时 Ctrl+S 直接覆盖两个文件（不弹窗）
- 另存为：Ctrl+Shift+S 总是弹窗
- 未保存保护：关闭窗口/新建/打开前若有修改 → 三选（保存 / 不保存 / 取消）
- 语法错误时禁止保存与导出（按计划书要求），状态栏与提示说明原因
- 保存成功后状态栏显示完整路径，标题去掉 `*`

---

## 12. 里程碑

### M0 基建（0.5 天）

- 目录骨架、`ATTM.sln`、csproj（WinExe/net10.0-windows/资源嵌入/版本信息）
- `app.manifest`（PerMonitorV2）、`icon.ico`（tools/make-icon.py 多尺寸）、字体资源
- 主题字典骨架（深/浅 Palette + Controls）、字体令牌（Lekton / Material Symbols Sharp）
- 空窗口 + 工具栏 + 状态栏占位，`dotnet build` 通过
- 验证：harness 读控件树确认字体族与主题色已应用

### M1 语法核心（1–1.5 天）

- Lexer / Parser / PitchMapper / Score / Diagnostic
- 全部诊断码 + 示例单测（第 5.7 节表格）
- 错误恢复：单行错误不影响其他行解析（保留尽可能多的小节）
- 验证：`dotnet test` 全绿；用 samples 跑出时间轴 dump

### M2 MIDI 导出（0.5 天）

- `MidiFileWriter` + `NoteMerger`（重叠覆盖、同 tick 排序）
- 验证：导出 `samples\*.mid`，用自写 python 解析器回读 tick/velocity/pitch，与时间轴 dump 对比一致；`ffprobe` 能识别时长

### M3 编辑器（1.5–2 天）

- `TextBuffer` + 撤销栈（200）+ 选区
- `AttmEditor` 渲染（行号、高亮、当前行、选区、插入符闪烁、滚动）
- 输入与 IME spike（中文输入法）+ 鼠标选择 + 双击/三击 + Ctrl+A
- 错误标记 + 波浪线 + ToolTip；播放高亮接口
- 验证：harness 逐行采样渲染 token 颜色/位置；中文输入法实测；5000 行性能测量

### M4 播放（1 天）

- `MidiOutDevice`（枚举/打开/发送/复位）+ `ScorePlayer`（调度/暂停/继续/停止）
- 编辑器播放高亮 + 自动滚动 + 状态栏进度
- 验证：CompositionTarget.Rendering 采样，高亮切换时间与期望 tick 误差 < 20 ms；暂停/继续后音符不残留

### M5 文件与设置（1 天）

- 打开/拖入/粘贴/新建/保存窗口/快速保存/关闭保护/最近目录
- `AppConfig` + 设置窗口（主题、主题色、目录、字号、输出设备、自动滚动）
- 验证：往返测试（保存→重开→内容一致；改主题→重启保持）

### M6 打磨（1 天）

- 快捷键全量、撤销重做 200 步上限、窗口状态记忆、空状态/错误 UX
- 主题细节（按钮动效、hover、焦点态）、高 DPI 检查（125%）
- 性能优化（解析防抖、渲染缓存、大文件）

### M7 打包发布（0.5 天）

- `dotnet publish` 自包含 win-x64（+ 框架依赖变体）
- `packaging\installer.iss`（Inno Setup 6，简中语言包，关联 .attm，附带 install-dotnet 脚本）
- README / LICENSING / THIRD-PARTY-NOTICES（Lekton OFL、Material Symbols Apache-2.0）/ AI-GENERATED-NOTICE
- 验证：安装→启动→新建→写谱→播放→保存→卸载全流程

合计约 6–7 个工作日当量（AI 会话里按 2–3 轮推进，每轮结束给可运行产物）。

---

## 13. 风险与规避

| 风险 | 影响 | 规避 |
| --- | --- | --- |
| 自绘控件 IME 兼容性 | 中文无法输入 | M3 第一天做 spike，先测微软拼音/搜狗；失败则用隐藏 TextBox 作输入代理（`TextCompositionManager` 事件转发） |
| winmm 端口被占用/无 GS 波表 | 无声 | 端口枚举 + 设置项 + 静音降级 + 明确提示 |
| 播放时钟抖动 | 高亮与音频不同步 | Stopwatch 绝对时间 + 提前量；音符量化到 ticks；采样验证 |
| 本机截图不可靠（虚拟显示器） | 无法目测 | 一律用进程内 harness 读 VisualTree / UIA 验证 |
| NuGet SSL / 包缺失 | 无法构建测试 | 运行时零依赖；测试若失败退回自写 harness |
| 高 DPI 125% 坐标偏差 | 点击/滚动错位 | 全部用 DIP；PerMonitorV2；harness 验证命中测试 |
| 大文件卡顿 | 体验差 | 可见行虚拟化 + 行缓存 + 200 ms 防抖解析 |
| 语法规则歧义 | 返工 | 规则集中为常量表；单测锁定；第 14 节待确认项先按默认实现 |
| .NET 10 SDK 被系统更新破坏 | 无法构建 | 会话内不更新；症状出现 `winget install Microsoft.DotNet.SDK.10` |

---

## 14. 已确认决策

**快捷键**
- 播放 / 暂停 / 停止回到开头：**只通过工具栏按钮**，不设快捷键。
- 放弃空格（播放）与 Ctrl+Space（停止）：编辑区是纯文本编辑，所有按键都用于输入。
- 保留计划书里的编辑类快捷键：Ctrl+Z / Ctrl+R（另加 Ctrl+Y）/ Ctrl+O / Ctrl+N / Ctrl+S。

**语法**
- 3/4 拍每行允许 0 / 1 / 2 / 3 个逗号（段数 1 / 2 / 3 / 4，每段 3 / 1.5 / 1 / 0.75 拍）。
- 休止 `0` 是占位单元并截断前一个音：`1` = 全音符，`1,0` = 半音符，`1,0,0,0` = 四分音符。
- 音符时值统一取"本行下一个单元起点"，没有下一个单元才延长到小节末尾。

**其他**
- 力度抖动每次播放/导出重新随机（导出文件内固定）。
- 新建文档给一段可播放的模板（带注释，Ctrl+A 可删）。
- 调式本期只支持 A-G 单字母，`PitchMapper` 预留 12 个根音的扩展点。
- 必须写完整的 `1 = C 4/4`，不能省掉 `1 =`。

---

## 15. 编码约定

- C# / XAML 文件 UTF-8；`.ps1` 必须 UTF-8 **with BOM**；`.cmd` 纯 ASCII
- XAML 注释禁止出现 `--`
- 所有 UI 文本中文；诊断消息中文；代码标识符英文
- 所有颜色/尺寸走 `DynamicResource`，禁止硬编码在 View 里（Palette 除外）
- 解析器纯函数式（输入 string → ParseResult），无静态可变状态，便于测试
- 提交粒度按里程碑；每个里程碑结束时 `dotnet build` + 测试全绿 + 可运行

---

## 16. 进度

| 里程碑 | 状态 | 说明 |
| --- | --- | --- |
| M0 基建 | 完成 | csproj / manifest / 多尺寸 icon.ico（tools/make-icon.py）/ 深色浅色调色板 / Lekton + Material Symbols 字体资源 / 工具栏 + 状态栏骨架 / 主题切换 |
| M1 语法核心 | 完成 | AttmLexer + AttmParser + PitchMapper + NoteMerger + 23 个诊断码；`tests/ATTM.Tests` 64 条断言全绿 |
| M2 MIDI 导出 | 完成 | 手写 SMF（format 0 / PPQ 480 / tempo / 拍号 / Program 0）；导出后用内置 reader 回读比对；ffprobe 验证 8.000s / 181 字节 |
| M3 编辑器 | 基本完成 | 自绘控件：输入、选区、鼠标、上下左右/Home/End/PageUp/Down、双击选词、三击选行、语法高亮、错误波浪线、行号、当前行、插入符闪烁、撤销 200 步（连续输入/退格合并）、Ctrl+滚轮缩放；IME 组合串渲染已接入，需实机验证中文输入 |
| M4 播放 | 完成 | winmm 直连 MIDI 输出端口（枚举 / 自动选择 / 打开 / 关闭）、事件调度（Stopwatch + 12ms 提前量）、暂停发 AllNotesOff 并记录正在响的音、恢复重发、停止回到开头、播放位置多区间高亮（支持和弦与并行声部） |
| M5 文件与设置 | 完成 | AppConfig（版本迁移）、设置窗口（主题三态 + MIDI 设备，改动即时生效）、打开（编码探测：BOM/UTF-8/GB18030）、拖入与粘贴文件路径、自定义保存对话框（双文件路径预览）、保存双写 .attm + .mid、Ctrl+S 快速覆盖、Ctrl+Shift+S 另存为、未保存保护 |
| M6 打磨 | 完成 | 主题三态（跟随系统 + 系统主题监听）、设置即时生效、默认字号 18 + 行距 1.38、从光标处播放 + 播放跟随滚动、窗口位置/大小/最大化记忆（离屏自动回退）、默认保存目录 音乐\ATTM、大文件（2000 行）性能验证。明确不做自定义主题色 |
| M7 打包 | 完成 | 默认**框架依赖**发布（ATTM.exe 1.7 MB、zip 0.8 MB、安装包 2.6 MB），安装时自动检测并安装 .NET 10 桌面运行时（`assets/install-dotnet.cmd`），仍保留 `-SelfContained` 选项；文件关联/开始菜单/卸载齐全。安装→启动→卸载实测通过 |

补充说明：

- 修复：字体文件实际在 `assets/Lekton/` 与 `assets/MSS-default.ttf`，与 `assets/fonts/` 的 URI 不一致，导致图标与等宽字体全部回退（图标显示为豆腐块）。现已统一移动到 `assets/fonts/`，并用 `GlyphTypeface.CharacterToGlyphMap` + 位图墨点统计做了真实字形验证。
- 测试最终采用零依赖控制台 harness（`tests/ATTM.Tests` 64 条），不依赖 NuGet 测试包；`tests/ATTM.UiChecks`（25 项）用进程内 VisualTree 采样 + 位图渲染验证字体、主题、编辑器输入/撤销/高亮/报错（本机截图不可靠）。
- 根目录不放 .sln：`dotnet build` 直接构建 `ATTM.csproj`；测试用 `dotnet run --project tests/ATTM.Tests`。
- `dotnet run --project tests/ATTM.Tests -- --export <in.attm> <out.mid>` 可以把任意 .attm 导出成 .mid，用于手工验证。

- `tests/ATTM.UiChecks -- --dump out.png` 可以把窗口渲染成 PNG，便于人工检查（本机截图工具不可靠）。

### M4 实测记录

- 本机枚举到 3 个 MIDI 输出设备：`Microsoft GS Wavetable Synth`、`MIDI-A`、`MIDI-B`。
- 播放调度用假时钟 + 录音输出做了逐事件断言（NoteOn / NoteOff 顺序、同 tick 上 NoteOff 先于 NoteOn、暂停重发、停止复位）。
- UI 测试里真实点击了播放按钮，走的是 winmm 实际端口，状态栏进入"播放中"，编辑器出现播放高亮，停止后回到"就绪"。

### 2026-11-01 反馈修复

- 主题"反向"根因：`MainWindow` 初始化时写成了 `SetTheme(!config.IsDark)`，参数取了反；设置窗口的下拉索引与按钮图标也随之错位。现已统一走 `ThemeService.SetMode(config.Mode)`，并新增 `AppTheme` 枚举（避免与 .NET 10 的 `System.Windows.ThemeMode` 撞名）。
- 行距偏松：Lekton 的字体行高（hhea）比实际字形高很多，旧公式 `max(sample.Height*1.35, fontSize*1.55)` 会把行高撑到字号的 1.8 倍以上。改为纯字号计算 `round(fontSize * 1.38)`，默认字号 15 → 18。
- 配置迁移：`Version` 字段（0/1 → 2）把旧配置的 15 号字提到 18；同时用 `[JsonIgnore]` 去掉序列化里的计算属性。
- 跟随系统：读注册表 `AppsUseLightTheme`，并用 `SystemEvents.UserPreferenceChanged` 监听（切回 UI 线程后刷新）。
- 设置窗口：去掉保存按钮，下拉框/字号按钮改动立即应用并写入配置；窗口标题栏随主题刷新。

### 2026-11-01 第二轮反馈

- 设置窗口精简：删掉编辑器字号调整、设备说明文字、底部"改动会立即生效并自动保存 + 关闭"（标题栏已有叉）。窗口只留主题三态与 MIDI 输出设备。
- 新增**从光标处播放**：工具栏第二个按钮（`Ctrl+播放` 位置），按光标所在的小节起播。实现要点：
  - 解析器记录 `MeasureStartLines`（每个小节第一行的行号），`ParseResult.GetMeasureIndex(offset)` 把光标行换算成小节序号。
  - `ScorePlayer.Play(TimeSpan? startAt)` 支持中途起播：跳过该时刻之前的事件，并把此刻仍在时值内的音补发出去，听感不缺拍。
  - `FindNextEventIndex` 改为严格小于比较，避免起点正好落在事件时刻时把第一个音吞掉。
  - 播放时若高亮音符跑出视野，编辑器自动纵向跟随滚动（受配置里 AutoScrollOnPlay 控制）。
- 文件功能（M5）：
  - `Services/TextFile.cs`：读 BOM → 严格 UTF-8 → GB18030 回退；写统一 UTF-8 无 BOM + LF。
  - `Views/SaveAsWindow`：文件名、目录（OpenFolderDialog）、.attm/.mid 双路径预览、覆盖确认。
  - 打开/新建/保存/另存为、拖入文件、Ctrl+V 粘贴文件路径、未保存保护（保存/不保存/取消）。
  - 保存时同时写 `.attm` 与同名 `.mid`；有语法错误时禁止保存并提示。

### 2026-11-01 第三轮反馈

- 明确不做自定义主题色：删掉 `AppConfig.AccentColor`，主题固定为 `#FF4E6A`，计划里 M6 不再保留该项。
- 默认保存目录改成 `音乐\ATTM`（`Environment.SpecialFolder.MyMusic`），首次使用时自动创建；之后记住上次保存的目录（`DefaultSaveFolder`）。
- 窗口位置/大小/最大化记忆：关闭时写入 `WindowLeft/Top/Width/Height/WindowMaximized`，启动时恢复；若落在虚拟屏幕外（拔了副屏）自动回退居中。
- 配置版本升到 3，兼容 1/2 的旧值。
- M7 落地：`tools/publish.ps1`（自包含单文件 + zip + 可选 Inno Setup）、`packaging/ATTM.iss`，以及 README、LICENSING、THIRD-PARTY-NOTICES、AI-GENERATED-NOTICE。

### 2026-11-01 第四轮反馈

- 新增 `ATTM 语法教程.md`：以项目计划书的语法章节为底，按实际实现逐条核对后重写（八度用 `+`/`-`、升号 `#` 紧贴数字、和弦括号、并行声部、注释规则、错误对照表），并加了"教程示例都能解析"的自动化测试防漂移。
- `packaging\ATTM.iss` 补全为可用状态（语言包、图标、开始菜单、`.attm` 关联、卸载清理），并把 `tools\publish.ps1` 的 Inno 探测改为"先读注册表，再查常见路径，最后接 `D:\Program Files (x86)\Inno Setup 6`"——本机 Inno 装在 D 盘也能找到。
- 实际产物：`dist\installer\ATTM-0.1.0-setup.exe`（54.7 MB），已跑通 静默安装 → 启动 → 静默卸载。

### 2026-11-01 打包策略调整

- 安装包改为**框架依赖**，不再默认自包含：`dist\installer\ATTM-0.1.0-setup.exe` 从 54.7 MB 降到 **2.6 MB**。
- `assets/install-dotnet.cmd` + `install-dotnet.ps1` 改名去掉临时备注、换成 ATTM 文案，随包一起分发。
- `packaging\ATTM.iss` 新增 `[Code] NeedsDotNet`（扫描 `%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\10.*`），
  缺运行时才在 `[Run]` 里静默调用 `install-dotnet.cmd /quiet`；开始菜单也放了一个手动入口。
- `tools\publish.ps1` 默认 `--self-contained false`，需要绿色版时加 `-SelfContained`。
- 实测：安装（含运行时检测）→ 启动 → 卸载 全流程通过；`install-dotnet.ps1` 在已安装环境下正确跳过并返回 0。

### 2026-11-01 第五轮反馈

- 八度标记支持叠加：`++1` 高两个八度、`+++#1` 高三个八度的 #1、`--1` 低两个八度；
  混用 `+-1` 报错，休止符仍不允许带八度标记。原来"重复标记报错"的测试改为断言合法性。
- 编辑器右侧新增缩略图 `Editing/EditorMinimap.cs`：整篇谱面压缩成小横条（注释/指令/音符三种颜色，
  错误行标红），叠加"当前视野"方框与光标行标记；点击或拖动可直接跳转，行数超过 320 行自动抽样绘制。
- 界面检查新增"缩略图已绘制在右侧"，确认位置在编辑器右侧 76px 栏内且有实际绘制内容。

### 2026-11-01 缩略图视觉调整

- 第一版每行一根实心条，长文档直接糊成一坨；改成"分桶 + 桶间留空"（桶高 4px、条高 1.8px）。
- 渐变版本对比度仍然偏高，最终改为**低透明度 + 降饱和**的纯色横条：
  正文 0x34、注释 0x20、指令 0x3A、错误 0x7C，饱和度分别下调 20%-35%。
- 视野框改成主题色 0x16 的圆角框，光标行改成右侧小圆角胶囊。
- 新增测试"缩略图：长文档不会糊成一片"，用 600 行文档统计墨点占比（实测 50%，阈值 4%-55%）。

### 2026-11-01 缩略图渲染修复与直角风格

- 修 bug：缩略图内容层原来用"TextBuffer 引用是否变化"判断重建，但 TextBuffer 是原地修改的，
  打字后引用不变 -> 内容一直不更新。改为 `TextBuffer.Version` 自增计数，并补上"面板尺寸变化"也重建。
- 正文区隐藏横向/纵向滚动条（导航交给右侧缩略图），Shift + 滚轮保留横向滚动。
- 全局去掉圆角：控件模板与保存对话框的 CornerRadius 全部改为 0，缩略图的条、视野框、光标条也改成直角。

### 2026-11-01 缩略图对齐修复

- 根因：缩略图用了两套坐标——缩略图的条按"固定 4px 一个桶"自上而下排，视野框却按
  "文档真实高度"缩放，短文档时两者完全对不上（20 行的谱子，条在顶部 80px，视野框却在 300px 处）。
- 改为统一映射：`linePixel = clamp((height - 边框) / 行数, 0.05, 6)`，
  条、视野框、光标标记全部用 `YForLine()` 同一个函数换算。
- 行太密时（linePixel 很小）按 `ceil(4 / linePixel)` 行合成一个桶，保证条与条之间仍有空隙。
- 新增测试"缩略图与内容对齐"：滚到 0% / 35% / 100% 三处，比较视野框顶边与
  `YForLine(offset / lineHeight)`，实测完全一致（3.0/193.4/546.9）。

### 2026-11-01 语法教程精简

- 585 行 -> 319 行，结构从 13 节收成 10 节 + 一页速查。
- 合并重复内容（时值说明、和弦/多声部、错误表），错误表改成 Markdown 表格。
- 补上第三轮之后的新语法：八度叠加 `++1 / +++#1 / --1`、升号调式 `1 = #C / 1 = C#`、
  右侧缩略图、无滚动条、保存双写、编码探测。
- 修正 README 示例里的两处真错误：4/4 下 3 段和弦（不合法）、连续 4 行被当成并行声部。
- 新增"README：示例谱面都能解析"测试，与"教程示例"一起防止文档和实现走散。

### 2026-11-01 双击关联打开修复

- 问题：安装包把 `.attm` 关联到 `ATTM.exe "%1"`，但程序从不看命令行参数，双击只会打开新建文档。
- 修法：`App.xaml` 去掉 `StartupUri`，改在 `OnStartup` 里解析参数（跳过 `-` 开头的选项、去掉引号、
  只接受真实存在的文件），再传给 `MainWindow(string? initialFile)`，在 Loaded 里调用 `LoadFile`。
- 参数不存在时回退到新建模板，不会弹错误。
- 测试：`命令行参数能打开 .attm（双击关联）`、`不存在的命令行参数回退到新建模板`。

### 2026-11-01 乐句预览侧边栏

- 工具栏新增切换按钮（Material Symbols `subtitles` U+E048），在缩略图右边再展开一块 460px 的侧边栏。
- `Editing/PhrasePreview.cs`：自绘面板，整行只排一次 FormattedText、再用 `SetForegroundBrush`
  按 token 上色，配色/字体与编辑器完全一致（第一版是分段各排一次，字距会错乱）。
- 换小节时整组文字逐行从"行框外"滑入（偶数行从左、奇数行从右，每行错峰 80ms，
  用 PushClip 裁到行框内），旧的一组同时淡出。进度用 TickCount64 自己算，
  `DispatcherTimer` 只负责触发重绘——离屏/虚拟显示环境下 WPF 动画时钟可能不推进。
- 实时高亮：`SetActiveSpans` 把"正在发声的音"映射成行内列区间，绘制时在该列区间下面垫
  一层 PlayHighlight 矩形 + 2px 主题色下边线，跟着文字一起滑动。
- 播放位置换算小节：`ticks / MeasureTicks`，静音段也能正确定位；停止时淡出消失。
- 侧边栏宽度自己驱动（16ms 定时器 + CubicEase），不用 WPF 动画，测试可确定性验证。
- 展开时窗口最小宽度提到 1180，太窄自动撑宽，避免正文被挤没。
- 新增 6 项界面检查：按钮存在、展开在缩略图右侧、并行声部两行都显示、逐行滑入、
  语法配色沿用、实时框选、重复点击收起、大字排版不重叠（墨迹宽度对比排版宽度）。

### 2026-11-01 乐句预览：退场先于入场

- 问题：旧句淡出（200ms）和新句滑入（340ms + 错峰）同时开始，两段大字在屏幕上重叠，看着发花。
- 修法：旧句退场压到 150ms 并逐行早 40ms 走（从上往下依次退），新句延后 190ms 再入场；
  这 190ms 里新句进度为 0，不会画出任何东西。
- 快节奏保护：如果上一句还没入场完就换句，空隙缩到 60ms，避免屏幕长时间空白。
- 新增检查"旧句先退场、新句后入场（不重叠）"：换句后 100ms 新句进度必须为 0（旧句仍在退场），
  330ms 时已入场，最终 1.0。

### 2026-11-01 乐句预览动效曲线 / 帧率 / 升降八度配色

- 曲线：入场改成缓出 `1-(1-t)^3`（起步快、收尾稳），出场改成缓入 `1-t^3`
  （起步慢、后段加速离开），两个函数提成 public static 便于测试。
- 帧率：原来每一帧都要重新 `FitFontSize`（按 64px 量每行文字）+ 逐 run 上色 +
  新建 RectangleGeometry，帧成本高。改成 `PhraseLayout` 排版缓存：字号、每行 FormattedText、
  左偏移、裁剪矩形、高亮矩形只在"内容/尺寸/主题"变化时重建一次；
  动画帧里只剩 PushTransform + DrawText。
  动画时钟也从 16ms DispatcherTimer 换成 `CompositionTarget.Rendering`（与渲染同步），
  另留一个 200ms 的 DispatcherTimer 兜底（离屏环境没有渲染事件）。
  实测 40 帧 0.68 ms/帧、排版重建 0 次。
- 配色：`+`（升八度）和 `-`（降八度）分开着色。新增 `EditorTokenClass.OctaveDown` 与
  主题资源 `Brush.Syntax.OctaveDown`（深色 #FF5FC8D8、浅色 #FF0E7C86），升号仍是紫色
  （深 #FFC792EA / 浅 #FF7C3AED）。编辑器和乐句预览同时生效。
- 界面检查加到 48 项：新增"升八度/降八度分开配色""入场缓出、出场缓入""动画帧只重绘不重排"。

### 2026-11-01 乐句预览节奏收紧

- 反馈："消失稍晚、出现也稍晚"——旧句在新小节里还赖着，新句又等太久才入画。
- 参数调整（都在 PhrasePreview 顶部常量里）：
  出场 170 -> 110ms，出场错峰 32 -> 15ms，入场等待 190 -> 125ms，
  入场时长 320 -> 280ms，行错峰 80 -> 60ms，快节奏空隙 60 -> 45ms，
  滑动距离 64 -> 40px（位移短，文字更快落到可读位置）。
- 现在的节奏：换句后 ~0ms 旧句开始退，~110ms 退完，~125ms 新句开始入画，
  ~190ms 已经能读，~405ms 完全就位。仍然保持先后次序，不重叠。

### 2026-11-01 乐句预览：新旧叠加，出场改纯渐隐

- 反馈："还是晚了"——只要出场没走完，新句就进不来，中间的空档就会被感知成延迟。
- 改成叠加：`InDelayMs = 0`，新句在换小节那一刻立即开始滑入+淡入；
  旧句在它下面纯渐隐（`OutDurationMs = 90`，线性 `1-t`，不位移、不错峰）。
- 旧句起点透明度压到 0.5（`OutAlphaCap`），新字压在上面读起来更清楚。
- 入场 240ms、行错峰 60ms、滑动 40px：约 60ms 就能看到新句、约 150ms 可读。
- 检查项跟着改：`换句时新句立即入场、旧句渐隐`（60ms 处新句进度必须 > 0.05），
  `入场缓出、出场线性淡出`（出场采样 1.00/0.75/0.50/0.25/0.00）。
- 升/降八度配色检查改成"token 分类 + 调色板取值 + 屏幕最近色"，不再依赖精确像素命中
  （小字号抗锯齿会把颜色带偏，之前会偶发失败）。

### 2026-11-01 乐句预览：小节结束前提前退场

- 反馈："入场保持不变，出场再早一点"。现在出场和新小节是同时开始的，感知上还是晚。
- 做法：播放位置每 tick 计算"距离本小节结束还有多久"，剩余 <= 160ms 时先让旧句开始渐隐
  （`MainWindow.PhraseFadeLeadSeconds`），等真正换小节时屏幕上已经是干净的，
  新句入场逻辑完全不变（0 延迟、滑入 + 淡入、240ms）。
- `_fadedMeasure` 记录已提前退场的小节，避免同一小节里"退了又显示"；
  暂停恢复 / 从头播放 / 从光标播放都会重置它。
- 出场时长 90 -> 85ms。
- 新增检查"小节结束前提前退场"：bpm 480（一小节 0.5 秒）下播放 1.4 秒，
  必须同时采样到"有内容"和"提前空场"两种状态。

### 2026-11-01 修乐句提前退场时的两处闪烁

- 现象：小节之间空场的那一小段，会闪出"播放时在这里显示当前乐句"的提示，
  右上角"第 N / M 小节"也会闪一下。
- 原因：`Hide()` 把 MeasureIndex 归零 -> 提示文案的判断条件成立、顶部计数消失；
  新小节 Show 之后又都恢复，于是闪一帧。
- 修法：
  - `Hide(keepMeasure)`：提前退场时保留小节号，只有真正停止/结束时才清空。
  - 空状态提示只在 `IsShowingIdleHint`（MeasureIndex < 0 且无内容无出场动画）时画。
- 测试补强："小节结束前提前退场"这条现在同时断言空场期间不露提示、不丢计数；
  采样循环遇到"播放结束"就退出，避免把正常清空误判成提前空场。

### 2026-11-01 保存/打开语义、示例谱子与默认模板

- 工具栏保存键改为"另存为"：始终弹出 SaveAsWindow 让用户重新选位置；
  `Ctrl+S` 才是快速保存（没保存过时自动弹窗），`Ctrl+Shift+S` 另存为。
- 状态栏文件名改成显示完整保存路径（过长省略），点击用 `explorer /select` 定位到文件；
  还没有路径时显示"未命名.attm"且不可点。
- 工具栏打开键与 Ctrl+O 改成"打开谱库文件夹"（音乐\ATTM），不再弹文件对话框；
  打开文件走"资源管理器里双击 .attm"（命令行参数已在 0.1.7 支持）或拖入/粘贴路径。
- 默认新建模板替换成 `samples/小星星 - 演示谱.attm` 的内容。
- 安装包新增 [Files]：把三个示例谱子（欢乐颂 / 小星星 / Affix Your Name）和
  《ATTM 语法教程.md》装到 `{code:GetMusicFolder}\ATTM`，
  `onlyifdoesntexist` 不覆盖用户改动、`uninsneveruninstall` 卸载也不删。
  `GetMusicFolder` 走注册表 `Shell Folders\My Music`（兼容用户把音乐目录重定向到 D 盘）。
  绿色版不做这一步。
- 测试：新增"示例谱子都能解析"（samples/*.attm 全部可播放）、
  "新建文档默认载入《小星星》示例"（逐字符比对模板与示例文件）、
  状态栏路径可点击；音符数断言改成相对比较，不再写死 11。

### 2026-11-01 新语法：行八度前缀 $ / % 与连奏 legato()

- `$` / `%`：词法新增 `TokenKind.LineOctaveUp/LineOctaveDown`（一字符一 token）；
  解析时在切段之前剥离行首前缀（`Parser.ReadLinePrefix`），把偏移带进 `VoiceLine.OctaveShift`，
  再由 `TryParseNote` 加到每个音的 octave 上——所以和弦内部的音也一起升降，且只影响本行。
  校验三条：必须在行首、后面必须跟空格（读原始 token 判断）、不能混用 `$` 和 `%`。
  诊断码 118/119/120。
- `legato(...)`：`ParseSegment` 里当作一个单元；`Unit.IsLegato` 标记。
  生成事件时组内音等分这一单位的时间，但每个音的结束位置取**所属段的末尾**
  （`flat` 里带上 `SegmentEnd`），所以 `legato(1234)` 每个音都撑到小节末，
  `legato(135+7),legato(13)` 第一组撑到第一段末、第二组撑到小节末。
  诊断码 121/122/123/124（缺括号 / 空 / 拍位数不是 2 的幂 / 嵌套或逗号）。
  组内允许休止符 0：它占一个拍位但不发声，其余音照样撑到段末（`legato(1020)`）。
- 高亮：`EditorTokenClass.Keyword`（对应 Directive 蓝色），`$`/`%` 复用升/降八度配色。
- 测试 89 条：行前缀三种写法 + 和弦 + 叠加、legato 时值表、legato 与 `$` 连用等价、
  四类非法写法。教程新增 3.1 与第 6 节，README 速查同步，错误表补 6 行。

### 2026-11-01 0.2.0 收尾：示例谱子与语法速查

- 默认模板同步到用户新版《小星星 - 演示谱》（带 $ / % 与 legato 示例、行尾注释）。
- `samples/语法速查.attm` 重写：按"指令 / 音符与时值 / 八度与升号 / 行前缀 / 和弦 /
  连奏 / 并行声部"七段组织，每段都配可直接播放的示例，12 个小节覆盖全部语法。
- 之前 `_probe.cs` 临时文件混进根目录顶替了入口点（CS7022），导致本地构建"成功但启动异常"，
  已删除；以后验证语法一律用独立临时项目。

### 2026-11-01 修"打开谱库文件夹"跳错目录

- 现象：点工具栏打开键，资源管理器跳到了"上次保存的目录"，而不是 音乐\ATTM。
- 原因：`OpenLibraryFolder()` 用的是 `AppConfig.ResolveSaveFolder(_config)`，
  它会优先返回 `config.DefaultSaveFolder`（上次另存为的目录）；测试把一份谱子存到
  samples 里，配置就被带偏了。
- 修法：新增 `AppConfig.LibraryFolder`（固定 = 系统音乐文件夹\ATTM，不存在就建），
  打开谱库只认它；`ResolveSaveFolder` 在没记过目录时也复用它。
- 测试：`谱库目录固定是 音乐ATTM`（断言 LibraryFolder 等于 MyMusic\ATTM，
  且即使 DefaultSaveFolder 指向别处，保存目录才跟随、谱库不跟随）。

### 2026-11-01 修安装程序静默装 .NET 时"卡住"

- 现象：安装包里自动装 .NET 10 运行时那一步没有任何窗口，长时间停在状态提示上，像卡死。
- 原因：[Run] 里用了 `Flags: runhidden`，把 60 MB 下载的进度条和"请在 UAC 窗口点是"
  的提示全藏起来了；而且脚本里 `Start-Process -Verb RunAs` 没等子进程，安装程序会提前往下走。
- 修法：
  - .iss 去掉 runhidden（保留 waituntilterminated），状态提示写明"会弹出一个窗口显示下载进度"。
  - install-dotnet.ps1：提权时 `-PassThru` + `WaitForExit()`，等子进程真正装完再判断结果，
    并在父窗口里汇报成功/失败；curl 加 `--retry/--connect-timeout/--max-time`（下载 30 分钟上限）；
    安装用 `WaitForExit(20 分钟)`，超时就 Kill 并提示手动安装，不再无限挂。
  - 安装结束时若仍缺运行时，`CurStepChanged(ssDone)` 弹一句人话提示；
    "立即启动 ATTM" 也加上 `Check: not NeedsDotNet`，避免直接报 .NET 错误。
- 验证：aka.ms 下载链接返回 60 MB 文件；脚本在本机（已装运行时）走"无需重复安装"分支退出 0；
  安装包静默安装/卸载正常；安装目录里手动跑 install-dotnet.cmd 也能正常返回。
