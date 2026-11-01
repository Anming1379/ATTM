using System.Text;

namespace ATTM.Plugin.MidiImport;

/// <summary>SMF 里的一个音，时间已换算成绝对 tick。</summary>
public sealed record SmfNote(long StartTicks, long DurationTicks, int Pitch, int Velocity)
{
    public long EndTicks => StartTicks + DurationTicks;
}

/// <summary>解析后的 SMF 文档：全局信息 + 音符列表（多轨已合并到同一条时间轴）。</summary>
public sealed record SmfDocument(
    int Format,
    int TrackCount,
    int TicksPerQuarter,
    double Bpm,
    int BeatsPerMeasure,
    int BeatUnit,
    int? KeyFifths,
    bool KeyMinor,
    IReadOnlyList<SmfNote> Notes)
{
    /// <summary>没有 tempo meta 时的默认值（120 BPM）。</summary>
    public const int DefaultTempoMicroseconds = 500_000;

    /// <summary>一个小节的 tick 数（使用文件自身的 PPQ）。</summary>
    public long MeasureTicks => (long)TicksPerQuarter * 4 * BeatsPerMeasure / BeatUnit;
}

/// <summary>MIDI 文件结构不合法时抛出。</summary>
public sealed class SmfFormatException : Exception
{
    public SmfFormatException(string message) : base(message) { }
}

/// <summary>
/// 标准 MIDI 文件读取器（format 0 / 1 / 2）。
/// 只取 ATTM 需要的信息：PPQ、速度、拍号、调号、音符；忽略控制器、歌词、SysEx 等。
/// </summary>
public static class SmfReader
{
    public static SmfDocument Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 14 || Encoding.ASCII.GetString(data, 0, 4) != "MThd")
            throw new SmfFormatException("不是 MIDI 文件（缺少 MThd 头）");

        var offset = 4;
        var headerLength = (int)ReadUInt32(data, ref offset);
        if (headerLength < 6) throw new SmfFormatException("MIDI 头长度非法");

        var format = ReadUInt16(data, ref offset);
        var trackCount = ReadUInt16(data, ref offset);
        var division = ReadUInt16(data, ref offset);
        offset += headerLength - 6;

        if ((division & 0x8000) != 0)
            throw new SmfFormatException("暂不支持 SMPTE 时间基准的 MIDI 文件");

        var ticksPerQuarter = Math.Max(1, (int)division);

        var tempo = SmfDocument.DefaultTempoMicroseconds;
        var beats = 4;
        var beatUnit = 4;
        int? keyFifths = null;
        var keyMinor = false;
        var notes = new List<SmfNote>();

        for (var track = 0; track < trackCount; track++)
        {
            if (offset + 8 > data.Length || Encoding.ASCII.GetString(data, offset, 4) != "MTrk")
                throw new SmfFormatException($"第 {track + 1} 个轨道头不是 MTrk");

            offset += 4;
            var trackEnd = offset + (int)ReadUInt32(data, ref offset);
            if (trackEnd > data.Length) throw new SmfFormatException("轨道长度超出文件末尾");

            long tick = 0;
            var runningStatus = 0;
            var active = new Dictionary<int, List<(long Start, int Velocity)>>();

            while (offset < trackEnd)
            {
                tick += ReadVarInt(data, ref offset);
                if (offset >= trackEnd) break;

                var status = (int)data[offset];

                if (status == 0xFF)
                {
                    offset++;
                    var type = data[offset++];
                    var length = (int)ReadVarInt(data, ref offset);

                    if (type == 0x51 && length == 3)
                        tempo = (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
                    else if (type == 0x58 && length >= 2)
                    {
                        beats = data[offset];
                        beatUnit = 1 << data[offset + 1];
                    }
                    else if (type == 0x59 && length >= 2)
                    {
                        var fifths = (sbyte)data[offset];
                        if (fifths is >= -7 and <= 7)
                        {
                            keyFifths ??= fifths;
                            keyMinor = data[offset + 1] != 0;
                        }
                    }

                    offset += length;
                    continue;
                }

                if (status < 0x80)
                {
                    if (runningStatus == 0) throw new SmfFormatException("使用了 running status 却没有前置状态字节");
                    status = runningStatus;
                }
                else
                {
                    runningStatus = status;
                    offset++;
                }

                switch (status & 0xF0)
                {
                    case 0x80:
                    case 0x90:
                    {
                        var pitch = data[offset];
                        var velocity = data[offset + 1];
                        offset += 2;

                        if ((status & 0xF0) == 0x90 && velocity > 0)
                        {
                            if (!active.TryGetValue(pitch, out var queue))
                            {
                                queue = new List<(long, int)>();
                                active[pitch] = queue;
                            }

                            queue.Add((tick, velocity));
                        }
                        else if (active.TryGetValue(pitch, out var queue) && queue.Count > 0)
                        {
                            var start = queue[0];
                            queue.RemoveAt(0);
                            if (queue.Count == 0) active.Remove(pitch);

                            var duration = tick - start.Start;
                            if (duration > 0) notes.Add(new SmfNote(start.Start, duration, pitch, start.Velocity));
                        }

                        break;
                    }
                    case 0xA0:
                    case 0xB0:
                    case 0xE0:
                        offset += 2;
                        break;
                    case 0xC0:
                    case 0xD0:
                        offset += 1;
                        break;
                    default:
                        throw new SmfFormatException($"无法识别的状态字节 0x{status:X2}");
                }
            }

            offset = trackEnd;
        }

        notes.Sort(static (a, b) => a.StartTicks != b.StartTicks
            ? a.StartTicks.CompareTo(b.StartTicks)
            : a.Pitch.CompareTo(b.Pitch));

        if (notes.Count == 0) throw new SmfFormatException("文件里没有可用的音符");

        var bpm = tempo > 0 ? 60_000_000.0 / tempo : 60_000_000.0 / SmfDocument.DefaultTempoMicroseconds;

        return new SmfDocument(format, trackCount, ticksPerQuarter, bpm, beats, beatUnit, keyFifths, keyMinor, notes);
    }

    private static int ReadUInt16(byte[] data, ref int offset)
    {
        if (offset + 2 > data.Length) throw new SmfFormatException("文件被截断");
        var value = (data[offset] << 8) | data[offset + 1];
        offset += 2;
        return value;
    }

    private static uint ReadUInt32(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length) throw new SmfFormatException("文件被截断");
        var value = ((uint)data[offset] << 24)
                    | ((uint)data[offset + 1] << 16)
                    | ((uint)data[offset + 2] << 8)
                    | data[offset + 3];
        offset += 4;
        return value;
    }

    private static long ReadVarInt(byte[] data, ref int offset)
    {
        long value = 0;
        for (var i = 0; i < 5; i++)
        {
            if (offset >= data.Length) throw new SmfFormatException("文件被截断");
            var b = data[offset++];
            value = (value << 7) | (long)(b & 0x7F);
            if ((b & 0x80) == 0) return value;
        }

        throw new SmfFormatException("变长整数超过 5 字节");
    }
}
