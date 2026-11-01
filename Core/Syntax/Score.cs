namespace ATTM.Core.Syntax;

/// <summary>一个发声音符。时间为绝对 tick（从文件开头算）。</summary>
public sealed record NoteEvent(
    int Pitch,
    int Velocity,
    long StartTicks,
    long DurationTicks,
    int MeasureIndex,
    int VoiceIndex,
    SourceSpan Span)
{
    public long EndTicks => StartTicks + DurationTicks;
}

/// <summary>解析成功后的乐谱（可能带有语法错误，出错时 UI 会禁止播放/保存）。</summary>
public sealed class Score
{
    public double Bpm { get; init; } = AttmSpec.DefaultBpm;
    public char KeyRoot { get; init; } = 'C';

    /// <summary>调式是否带升号（1 = #C）。</summary>
    public bool KeySharp { get; init; }
    public int BeatsPerMeasure { get; init; } = AttmSpec.DefaultBeatsPerMeasure;
    public int BeatUnit { get; init; } = AttmSpec.DefaultBeatUnit;
    public int VelocityBase { get; init; } = AttmSpec.DefaultVelocity;
    public int VelocityJitter { get; init; }
    public int TicksPerQuarter { get; init; } = AttmSpec.TicksPerQuarter;
    public int MeasureCount { get; init; }
    public IReadOnlyList<NoteEvent> Notes { get; init; } = Array.Empty<NoteEvent>();

    public int MeasureTicks => TicksPerQuarter * 4 * BeatsPerMeasure / BeatUnit;

    public long TotalTicks => (long)MeasureCount * MeasureTicks;

    public double SecondsPerTick => 60.0 / (Bpm * TicksPerQuarter);

    public TimeSpan Duration => TimeSpan.FromSeconds(TotalTicks * SecondsPerTick);

    public string KeyName => $"{PitchMapper.DescribeKey(KeyRoot, KeySharp)} 大调";

    public string TimeSignatureName => $"{BeatsPerMeasure}/{BeatUnit}";

    public long TicksToMilliseconds(long ticks) => (long)Math.Round(ticks * SecondsPerTick * 1000.0);

    /// <summary>第 <paramref name="measureIndex"/> 个小节起点对应的 tick（越界自动 clamp）。</summary>
    public long GetMeasureStartTicks(int measureIndex)
        => (long)Math.Clamp(measureIndex, 0, Math.Max(0, MeasureCount)) * MeasureTicks;

    /// <summary>第 <paramref name="measureIndex"/> 个小节起点对应的时间。</summary>
    public TimeSpan GetMeasureStartTime(int measureIndex)
        => TimeSpan.FromSeconds(GetMeasureStartTicks(measureIndex) * SecondsPerTick);

    /// <summary>把播放位置换算成小节序号（0 起，越界 clamp）。</summary>
    public int GetMeasureIndex(TimeSpan position)
    {
        if (MeasureCount <= 0 || MeasureTicks <= 0) return 0;

        var ticks = position.TotalSeconds / SecondsPerTick;
        var index = (int)Math.Floor(ticks / MeasureTicks);
        return Math.Clamp(index, 0, MeasureCount - 1);
    }
}
