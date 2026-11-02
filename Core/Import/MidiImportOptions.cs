namespace ATTM.Core.Import;

/// <summary>MIDI 导入选项。界面上不暴露；先用这套默认值，后面要加开关也放这里。</summary>
public sealed record MidiImportOptions
{
    /// <summary>打击乐通道（channel 10）是否也导进来。默认丢掉。</summary>
    public bool IncludePercussion { get; init; }

    /// <summary>把延音踏板（CC64）算进时值。</summary>
    public bool ApplySustainPedal { get; init; } = true;

    /// <summary>用 legato() 表达"均衡铺满一段、并且延音到段末"的音型。</summary>
    public bool UseLegato { get; init; } = true;

    /// <summary>自动检测调性；关掉则用 KeyRoot / KeySharp。</summary>
    public bool DetectKey { get; init; } = true;

    public char KeyRoot { get; init; } = 'C';

    public bool KeySharp { get; init; }

    /// <summary>指定速度；null 表示用文件里占比最大的那个。</summary>
    public double? Bpm { get; init; }
}

/// <summary>导入过程里攒下来的统计与警告，用来给用户一句人话提示。</summary>
public sealed class MidiImportReport
{
    public int SourceFormat { get; init; }

    public int SourceTrackCount { get; init; }

    public double Bpm { get; set; } = 120;

    public int BeatsPerMeasure { get; set; } = 4;

    public int BeatUnit { get; set; } = 4;

    /// <summary>拍号做了映射时的说明，例如 "2/4 每两小节并成一节 4/4"。</summary>
    public string? TimeSignatureNote { get; set; }

    public char KeyRoot { get; set; } = 'C';

    public bool KeySharp { get; set; }

    /// <summary>调性是不是猜出来的（用户没指定时）。</summary>
    public bool KeyDetected { get; set; }

    public int NoteCount { get; set; }

    public int MeasureCount { get; set; }

    /// <summary>输出文本的行数（不含空行与指令行）。</summary>
    public int LineCount { get; set; }

    /// <summary>用了几次 legato()。</summary>
    public int LegatoGroups { get; set; }

    /// <summary>用 legato() 连起来的音数（这些音靠"延音到段末"保时值，不重复起音）。</summary>
    public int TiedNotes { get; set; }

    /// <summary>时值没法写成一整格，只能截短/拉长的音数。</summary>
    public int ShortenedNotes { get; set; }

    /// <summary>一个小节里最多几行（声部数）。</summary>
    public int MaxLinesPerMeasure { get; set; }

    /// <summary>量化造成的最大偏差（毫秒）。</summary>
    public double MaxQuantizeErrorMs { get; set; }

    /// <summary>因为跨段而重复起音的个数。</summary>
    public int BeatSplits { get; set; }

    /// <summary>跨过小节线、尾巴很短所以直接收掉的音数。</summary>
    public int CrossMeasureTruncated { get; set; }

    /// <summary>跨过小节线、尾巴太长只能在下一个小节重新起音的音数。</summary>
    public int CrossMeasureRestarts { get; set; }

    public int SkippedPercussion { get; set; }

    public int DroppedNotes { get; set; }

    public List<string> Warnings { get; } = new();

    public void Warn(string message)
    {
        if (!Warnings.Contains(message)) Warnings.Add(message);
    }

    /// <summary>给状态栏用的一句话。</summary>
    public string Summary
    {
        get
        {
            var text = $"已导入 {NoteCount} 个音 · {MeasureCount} 小节 · {LineCount} 行";
            if (LegatoGroups > 0) text += $" · {LegatoGroups} 处连奏";
            if (MaxQuantizeErrorMs >= 1) text += $" · 最大量化偏差 {MaxQuantizeErrorMs:0} ms";
            return text;
        }
    }
}

/// <summary>导入结果：文本 + 统计。</summary>
public sealed record MidiImportResult(string Text, MidiImportReport Report);
