using System.IO;

namespace ATTM.Core.Midi;

/// <summary>
/// 标准 MIDI 文件（SMF）读取器：format 0 / 1 / 2，支持 running status、
/// note on 力度 0 当作 note off、同音叠音、tempo / 拍号 / 调号 / 延音踏板。
/// 只做"把文件摊平成音符列表"，不做任何音乐上的判断。
/// </summary>
public static class MidiFileReader
{
    public static MidiFileData Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new MidiFileException("没有指定 MIDI 文件。");
        if (!File.Exists(path)) throw new MidiFileException($"找不到文件：{path}");

        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new MidiFileException($"读取失败：{ex.Message}");
        }

        return Read(data);
    }

    public static MidiFileData Read(byte[] data)
    {
        if (data.Length < 14) throw new MidiFileException("文件太短，不像 MIDI 文件。");
        if (!Matches(data, 0, "MThd"u8)) throw new MidiFileException("不是 MIDI 文件（缺 MThd 头）。");

        var headerLength = ReadUInt32(data, 4);
        if (headerLength < 6) throw new MidiFileException("MIDI 头长度异常。");

        var format = ReadUInt16(data, 8);
        var trackCount = ReadUInt16(data, 10);
        var division = ReadUInt16(data, 12);

        var smpte = (division & 0x8000) != 0;
        if (smpte) throw new MidiFileException("这个 MIDI 用的是 SMPTE 时基（按时间码计），暂不支持导入。");

        var ticksPerQuarter = division == 0 ? 480 : division;

        var notes = new List<MidiFileNote>();
        var tempos = new List<MidiTempoChange>();
        var timeSignatures = new List<MidiTimeSignatureChange>();
        var controls = new List<MidiControlChange>();
        var programs = new SortedSet<int>();
        var hasKeySignature = false;
        var keySharps = 0;
        var lastTick = 0L;

        var offset = 8 + (int)headerLength;
        var tracksRead = 0;

        while (offset + 8 <= data.Length && tracksRead < Math.Max(trackCount, 1))
        {
            if (!Matches(data, offset, "MTrk"u8))
            {
                // 跳过不认识的块（有些文件带非标准块）
                if (offset + 8 > data.Length) break;
                var chunkLength = ReadUInt32(data, offset + 4);
                offset += 8 + (int)chunkLength;
                continue;
            }

            var length = ReadUInt32(data, offset + 4);
            var start = offset + 8;
            var end = start + (int)length;
            if (end > data.Length || end < start) throw new MidiFileException("MIDI 轨道数据被截断或长度异常。");

            var track = new TrackReader(
                data, start, end, ticksPerQuarter, notes, tempos, timeSignatures, controls, programs);

            track.Run(out var trackLastTick, out var trackHasKey, out var trackKeySharps);

            lastTick = Math.Max(lastTick, trackLastTick);
            if (trackHasKey)
            {
                hasKeySignature = true;
                keySharps = trackKeySharps;
            }

            offset = end;
            tracksRead++;
        }

        if (notes.Count == 0 && tracksRead == 0) throw new MidiFileException("MIDI 里没有任何轨道。");

        notes.Sort(static (a, b) =>
        {
            var byStart = a.StartTicks.CompareTo(b.StartTicks);
            return byStart != 0 ? byStart : a.Pitch.CompareTo(b.Pitch);
        });

        tempos.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        timeSignatures.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        controls.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));

        return new MidiFileData
        {
            Format = format,
            TrackCount = tracksRead,
            TicksPerQuarter = ticksPerQuarter,
            IsSmpteTiming = smpte,
            Notes = notes,
            Tempos = tempos,
            TimeSignatures = timeSignatures,
            ControlChanges = controls,
            Programs = programs.ToList(),
            HasKeySignature = hasKeySignature,
            KeySignatureSharps = keySharps,
            LastTick = lastTick
        };
    }

    private static bool Matches(byte[] data, int offset, ReadOnlySpan<byte> tag)
    {
        if (offset + tag.Length > data.Length) return false;

        for (var i = 0; i < tag.Length; i++)
        {
            if (data[offset + i] != tag[i]) return false;
        }

        return true;
    }

    private static int ReadUInt16(byte[] data, int offset)
    {
        if (offset + 2 > data.Length) throw new MidiFileException("MIDI 数据被截断。");
        return (data[offset] << 8) | data[offset + 1];
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) throw new MidiFileException("MIDI 数据被截断。");

        return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) | data[offset + 3];
    }

    /// <summary>单条轨道的解析状态。</summary>
    private sealed class TrackReader
    {
        private readonly byte[] _data;
        private readonly int _start;
        private readonly int _end;
        private readonly List<MidiFileNote> _notes;
        private readonly List<MidiTempoChange> _tempos;
        private readonly List<MidiTimeSignatureChange> _timeSignatures;
        private readonly List<MidiControlChange> _controls;
        private readonly SortedSet<int> _programs;

        /// <summary>同音高叠音用栈配对：(通道, 音高) -> 起始 tick 栈。</summary>
        private readonly Dictionary<int, Stack<(long Start, int Velocity)>> _open = new();

        public TrackReader(
            byte[] data, int start, int end, int ticksPerQuarter,
            List<MidiFileNote> notes, List<MidiTempoChange> tempos,
            List<MidiTimeSignatureChange> timeSignatures, List<MidiControlChange> controls,
            SortedSet<int> programs)
        {
            _data = data;
            _start = start;
            _end = end;
            _notes = notes;
            _tempos = tempos;
            _timeSignatures = timeSignatures;
            _controls = controls;
            _programs = programs;
        }

        public void Run(out long lastTick, out bool hasKeySignature, out int keySharps)
        {
            lastTick = 0;
            hasKeySignature = false;
            keySharps = 0;

            var position = _start;
            var tick = 0L;
            var runningStatus = 0;

            while (position < _end)
            {
                tick += ReadVariableLength(ref position);
                if (position >= _end) break;

                var status = _data[position];
                if (status < 0x80)
                {
                    // running status：省略状态字节，沿用上一个
                    if (runningStatus == 0) throw new MidiFileException("MIDI 事件缺少状态字节。");
                    status = (byte)runningStatus;
                }
                else
                {
                    position++;
                    if (status < 0xF0) runningStatus = status;
                }

                if (status == 0xFF)
                {
                    var type = ReadByte(ref position);
                    var length = (int)ReadVariableLength(ref position);
                    var dataStart = position;
                    position = Math.Min(_end, position + length);
                    var span = new ReadOnlySpan<byte>(_data, dataStart, Math.Max(0, Math.Min(length, _end - dataStart)));

                    switch (type)
                    {
                        case 0x51 when span.Length >= 3:
                        {
                            var microseconds = (span[0] << 16) | (span[1] << 8) | span[2];
                            if (microseconds > 0) _tempos.Add(new MidiTempoChange(tick, 60_000_000.0 / microseconds));
                            break;
                        }
                        case 0x58 when span.Length >= 2:
                        {
                            var numerator = span[0];
                            var denominator = 1 << Math.Clamp((int)span[1], 0, 7);
                            if (numerator > 0) _timeSignatures.Add(new MidiTimeSignatureChange(tick, numerator, denominator));
                            break;
                        }
                        case 0x59 when span.Length >= 1:
                        {
                            hasKeySignature = true;
                            keySharps = (sbyte)span[0];
                            break;
                        }
                        case 0x2F:
                            // end of track
                            position = _end;
                            break;
                    }

                    if (position >= _end) break;
                    continue;
                }

                if (status is 0xF0 or 0xF7)
                {
                    var length = (int)ReadVariableLength(ref position);
                    position = Math.Min(_end, position + length);
                    continue;
                }

                var command = status & 0xF0;
                var channel = status & 0x0F;

                switch (command)
                {
                    case 0x80:
                    {
                        var pitch = ReadByte(ref position);
                        ReadByte(ref position);  // 释放力度
                        CloseNote(channel, pitch, tick);
                        break;
                    }
                    case 0x90:
                    {
                        var pitch = ReadByte(ref position);
                        var velocity = ReadByte(ref position);
                        if (velocity == 0) CloseNote(channel, pitch, tick);
                        else OpenNote(channel, pitch, velocity, tick);
                        break;
                    }
                    case 0xA0:
                        ReadByte(ref position);
                        ReadByte(ref position);
                        break;
                    case 0xB0:
                    {
                        var controller = ReadByte(ref position);
                        var value = ReadByte(ref position);
                        _controls.Add(new MidiControlChange(tick, channel, controller, value));
                        break;
                    }
                    case 0xC0:
                    {
                        var program = ReadByte(ref position);
                        _programs.Add(program);
                        break;
                    }
                    case 0xD0:
                        ReadByte(ref position);
                        break;
                    case 0xE0:
                        ReadByte(ref position);
                        ReadByte(ref position);
                        break;
                    default:
                        throw new MidiFileException($"不认识的 MIDI 事件 0x{status:X2}。");
                }
            }

            // 轨道结束还没松开的音，按轨道末尾收尾
            CloseAll(tick);
            lastTick = tick;
        }

        private void OpenNote(int channel, int pitch, int velocity, long tick)
        {
            var key = NoteKey(channel, pitch);
            if (!_open.TryGetValue(key, out var stack))
            {
                stack = new Stack<(long Start, int Velocity)>();
                _open[key] = stack;
            }

            stack.Push((tick, velocity));
        }

        private void CloseNote(int channel, int pitch, long tick)
        {
            var key = NoteKey(channel, pitch);
            if (!_open.TryGetValue(key, out var stack) || stack.Count == 0) return;

            var (start, velocity) = stack.Pop();
            if (tick <= start) return;

            _notes.Add(new MidiFileNote(pitch, velocity, channel, start, tick));
        }

        private void CloseAll(long tick)
        {
            foreach (var (key, stack) in _open)
            {
                var channel = key >> 8;
                var pitch = key & 0xFF;

                while (stack.Count > 0)
                {
                    var (start, velocity) = stack.Pop();
                    if (tick > start) _notes.Add(new MidiFileNote(pitch, velocity, channel, start, tick));
                }
            }

            _open.Clear();
        }

        private static int NoteKey(int channel, int pitch) => (channel << 8) | pitch;

        private int ReadByte(ref int position)
        {
            if (position >= _end) throw new MidiFileException("MIDI 数据被截断。");
            return _data[position++];
        }

        private long ReadVariableLength(ref int position)
        {
            var value = 0L;

            for (var i = 0; i < 4; i++)
            {
                var b = ReadByte(ref position);
                value = (value << 7) | (long)(b & 0x7F);
                if ((b & 0x80) == 0) return value;
            }

            throw new MidiFileException("MIDI 变长整数格式错误。");
        }
    }
}
