using System.IO;
using System.Text;
using ATTM.Core.Import;
using ATTM.Core.Midi;
using ATTM.Core.Syntax;

namespace ATTM.Tests;

/// <summary>
/// MIDI 读取 / MIDI 导入的测试。为了让"读取"和"导入"都能被测到，
/// 这里带一个手写的小 SMF 构造器（测试专用，不进产品代码）。
/// </summary>
internal static class ImportChecks
{
    public static void Run(Action<string, Func<bool>> check)
    {
        check("MIDI 读取：导出的文件能读回来", () =>
        {
            var original = ParseScore(File.ReadAllText(SamplePath("欢乐颂 - 演示谱.attm")));
            if (original is null) return false;

            var path = Path.Combine(Path.GetTempPath(), "attm-readback.mid");
            MidiFileWriter.Write(original, path);

            var read = MidiFileReader.Read(path);
            File.Delete(path);

            return read.Format == 0
                   && read.TicksPerQuarter == 480
                   && read.Notes.Count == original.Notes.Count
                   && Math.Abs(read.DominantBpm - original.Bpm) < 0.1
                   && read.FirstTimeSignature == (4, 4);
        });

        check("MIDI 导入：欢乐颂 环回（音高与小节数一致）", () =>
        {
            var original = ParseScore(File.ReadAllText(SamplePath("欢乐颂 - 演示谱.attm")));
            if (original is null) return false;

            var path = Path.Combine(Path.GetTempPath(), "attm-roundtrip.mid");
            MidiFileWriter.Write(original, path);

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            var imported = ParseScore(result.Text);
            if (imported is null) return false;

            var originalPitches = original.Notes.Select(n => n.Pitch).OrderBy(p => p).ToList();
            var importedPitches = imported.Notes.Select(n => n.Pitch).OrderBy(p => p).ToList();

            return originalPitches.SequenceEqual(importedPitches)
                   && imported.MeasureCount == original.MeasureCount
                   && Math.Abs(imported.Bpm - original.Bpm) < 0.1
                   && result.Report.MaxLinesPerMeasure >= 2;
        });

        check("MIDI 导入：生成的文本 0 语法错误", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-clean.mid");
            var builder = new SmfBuilder();
            builder.Note(0, 60, 100, 0, 480).Note(0, 62, 100, 480, 960).Note(0, 64, 100, 960, 1920);
            File.WriteAllBytes(path, builder.Build());

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            var parsed = AttmParser.Parse(result.Text);
            return parsed.Diagnostics.Count == 0 && result.Text.Contains("1 = C");
        });

