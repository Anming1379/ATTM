using System.Globalization;
using System.Text;
using ATTM.Core.Midi;
using ATTM.Core.Syntax;

namespace ATTM.Core.Import;

/// <summary>
/// MIDI -> ATTM 文本。规则按"一行一个音、不用和弦括号、延音用 legato()"来定：
/// <list type="bullet">
/// <item>时间对齐到小节的 1/32 网格；</item>
/// <item>每行一个小节、行里最多一个音（重叠的音各占一行）；</item>
/// <item>踏板延音算进时值；铺满一段又延音到段末的音型用 <c>legato()</c> 表达；</item>
/// <item>生成完立刻回读一遍，解析不通过就抛异常（绝不把坏文本交给界面）。</item>
/// </list>
/// </summary>
public static class MidiImporter
{
    public const int TicksPerQuarter = 480;

    /// <summary>一个节拍段里最多 32 个单元（再细就没有意义了）。</summary>
    private const int MaxUnitsPerSegment = 32;

    /// <summary>legato 组里最多几个音（段内拍位数）。</summary>
    private const int MaxLegatoEntries = 16;

    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };

    public static MidiImportResult Convert(string path, MidiImportOptions? options = null)
        => Convert(MidiFileReader.Read(path), options);

    public static MidiImportResult Convert(MidiFileData midi, MidiImportOptions? options = null)
    {
        options ??= new MidiImportOptions();
        var report = new MidiImportReport { SourceFormat = midi.Format, SourceTrackCount = midi.TrackCount };

        var (beats, unit, signatureNote) = MapTimeSignature(midi.FirstTimeSignature);
        report.BeatsPerMeasure = beats;
        report.BeatUnit = unit;
        report.TimeSignatureNote = signatureNote;

        var measureTicks = TicksPerQuarter * 4 * beats / unit;
        var scale = (double)TicksPerQuarter / midi.TicksPerQuarter;

        var bpm = Math.Clamp(Math.Round(options.Bpm ?? midi.DominantBpm, 1), 1, 1000);
        report.Bpm = bpm;

        // ---- 音符：换时基、丢打击乐 ----
        var notes = new List<WorkNote>(midi.Notes.Count);
        foreach (var note in midi.Notes)
        {
            if (!options.IncludePercussion && note.Channel == 9)
            {
                report.SkippedPercussion++;
                continue;
            }

            var start = (int)Math.Round(note.StartTicks * scale);
            var end = (int)Math.Round(note.EndTicks * scale);
            if (end <= start)
            {
                report.DroppedNotes++;
                continue;
            }

            notes.Add(new WorkNote(note.Pitch, note.Velocity, note.Channel, start, end));
        }

        if (notes.Count == 0) throw new MidiFileException("这个 MIDI 里没有可以导入的音符（可能全是打击乐）。");

        // ---- 踏板、重叠修剪、量化 ----
        if (options.ApplySustainPedal) ApplySustainPedal(notes, midi.ControlChanges, scale);
        TrimSamePitchOverlaps(notes, Math.Max(1, measureTicks / 64), report);

        // 量化网格取"一拍的三十二分之一"（4/4 与 3/4 都是 60 tick），
        // 这样 6/8 的八分音符（240 tick）也能精确落格
        var quantum = Math.Max(1, measureTicks / beats / 8);
        var maxErrorTicks = Quantize(notes, quantum, report);
        report.MaxQuantizeErrorMs = Math.Round(maxErrorTicks * 60_000.0 / (bpm * TicksPerQuarter), 1);

        // 同音高在任意声部之间重叠时，后一个音会截断前一个（ATTM 的"同音高覆盖"规则）。
        // 这里先自己截断，免得解析器把音悄悄合并掉，导致"写出来的和解析出来的不一致"。
        TrimSamePitchAnywhere(notes, report);

        notes = notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        if (notes.Count == 0) throw new MidiFileException("所有音符都被量化掉了（时值太短）。");

        // ATTM 里一个音不能跨小节线：尾巴短的收掉，尾巴长的只能在小节线上重新起音
        notes = SplitAtMeasureLines(notes, measureTicks, report);

        // ---- 调性 ----
        var (keyRoot, keySharp) = options.DetectKey
            ? DetectKey(notes)
            : (options.KeyRoot, options.KeySharp);

        report.KeyRoot = keyRoot;
        report.KeySharp = keySharp;
        report.KeyDetected = options.DetectKey;

        if (midi.Tempos.Select(t => Math.Round(t.Bpm, 1)).Distinct().Count() > 1)
            report.Warn($"文件里有 {midi.Tempos.Count} 个速度变化，只取了占时最长的 {bpm:0.#} bpm。");

        if (midi.Programs.Count > 1) report.Warn("文件里有多个音色，ATTM 只能用钢琴音色。");

        // ---- 逐小节生成 ----
        var measureCount = Math.Max(1, (notes.Max(n => n.End) + measureTicks - 1) / measureTicks);
        report.MeasureCount = measureCount;
        report.NoteCount = notes.Count;

        var emittedTotal = 0;
        var builder = new StringBuilder(1024);
        builder.Append("bpm = ").Append(FormatNumber(bpm)).Append('\n');
        builder.Append("1 = ").Append(keyRoot).Append(keySharp ? "#" : string.Empty)
               .Append(' ').Append(beats).Append('/').Append(unit).Append('\n');
        builder.Append("Vel = ").Append(MedianVelocity(notes)).Append(" +-0\n");

        for (var measure = 0; measure < measureCount; measure++)
        {
            var measureStart = measure * measureTicks;
            var measureEnd = measureStart + measureTicks;
            var measureNotes = notes
                .Where(n => n.Start >= measureStart && n.Start < measureEnd)
                .Select(n => new WorkNote(n.Pitch, n.Velocity, n.Channel, n.Start - measureStart, n.End - measureStart))
                .ToList();

            var lines = BuildMeasure(measureNotes, measureTicks, beats, keyRoot, keySharp, options, report, out var emitted);
            emittedTotal += emitted;



            builder.Append('\n');
            if (lines.Count == 0)
            {
                builder.Append("0\n");
                continue;
            }

            foreach (var line in lines) builder.Append(line).Append('\n');

            report.LineCount += lines.Count;
            report.MaxLinesPerMeasure = Math.Max(report.MaxLinesPerMeasure, lines.Count);
        }

        var text = builder.ToString();
        if (Environment.GetEnvironmentVariable("ATTM_IMPORT_DEBUG") == "1")
        {
            var debugPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "attm-import-debug.attm");
            System.IO.File.WriteAllText(debugPath, text);
            Console.WriteLine($"（调试：生成的简谱已写到 {debugPath}）");
        }

        Verify(text, emittedTotal);

        if (report.LegatoGroups > 0)
            report.Warn($"有 {report.LegatoGroups} 组铺满整段的延音，用 legato() 连着写（{report.TiedNotes} 个音不会重复起音）。");

        if (report.CrossMeasureTruncated > 0)
            report.Warn($"有 {report.CrossMeasureTruncated} 个音跨过小节线、只剩一点尾巴，" +
                        "就在小节线收掉了（简谱没法跨小节连线）。");

        if (report.CrossMeasureRestarts > 0)
            report.Warn($"有 {report.CrossMeasureRestarts} 个音跨过小节线，尾巴还挺长，" +
                        "只能在下一个小节重新起音（否则整段延音都会没掉）。");

        if (report.ShortenedNotes > 0)
            report.Warn($"有 {report.ShortenedNotes} 个音的时值不是 1/2、1/4、1/8 …拍的整格" +
                        "（附点、三连音之类），简谱写不出来，就近取一格。");
        if (report.MaxLinesPerMeasure >= 4) report.Warn($"最密的小节有 {report.MaxLinesPerMeasure} 行（重叠音各占一行）。");

        return new MidiImportResult(text, report);
    }

    // ------------------------------------------------------------ 拍号

    private static (int Beats, int Unit, string? Note) MapTimeSignature((int Numerator, int Denominator) signature)
    {
        var (numerator, denominator) = signature;
        if (numerator <= 0 || denominator <= 0) return (4, 4, null);

        var sourceBarTicks = TicksPerQuarter * 4 * numerator / denominator;

        if (numerator == 4 && denominator == 4) return (4, 4, null);
        if (numerator == 3 && denominator == 4) return (3, 4, null);

        // 小节长度刚好等于 4/4 或 3/4 的（2/2、8/8、6/8…），一小节当一小节用
        if (sourceBarTicks == 1920) return (4, 4, $"{numerator}/{denominator} 一小节正好等于一节 4/4");
        if (sourceBarTicks == 1440) return (3, 4, $"{numerator}/{denominator} 一小节正好等于一节 3/4");

        // 比 4/4 短的小节，合并成整数个小节（音符位置不变，只是小节线挪了）
        if (sourceBarTicks > 0 && 1920 % sourceBarTicks == 0 && 1920 / sourceBarTicks <= 4)
        {
            var merge = 1920 / sourceBarTicks;
            return (4, 4, $"{numerator}/{denominator} 每 {merge} 小节并成一节 4/4");
        }

        if (sourceBarTicks > 0 && 1440 % sourceBarTicks == 0 && 1440 / sourceBarTicks <= 4)
        {
            var merge = 1440 / sourceBarTicks;
            return (3, 4, $"{numerator}/{denominator} 每 {merge} 小节并成一节 3/4");
        }

        throw new MidiFileException(
            $"这个 MIDI 是 {numerator}/{denominator} 拍，ATTM 只能写 4/4 和 3/4。" +
            "可以先用别的软件把它换成 4/4 再导进来。");
    }

    // ------------------------------------------------------------ 踏板 / 重叠 / 量化

    private static void ApplySustainPedal(List<WorkNote> notes, IReadOnlyList<MidiControlChange> controls, double scale)
    {
        if (controls.Count == 0) return;

        var intervals = new Dictionary<int, List<(int Start, int End)>>();
        var open = new Dictionary<int, int>();

        foreach (var change in controls)
        {
            if (change.Controller != 64) continue;

            var tick = (int)Math.Round(change.Tick * scale);

            if (change.Value >= 64)
            {
                open.TryAdd(change.Channel, tick);
                continue;
            }

            if (!open.Remove(change.Channel, out var start)) continue;
            if (tick <= start) continue;

            if (!intervals.TryGetValue(change.Channel, out var list))
            {
                list = new List<(int, int)>();
                intervals[change.Channel] = list;
            }

            list.Add((start, tick));
        }

        // 一直踩到结尾没收起来的，按最后结束时间处理
        var lastTick = notes.Count == 0 ? 0 : notes.Max(n => n.End);
        foreach (var (channel, start) in open)
        {
            if (!intervals.TryGetValue(channel, out var list))
            {
                list = new List<(int, int)>();
                intervals[channel] = list;
            }

            list.Add((start, Math.Max(start, lastTick)));
        }

        if (intervals.Count == 0) return;

        foreach (var note in notes)
        {
            if (!intervals.TryGetValue(note.Channel, out var list)) continue;

            foreach (var (start, end) in list)
            {
                if (note.End >= start && note.End < end) note.End = end;
            }
        }
    }

    /// <summary>
    /// 同音高反复时常见几 tick 的重叠（踏板或手滑），先把这种小重叠裁掉，
    /// 免得它们把旋律拆成好几行。
    /// </summary>
    private static void TrimSamePitchOverlaps(List<WorkNote> notes, int tolerance, MidiImportReport report)
    {
        var byPitch = notes.OrderBy(n => n.Pitch).ThenBy(n => n.Start).ToList();

        for (var i = 0; i + 1 < byPitch.Count; i++)
        {
            var a = byPitch[i];
            var b = byPitch[i + 1];
            if (a.Pitch != b.Pitch) continue;
            if (a.End <= b.Start) continue;
            if (a.End - b.Start > tolerance) continue;

            a.End = b.Start;
            report.Warn("有同音高的微小重叠，已经按连音处理。");
        }

        notes.RemoveAll(n => n.End <= n.Start);
    }

    /// <summary>对齐到量化网格，返回最大偏差（tick）。</summary>
    private static int Quantize(List<WorkNote> notes, int quantum, MidiImportReport report)
    {
        var maxError = 0;

        foreach (var note in notes)
        {
            var start = Snap(note.Start, quantum, out var startError);
            var end = Snap(note.End, quantum, out var endError);
            if (end <= start) end = start + quantum;

            maxError = Math.Max(maxError, Math.Max(startError, endError));
            note.Start = start;
            note.End = end;
        }

        var before = notes.Count;
        notes.RemoveAll(n => n.End <= n.Start);
        report.DroppedNotes += before - notes.Count;
        return maxError;
    }

    /// <summary>同音高在任意声部之间重叠时，把前一个音截断到后一个音的起点。</summary>
    private static void TrimSamePitchAnywhere(List<WorkNote> notes, MidiImportReport report)
    {
        var ordered = notes.OrderBy(n => n.Pitch).ThenBy(n => n.Start).ToList();
        var trimmed = 0;

        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var current = ordered[i];
            var next = ordered[i + 1];

            if (current.Pitch != next.Pitch) continue;
            if (current.End <= next.Start) continue;

            current.End = next.Start;
            trimmed++;
        }

        if (trimmed == 0) return;

        var before = notes.Count;
        notes.RemoveAll(n => n.End <= n.Start);
        report.DroppedNotes += before - notes.Count;
    }

    /// <summary>
    /// ATTM 一个音没法跨小节线，只能二选一：尾巴很短就在小节线收掉（不留音头），
    /// 尾巴很长就收掉的话整段延音都没了，只好在下一个小节重新起音。
    /// </summary>
    private static List<WorkNote> SplitAtMeasureLines(List<WorkNote> notes, int measureTicks, MidiImportReport report)
    {
        var result = new List<WorkNote>(notes.Count);

        foreach (var note in notes)
        {
            var cursor = note.Start;
            var guard = 0;

            while (cursor < note.End && guard++ < 64)
            {
                var measureEnd = (cursor / measureTicks + 1) * measureTicks;
                var end = Math.Min(note.End, measureEnd);
                var tail = note.End - end;

                result.Add(new WorkNote(note.Pitch, note.Velocity, note.Channel, cursor, end)
                {
                    Source = note.Root,
                });

                if (tail <= 0) break;

                // 尾巴不到一半，收掉比重新起音更不容易被听出来
                if (tail < (note.End - note.Start) / 2 && tail < measureTicks / 2)
                {
                    report.CrossMeasureTruncated++;
                    break;
                }

                report.CrossMeasureRestarts++;
                cursor = end;
            }
        }

        return result;
    }

    private static int Snap(int tick, int quantum, out int error)
    {
        var snapped = (int)Math.Round((double)tick / quantum) * quantum;
        error = Math.Abs(snapped - tick);
        return Math.Max(0, snapped);
    }

    // ------------------------------------------------------------ 调性

    private static (char Root, bool Sharp) DetectKey(List<WorkNote> notes)
    {
        var weights = new double[12];

        foreach (var note in notes)
        {
            weights[((note.Pitch % 12) + 12) % 12] += Math.Max(1, note.End - note.Start);
        }

        return KeyDetector.Detect(weights, notes[0].Pitch);
    }

    // ------------------------------------------------------------ 逐小节

    private static List<string> BuildMeasure(
        List<WorkNote> measureNotes, int measureTicks, int beats,
        char keyRoot, bool keySharp, MidiImportOptions options, MidiImportReport report, out int emitted)
    {
        emitted = 0;
        if (measureNotes.Count == 0) return new List<string>();

        var lines = new List<(string Text, double Pitch)>();
        var used = new HashSet<WorkNote>();

        // 1) 先找能写成 legato() 的"铺满一段并且延音到段末"的音型
        if (options.UseLegato)
        {
            foreach (var (segmentCount, segmentIndex, segmentStart, segmentEnd) in EnumerateSegments(measureTicks, beats))
            {
                // 段内所有"延音到段末"的音（通常就是踩踏板按住的音）
                var sustained = measureNotes
                    .Where(n => !used.Contains(n) && n.Start >= segmentStart && n.Start < segmentEnd && n.End == segmentEnd)
                    .OrderBy(n => n.Start)
                    .ToList();

                if (sustained.Count == 0) continue;
                if (sustained.Count > MaxLegatoEntries) continue;

                // 段内还有别的（中途就结束的）音：它们挤不进同一行，这一轮先不处理
                if (measureNotes.Any(n => !used.Contains(n) && !sustained.Contains(n) &&
                                          n.Start >= segmentStart && n.Start < segmentEnd))
                    continue;

                // 只有一个音时，普通写法（把它放在段内最后一格）本来就能延到段末，不必套 legato()
                if (sustained.Count == 1 && FitsAsLastUnit(sustained[0], segmentStart, segmentEnd))
                    continue;

                // legato 内部是"段内等距拍位"，起点必须落在 segLen/K 的格子上（K 是 2 的幂）
                if (!TryGetLegatoEntries(sustained, segmentStart, segmentEnd, out var entries)) continue;

                used.UnionWith(sustained);
                lines.Add((BuildLegatoLine(sustained, entries, segmentCount, segmentIndex, measureTicks, keyRoot, keySharp),
                    AveragePitch(sustained)));
                emitted += sustained.Count;
                report.LegatoGroups++;
                report.TiedNotes += sustained.Count;
            }
        }

        // 2) 剩下的音：每个音都完整落在"一格"里，绝不切到下一段去，所以不会重复起音。
        //    一格多长 = 这一段里所有音共用的步长；同一段里时值不一样的音没法挤一行，各占一行。
        var rest = measureNotes.Where(n => !used.Contains(n)).OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();

        foreach (var voice in SplitVoices(rest))
        {
            foreach (var planned in PlanVoice(voice, measureTicks, beats, keyRoot, keySharp, report))
            {
                lines.Add((planned.Text, planned.Pitch));
                emitted += planned.Notes;
            }
        }

        // 旋律在上、低音在下
        lines.Sort((a, b) => b.Pitch.CompareTo(a.Pitch));
        return lines.Select(line => line.Text).ToList();
    }

    private static IEnumerable<(int SegmentCount, int SegmentIndex, int Start, int End)> EnumerateSegments(
        int measureTicks, int beats)
    {
        foreach (var segmentCount in SegmentCounts(beats))
        {
            var segmentTicks = measureTicks / segmentCount;

            for (var index = 0; index < segmentCount; index++)
            {
                yield return (segmentCount, index, index * segmentTicks, (index + 1) * segmentTicks);
            }
        }
    }

    /// <summary>4/4 只能是 1/2/4 段；3/4 还可以 3 段。</summary>
    private static int[] SegmentCounts(int beats) => beats == 4 ? new[] { 1, 2, 4 } : new[] { 1, 2, 3, 4 };

    private static string BuildLegatoLine(
        List<WorkNote> notes, int[] entries, int segmentCount, int segmentIndex, int measureTicks,
        char keyRoot, bool keySharp)
    {
        var shift = ChooseLineShift(notes, keyRoot, keySharp);
        var inner = string.Concat(entries.Select(pitch =>
            pitch < 0 ? "0" : RenderNote(pitch, keyRoot, keySharp, shift)));

        var parts = new string[segmentCount];
        for (var i = 0; i < segmentCount; i++) parts[i] = i == segmentIndex ? $"legato({inner})" : "0";

        return Prefix(shift) + string.Join(",", parts);
    }

    /// <summary>
    /// 给一行排版：先按代价挑段数；某些音的时值在这个网格里写不出来（会被截短）时，
    /// 如果还允许拆行，就把它们剔出来交给调用方单独成行（粗网格往往刚好写得下）；
    /// 不允许拆行时，就按截短的样子写出来，并记进报告。
    /// </summary>
    private readonly record struct PlannedLine(string Text, double Pitch, int Notes);

    /// <summary>
    /// 给一条单声部排文本。规则：一个音 = 一格，一格的时值就是这一段用的步长，
    /// 所以音永远落在一段之内（不会被段线切开，也就不会重新起音）；代价是时值要就近取一格。
    /// </summary>
    private static List<PlannedLine> PlanVoice(
        List<WorkNote> voice, int measureTicks, int beats, char keyRoot, bool keySharp, MidiImportReport report)
    {
        var result = new List<PlannedLine>();
        if (voice.Count == 0) return result;

        var shift = ChooseLineShift(voice, keyRoot, keySharp);

        EmitPlan? best = null;

        foreach (var segmentCount in SegmentCounts(beats))
        {
            var plan = TryPlan(voice, segmentCount, measureTicks);
            if (plan is null) continue;
            if (best is not null && plan.Cost >= best.Cost) continue;

            best = plan;
        }

        if (best is null) return result;

        foreach (var line in best.Lines)
        {
            var text = RenderPlanLine(line, best.SegmentCount, measureTicks, keyRoot, keySharp, shift);
            var pitches = line.Items.Select(item => item.Note).ToList();

            result.Add(new PlannedLine(text, AveragePitch(pitches), line.Items.Count));
            report.ShortenedNotes += line.Items.Count(item => item.Error > 1);
        }

        return result;
    }

    private sealed class EmitPlan
    {
        public int SegmentCount { get; init; }

        public int Cost { get; set; }

        public List<PlanLine> Lines { get; } = new();
    }

    private sealed class PlanLine
    {
        public Dictionary<int, int> StepBySegment { get; } = new();

        public List<PlanItem> Items { get; } = new();
    }

    private sealed class PlanItem
    {
        public required WorkNote Note { get; init; }

        public int Segment { get; init; }

        public int Offset { get; init; }

        public int Step { get; init; }

        public int Error { get; init; }
    }

    /// <summary>按给定段数试着排一遍：每个音挑一格，再按"段内步长"分装到若干行。</summary>
    private static EmitPlan? TryPlan(List<WorkNote> voice, int segmentCount, int measureTicks)
    {
        var segmentTicks = measureTicks / segmentCount;
        if (segmentTicks <= 0) return null;

        var plan = new EmitPlan { SegmentCount = segmentCount };
        var errorSum = 0;

        for (var index = 0; index < voice.Count; index++)
        {
            var note = voice[index];
            var segment = Math.Clamp(note.Start / segmentTicks, 0, segmentCount - 1);
            var offset = note.Start - segment * segmentTicks;
            var duration = note.End - note.Start;

            // 一格不能盖住同一行后面那个音，所以最多撑到它起头为止
            var limit = segmentTicks - offset;
            if (index + 1 < voice.Count) limit = Math.Min(limit, voice[index + 1].Start - note.Start);
            if (limit <= 0) return null;

            if (!TryPickStep(segmentTicks, offset, duration, limit, out var step, out var stepError)) return null;

            var item = new PlanItem
            {
                Note = note,
                Segment = segment,
                Offset = offset,
                Step = step,
                Error = stepError,
            };

            // 同一小段在一行里只能有一种步长：步长一样的可以并到一行，不一样就得另起一行
            var target = plan.Lines.FirstOrDefault(line =>
                !line.StepBySegment.TryGetValue(item.Segment, out var usedStep) || usedStep == item.Step);
            if (target is null)
            {
                target = new PlanLine();
                plan.Lines.Add(target);
            }

            target.StepBySegment[item.Segment] = item.Step;
            target.Items.Add(item);
            errorSum += stepError;
        }

        var units = 0;

        foreach (var line in plan.Lines)
        {
            for (var segment = 0; segment < segmentCount; segment++)
            {
                units += line.StepBySegment.TryGetValue(segment, out var step) && step > 0
                    ? segmentTicks / step
                    : 1;
            }
        }

        // 代价：单元数（可读性）+ 行数 + 时值误差（60 ticks ≈ 1/8 拍记 1 分）
        plan.Cost = units + plan.Lines.Count * 8 + errorSum / 60;
        return plan;
    }

    /// <summary>给一个音挑"这一格多长"：要能整除段内位置、不超过可用长度，并且尽量贴近原时值。</summary>
    private static bool TryPickStep(int segmentTicks, int offset, int duration, int limit, out int step, out int error)
    {
        step = 0;
        error = int.MaxValue;

        for (var units = 1; units <= MaxUnitsPerSegment; units *= 2)
        {
            if (segmentTicks % units != 0) continue;

            var candidate = segmentTicks / units;
            if (candidate <= 0 || candidate > limit) continue;
            if (offset % candidate != 0) continue;

            var candidateError = Math.Abs(candidate - duration);
            if (candidateError >= error) continue;

            error = candidateError;
            step = candidate;
        }

        return step > 0;
    }

    /// <summary>把排好的行渲染成简谱文本。</summary>
    private static string RenderPlanLine(
        PlanLine line, int segmentCount, int measureTicks, char keyRoot, bool keySharp, int shift)
    {
        var segmentTicks = measureTicks / segmentCount;
        var parts = new List<string>(segmentCount);

        for (var segment = 0; segment < segmentCount; segment++)
        {
            if (!line.StepBySegment.TryGetValue(segment, out var step) || step <= 0)
            {
                parts.Add("0");
                continue;
            }

            var units = Math.Max(1, segmentTicks / step);
            var cells = new string[units];
            Array.Fill(cells, "0");

            foreach (var item in line.Items)
            {
                if (item.Segment != segment) continue;

                var index = item.Offset / step;
                if (index < 0 || index >= units) continue;

                cells[index] = RenderNote(item.Note.Pitch, keyRoot, keySharp, shift);
            }

            parts.Add(string.Concat(cells));
        }

        return Prefix(shift) + string.Join(",", parts);
    }

    /// <summary>一行一个音：按开始时间贪心分给"已经空出来的行"里音高最接近的那条。</summary>
    private static List<List<WorkNote>> SplitVoices(List<WorkNote> notes)
    {
        var voices = new List<List<WorkNote>>();
        var lastEnd = new List<int>();

        foreach (var note in notes)
        {
            var bestIndex = -1;
            var bestDistance = int.MaxValue;

            for (var i = 0; i < voices.Count; i++)
            {
                if (lastEnd[i] > note.Start) continue;

                var distance = Math.Abs(voices[i][^1].Pitch - note.Pitch);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                bestIndex = i;
            }

            if (bestIndex < 0)
            {
                voices.Add(new List<WorkNote> { note });
                lastEnd.Add(note.End);
                continue;
            }

            voices[bestIndex].Add(note);
            lastEnd[bestIndex] = note.End;
        }

        return voices;
    }

    /// <summary>
    /// 把"延音到段末"的一组音排成 legato() 的拍位表：返回每个拍位上要写的音（没音写 -1），
    /// 拍位数是 2 的幂。段内位置必须是 segLen/K 的整数倍，否则排不进去。
    /// </summary>
    private static bool TryGetLegatoEntries(
        List<WorkNote> notes, int segmentStart, int segmentEnd, out int[] entries)
    {
        entries = Array.Empty<int>();

        var segmentTicks = segmentEnd - segmentStart;
        if (segmentTicks <= 0) return false;

        var divisor = segmentTicks;
        foreach (var note in notes) divisor = Gcd(divisor, note.Start - segmentStart);

        var count = segmentTicks / Math.Max(1, divisor);
        if (count < 1) return false;
        if (count > MaxLegatoEntries) return false;
        if (!IsPowerOfTwo(count)) return false;

        var step = segmentTicks / count;
        var table = new int[count];
        Array.Fill(table, -1);

        foreach (var note in notes)
        {
            var offset = note.Start - segmentStart;
            if (offset % step != 0) return false;

            var index = offset / step;
            if (index < 0 || index >= count) return false;
            if (table[index] >= 0) return false;   // 同一拍位两个音：一行装不下

            table[index] = note.Pitch;
        }

        entries = table;
        return true;
    }

    /// <summary>这个音能不能直接落在"段内最后一格"上？能的话它普通写法就会延到段末。</summary>
    private static bool FitsAsLastUnit(WorkNote note, int segmentStart, int segmentEnd)
    {
        var segmentTicks = segmentEnd - segmentStart;
        var offset = note.Start - segmentStart;

        for (var units = 1; units <= 64; units *= 2)
        {
            if (segmentTicks % units != 0) continue;

            var step = segmentTicks / units;
            if (step <= 0) continue;
            if (offset % step != 0) continue;
            if (offset / step == units - 1) return true;
        }

        return false;
    }

    private static int Gcd(int a, int b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);

        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static double AveragePitch(List<WorkNote> notes) => notes.Average(n => n.Pitch);

    // ------------------------------------------------------------ 单个音怎么写

    private static int ChooseLineShift(List<WorkNote> notes, char keyRoot, bool keySharp)
    {
        var rootOffset = KeyDetector.RootOffset(keyRoot, keySharp);
        var counts = new Dictionary<int, int>();

        foreach (var note in notes)
        {
            var octave = OctaveOf(note.Pitch, rootOffset);

            counts.TryGetValue(octave, out var count);
            counts[octave] = count + 1;
        }

        if (counts.Count == 0) return 0;

        var best = counts.OrderByDescending(pair => pair.Value).ThenBy(pair => Math.Abs(pair.Key)).First().Key;
        return Math.Clamp(best, -3, 3);
    }

    private static int OctaveOf(int pitch, int rootOffset)
    {
        var relative = pitch - 60 - rootOffset;
        var within = ((relative % 12) + 12) % 12;

        return (relative - within) / 12;
    }

    private static string Prefix(int shift) =>
        shift > 0 ? new string('$', shift) + " " : shift < 0 ? new string('%', -shift) + " " : string.Empty;

    private static string RenderNote(int pitch, char keyRoot, bool keySharp, int lineShift)
    {
        var rootOffset = KeyDetector.RootOffset(keyRoot, keySharp);
        var relative = pitch - 60 - rootOffset;
        var within = ((relative % 12) + 12) % 12;

        int degree;
        var sharp = false;
        var index = Array.IndexOf(MajorScale, within);

        if (index >= 0)
        {
            degree = index + 1;
        }
        else
        {
            index = Array.IndexOf(MajorScale, within - 1);
            degree = index >= 0 ? index + 1 : 1;
            sharp = true;
        }

        var octave = (relative - within) / 12 - lineShift;

        var text = new StringBuilder(4);
        if (octave > 0) text.Append('+', octave);
        else if (octave < 0) text.Append('-', -octave);

        if (sharp) text.Append('#');
        text.Append((char)('0' + degree));
        return text.ToString();
    }

    // ------------------------------------------------------------ 收尾

    private static int MedianVelocity(List<WorkNote> notes)
    {
        var velocities = notes.Select(n => n.Velocity).OrderBy(v => v).ToList();
        var median = velocities[velocities.Count / 2];
        return Math.Clamp(median, 1, 127);
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>生成完必须能读回来：0 诊断 + 音符数对得上。</summary>
    private static void Verify(string text, int expectedNotes)
    {
        var parsed = AttmParser.Parse(text);

        if (parsed.Diagnostics.Count > 0)
        {
            var first = parsed.Diagnostics[0];
            throw new InvalidOperationException(
                $"MIDI 转换生成了无法解析的文本（第 {first.Span.Start} 字符：{first.Message}）。这是内部错误，请反馈。");
        }

        var parsedNotes = parsed.Score?.Notes.Count ?? 0;
        if (parsedNotes != expectedNotes)
        {
            throw new InvalidOperationException(
                $"MIDI 转换后音符数不一致（写出 {expectedNotes}，解析出 {parsedNotes}）。这是内部错误，请反馈。");
        }
    }

    /// <summary>转换过程中会改时值（踏板、修剪、量化），所以用可变类。</summary>
    private sealed class WorkNote
    {
        public WorkNote(int pitch, int velocity, int channel, int start, int end)
        {
            Pitch = pitch;
            Velocity = velocity;
            Channel = channel;
            Start = start;
            End = end;
        }

        public int Pitch { get; }

        /// <summary>跨段切开后的碎片指向原来那个音；原始音自身为 null。</summary>
        public WorkNote? Source { get; init; }

        /// <summary>碎片对应的原始音（原始音返回自己）。</summary>
        public WorkNote Root => Source?.Root ?? this;

        public int Velocity { get; }

        public int Channel { get; }

        public int Start { get; set; }

        public int End { get; set; }
    }
}
