using System.IO;
using System.Text;

namespace ATTM.Plugin.MidiAi;

/// <summary>一个音（绝对 tick）。</summary>
internal sealed record MiniNote(long Start, long Duration, int Pitch, int Velocity)
{
    public long End => Start + Duration;
}

/// <summary>本插件需要的 MIDI 信息，故意做得比完整实现窄。</summary>
internal sealed record MiniScore(
    int TicksPerQuarter,
    double Bpm,
    int BeatsPerMeasure,
    int BeatUnit,
    int? KeyFifths,
    IReadOnlyList<MiniNote> Notes)
{
    public long MeasureTicks => (long)TicksPerQuarter * 4 * BeatsPerMeasure / BeatUnit;
}

/// <summary>
/// 精简 SMF 读取（format 0/1，含 running status）。
/// 插件自带一份，避免依赖别的插件程序集。
/// </summary>
internal static class MiniSmf
{
    public static MiniScore Parse(byte[] data)
    {
        if (data.Length < 14 || Encoding.ASCII.GetString(data, 0, 4) != "MThd")
            throw new InvalidDataException("不是 MIDI 文件");

        var offset = 4;
        var headerLength = (int)ReadUInt32(data, ref offset);
        var format = ReadUInt16(data, ref offset);          // 0/1/2，本插件不区分
        var trackCount = ReadUInt16(data, ref offset);
        var division = ReadUInt16(data, ref offset);
        offset += headerLength - 6;

        _ = format;

        if ((division & 0x8000) != 0) throw new InvalidDataException("不支持 SMPTE 时间基准");

        var ppq = Math.Max(1, (int)division);
        var tempo = 500_000;
        var beats = 4;
        var beatUnit = 4;
        int? fifths = null;
        var notes = new List<MiniNote>();

        for (var track = 0; track < trackCount; track++)
        {
            if (offset + 8 > data.Length || Encoding.ASCII.GetString(data, offset, 4) != "MTrk") break;

            offset += 4;
            var end = offset + (int)ReadUInt32(data, ref offset);
            if (end > data.Length) end = data.Length;

            long tick = 0;
            var running = 0;
            var open = new Dictionary<int, Queue<(long Start, int Velocity)>>();

            while (offset < end)
            {
                tick += ReadVarInt(data, ref offset);
                if (offset >= end) break;

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
                    else if (type == 0x59 && length >= 2 && fifths is null)
                    {
                        fifths = (sbyte)data[offset];
                    }

                    offset += length;
                    continue;
                }

                if (status < 0x80) status = running;
                else { running = status; offset++; }

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
                            if (!open.TryGetValue(pitch, out var queue))
                            {
                                queue = new Queue<(long, int)>();
                                open[pitch] = queue;
                            }

                            queue.Enqueue((tick, velocity));
                        }
                        else if (open.TryGetValue(pitch, out var queue) && queue.Count > 0)
                        {
                            var on = queue.Dequeue();
                            if (queue.Count == 0) open.Remove(pitch);

                            if (tick > on.Start) notes.Add(new MiniNote(on.Start, tick - on.Start, pitch, on.Velocity));
                        }

                        break;
                    }
                    case 0xA0:
                    case 0xB0:
                    case 0xE0: offset += 2; break;
                    case 0xC0:
                    case 0xD0: offset += 1; break;
                    default: throw new InvalidDataException($"无法识别的状态字节 0x{status:X2}");
                }
            }

            offset = end;
        }

        if (notes.Count == 0) throw new InvalidDataException("文件里没有音符");

        notes.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));

        var bpm = tempo > 0 ? 60_000_000.0 / tempo : 120.0;
        return new MiniScore(ppq, bpm, beats, beatUnit, fifths, notes);
    }

    private static int ReadUInt16(byte[] d, ref int o)
    {
        var v = (d[o] << 8) | d[o + 1];
        o += 2;
        return v;
    }

    private static uint ReadUInt32(byte[] d, ref int o)
    {
        var v = ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
        o += 4;
        return v;
    }

    private static long ReadVarInt(byte[] d, ref int o)
    {
        long v = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = d[o++];
            v = (v << 7) | (long)(b & 0x7F);
            if ((b & 0x80) == 0) return v;
        }

        throw new InvalidDataException("变长整数过长");
    }
}
