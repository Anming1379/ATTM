using System.Text;
using ATTM.Core.Midi;
using ATTM.Core.Syntax;

namespace ATTM.Plugin.MidiImport;

/// <summary>一次 MIDI 导入的结果。</summary>
public sealed record MidiImportResult(
    string Text,
    int MeasureCount,
    int NoteCount,
    string KeyName,
    string TimeSignatureName,
    double Bpm,
    bool RhythmExact,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 把 MIDI 文件自动转换成 ATTM 文本。
///
/// 转换规则：
/// 1. tick 统一换算到 PPQ 480；速度取第一个 tempo meta（缺省 120）。
/// 2. 拍号只保留 4/4 与 3/4，其它按 4/4 排版并给出提示。
/// 3. 调式优先用调号 meta，缺失时按音级直方图推断最接近的大调。
/// 4. 同一时刻起音的音合并成和弦；其余按节奏网格排成一行。
/// 5. 节奏：先尝试 1 / 2 / 4（3/4 时 1 / 2 / 3 / 4）分段，逐段用 1 / 2 / 4 / 8 个单元，
///    要求每个音恰好占满一个单元且时值完全对齐；都不成立时退回十六分网格量化。
///
/// 已知不保留：多声部（并行行）、连奏 legato、踏板、渐强渐弱。
/// </summary>
public static class MidiToAttm
{
    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };

    /// <summary>转换。<paramref name="sourceName"/> 只用于生成文件头注释。</summary>
    public static MidiImportResult Convert(SmfDocument document, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var warnings = new List<string>();
        var scale = (double)AttmSpec.TicksPerQuarter / document.TicksPerQuarter;

        var beats = document.BeatsPerMeasure;
        var beatUnit = document.BeatUnit;
        if (!((beats == 4 && beatUnit == 4) || (beats == 3 && beatUnit == 4)))
        {
            warnings.Add($"原曲拍号 {beats}/{beatUnit}，ATTM 只支持 4/4 与 3/4，已按 4/4 排版");
            beats = 4;
            beatUnit = 4;
        }

        var measureTicks = (long)AttmSpec.TicksPerQuarter * 4 * beats / beatUnit;

        var (root, keySharp) = document.KeyFifths is { } fifths
            ? RootFromFifths(fifths, document.KeyMinor)
            : InferKey(document.Notes);

        var slices = new List<Slice>(document.Notes.Count);
        foreach (var note in document.Notes)
        {
            var start = (long)Math.Round(note.StartTicks * scale);
            var end = (long)Math.Round(note.EndTicks * scale);
            if (end <= start || note.Pitch is < 0 or > 127) continue;
            slices.Add(new Slice(start, end, note.Pitch, note.Velocity));
        }

        if (slices.Count == 0) throw new InvalidOperationException("没有可转换的音符");

        var measures = SplitByMeasure(slices, measureTicks, out var measureCount);

        var exact = true;
        var lines = new List<string>(measureCount);
        foreach (var notes in measures)
        {
            var (line, isExact) = ComposeMeasure(notes, measureTicks, beats, root, keySharp);
            if (!isExact) exact = false;
            lines.Add(line);
        }

        if (!exact) warnings.Add("部分节奏无法整除到简谱网格，已量化到十六分音符（个别长音会被重复起音）");

        var bpm = Math.Clamp(Math.Round(document.Bpm, 2), AttmSpec.MinBpm, AttmSpec.MaxBpm);
        var (velocityBase, velocityJitter) = SummarizeVelocity(slices);

        var keyName = MidiKeyName(root, keySharp);
        var bpmText = bpm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        var text = new StringBuilder();
        text.Append("# 由 ").Append(sourceName ?? "MIDI 文件").Append(" 自动转换");
        text.Append(" · ").Append(keyName).Append(" · ").Append(beats).Append('/').Append(beatUnit);
        text.Append(" · ").Append(bpmText).Append(" BPM");
        text.Append(" · ").Append(slices.Count).Append(" 个音").Append('\n');
        text.Append("# 多声部、踏板、渐强渐弱不会被保留；改完记得试听").Append('\n');

        foreach (var warning in warnings)
            text.Append("# 提示：").Append(warning).Append('\n');

        text.Append('\n');
        text.Append("bpm = ").Append(bpmText).Append('\n');
        text.Append("1 = ").Append(keyName).Append(' ').Append(beats).Append('/').Append(beatUnit).Append('\n');
        text.Append("Vel = ").Append(velocityBase);
        if (velocityJitter > 0) text.Append(" +-").Append(velocityJitter);
        text.Append('\n');

        foreach (var line in lines)
            text.Append('\n').Append(line).Append('\n');

        return new MidiImportResult(
            text.ToString(),
            measureCount,
            slices.Count,
            keyName,
            beats + "/" + beatUnit,
            bpm,
            exact,
            warnings);
    }

    // ---------------------------------------------------------------- 节奏

    private static (string Line, bool Exact) ComposeMeasure(
        List<Slice> notes, long measureTicks, int beats, char root, bool keySharp)
    {
        var segments = beats == 4 ? new[] { 1, 2, 4 } : new[] { 1, 2, 3, 4 };

        foreach (var count in segments)
        {
            var line = TryCompose(notes, measureTicks, count, root, keySharp);
            if (line is not null) return (line, true);
        }

        return (ComposeQuantized(notes, measureTicks, beats, root, keySharp), false);
    }

    /// <summary>按 <paramref name="segments"/> 分段排版；无法精确表达时返回 null。</summary>
    private static string? TryCompose(List<Slice> notes, long measureTicks, int segments, char root, bool keySharp)
    {
        if (measureTicks % segments != 0) return null;

        var segTicks = measureTicks / segments;
        var tokens = new List<string>(segments * 2);

        for (var segment = 0; segment < segments; segment++)
        {
            var lo = segment * segTicks;
            var hi = lo + segTicks;

            var inside = new List<Slice>();
            foreach (var note in notes)
            {
                if (note.Start < lo || note.Start >= hi) continue;
                if (note.End > hi) return null;   // 跨段的长音：这个分段方案表达不了
                inside.Add(note);
            }

            var units = 1;
            while (units <= 8)
            {
                var width = segTicks / units;
                if (width <= 0) return null;

                var aligned = true;
                foreach (var note in inside)
                {
                    if ((note.Start - lo) % width != 0 || (note.End - lo) % width != 0) { aligned = false; break; }
                    if (note.End - note.Start != width) { aligned = false; break; }
                }

                if (aligned) break;
                units *= 2;
            }

            if (units > 8) return null;

            var unitTicks = segTicks / units;
            for (var slot = 0; slot < units; slot++)
            {
                var at = lo + slot * unitTicks;
                var chord = new List<int>();
                foreach (var note in inside)
                {
                    if (note.Start == at) chord.Add(note.Pitch);
                }

                tokens.Add(chord.Count == 0 ? "0" : ChordToken(chord, root, keySharp));
            }
        }

        return string.Join(",", tokens);
    }

    /// <summary>兜底：按十六分网格量化，长音会被重复起音。</summary>
    private static string ComposeQuantized(List<Slice> notes, long measureTicks, int beats, char root, bool keySharp)
    {
        var segments = beats;
        var segTicks = measureTicks / segments;
        var grid = Math.Max(1, segTicks / 4);
        var slots = (int)(measureTicks / grid);

        var buckets = new List<int>[slots];
        for (var i = 0; i < slots; i++) buckets[i] = new List<int>();

        foreach (var note in notes)
        {
            var slot = (int)Math.Round(note.Start / (double)grid, MidpointRounding.AwayFromZero);
            if (slot < 0) slot = 0;
            if (slot >= slots) slot = slots - 1;
            buckets[slot].Add(note.Pitch);
        }

        var builder = new StringBuilder();
        for (var index = 0; index < slots; index++)
        {
            if (index > 0 && index % 4 == 0) builder.Append(',');
            builder.Append(buckets[index].Count == 0 ? "0" : ChordToken(buckets[index], root, keySharp));
        }

        return builder.ToString();
    }

    // ---------------------------------------------------------------- 音高

    private static string ChordToken(List<int> pitches, char root, bool keySharp)
    {
        pitches.Sort();

        var parts = new List<string>(pitches.Count);
        var last = -1;
        foreach (var pitch in pitches)
        {
            if (pitch == last) continue;
            last = pitch;
            var token = NoteToken(pitch, root, keySharp);
            if (token.Length > 0) parts.Add(token);
        }

        if (parts.Count == 0) return "0";
        if (parts.Count == 1) return parts[0];
        return "(" + string.Concat(parts) + ")";
    }

    /// <summary>音高 → 简谱记号（八度前缀 + 可选升号 + 数字）。</summary>
    private static string NoteToken(int pitch, char root, bool keySharp)
    {
        if (!PitchMapper.TryGetRootOffset(root, out var rootOffset)) rootOffset = 0;

        var rel = pitch - (AttmSpec.MiddleC + rootOffset + (keySharp ? 1 : 0));
        var octave = (int)Math.Floor(rel / 12.0);
        var pc = rel - octave * 12;

        var degree = -1;
        var sharp = false;

        for (var d = 1; d <= 7; d++)
        {
            if (MajorScale[d - 1] == pc) { degree = d; break; }
        }

        if (degree < 0)
        {
            for (var d = 1; d <= 7; d++)
            {
                if (MajorScale[d - 1] + 1 == pc) { degree = d; sharp = true; break; }
            }
        }

        if (degree < 0) return string.Empty;

        var builder = new StringBuilder(4);
        if (octave > 0) builder.Append(new string('+', octave));
        else if (octave < 0) builder.Append(new string('-', -octave));
        if (sharp) builder.Append('#');
        builder.Append((char)('0' + degree));
        return builder.ToString();
    }

    // ---------------------------------------------------------------- 调式

    private static (char Root, bool Sharp) RootFromFifths(int fifths, bool minor)
    {
        var tonic = Mod(fifths * 7 + (minor ? 3 : 0), 12);
        return PitchClassToRoot(tonic);
    }

    private static (char Root, bool Sharp) InferKey(IReadOnlyList<SmfNote> notes)
    {
        var histogram = new int[12];
        foreach (var note in notes) histogram[Mod(note.Pitch, 12)]++;

        var bestTonic = 0;
        var bestScore = double.MinValue;

        for (var tonic = 0; tonic < 12; tonic++)
        {
            var score = 0.0;
            for (var pc = 0; pc < 12; pc++)
            {
                if (histogram[pc] == 0) continue;
                var inScale = Array.IndexOf(MajorScale, Mod(pc - tonic, 12)) >= 0;
                score += histogram[pc] * (inScale ? 1.0 : -1.2);
                if (pc == tonic) score += histogram[pc] * 0.8;
            }

            if (score > bestScore) { bestScore = score; bestTonic = tonic; }
        }

        return PitchClassToRoot(bestTonic);
    }

    private static (char Root, bool Sharp) PitchClassToRoot(int pitchClass) => Mod(pitchClass, 12) switch
    {
        0 => ('C', false),
        1 => ('C', true),
        2 => ('D', false),
        3 => ('D', true),
        4 => ('E', false),
        5 => ('F', false),
        6 => ('F', true),
        7 => ('G', false),
        8 => ('G', true),
        9 => ('A', false),
        10 => ('A', true),
        _ => ('B', false)
    };

    private static string MidiKeyName(char root, bool sharp) => sharp ? root + "#" : root.ToString();

    // ---------------------------------------------------------------- 工具

    private static List<List<Slice>> SplitByMeasure(List<Slice> notes, long measureTicks, out int measureCount)
    {
        var maxTick = 0L;
        foreach (var note in notes) if (note.End > maxTick) maxTick = note.End;

        measureCount = (int)Math.Max(1, (maxTick + measureTicks - 1) / measureTicks);

        var measures = new List<List<Slice>>(measureCount);
        for (var i = 0; i < measureCount; i++) measures.Add(new List<Slice>());

        foreach (var note in notes)
        {
            var first = (int)(note.Start / measureTicks);
            var last = (int)((note.End - 1) / measureTicks);

            for (var m = first; m <= last && m < measureCount; m++)
            {
                var lo = m * measureTicks;
                var start = Math.Max(note.Start, lo) - lo;
                var end = Math.Min(note.End, lo + measureTicks) - lo;
                if (end <= start) continue;

                measures[m].Add(new Slice(start, end, note.Pitch, note.Velocity));
            }
        }

        return measures;
    }

    private static (int Base, int Jitter) SummarizeVelocity(List<Slice> slices)
    {
        if (slices.Count == 0) return (AttmSpec.DefaultVelocity, 0);

        var sum = 0.0;
        foreach (var slice in slices) sum += slice.Velocity;
        var mean = sum / slices.Count;

        var variance = 0.0;
        foreach (var slice in slices)
        {
            var delta = slice.Velocity - mean;
            variance += delta * delta;
        }

        var deviation = Math.Sqrt(variance / slices.Count);

        var velocityBase = Math.Clamp((int)Math.Round(mean), AttmSpec.MinVelocity, AttmSpec.MaxVelocity);
        var jitter = Math.Clamp((int)Math.Round(deviation), 0, AttmSpec.MaxVelocityJitter);

        return (velocityBase, jitter);
    }

    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private readonly record struct Slice(long Start, long End, int Pitch, int Velocity);
}
