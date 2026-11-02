namespace ATTM.Core.Midi;

/// <summary>MIDI 文件里读出来的一个音（tick 是文件自己的时基）。</summary>
public sealed record MidiFileNote(int Pitch, int Velocity, int Channel, long StartTicks, long EndTicks);

public sealed record MidiTempoChange(long Tick, double Bpm);

public sealed record MidiTimeSignatureChange(long Tick, int Numerator, int Denominator);

/// <summary>控制器变化（导入只关心延音踏板 CC64）。</summary>
public sealed record MidiControlChange(long Tick, int Channel, int Controller, int Value);

/// <summary>
/// 读出来的 MIDI 内容。所有 tick 都用文件本身的 TicksPerQuarter，换算交给上层。
/// </summary>
public sealed class MidiFileData
{
    public int Format { get; init; }

    public int TrackCount { get; init; }

    /// <summary>文件时基（每四分音符 tick 数）。</summary>
    public int TicksPerQuarter { get; init; } = 480;

    public bool IsSmpteTiming { get; init; }

    public IReadOnlyList<MidiFileNote> Notes { get; init; } = Array.Empty<MidiFileNote>();

    public IReadOnlyList<MidiTempoChange> Tempos { get; init; } = Array.Empty<MidiTempoChange>();

    public IReadOnlyList<MidiTimeSignatureChange> TimeSignatures { get; init; } =
        Array.Empty<MidiTimeSignatureChange>();

    public IReadOnlyList<MidiControlChange> ControlChanges { get; init; } = Array.Empty<MidiControlChange>();

    /// <summary>用到的音色编号（去重，只用来提示"音色会丢"）。</summary>
    public IReadOnlyList<int> Programs { get; init; } = Array.Empty<int>();

    /// <summary>有没有读到调号 meta（导入时只作为提示，最终调性靠音高统计）。</summary>
    public bool HasKeySignature { get; init; }

    /// <summary>调号里升降号的个数（正数升号、负数降号）。</summary>
    public int KeySignatureSharps { get; init; }

    /// <summary>文件里最后一个事件的 tick，用来算主导速度的权重。</summary>
    public long LastTick { get; init; }

    /// <summary>按时间占比最大的那个速度（导入只用一个 bpm）。</summary>
    public double DominantBpm => PickDominantTempo(Tempos, LastTick);

    /// <summary>第一个拍号（没有就按 4/4）。</summary>
    public (int Numerator, int Denominator) FirstTimeSignature =>
        TimeSignatures.Count == 0 ? (4, 4) : (TimeSignatures[0].Numerator, TimeSignatures[0].Denominator);

    public static double PickDominantTempo(IReadOnlyList<MidiTempoChange> tempos, long lastTick)
    {
        if (tempos.Count == 0) return 120;

        var best = tempos[0].Bpm;
        var bestSpan = -1L;

        for (var i = 0; i < tempos.Count; i++)
        {
            var start = tempos[i].Tick;
            var end = i + 1 < tempos.Count ? tempos[i + 1].Tick : Math.Max(lastTick, start + 1);
            var span = end - start;
            if (span <= bestSpan) continue;

            bestSpan = span;
            best = tempos[i].Bpm;
        }

        return best;
    }
}

/// <summary>MIDI 文件读不了的时候抛这个，消息可以直接给用户看。</summary>
public sealed class MidiFileException : Exception
{
    public MidiFileException(string message) : base(message) { }
}
