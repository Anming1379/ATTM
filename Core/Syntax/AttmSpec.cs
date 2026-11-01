namespace ATTM.Core.Syntax;

/// <summary>
/// ATTM 语法规格里的固定数值。
/// 改这里等于改语言——必须同步 <c>docs/02-语法参考.md</c> 与 <c>tests/ATTM.Tests</c>。
/// </summary>
public static class AttmSpec
{
    /// <summary>每四分音符的 tick 数（MIDI PPQ）。</summary>
    public const int TicksPerQuarter = 480;

    /// <summary>最短可表示的单元时值：三十二分之一拍（15 tick）。</summary>
    public const int MinUnitTicks = TicksPerQuarter / 32;

    /// <summary>简谱 1 = C 时的基音（中央 C）。</summary>
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
}
