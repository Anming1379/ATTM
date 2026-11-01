namespace ATTM.Core.Syntax;

/// <summary>
/// 简谱数字到 MIDI 音高的映射。
/// 约定：1 = C 时主音是中央 C（MIDI 60，C4），自然大调音阶。
/// 调式根音可以带升号（1 = #C / 1 = C#），整体加一个半音。
/// </summary>
public static class PitchMapper
{
    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };

    public static bool TryGetRootOffset(char key, out int offset)
    {
        switch (char.ToUpperInvariant(key))
        {
            case 'C': offset = 0; return true;
            case 'D': offset = 2; return true;
            case 'E': offset = 4; return true;
            case 'F': offset = 5; return true;
            case 'G': offset = 7; return true;
            case 'A': offset = 9; return true;
            case 'B': offset = 11; return true;
            default: offset = 0; return false;
        }
    }

    /// <param name="keyRoot">调式主音字母 A-G。</param>
    /// <param name="degree">简谱数字 1-7。</param>
    /// <param name="octave">八度偏移（+ 为 1，- 为 -1）。</param>
    /// <param name="sharp">是否给这个音加升号。</param>
    public static bool TryMap(char keyRoot, int degree, int octave, bool sharp, out int pitch)
        => TryMap(keyRoot, keySharp: false, degree, octave, sharp, out pitch);

    /// <param name="keyRoot">调式主音字母 A-G。</param>
    /// <param name="keySharp">调式是否带升号（1 = #C）。</param>
    /// <param name="degree">简谱数字 1-7。</param>
    /// <param name="octave">八度偏移（+ 为 1，- 为 -1）。</param>
    /// <param name="sharp">是否给这个音加升号。</param>
    public static bool TryMap(char keyRoot, bool keySharp, int degree, int octave, bool sharp, out int pitch)
    {
        pitch = 0;
        if (degree is < 1 or > 7) return false;
        if (!TryGetRootOffset(keyRoot, out var root)) return false;

        var value = AttmSpec.MiddleC + root + (keySharp ? 1 : 0) + MajorScale[degree - 1] + octave * 12 + (sharp ? 1 : 0);
        if (value is < AttmSpec.MinPitch or > AttmSpec.MaxPitch) return false;

        pitch = value;
        return true;
    }

    /// <summary>调式名称，例如 "C"、"F#"。</summary>
    public static string DescribeKey(char keyRoot, bool keySharp)
        => $"{char.ToUpperInvariant(keyRoot)}{(keySharp ? "#" : string.Empty)}";
}
