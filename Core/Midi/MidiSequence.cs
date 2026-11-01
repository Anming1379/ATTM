using ATTM.Core.Syntax;

namespace ATTM.Core.Midi;

public enum MidiEventKind
{
    NoteOff = 0,
    NoteOn = 1
}

public readonly record struct MidiNoteMessage(long Tick, MidiEventKind Kind, int Pitch, int Velocity);

/// <summary>
/// 乐谱 -> MIDI 短消息序列。播放器和导出器共用同一份时间轴。
/// 同一 tick 上 NoteOff 排在 NoteOn 前面，避免同音高重复触发丢失。
/// </summary>
public static class MidiSequence
{
    public const int Channel = 0;

    public static List<MidiNoteMessage> BuildNoteMessages(Score score, Random? random = null)
    {
        var messages = new List<MidiNoteMessage>(score.Notes.Count * 2);

        foreach (var note in score.Notes)
        {
            var velocity = ApplyJitter(score.VelocityBase, score.VelocityJitter, random);
            messages.Add(new MidiNoteMessage(note.StartTicks, MidiEventKind.NoteOn, note.Pitch, velocity));
            messages.Add(new MidiNoteMessage(note.EndTicks, MidiEventKind.NoteOff, note.Pitch, 0));
        }

        messages.Sort(static (a, b) =>
        {
            var byTick = a.Tick.CompareTo(b.Tick);
            return byTick != 0 ? byTick : a.Kind.CompareTo(b.Kind);
        });

        return messages;
    }

    public static int ApplyJitter(int baseVelocity, int jitter, Random? random)
    {
        if (jitter <= 0 || random is null)
            return Math.Clamp(baseVelocity, AttmSpec.MinVelocity, AttmSpec.MaxVelocity);

        var offset = random.Next(-jitter, jitter + 1);
        return Math.Clamp(baseVelocity + offset, AttmSpec.MinVelocity, AttmSpec.MaxVelocity);
    }
}
