# 05 · 播放与 MIDI

> 一块是「把乐谱写进文件」，一块是「把乐谱送进声卡」。两者共用同一条 MIDI 短消息时间轴。
>
> 相关源码：`Core/Midi/MidiSequence.cs`、`Core/Midi/MidiFileWriter.cs`、`Core/Playback/*`。

---

## 1. 总览

```text
Score (绝对 tick)
   │
   ▼
MidiSequence.BuildNoteMessages(score, random)   <- 唯一的时间轴来源
   │  List<MidiNoteMessage>{ Tick, Kind(On/Off), Pitch, Velocity }
   ├──────────────► MidiFileWriter.Write      ->  *.mid（SMF format 0）
   └──────────────► ScorePlayer.BuildEvents   ->  TimeSpan 事件表 -> IMidiOutput
```

因为两个消费者都从 `MidiSequence` 取事件，**导出的 .mid 与听到的声音在时间与力度上完全一致**（力度抖动除外：导出与播放各用各的 Random）。

---

## 2. 消息生成 `MidiSequence`

```csharp
public const int Channel = 0;
public static List<MidiNoteMessage> BuildNoteMessages(Score score, Random? random = null);
public static int ApplyJitter(int baseVelocity, int jitter, Random? random);
```

步骤：

1. 每个 `NoteEvent` 产出两条消息：`NoteOn(StartTicks)` 与 `NoteOff(EndTicks, velocity = 0)`。
2. 排序键：`Tick` 升序，同 tick 按 `Kind` 升序——`NoteOff = 0` 在 `NoteOn = 1` **之前**。
3. 力度：`ApplyJitter(VelocityBase, VelocityJitter, random)`。

**为什么 NoteOff 要先发**：同 tick 上如果先发 NoteOn 再发 NoteOff（同一个音高），很多合成器会把刚触发的音直接关掉，听感缺音。

### 力度抖动

```csharp
if (jitter <= 0 || random == null) return clamp(baseVelocity, 1, 127);
return clamp(baseVelocity + random.Next(-jitter, jitter + 1), 1, 127);
```

`Vel = 90 +-10` 时每个音独立取 `[-10, +10]` 的整数偏移，结果永远落在 1–127。

---

## 3. 文件写入 `MidiFileWriter`

```csharp
public static void Write(Score score, string path, Random? random = null);
public static byte[] BuildTrackData(Score score, Random? random = null);
```

### 3.1 文件头

```text
NaN
00 00 00 06          长度固定 6
00 00                format 0（单轨）
00 01                1 个轨道
01 E0                division = 480（PPQ）
```

### 3.2 轨道块

```text
NaN
```

轨道数据按顺序：

| 顺序 | 内容 | 字节 |
| --- | --- | --- |
| 1 | Tempo meta | `FF 51 03 t t t`，t = round(60 000 000 / Bpm)，clamp 到 0xFFFFFF |
| 2 | Time Signature meta | `FF 58 04 nn dd 18 08`，nn = 分子，dd = log2(分母) |
| 3 | Program Change | `C0 00`（通道 0，大钢琴） |
| 4 | 音符消息 | 每条前加 delta 变长整数 |
| 5 | End of Track | `FF 2F 00`，delta = 剩余到 TotalTicks 的 tick 数 |

分母编码：`1→0, 2→1, 4→2, 8→3, 16→4`，其它回退 2（即 4/4 的 4）。

### 3.3 delta 变长整数

标准 SMF 7-bit 变长编码：低位在前反向填充，除最后一个字节外最高位置 1。实现见 `WriteVarInt`。

### 3.4 与播放的差异

| 项 | 导出 | 播放 |
| --- | --- | --- |
| 力度抖动 | 若传入 Random 才随机 | 总是随机（`ScorePlayer` 持有 `Random`） |
| 时间轴 | tick | tick × SecondsPerTick → TimeSpan |
| 踏板/控制器 | 不写 | 不主动发踏板，仅起播/停止时重置 |

