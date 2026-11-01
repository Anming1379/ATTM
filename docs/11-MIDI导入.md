# 11 · MIDI 导入

> **本文描述的功能现在是一个插件**（`attm.midi-import`，源码在 `plugins/ATTM.Plugin.MidiImport/`）。
> 插件的通用机制见 [12-插件系统](12-插件系统.md)；删掉插件目录后这个功能会整体消失，程序其余部分不受影响。
>
> 把已有的 `.mid` 一键转成 ATTM 简谱文本，接着就能改、能播、能再导出。

---

## 1. 怎么用

| 入口 | 操作 |
| --- | --- |
| 工具栏 | 「导入 MIDI」按钮（`︎E8E5` 上传图标），在「打开」和「保存」之间 |
| 快捷键 | `Ctrl+I` |
| 拖放 | 把 `.mid` / `.midi` 拖进窗口 |
| 剪贴板 | 复制一个 `.mid` 文件后 `Ctrl+V` |
| 命令行 / 打开方式 | `ATTM.exe 某曲.mid`（有未保存改动会先问） |

导入后：

- 编辑器内容被替换成生成的简谱文本；
- 文件名变成「原文件名.attm」，但**不绑定路径**——第一次保存会重新问位置，避免和原 `.mid` 同名互相覆盖；
- 状态标记为「未保存」；
- 如果转换有近似（见 §4），会弹一次提示说明原因，同样的内容也写进了文件头注释。

---

## 2. 转换管线

```text
*.mid / *.midi
   │  Core.Midi.SmfReader          解析 SMF（format 0/1/2，含 running status）
   ▼
SmfDocument
   │  { Format, TicksPerQuarter, Bpm, BeatsPerMeasure, BeatUnit, KeyFifths, KeyMinor, Notes[] }
   │  Core.Import.MidiToAttm       量化 + 调式换算 + 排版
   ▼
MidiImportResult { Text, MeasureCount, NoteCount, KeyName, TimeSignatureName, Bpm, RhythmExact, Warnings }
   │
   ▼
编辑器（就是普通 ATTM 文本，随后走既有的解析 / 播放 / 保存流程）
```

设计上**没有**让导入器直接产出 `Score`：它产出**文本**，因为文本才是用户要改的东西，也天然复用了既有的解析、错误提示和 MIDI 导出链路。

---

## 3. 转换规则

### 3.1 时间基准

源文件的 `division`（PPQ）可能不是 480，所有 tick 按 `480 / 源PPQ` 等比缩放并四舍五入。SMPTE 时间基准（`division & 0x8000`）不支持，直接报错。

### 3.2 速度与拍号

- 速度取文件里第一个 tempo meta（`FF 51 03`），缺省 120 BPM；结果四舍五入到两位小数并 clamp 到 1–1000。
- 拍号取 `FF 58`。**只支持 4/4 与 3/4**；其它（6/8、5/4…）按 4/4 排版并给出提示。

### 3.3 调式

1. 有调号 meta（`FF 59`）→ 用五度圈换算主音：`tonic = (fifths × 7) mod 12`；小调取关系大调（+3 半音）。
2. 没有 → 用音级直方图给 12 个主音各打一个分（音阶内 +1，音阶外 −1.2，主音额外 +0.8），取最高分。
3. 主音映射到 ATTM 能表达的写法（12 个音级全覆盖）：
   `C C# D D# E F F# G G# A A# B`（`D#` 即 `Eb`，`G#` 即 `Ab`，都是等音）。

### 3.4 音高

```text
rel    = pitch − (60 + 根音偏移 + (调式带升号 ? 1 : 0))
octave = floor(rel / 12)
pc     = rel − octave × 12
```

`pc` 命中自然大调音阶 → 直接写数字；否则找「某个音级的自然音 + 1 半音」→ 写 `#`；数字前按八度写 `+`/`-`（可叠加）。

同一时刻（同一 tick）起音的音合成和弦：`(46+1)`。

### 3.5 节奏（关键）

简谱文本的节奏是「一个音响到下一个单元开始」，所以**排版形状本身就决定了时值**。导入器按以下顺序尝试：

