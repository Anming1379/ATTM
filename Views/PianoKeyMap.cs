using System.Windows.Input;

namespace ATTM.Views;

/// <summary>
/// 电脑键盘到钢琴键的映射：下排 Z-M 是一个八度，上排 Q-P 是更高的两个八度。
/// 和常见 DAW / 软音源的习惯一致，弹起来不用重新学。
/// </summary>
public static class PianoKeyMap
{
    private static readonly (Key Key, int Offset)[] Bindings =
    {
        // 低一个八度：白键 Z X C V B N M，黑键 S D G H J
        (Key.Z, 0), (Key.S, 1), (Key.X, 2), (Key.D, 3),
        (Key.C, 4), (Key.V, 5), (Key.G, 6),
        (Key.B, 7), (Key.H, 8), (Key.N, 9),
        (Key.J, 10), (Key.M, 11),
        (Key.OemComma, 12), (Key.L, 13), (Key.OemPeriod, 14),
        (Key.OemSemicolon, 15), (Key.OemQuestion, 16),

        // 高一个 / 高两个八度：Q 2 W 3 E R 5 T 6 Y 7 U ...
        (Key.Q, 12), (Key.D2, 13), (Key.W, 14), (Key.D3, 15),
        (Key.E, 16), (Key.R, 17), (Key.D5, 18), (Key.T, 19),
        (Key.D6, 20), (Key.Y, 21), (Key.D7, 22), (Key.U, 23),
        (Key.I, 24), (Key.D9, 25), (Key.O, 26), (Key.D0, 27),
        (Key.P, 28), (Key.OemOpenBrackets, 29), (Key.OemPlus, 30),
        (Key.OemCloseBrackets, 31), (Key.OemPipe, 33)
    };

    private static readonly Dictionary<Key, int> Offsets = new();

    /// <summary>一共覆盖多少个半音（从基准八度的 C 往上算）。</summary>
    public static int Span { get; } = Bindings.Max(static binding => binding.Offset) + 1;

    static PianoKeyMap()
    {
        foreach (var (key, offset) in Bindings) Offsets.TryAdd(key, offset);
    }

    /// <summary>这个键对应基准八度的第几个半音；不认识的键返回 false。</summary>
    public static bool TryGetOffset(Key key, out int offset) => Offsets.TryGetValue(key, out offset);
}