        check("MIDI 导入：3/4 拍原样保留", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-3-4.mid");
            var builder = new SmfBuilder();
            builder.TimeSignature(3, 4);
            builder.Note(0, 60, 100, 0, 480).Note(0, 62, 100, 480, 960).Note(0, 64, 100, 960, 1440);
            File.WriteAllBytes(path, builder.Build());

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            return result.Text.Contains("3/4") && result.Report.BeatsPerMeasure == 3
                   && AttmParser.Parse(result.Text).Diagnostics.Count == 0;
        });

        check("MIDI 导入：6/8 精确映射成 3/4", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-6-8.mid");
            var builder = new SmfBuilder();
            builder.TimeSignature(6, 8);

            // 6/8 一小节 = 6 个八分 = 1440 tick，正好等于 3/4 一小节
            for (var i = 0; i < 6; i++) builder.Note(0, 60 + i, 100, i * 240, (i + 1) * 240);
            File.WriteAllBytes(path, builder.Build());

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            return result.Report.BeatsPerMeasure == 3
                   && result.Report.TimeSignatureNote is not null
                   && AttmParser.Parse(result.Text).Score?.MeasureCount == 1;
        });

        check("MIDI 导入：2/4 两小节并成一节 4/4", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-2-4.mid");
            var builder = new SmfBuilder();
            builder.TimeSignature(2, 4);
            for (var i = 0; i < 4; i++) builder.Note(0, 60 + i, 100, i * 480, (i + 1) * 480);
            File.WriteAllBytes(path, builder.Build());

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            return result.Report.BeatsPerMeasure == 4
                   && result.Report.TimeSignatureNote is not null
                   && AttmParser.Parse(result.Text).Score?.MeasureCount == 1;
        });

        check("MIDI 导入：5/4 直接拒绝并说明原因", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-5-4.mid");
            var builder = new SmfBuilder();
            builder.TimeSignature(5, 4);
            builder.Note(0, 60, 100, 0, 480);
            File.WriteAllBytes(path, builder.Build());

            try
            {
                MidiImporter.Convert(path);
                return false;
            }
            catch (MidiFileException ex)
            {
                return ex.Message.Contains("5/4") && ex.Message.Contains("4/4");
            }
            finally
            {
                File.Delete(path);
            }
        });

        check("MIDI 导入：打击乐通道被丢掉", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-drums.mid");
            var builder = new SmfBuilder();
            builder.Note(0, 60, 100, 0, 480);
            builder.Note(9, 36, 100, 0, 120).Note(9, 38, 100, 240, 360);
            File.WriteAllBytes(path, builder.Build());

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            return result.Report.SkippedPercussion == 2 && result.Report.NoteCount == 1;
        });

        check("MIDI 导入：延音踏板把音拉长并用 legato 写", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-pedal.mid");
            var builder = new SmfBuilder();

            // 四个八分音符，每个只响 240 tick，但踏板踩满整小节
            for (var i = 0; i < 4; i++) builder.Note(0, 60 + i, 100, i * 480, i * 480 + 120);
            builder.Control(0, 64, 127, 0).Control(0, 64, 0, 1920);
            File.WriteAllBytes(path, builder.Build());

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            var parsed = AttmParser.Parse(result.Text);
            var notes = parsed.Score?.Notes ?? Array.Empty<NoteEvent>();

            // 踏板把四个音都延到 1920，所以每个音都应该撑到小节末
            return result.Text.Contains("legato(")
                   && notes.Count == 4
                   && notes.All(n => n.EndTicks == 1920);
        });

        check("MIDI 导入：running status 与力度 0 当 note off", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-running.mid");
            File.WriteAllBytes(path, new RunningStatusBuilder().Build());

            var read = MidiFileReader.Read(path);
            var result = MidiImporter.Convert(path);
            File.Delete(path);

            return read.Notes.Count == 2
                   && read.Notes[0].Pitch == 60
                   && read.Notes[1].Pitch == 64
                   && AttmParser.Parse(result.Text).Diagnostics.Count == 0;
        });

        check("MIDI 导入：同音叠音用栈配对", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-stack.mid");
            var builder = new SmfBuilder();

            // 同一个音按两次，松两次：应该配成两条音，而不是丢掉一条
            builder.Raw(0, 0x90, 60, 100).Raw(240, 0x90, 60, 100);
            builder.Raw(480, 0x80, 60, 0).Raw(960, 0x80, 60, 0);
            File.WriteAllBytes(path, builder.Build());

            var read = MidiFileReader.Read(path);
            File.Delete(path);

            return read.Notes.Count == 2;
        });

        check("MIDI 导入：调性自动检测（G 大调）", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-key.mid");
            var builder = new SmfBuilder();

            // G 大调音阶：G A B C D E F#
            int[] pitches = { 67, 69, 71, 72, 74, 76, 78, 67 };
            for (var i = 0; i < pitches.Length; i++) builder.Note(0, pitches[i], 100, i * 480, (i + 1) * 480);
            File.WriteAllBytes(path, builder.Build());

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            return result.Text.Contains("1 = G 4/4") && result.Report.KeyDetected;
        });

        check("MIDI 导入：三连音会被量化但文本仍然合法", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-triplet.mid");
            var builder = new SmfBuilder();

            // 一拍分成三份（160 tick 一份）
            for (var i = 0; i < 3; i++) builder.Note(0, 60 + i, 100, i * 160, (i + 1) * 160);
            File.WriteAllBytes(path, builder.Build());

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            var parsed = AttmParser.Parse(result.Text);
            var pitches = parsed.Score?.Notes.Select(n => n.Pitch).ToHashSet() ?? new HashSet<int>();

            // 三连音没法精确写，量化到最近的格子（会留下量化偏差），但三个音都要在
            return parsed.Diagnostics.Count == 0
                   && pitches.IsSupersetOf(new[] { 60, 61, 62 })
                   && result.Report.MaxQuantizeErrorMs > 0;
        });

        check("MIDI 导入：踏板 + 同音高重叠不会漏音（回归）", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-dense.mid");
            var builder = new SmfBuilder();

            // 左手长音 + 右手同音高反复，再踩满踏板：会同时触发
            // "同音高覆盖"、"跨小节切分"、"跨段重复起音" 三条路径
            builder.Note(0, 48, 100, 0, 3840);
            for (var i = 0; i < 8; i++) builder.Note(0, 60, 100, i * 240, i * 240 + 120);
            for (var i = 0; i < 4; i++) builder.Note(0, 64, 100, i * 480, i * 480 + 480);
            builder.Control(0, 64, 127, 0).Control(0, 64, 0, 3840);

            File.WriteAllBytes(path, builder.Build());

            var result = MidiImporter.Convert(path);
            File.Delete(path);

            var parsed = AttmParser.Parse(result.Text);
            var pitches = parsed.Score?.Notes.Select(n => n.Pitch).ToHashSet() ?? new HashSet<int>();

            // Convert 内部已经校验过"写出来的音 == 解析出来的音"，这里再确认三个音都在
            return parsed.Diagnostics.Count == 0
                   && parsed.Score?.MeasureCount == 2
                   && pitches.IsSupersetOf(new[] { 48, 60, 64 })
                   && result.Report.MaxLinesPerMeasure >= 2;
        });

        check("MIDI 导入：文件损坏时报人话错误", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "attm-broken.mid");
            File.WriteAllBytes(path, new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xE0 });

            try
            {
                MidiImporter.Convert(path);
                return false;
            }
            catch (MidiFileException ex)
            {
                return ex.Message.Contains("MIDI");
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    private static string SamplePath(string name) =>
        Path.Combine(FindRepositoryRoot(), "samples", name);

    private static Score? ParseScore(string text)
    {
        var result = AttmParser.Parse(text);
        return result.HasErrors ? null : result.Score;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples")))
            directory = directory.Parent;

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    /// <summary>测试用的最小 SMF 构造器（所有事件都带状态字节）。</summary>
    private sealed class SmfBuilder
    {
        private readonly List<(long Tick, byte[] Data)> _events = new();

        public int TicksPerQuarter { get; init; } = 480;

        public SmfBuilder Note(int channel, int pitch, int velocity, long start, long end)
        {
            Raw(start, (byte)(0x90 | channel), (byte)pitch, (byte)velocity);
            Raw(end, (byte)(0x80 | channel), (byte)pitch, 0);
            return this;
        }

        public SmfBuilder Control(int channel, int controller, int value, long tick)
            => Raw(tick, (byte)(0xB0 | channel), (byte)controller, (byte)value);

        public SmfBuilder Program(int channel, int program, long tick)
            => Raw(tick, (byte)(0xC0 | channel), (byte)program);

        public SmfBuilder Tempo(double bpm, long tick = 0)
        {
            var microseconds = (int)Math.Round(60_000_000 / bpm);
            var data = new byte[] { 0xFF, 0x51, 0x03, (byte)(microseconds >> 16), (byte)(microseconds >> 8), (byte)microseconds };
            _events.Add((tick, data));
            return this;
        }

        public SmfBuilder TimeSignature(int numerator, int denominator, long tick = 0)
        {
            var power = 0;
            while ((1 << power) < denominator) power++;

            _events.Add((tick, new byte[] { 0xFF, 0x58, 0x04, (byte)numerator, (byte)power, 24, 8 }));
            return this;
        }

        public SmfBuilder Raw(long tick, params byte[] data)
        {
            _events.Add((tick, data));
            return this;
        }

        public byte[] Build()
        {
            var ordered = _events.OrderBy(e => e.Tick).ToList();

            using var track = new MemoryStream();
            var last = 0L;

            foreach (var (tick, data) in ordered)
            {
                WriteVarInt(track, tick - last);
                last = tick;
                track.Write(data);
            }

            WriteVarInt(track, 0);
            track.Write(new byte[] { 0xFF, 0x2F, 0x00 });

            using var file = new MemoryStream();
            using var writer = new BinaryWriter(file);

            writer.Write("MThd"u8);
            writer.Write(BigEndian(6));
            writer.Write(BigEndian16(0));
            writer.Write(BigEndian16(1));
            writer.Write(BigEndian16((ushort)TicksPerQuarter));
            writer.Write("MTrk"u8);
            writer.Write(BigEndian((uint)track.Length));
            writer.Write(track.ToArray());
            writer.Flush();

            return file.ToArray();
        }

        private static byte[] BigEndian(uint value) =>
            new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        private static byte[] BigEndian16(ushort value) => new[] { (byte)(value >> 8), (byte)value };

        private static void WriteVarInt(Stream stream, long value)
        {
            var buffer = new byte[4];
            var index = 3;
            buffer[index] = (byte)(value & 0x7F);

            while ((value >>= 7) > 0)
            {
                buffer[--index] = (byte)((value & 0x7F) | 0x80);
            }

            stream.Write(buffer, index, 4 - index);
        }
    }

    /// <summary>专门构造一个用 running status 的文件：第二个 note on / off 省略状态字节。</summary>
    private sealed class RunningStatusBuilder
    {
        public byte[] Build()
        {
            using var track = new MemoryStream();
            var writer = new BinaryWriter(track);

            WriteVarInt(track, 0);
            writer.Write(new byte[] { 0x90, 60, 100 });       // note on C4

            WriteVarInt(track, 480);
            writer.Write(new byte[] { 0x40, 0 });             // 只有数据：note on vel 0 = note off

            WriteVarInt(track, 0);
            writer.Write(new byte[] { 64, 100 });             // running status：note on E4

            WriteVarInt(track, 480);
            writer.Write(new byte[] { 0x80, 64, 0 });         // 显式 note off

            WriteVarInt(track, 0);
            writer.Write(new byte[] { 0xFF, 0x2F, 0x00 });
            writer.Flush();

            using var file = new MemoryStream();
            var head = new BinaryWriter(file);
            head.Write("MThd"u8);
            head.Write(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0 });
            head.Write("MTrk"u8);
            var length = (uint)track.Length;
            head.Write(new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length });
            head.Write(track.ToArray());
            head.Flush();

            return file.ToArray();
        }

        private static void WriteVarInt(Stream stream, long value)
        {
            var buffer = new byte[4];
            var index = 3;
            buffer[index] = (byte)(value & 0x7F);

            while ((value >>= 7) > 0)
            {
                buffer[--index] = (byte)((value & 0x7F) | 0x80);
            }

            stream.Write(buffer, index, 4 - index);
        }
    }
}