---

## 4. 输出端口 `IMidiOutput`

```csharp
public interface IMidiOutput : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    void NoteOn(int channel, int pitch, int velocity);
    void NoteOff(int channel, int pitch);
    void ControlChange(int channel, int controller, int value);
    void AllNotesOff(int channel);
    void ResetControllers(int channel);
}
```

| 实现 | 何时使用 |
| --- | --- |
| `Win32MidiOutput` | 成功打开真实设备 |
| `NullMidiOutput` | 没有设备 / 打开失败。`IsOpen == false`，状态栏显示「（静音：没有可用的 MIDI 输出设备）」 |

**抽象的意义**：`tests/ATTM.Tests/PlaybackChecks.cs` 用一个记录型输出 + 假时钟，完全离线验证播放时序，不需要声卡。

---

## 5. winmm 封装 `Win32MidiOutput`

### 5.1 P/Invoke 清单

| 函数 | 用途 |
| --- | --- |
| `midiOutGetNumDevs` | 设备数量 |
| `midiOutGetDevCapsW` | 设备名（`MIDIOUTCAPS`，Name 固定 32 字符） |
| `midiOutOpen` | 打开设备句柄 |
| `midiOutClose` | 关闭 |
| `midiOutShortMsg` | 发送短消息 |
| `midiOutReset` | 复位（关掉所有挂着的音） |

### 5.2 短消息打包

```csharp
NoteOn  : (uint)(0x90 | (channel & 0x0F)) | (uint)(pitch & 0x7F) << 8 | (uint)(velocity & 0x7F) << 16
NoteOff : (uint)(0x80 | (channel & 0x0F)) | (uint)(pitch & 0x7F) << 8
CC      : (uint)(0xB0 | (channel & 0x0F)) | (uint)(controller & 0x7F) << 8 | (uint)(value & 0x7F) << 16
```

### 5.3 控制器约定

| 控制器 | 含义 | 调用时机 |
| --- | --- | --- |
| CC123 | All Notes Off | 停止 / 暂停 / 完成 |
| CC120 | All Sound Off | 同上 |
| CC64 / 66 / 67 | 延音 / 持音 / 弱音踏板置 0 | `ResetControllers`（起播前、停止时） |

### 5.4 设备选择

```csharp
public static bool TryOpen(int? deviceIndex, out Win32MidiOutput? output, out string error);
```

- `deviceIndex == null` → 打开 `MIDI Mapper`（`0xFFFFFFFF`），由系统决定路由到哪个合成器。
- 失败时 `error` 里带 winmm 返回码，调用方（`MainWindow.ApplyMidiDevice`）回退到 `NullMidiOutput`，界面仍可编辑与高亮。

### 5.5 释放

`Dispose` 顺序：`AllNotesOff(0)` → `ResetControllers(0)` → `midiOutReset` → `midiOutClose`。中间任何一步抛异常都被吞掉（设备可能已被拔出）。

---

## 6. 播放引擎 `ScorePlayer`

### 6.1 状态机

```text
          Play(startAt / null)                 Play()
Stopped ───────────────────► Playing ◄────── Paused
   ▲                            │  Pause        ▲
   │                            └───────────────┘
   │  Stop / 到达 Duration（触发 Finished）
   └──────────────────────────────────────────
```

状态：`enum PlayerState { Stopped, Playing, Paused }`。

### 6.2 位置

```csharp
Position = Playing -> clamp(_base + clock.Elapsed)
         = Paused  -> clamp(_base)
         = Stopped -> TimeSpan.Zero
```

`_base` 是「本次播放的起点秒数」，`clock.Elapsed` 是自那次 `Restart` 以来的流逝。

### 6.3 起播 `Play(startAt)`

