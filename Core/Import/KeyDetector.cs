using ATTM.Core.Midi;

namespace ATTM.Core.Import;

/// <summary>
/// 猜调性：在 12 个候选大调里挑"音阶内时长占比"最高的那个。
/// 小调会自动落到关系大调上（A 小调 -> 1 = C，主音写成 6），这正是简谱的习惯。
/// </summary>
public static class KeyDetector
{
    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly char[] Letters = { 'C', 'C', 'D', 'D', 'E', 'F', 'F', 'G', 'G', 'A', 'A', 'B' };

    /// <summary>音级 -> 是不是要写成带升号的根音（C# / D# / F# / G# / A#）。</summary>
    private static readonly bool[] SharpRoots = { false, true, false, true, false, false, true, false, true, false, true, false };

    /// <param name="pitchClassWeights">12 个音级各自的权重（一般用总时长）。</param>
    /// <param name="firstPitch">第一个音，用来在得分相同时偏向主音。</param>
    public static (char Root, bool Sharp) Detect(IReadOnlyList<double> pitchClassWeights, int? firstPitch = null)
    {
        if (pitchClassWeights.Count != 12) throw new ArgumentException("需要 12 个音级权重", nameof(pitchClassWeights));

        var bestRoot = 0;
        var bestScore = double.NegativeInfinity;

        for (var root = 0; root < 12; root++)
        {
            var inScale = 0.0;
            var outOfScale = 0.0;

            for (var pc = 0; pc < 12; pc++)
            {
                var weight = pitchClassWeights[pc];
                if (weight <= 0) continue;

                if (Array.IndexOf(MajorScale, ((pc - root) % 12 + 12) % 12) >= 0) inScale += weight;
                else outOfScale += weight;
            }

            var score = inScale - outOfScale * 1.5;

            // 主音自身有分量时加分（大调的主音通常是出现最多的音之一）
            score += pitchClassWeights[root] * 0.25;

            // 第一个音是主音或属音时稍微偏向它
            if (firstPitch is { } first)
            {
                var interval = ((first - root) % 12 + 12) % 12;
                if (interval == 0) score += 0.5;
                else if (interval == 7) score += 0.2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestRoot = root;
            }
        }

        return (Letters[bestRoot], SharpRoots[bestRoot]);
    }

    public static string Name(char root, bool sharp) => $"{root}{(sharp ? "#" : string.Empty)}";

    /// <summary>调式根音相对 C 的半音数（含升号）。</summary>
    public static int RootOffset(char root, bool sharp)
    {
        var offset = char.ToUpperInvariant(root) switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => 0
        };

        return offset + (sharp ? 1 : 0);
    }
}