```text
对每个小节：
  对 分段数 S ∈ {1,2,4}（4/4）或 {1,2,3,4}（3/4）：
     把小节切成 S 等份；
     逐段求最小单元数 U ∈ {1,2,4,8}，使段内每个音的起点与终点都落在单元边界上；
     再要求「每个音的时值恰好等于一个单元」；
     全部满足 → 采用这个方案（输出 N 个 token，空的写 0）
  都不满足 → 退回十六分网格量化，并标记 RhythmExact = false
```

因为 `S` 与每段的 `U` 可以不同，四分音符、八分音符、半音符混排通常是**精确**的：

```text
原曲：│ 1 2 3 4 │ 5  5  6 6 │
输出：1,2,3,4
      5,56,6,0        ← 第三段是八分音符对
```

### 3.6 力度

`Vel` 取全曲平均力度，抖动取标准差（都 clamp 到合法区间）。例如平均 96、标准差 7 → `Vel = 96 +-7`。

---

## 4. 会丢什么（务必知道）

| 项目 | 结果 | 原因 |
| --- | --- | --- |
| 多声部 / 多轨 | 合并成单声部（同一时刻的音变和弦） | 简谱并行行需要人工判断哪条是旋律 |
| 连奏、延音踏板 | 不保留 | 文本里没有对应记号 |
| 渐强渐弱、弯音、控制器 | 不保留 | 只读音符 |
| 长音跨小节 | 拆成两段（同音重复） | 没有连结线机制 |
| 三连音等非二等分节奏 | 量化到十六分网格 | 段内单元数只能是 2 的幂 |
| 弱起小节 | 原样保留为若干 `0` | 与小节对齐，属于预期行为 |

导入结果在语义上是「**可编辑的近似谱**」，不是无损还原。

---

## 5. API

```csharp
namespace ATTM.Core.Midi;

public sealed record SmfNote(long StartTicks, long DurationTicks, int Pitch, int Velocity);
public sealed record SmfDocument(int Format, int TrackCount, int TicksPerQuarter, double Bpm,
    int BeatsPerMeasure, int BeatUnit, int? KeyFifths, bool KeyMinor, IReadOnlyList<SmfNote> Notes);

public static class SmfReader
{
    public static SmfDocument Read(string path);
    public static SmfDocument Read(byte[] data);      // 结构非法时抛 SmfFormatException
}
```

```csharp
namespace ATTM.Core.Import;

public sealed record MidiImportResult(string Text, int MeasureCount, int NoteCount, string KeyName,
    string TimeSignatureName, double Bpm, bool RhythmExact, IReadOnlyList<string> Warnings);

public static class MidiToAttm
{
    public static MidiImportResult Convert(SmfDocument document, string? sourceName = null);
}
```

`Convert` 是**纯函数**：同样的 `SmfDocument` 永远产出同样的文本，不读写文件、不碰 UI，所以可以直接在控制台测试里跑（见 §6）。

---

## 6. 测试覆盖

`tests/ATTM.Tests` 里的「MIDI 导入」一组：

| 断言 | 验证点 |
| --- | --- |
| 4/4 四分音符往返后音高与时值一致 | 写 MIDI → 读回 → 转文本 → 再解析，逐个音比对 |
| 和弦转成括号 | 同时起音合并成 `(…)` |
| 3/4 保持三拍 | 拍号与小节数不串 |
| 长音不被拆碎 | 半音符仍是 960 tick |
| 调式推断为 C 大调 | 无调号 meta 时的推断 |
| 仓库示例 `.mid` 能转换回可播放谱面 | 真实文件端到端 |
| 坏文件给出明确异常 | 错误路径 |

---

## 7. 排错

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 「不是 MIDI 文件（缺少 MThd 头）」 | 文件其实是 RMI/RIFF 封装或已损坏 | 用 DAW 另存为 `.mid` |
| 「暂不支持 SMPTE 时间基准」 | 极老的序列器文件 | 先转成 PPQ 时间基准 |
| 「文件里没有可用的音符」 | 只有控制器/歌词，或全被力度 0 关闭 | 检查源文件 |
| 生成的谱子难听 | 三连音被量化、多声部被合并 | 手工改；一次导入只当草稿 |
| 音高出现大量 `#` | 调式推断选错了主音 | 直接把 `1 = X` 改成正确的调 |