1. `BuildEvents()`：把 MIDI 消息转成 `TimeSpan` 事件表（`tick * SecondsPerTick`）。
2. `_base = startAt`，`_next = FindNextEventIndex(startAt)`（二分，用严格小于：正好落在某事件时刻时该事件仍要发）。
3. `Output.ResetControllers(0)`：清掉上一次残留的音与踏板。
4. 若 `startAt > 0`：`CollectSounding(startAt)` 找出**跨越该时刻**的音并立即 `NoteOn`，避免从中间起播时听感缺拍。
5. `clock.Restart()`，状态切 `Playing`，抛 `StateChanged` + `PositionChanged`。

### 6.4 续播（Paused → Play(null)）

1. 记下 `Position`。
2. 把暂停时保存的「仍在时值内的音」重新 `NoteOn`。
3. `_next = FindNextEventIndex(position)`，时钟重启，状态切 `Playing`。

### 6.5 暂停 `Pause`

`_base = Position` → 停表 → `CollectSounding(position)` → `AllNotesOff` → 状态 `Paused`。

### 6.6 推进 `Advance`（由 UI 每 10 ms 调用）

```csharp
var position = Position;
var horizon  = position + Lookahead;          // Lookahead = 12 ms
while (_next < _events.Count && _events[_next].Time <= horizon) 发送该事件;
if (position >= Duration) Complete();
else PositionChanged?.Invoke(...);
```

**前瞻 12 ms 的意义**：10 ms 定时器抖动最大约 10 ms，加上 12 ms 余量后，消息总能「不晚于应有的时刻」发出；晚发会被感知为节奏不稳，早发则不可感知。

### 6.7 完成 `Complete`

`AllNotesOff` → 复位时钟与游标 → 状态 `Stopped` → 抛 `StateChanged` / `PositionChanged` / `Finished`。

### 6.8 播放高亮 `GetActiveSpans`

```csharp
foreach (var note in _score.Notes) {
    if (note.StartTicks * spt > seconds) break;      // Notes 按 StartTicks 升序，可以提前退出
    if (note.EndTicks * spt > seconds) spans.Add(note.Span);
}
```

返回的区间由 `AttmEditor.SetPlaybackSpans` 同时喂给编辑部与乐句预览。

---

## 7. 播放相关的 UI 时序

| 定时器 | 间隔 | 优先级 | 作用 |
| --- | --- | --- | --- |
| `_parseTimer` | 180 ms | 默认 | 解析防抖 |
| `_playTimer` | 10 ms | `Render` | 驱动 `Advance()` |

`UpdatePlaybackPosition()` 里做四件事：

1. `SetPlaybackSpans(_player.GetActiveSpans())`
2. 乐句预览：`ShouldFadeOutPhrase(measure)` → `FadeOutPhrase()`，否则 `ShowPhrase(measure)`
3. `AutoScrollOnPlay` 时 `ScrollOffsetIntoView(spans[0].Start)`
4. 秒数变化时刷新状态栏（不每帧刷，`_lastSecond` 去重）

---

## 7A. 反向：MIDI 导入

本文件讲的是「乐谱 → 声音 / 文件」。反方向（`.mid` → 简谱文本）由 `Core.Midi.SmfReader` + `Core.Import.MidiToAttm` 完成，见 [11-MIDI导入](11-MIDI导入.md)。

---

## 8. 常见问题

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 界面高亮正常但没声音 | 没有 MIDI 输出端口 / 设备被占用 | 状态栏显示「（静音）」；设置里换设备或装 GS Wavetable |
| 起播第一拍缺音 | 上一次播放未被 `AllNotesOff` | 已在 `Play` 里 `ResetControllers`，若仍出现请检查设备自身延迟 |
| 导出的 .mid 比听到的短 | `.mid` 结尾写在 `TotalTicks`，而最后一个小节若有提前休止，尾巴也是静音 | 属预期行为 |
| 同名同音的重复触发丢失 | 未走 `NoteMerger` | 解析层已保证；自定义流程请先过 `NoteMerger.Apply` |