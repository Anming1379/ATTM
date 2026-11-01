using System.Text;

namespace ATTM.Plugin.MidiAi;

/// <summary>把 MIDI 概要变成提示词。</summary>
internal static class Prompt
{
    private const int MaxMeasures = 8;

    public const string System = """
        你是简谱排版助手。把用户给的 MIDI 音符数据整理成 ATTM 简谱文本。

        ATTM 语法（必须严格遵守）：
        1. 文件开头必须先写指令，顺序随意但必须在所有小节之前：
           bpm = 120
           1 = C 4/4          （根音 C/D/E/F/G/A/B，可加 #；拍号只支持 4/4 与 3/4）
           Vel = 90 +-0
        2. 空行分隔小节；连续多行（不空行）= 同一小节的并行声部。
        3. 一行用逗号分段。4/4 每行段数只能是 1、2 或 4；3/4 是 1、2、3 或 4。
        4. 每段里的音符个数必须是 1、2、4、8……
        5. 一个音的时值 = 它到本行下一个音符的距离；行内最后一个音延到小节末尾。
        6. 0 是休止，会截断前一个音：1,0,0,0 表示一个四分音符 + 三拍空。
        7. 八度：+1 升一个八度，-1 降一个，可叠加；#4 是升半音；++#1 合法。
        8. 和弦用括号： (46+1)；( 里的音同时发声。
        9. 绝对不要写注释以外的解释文字，不要用 Markdown 代码块。

        只输出 ATTM 谱面文本本身。

        示例（输入 1 小节 4 个四分音符）：
        输入：m1: C4:0:480 D4:480:480 E4:960:480 F4:1440:480
        输出：
        bpm = 120
        1 = C 4/4
        Vel = 90 +-0

        1,2,3,4
        """;

    public static string Build(MiniScore score)
    {
        var builder = new StringBuilder();

        var key = score.KeyFifths is { } fifths ? KeyFromFifths(fifths) : "（未知，请根据音高判断）";

        builder.AppendLine($"原始信息：PPQ={score.TicksPerQuarter}，BPM={score.Bpm:0.##}，拍号={score.BeatsPerMeasure}/{score.BeatUnit}，调号提示={key}");
        builder.AppendLine();
        builder.AppendLine("音符数据（每行一个小节：小节号: 音名:起点tick:时值tick，空格分隔）：");

        var measureTicks = score.MeasureTicks;
        var measureCount = (int)(score.Notes.Max(n => n.End) / Math.Max(1, measureTicks)) + 1;
        var limit = Math.Min(measureCount, MaxMeasures);

        for (var m = 0; m < limit; m++)
        {
            var lo = m * measureTicks;
            var hi = lo + measureTicks;

            var items = score.Notes
                .Where(n => n.Start >= lo && n.Start < hi)
                .Select(n => $"{Name(n.Pitch)}:{n.Start - lo}:{Math.Min(n.Duration, hi - n.Start)}")
                .ToList();

            if (items.Count == 0) continue;

            builder.Append("m").Append(m + 1).Append(": ").AppendLine(string.Join(" ", items));
        }

        if (measureCount > limit)
            builder.AppendLine($"(后面还有 {measureCount - limit} 个小节，已省略)");

        builder.AppendLine();
        builder.AppendLine("要求：");
        builder.AppendLine("1. 先判断调式与拍号，写出 1 = X 与 bpm；");
        builder.AppendLine("2. 一小节一行（和弦多的可以考虑并行声部，但不要超过 3 行）；");
        builder.AppendLine("3. 把节奏规整到 4/4 或 3/4 的合理网格上，宁可简化也不要写出非法段数；");
        builder.AppendLine("4. 只输出这 8 个小节的谱面行 + 文件头的三行指令，不要任何解释文字；");
        builder.AppendLine("5. 每小节一行，用逗号分段，不要写小节号注释。");

        return builder.ToString();
    }

    private static string Name(int pitch)
    {
        string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        return names[((pitch % 12) + 12) % 12] + (pitch / 12 - 1);
    }

    private static string KeyFromFifths(int fifths)
    {
        string[] major = { "C", "G", "D", "A", "E", "B", "F#", "C#" };
        var index = Math.Abs(fifths);
        if (index > 7) return "C";

        if (fifths >= 0) return major[index];

        string[] flat = { "C", "F", "Bb", "Eb", "Ab", "Db", "Gb", "Cb" };
        return flat[index];
    }
}
