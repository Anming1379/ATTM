using System.IO;
using System.Text;
using ATTM.Core.Midi;
using ATTM.Plugin.MidiImport;
using ATTM.Plugins;
using ATTM.Core.Syntax;
using ATTM.Editing;
using ATTM.Services;

namespace ATTM.Tests;

/// <summary>
/// 零依赖断言测试：dotnet run --project tests/ATTM.Tests
/// 退出码 0 = 全绿，非 0 = 有失败。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    /// <summary>系统的「音乐」目录；拿不到时退回家目录（与 AppConfig.LibraryFolder 的规则保持一致）。</summary>
    private static string MusicFolder
    {
        get
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            return string.IsNullOrEmpty(music)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : music;
        }
    }

    /// <summary>尝试建目录；没有权限时返回 false（仅用于跳过依赖文件系统的断言）。</summary>
    private static bool TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>测试用临时目录：放在程序输出目录下，避免受系统 %TEMP% 权限影响。</summary>
    private static string TempRoot
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "tmp");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length >= 3 && args[0] == "--export")
        {
            var result = AttmParser.Parse(File.ReadAllText(args[1]));
            foreach (var diagnostic in result.Diagnostics)
                Console.WriteLine($"  {diagnostic.Line}:{diagnostic.Column} E{diagnostic.CodeNumber} {diagnostic.Message}");

            if (result.Score is null || result.HasErrors)
            {
                Console.WriteLine("存在语法错误，拒绝导出");
                return 2;
            }

            MidiFileWriter.Write(result.Score, args[2]);
            Console.WriteLine($"已导出 {args[2]}（{result.Score.Notes.Count} 个音符，{result.Score.Duration:mm\\:ss}）");
            return 0;
        }

        const string Header = "bpm = 120\n1 = C 4/4\n\n";

        // ---------------------------------------------------------- 词法

        Check("词法：注释行", () =>
        {
            var tokens = AttmLexer.Lex("# 这是一行注释");
            return tokens.Count == 1 && tokens[0].Kind == TokenKind.Comment;
        });

        Check("词法：#4 是升号而不是注释", () =>
        {
            var tokens = AttmLexer.Lex("#4");
            return tokens.Count == 2
                   && tokens[0].Kind == TokenKind.Sharp
                   && tokens[1].Kind == TokenKind.Number;
        });

        Check("词法：每个数字字符单独成 token", () =>
        {
            var tokens = AttmLexer.Lex("1134");
            return tokens.Count == 4 && tokens.All(t => t.Kind == TokenKind.Number);
        });

        Check("词法：行尾注释", () =>
        {
            var tokens = AttmLexer.Lex("1,2 # ok");
            return tokens[^1].Kind == TokenKind.Comment && tokens[^1].Text == "# ok";
        });

        // ---------------------------------------------------------- 指令

        Check("指令：bpm 支持小数", () =>
        {
            var r = AttmParser.Parse("bpm = 120.5\n1 = C 4/4\n\n1");
            return r.Score is { Bpm: 120.5 } && !r.HasErrors;
        });

        Check("指令：bpm = 120 报 E3", () =>
        {
            var r = AttmParser.Parse("bpm=120\n1 = C 4/4\n\n1");
            return HasCode(r, DiagnosticCode.DirectiveSyntax);
        });

        Check("指令：缺少 bpm 报 E1", () =>
        {
            var r = AttmParser.Parse("1 = C 4/4\n\n1");
            return HasCode(r, DiagnosticCode.MissingBpm) && r.Score is null;
        });

        Check("指令：缺少调式报 E2", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n\n1");
            return HasCode(r, DiagnosticCode.MissingKeySignature) && r.Score is null;
        });

        Check("指令：调式必须 A-G（E110）", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = H 4/4\n\n1");
            return HasCode(r, DiagnosticCode.InvalidKeyRoot);
        });

        Check("指令：只支持 4/4 与 3/4（E111）", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = C 6/8\n\n1");
            return HasCode(r, DiagnosticCode.InvalidTimeSignature);
        });

        Check("指令：bpm 重复报 E4", () =>
        {
            var r = AttmParser.Parse("bpm = 120\nbpm = 90\n1 = C 4/4\n\n1");
            return HasCode(r, DiagnosticCode.DuplicateDirective);
        });

        Check("指令：出现在小节之后报 E5", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = C 4/4\n\n1\n\nbpm = 90");
            return HasCode(r, DiagnosticCode.DirectiveAfterContent);
        });

        Check("指令：Vel 解析", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = C 4/4\nVel = 100 +-20\n\n1");
            return r.Score is { VelocityBase: 100, VelocityJitter: 20 } && !r.HasErrors;
        });

        Check("指令：Vel 省略时默认 90 +-0", () =>
        {
            var r = Parse(Header, "1");
            return r.Score is { VelocityBase: 90, VelocityJitter: 0 };
        });

        Check("指令：Vel 超范围报 E109", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = C 4/4\nVel = 200\n\n1");
            return HasCode(r, DiagnosticCode.VelocityOutOfRange);
        });

        // ---------------------------------------------------------- 计划书 5.7 表格

        Check("时值：`1` 是全音符", () =>
            Notes(Parse(Header, "1"), (60, 0, 1920)));

        Check("时值：`1,0` 被休止截断为半音符", () =>
            Notes(Parse(Header, "1,0"), (60, 0, 960)));

        Check("时值：`1,0,0,0` 被休止截断为四分音符", () =>
            Notes(Parse(Header, "1,0,0,0"), (60, 0, 480)));

        Check("时值：`3,3,4,5`", () =>
            Notes(Parse(Header, "3,3,4,5"),
                (64, 0, 480), (64, 480, 480), (65, 960, 480), (67, 1440, 480)));

        Check("时值：`11,12,3,05`", () =>
            Notes(Parse(Header, "11,12,3,05"),
                (60, 0, 240), (60, 240, 240), (60, 480, 240), (62, 720, 240),
                (64, 960, 480), (67, 1680, 240)));

        Check("时值：`1134,5,12,0057`", () =>
            Notes(Parse(Header, "1134,5,12,0057"),
                (60, 0, 120), (60, 120, 120), (64, 240, 120), (65, 360, 120),
                (67, 480, 480),
                (60, 960, 240), (62, 1200, 240),
                (67, 1680, 120), (71, 1800, 120)));

        Check("时值：`3,02,2,0`", () =>
            Notes(Parse(Header, "3,02,2,0"),
                (64, 0, 480), (62, 720, 240), (62, 960, 480)));

        Check("休止：`0,0,0,0` 合法且无音符", () =>
        {
            var r = Parse(Header, "0,0,0,0");
            return !r.HasErrors && r.Score is { Notes.Count: 0 } and { MeasureCount: 1 };
        });

        // ---------------------------------------------------------- 和弦

        Check("和弦：`(46+1),(57+2),(357),(6+1+3)`", () =>
            Notes(Parse(Header, "(46+1),(57+2),(357),(6+1+3)"),
                (65, 0, 480), (69, 0, 480), (72, 0, 480),
                (67, 480, 480), (71, 480, 480), (74, 480, 480),
                (64, 960, 480), (67, 960, 480), (71, 960, 480),
                (69, 1440, 480), (72, 1440, 480), (76, 1440, 480)));

        Check("和弦：反拍 `0(46+1),...`", () =>
            Notes(Parse(Header, "0(46+1),0(57+2),(357),(6+1+3)"),
                (65, 240, 240), (69, 240, 240), (72, 240, 240),
                (67, 720, 240), (71, 720, 240), (74, 720, 240),
                (64, 960, 480), (67, 960, 480), (71, 960, 480),
                (69, 1440, 480), (72, 1440, 480), (76, 1440, 480)));

        Check("和弦：组内音共享源码区间", () =>
        {
            var r = Parse(Header, "(46+1)");
            var notes = r.Score!.Notes;
            return notes.Count == 3 && notes[0].Span == notes[1].Span && notes[1].Span == notes[2].Span;
        });

        // ---------------------------------------------------------- 并行小节

        Check("并行：三行同时演奏", () =>
        {
            var r = Parse(Header, "4,5,3,6\n6,7,5,+1\n+1,+2,7,+3");
            return !r.HasErrors
                   && r.Score is { MeasureCount: 1, Notes.Count: 12 }
                   && r.Score.Notes.Count(n => n.VoiceIndex == 0) == 4
                   && r.Score.Notes.Count(n => n.VoiceIndex == 1) == 4
                   && r.Score.Notes.Count(n => n.VoiceIndex == 2) == 4;
        });

        Check("并行：空行分隔出两个小节", () =>
        {
            var r = Parse(Header, "1\n\n1");
            return r.Score is { MeasureCount: 2 }
                   && Notes(r, (60, 0, 1920), (60, 1920, 1920));
        });

        Check("并行：注释行不打断小节组", () =>
        {
            var r = Parse(Header, "4,5,3,6\n# 注释\n6,7,5,+1");
            return r.Score is { MeasureCount: 1, Notes.Count: 8 };
        });

        Check("并行：同音高重叠被覆盖", () =>
        {
            var r = Parse(Header, "1\n1");
            return r.Score is { Notes.Count: 1 };
        });

        // ---------------------------------------------------------- 3/4

        Check("3/4：`1,2,3` 合法（三个四分音符）", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = C 3/4\n\n1,2,3");
            return !r.HasErrors
                   && Notes(r, (60, 0, 480), (62, 480, 480), (64, 960, 480));
        });

        Check("3/4：`1,2` 合法（附点二分音符切成两段）", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = C 3/4\n\n1,2");
            return !r.HasErrors && Notes(r, (60, 0, 720), (62, 720, 720));
        });

        Check("3/4：3 个逗号合法", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = C 3/4\n\n1,2,3,4");
            return !r.HasErrors
                   && Notes(r, (60, 0, 360), (62, 360, 360), (64, 720, 360), (65, 1080, 360));
        });

        Check("4/4：2 个逗号报 E108", () =>
            HasCode(Parse(Header, "1,2,3"), DiagnosticCode.InvalidSegmentCount));

        // ---------------------------------------------------------- 错误

        Check("错误：段内个数不是 2 的幂（E107）", () =>
            HasCode(Parse(Header, "123"), DiagnosticCode.UnitCountNotPowerOfTwo));

        Check("错误：空段（E114）", () =>
            HasCode(Parse(Header, "1,,2"), DiagnosticCode.EmptySegment));

        Check("错误：行尾逗号（E114）", () =>
            HasCode(Parse(Header, "1,"), DiagnosticCode.EmptySegment));

        Check("错误：括号未闭合（E104）", () =>
            HasCode(Parse(Header, "(46+1"), DiagnosticCode.UnclosedParenthesis));

        Check("错误：空和弦（E105）", () =>
            HasCode(Parse(Header, "()"), DiagnosticCode.EmptyChord));

        Check("错误：和弦里有 0（E106）", () =>
            HasCode(Parse(Header, "(406)"), DiagnosticCode.RestInChord));

        Check("错误：数字 8 非法（E101）", () =>
            HasCode(Parse(Header, "8"), DiagnosticCode.InvalidDigit));

        Check("错误：`+` 后没有数字（E117）", () =>
            HasCode(Parse(Header, "1+"), DiagnosticCode.OctaveMarkerWithoutDigit));

        Check("错误：`#` 后接空格是注释，不产生音符", () =>
        {
            var r = Parse(Header, "1,2,3,4\n\n# 4 5");
            return !r.HasErrors && r.Score is { Notes.Count: 4 };
        });

        Check("八度：`++1` 合法（= 高两个八度）", () =>
            !Parse(Header, "++1").HasErrors);

        Check("错误：混用八度标记 `+-1` 报 E102", () =>
            HasCode(Parse(Header, "+-1"), DiagnosticCode.InvalidCharacter));

        // ---------------------------------------------------------- 音高映射

        Check("音高：1 = D 时 1/+1/-1", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = D 4/4\n\n1\n\n+1\n\n-1");
            return Notes(r, (62, 0, 1920), (74, 1920, 1920), (50, 3840, 1920));
        });

        Check("音高：1 = B 时 7/+/#", () =>
        {
            var r = AttmParser.Parse("bpm = 120\n1 = B 4/4\n\n7\n\n#7\n\n+1");
            return Notes(r, (82, 0, 1920), (83, 1920, 1920), (83, 3840, 1920));
        });

        Check("音高：超出范围报 E115", () =>
        {
            // 本语法单次只能 ±1 个八度，正常输入不可达；直接验证映射器的边界保护
            return !PitchMapper.TryMap('C', 1, 6, false, out _)
                   && PitchMapper.TryMap('C', 1, 5, false, out var top) && top == 120;
        });

        // ---------------------------------------------------------- 位置换算

        Check("位置：偏移换算行列", () =>
        {
            const string text = "bpm = 120\n1 = C 4/4\n\n1,2,3,4";
            var r = AttmParser.Parse(text);
            var offset = text.IndexOf("2,3", StringComparison.Ordinal);
            var (line, column) = r.GetLineColumn(offset);
            return line == 4 && column == 3;
        });

        // ---------------------------------------------------------- 带错误的乐谱

        Check("容错：语法错误时仍可得到部分乐谱", () =>
        {
            var r = Parse(Header, "1,2,3,4\n\n123");
            return r.Score is not null && r.HasErrors && r.Score.Notes.Count == 4;
        });

        // ---------------------------------------------------------- MIDI 导出

        Check("MIDI：导出后回读一致（`3,02,2,0`）", () =>
        {
            var r = Parse(Header, "3,02,2,0");
            if (r.Score is null) return false;

            var path = Path.Combine(TempRoot, "attm-test-1.mid");
            MidiFileWriter.Write(r.Score, path, random: null);
            var midi = MidiReader.Read(path);
            File.Delete(path);

            var expected = new[]
            {
                new MidiReader.RawNote(0, 480, 64, 90),
                new MidiReader.RawNote(720, 240, 62, 90),
                new MidiReader.RawNote(960, 480, 62, 90)
            };

            return midi.TicksPerQuarter == 480 && midi.Notes.SequenceEqual(expected);
        });

        Check("MIDI：文件头为 PPQ 480 / 120bpm / 4-4", () =>
        {
            var r = Parse(Header, "1");
            if (r.Score is null) return false;

            var path = Path.Combine(TempRoot, "attm-test-2.mid");
            MidiFileWriter.Write(r.Score, path);
            var midi = MidiReader.Read(path);
            File.Delete(path);

            return midi.Format == 0
                   && midi.TrackCount == 1
                   && midi.TicksPerQuarter == 480
                   && midi.TempoMicroseconds == 500_000
                   && midi.BeatsPerMeasure == 4
                   && midi.BeatUnit == 4;
        });

        Check("MIDI：3/4 拍号写入正确", () =>
        {
            var r = AttmParser.Parse("bpm = 90\n1 = G 3/4\n\n1,2,3");
            if (r.Score is null) return false;

            var path = Path.Combine(TempRoot, "attm-test-3.mid");
            MidiFileWriter.Write(r.Score, path);
            var midi = MidiReader.Read(path);
            File.Delete(path);

            return midi.BeatsPerMeasure == 3
                   && midi.BeatUnit == 4
                   && midi.TempoMicroseconds == 666_667
                   && midi.Notes.Count == 3
                   && midi.Notes[0].Pitch == 67;
        });

        Check("MIDI：力度抖动被限制在 1-127", () =>
        {
            var random = new Random(1234);
            var ok = true;
            for (var i = 0; i < 400; i++)
            {
                var velocity = MidiSequence.ApplyJitter(120, 40, random);
                if (velocity is < 1 or > 127) { ok = false; break; }
            }

            return ok && MidiSequence.ApplyJitter(90, 0, null) == 90;
        });

        // ---------------------------------------------------------- 文本缓冲

        Check("缓冲：插入与行映射", () =>
        {
            var buffer = new TextBuffer("abc\ndef");
            buffer.Replace(1, 0, "XY");
            if (buffer.Text != "aXYbc\ndef") return false;
            if (buffer.LineCount != 2) return false;
            if (buffer.GetLineColumn(5) != (1, 6)) return false;
            if (buffer.GetOffset(1, 2) != 8) return false;
            return buffer.GetLine(1) == "def";
        });

        Check("缓冲：删除跨行文本", () =>
        {
            var buffer = new TextBuffer("abc\ndef");
            buffer.Replace(2, 3, "");
            return buffer.Text == "abef" && buffer.LineCount == 1;
        });

        // ---------------------------------------------------------- 语法高亮分区

        Check("高亮：注释 / 指令 / 音符 / 休止 / 升降号", () =>
        {
            var text = "# hi\nbpm = 120\n1 = C 4/4\n\n1,0,+#4";
            var r = AttmParser.Parse(text);
            var runs = EditorHighlighter.Build(r);

            static int Total(List<HighlightRun> runs, EditorTokenClass cls)
                => runs.Where(x => x.Class == cls).Sum(x => x.Length);

            return Total(runs[0], EditorTokenClass.Comment) > 0
                   && Total(runs[1], EditorTokenClass.Directive) == 3
                   && Total(runs[1], EditorTokenClass.Value) == 3
                   && Total(runs[4], EditorTokenClass.Note) == 2
                   && Total(runs[4], EditorTokenClass.Rest) == 1
                   && Total(runs[4], EditorTokenClass.Octave) == 1
                   && Total(runs[4], EditorTokenClass.Sharp) == 1;
        });

        Check("高亮：行号与源码行一一对应", () =>
        {
            var text = "bpm = 120\n1 = C 4/4\n\n1,2,3,4\n\n3,02,2,0";
            var r = AttmParser.Parse(text);
            var runs = EditorHighlighter.Build(r);
            return runs.Length == r.LineStarts.Count && runs.Length == 6;
        });

        Check("主题：配置字符串与枚举映射正确", () =>
            ThemeService.ParseMode("Dark") == AppTheme.Dark
            && ThemeService.ParseMode("Light") == AppTheme.Light
            && ThemeService.ParseMode("System") == AppTheme.System
            && ThemeService.ToConfigString(AppTheme.Light) == "Light"
            && ThemeService.ToConfigString(AppTheme.System) == "System");

        Check("主题：Light 配置不会被判成深色", () =>
        {
            var config = new AppConfig { Theme = "Light" };
            return !config.IsDark && config.Mode == AppTheme.Light;
        });

        Check("配置：默认字号已放大到 18", () => Math.Abs(new AppConfig().EditorFontSize - 18) < 0.01);

        Check("文件：UTF-8 无 BOM + LF 往返", () =>
        {
            const char cr = (char)13;
            const char lf = (char)10;

            var path = Path.Combine(TempRoot, "attm-io-roundtrip.attm");
            TextFile.Write(path, "bpm = 120" + cr + lf + "1 = C 4/4" + lf + "1,2,3");

            var text = TextFile.Read(path);
            var bytes = File.ReadAllBytes(path);
            File.Delete(path);

            return text == "bpm = 120" + lf + "1 = C 4/4" + lf + "1,2,3"
                   && bytes[0] != 0xEF;
        });

        Check("文件：BOM 与 GBK 老文件都能读", () =>
        {
            const char lf = (char)10;

            var bom = Path.Combine(TempRoot, "attm-io-bom.attm");
            File.WriteAllBytes(bom, new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(Encoding.UTF8.GetBytes("1,2,3,4")).ToArray());
            var bomOk = TextFile.Read(bom) == "1,2,3,4";
            File.Delete(bom);

            var gbkOk = true;
            try
            {
                var gbkPath = Path.Combine(TempRoot, "attm-io-gbk.attm");
                File.WriteAllBytes(gbkPath, Encoding.GetEncoding(936).GetBytes("# 欢乐颂" + lf + "1,2,3"));
                gbkOk = TextFile.Read(gbkPath).Contains("欢乐颂");
                File.Delete(gbkPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("      GBK 不可用：" + ex.GetType().Name);
            }

            return bomOk && gbkOk;
        });

        Check("配置：默认保存目录是 音乐/ATTM", () =>
        {
            var folder = AppConfig.ResolveSaveFolder(new AppConfig());
            var expected = Path.Combine(MusicFolder, "ATTM");
            Console.WriteLine("      " + folder);

            // 目录能不能建出来取决于运行环境权限，不参与断言；这里只校验契约（路径）。
            var creatable = TryCreateDirectory(expected);
            return string.Equals(folder, expected, StringComparison.OrdinalIgnoreCase)
                   && (!creatable || Directory.Exists(folder));
        });

        Check("性能：两千行谱面解析在 1 秒内", () =>
        {
            const char lf = (char)10;
            var builder = new StringBuilder("bpm = 120" + lf + "1 = C 4/4" + lf + lf);
            for (var i = 0; i < 500; i++)
                builder.Append("1,2,3,4" + lf + lf);

            var text = builder.ToString();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = AttmParser.Parse(text);
            watch.Stop();

            Console.WriteLine($"      {result.Score?.Notes.Count ?? 0} 音符，{watch.ElapsedMilliseconds} ms");
            return result.Score is not null && watch.ElapsedMilliseconds < 1000;
        });

        Check("文件：保存同时产出 .attm 和 .mid", () =>
        {
            const char lf = (char)10;

            var dir = Path.Combine(TempRoot, "attm-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var attmPath = Path.Combine(dir, "测试.attm");
            var midiPath = Path.ChangeExtension(attmPath, ".mid");
            var text = "bpm = 120" + lf + "1 = C 4/4" + lf + lf + "1,2,3,4";

            TextFile.Write(attmPath, text);

            var result = AttmParser.Parse(TextFile.Read(attmPath));
            if (result.Score is null) return false;

            MidiFileWriter.Write(result.Score, midiPath);

            var midi = File.ReadAllBytes(midiPath);
            var ok = midi.Length > 30
                     && Encoding.ASCII.GetString(midi, 0, 4) == "MThd"
                     && TextFile.Read(attmPath) == text;

            Directory.Delete(dir, true);
            return ok;
        });

        Check("教程：文档里的示例谱面都能解析", () =>
        {
            const char lf = (char)10;
            string J(params string[] lines) => string.Join(lf, lines);

            var examples = new (string Name, string Text)[]
            {
                ("五分钟上手", J("bpm = 120", "1 = C 4/4", "",
                    "1,2,3,4", "", "3,02,2,0", "", "3,4,5,4")),
                ("八度叠加", J("bpm = 120", "1 = C 4/4", "", "1,+1,++1,+++#1", "", "-1,--1,++1,---#1")),
                ("三种等价时值", J("bpm = 120", "1 = C 4/4", "", "1", "", "1,0", "", "1,0,0,0")),
                ("八分与十六分", J("bpm = 120", "1 = C 4/4", "",
                    "11,12,3,05", "", "1134,5,12,0057", "", "1111,2222,3333,4444")),
                ("升降号", J("bpm = 120", "1 = C 4/4", "", "#4,+#4,1,2")),
                ("三和弦", J("bpm = 90", "1 = C 4/4", "", "(46+1),(57+2),(357),(6+1+3)")),
                ("反拍和弦", J("bpm = 90", "1 = C 4/4", "", "0(46+1),0(57+2),0(357),0(6+1+3)")),
                ("多声部", J("bpm = 100", "1 = C 4/4", "",
                    "1,2,3,4", "3,4,5,4", "5,6,7,+1")),
                ("注释不打断分组", J("bpm = 120", "1 = C 4/4", "",
                    "1,2,3,4", "# 注释", "5,6,7,+1")),
                ("3/4 拍", J("bpm = 100", "1 = C 3/4", "", "1", "", "1,0", "", "1,0,0", "")),
                ("升号调式 #F", J("bpm = 120", "1 = #F 4/4", "", "1,#1,4,5")),
                ("升号调式 C#", J("bpm = 120", "1 = C# 4/4", "", "1,2,3,4")),
                ("力度抖动", J("bpm = 120", "1 = C 4/4", "Vel = 90 +-10", "", "1,2,3,4")),
                ("小数 bpm", J("bpm = 92.5", "1 = C 4/4", "", "1,2,3,4"))
            };

            var ok = true;
            foreach (var (name, text) in examples)
            {
                var result = AttmParser.Parse(text);
                if (result.CanPlay) continue;

                ok = false;
                var first = result.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
                Console.WriteLine($"      {name} 失败：{first?.Message}");
            }

            return ok;
        });

        Check("README：示例谱面都能解析", () =>
        {
            const char lf = (char)10;

            var text = string.Join(lf, new[]
            {
                "bpm = 120",
                "1 = C 4/4",
                "",
                "1,2,3,4",
                "",
                "3,02,2,0",
                "",
                "3,4,5,4",
                "",
                "5,6,7,+1",
                "(1+3+5),(2+4+6),(3+5+7),(4+6+1)",
                "",
                "(46+1),(57+2),(357),(6+1+3)"
            });

            var result = AttmParser.Parse(text);
            if (!result.CanPlay)
                Console.WriteLine("      " + result.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.Message);

            return result.CanPlay;
        });

        Check("谱库目录固定是 音乐ATTM", () =>
        {
            var expected = Path.Combine(MusicFolder, "ATTM");

            var library = AppConfig.LibraryFolder;

            // 即使上次保存到了别处，谱库也不跟着跑
            var config = new AppConfig { DefaultSaveFolder = TempRoot };
            var saveFolder = AppConfig.ResolveSaveFolder(config);

            Console.WriteLine($"      谱库 {library}");
            Console.WriteLine($"      上次保存目录存在时：保存用 {saveFolder}");

            var creatable = TryCreateDirectory(expected);

            return string.Equals(library, expected, StringComparison.OrdinalIgnoreCase)
                   && (!creatable || Directory.Exists(library))
                   && string.Equals(
                       saveFolder.TrimEnd(Path.DirectorySeparatorChar),
                       TempRoot.TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        });

        Check("示例谱子都能解析", () =>
        {
            var root = FindRepoRoot();
            if (root is null) return false;

            var folder = Path.Combine(root, "samples");
            if (!Directory.Exists(folder)) return false;

            var ok = true;
            foreach (var file in Directory.GetFiles(folder, "*.attm").OrderBy(f => f))
            {
                var result = AttmParser.Parse(File.ReadAllText(file, Encoding.UTF8));
                if (result.CanPlay) continue;

                ok = false;
                var first = result.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
                Console.WriteLine($"      {Path.GetFileName(file)}：{first?.Message}");
            }

            return ok;
        });

        Check("调式：升号调式 #C / C# 都支持", () =>
        {
            const char lf = (char)10;

            var prefix = AttmParser.Parse("bpm = 120" + lf + "1 = #C 4/4" + lf + lf + "1,4,5,7");
            var suffix = AttmParser.Parse("bpm = 120" + lf + "1 = C# 4/4" + lf + lf + "1,4,5,7");

            if (!prefix.CanPlay || !suffix.CanPlay) return false;

            var a = prefix.Score!.Notes.Select(n => n.Pitch).ToArray();
            var b = suffix.Score!.Notes.Select(n => n.Pitch).ToArray();

            Console.WriteLine($"      #C: {string.Join(",", a)}  C#: {string.Join(",", b)}  {prefix.Score.KeyName}");
            return a.SequenceEqual(new[] { 61, 66, 68, 72 })
                   && b.SequenceEqual(a)
                   && prefix.Score.KeyName == "C# 大调";
        });

        Check("调式：升号调式再加 # 是双重升号", () =>
        {
            const char lf = (char)10;

            var result = AttmParser.Parse("bpm = 120" + lf + "1 = #F 4/4" + lf + lf + "1,#1,4,5");
            if (!result.CanPlay) return false;

            var pitches = result.Score!.Notes.Select(n => n.Pitch).ToArray();
            Console.WriteLine($"      #F: 1,#1,4,5 -> {string.Join(",", pitches)}  {result.Score.KeyName}");

            return pitches.SequenceEqual(new[] { 66, 67, 71, 73 });
        });

        Check("调式：非法根音仍然报错", () =>
        {
            const char lf = (char)10;

            var bad = AttmParser.Parse("bpm = 120" + lf + "1 = #H 4/4" + lf + lf + "1,2,3,4");
            var ok = AttmParser.Parse("bpm = 120" + lf + "1 = #B 4/4" + lf + lf + "1,2,3,4");

            return !bad.CanPlay
                   && bad.Diagnostics.Any(d => d.Code == DiagnosticCode.InvalidKeyRoot)
                   && ok.CanPlay
                   && ok.Score!.Notes[0].Pitch == 72;
        });

        Check("行前缀：$ 整行升八度、% 整行降八度", () =>
        {
            const char lf = (char)10;

            var up = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "$ 1,2,3,4", "", "1,3,5,7"));
            var down = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "% 1,2,3,4"));

            if (!up.CanPlay || !down.CanPlay) return false;

            var pitches = up.Score!.Notes.Select(n => n.Pitch).ToArray();
            var downPitches = down.Score!.Notes.Select(n => n.Pitch).ToArray();

            Console.WriteLine($"      $ {string.Join(",", pitches)}  % {string.Join(",", downPitches)}");

            // 前四个音整体 +12，下一行不受影响
            return pitches.SequenceEqual(new[] { 72, 74, 76, 77, 60, 64, 67, 71 })
                   && downPitches.SequenceEqual(new[] { 48, 50, 52, 53 });
        });

        Check("行前缀：可以叠加，也能作用到和弦", () =>
        {
            const char lf = (char)10;

            var twice = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "$$ 1,2,3,4"));
            var chord = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "$ (45),(-6-1),1,2"));

            if (!twice.CanPlay || !chord.CanPlay) return false;

            var twicePitches = twice.Score!.Notes.Select(n => n.Pitch).ToArray();
            var chordPitches = chord.Score!.Notes.Select(n => n.Pitch).ToArray();

            Console.WriteLine($"      $$ {string.Join(",", twicePitches)}  和弦 {string.Join(",", chordPitches)}");

            return twicePitches.SequenceEqual(new[] { 84, 86, 88, 89 })
                   && chordPitches.SequenceEqual(new[] { 77, 79, 60, 69, 72, 74 });
        });

        Check("行前缀：必须行首、必须跟空格、不能混用", () =>
        {
            const char lf = (char)10;

            var noSpace = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "$1,2,3,4"));
            var mixed = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "$% 1,2,3,4"));
            var midLine = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "1,$2,3,4"));

            return noSpace.Diagnostics.Any(d => d.Message.Contains("必须跟一个空格"))
                   && mixed.Diagnostics.Any(d => d.Message.Contains("不能混用"))
                   && midLine.Diagnostics.Any(d => d.Message.Contains("只能写在行首"));
        });

        Check("legato：组内音依次起、时值撑到段末", () =>
        {
            const char lf = (char)10;

            var whole = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "legato(1234)"));
            var split = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "legato(135+7),legato(13)"));

            if (!whole.CanPlay || !split.CanPlay) return false;

            var wholeNotes = whole.Score!.Notes
                .Select(n => (n.Pitch, n.StartTicks, n.DurationTicks)).ToArray();
            var splitNotes = split.Score!.Notes
                .Select(n => (n.Pitch, n.StartTicks, n.DurationTicks)).ToArray();

            Console.WriteLine($"      legato(1234) -> {string.Join(" ", wholeNotes.Select(n => $"{n.Pitch}@{n.StartTicks}+{n.DurationTicks}"))}");
            Console.WriteLine($"      分组 -> {string.Join(" ", splitNotes.Select(n => $"{n.Pitch}@{n.StartTicks}+{n.DurationTicks}"))}");

            // 一个小节里每个音都撑到 1920 结束
            var wholeOk = wholeNotes.SequenceEqual(new[]
            {
                (60, 0L, 1920L), (62, 480L, 1440L), (64, 960L, 960L), (65, 1440L, 480L)
            });

            // 第一组撑到 960（第一段末），第二组撑到 1920
            var splitOk = splitNotes.SequenceEqual(new[]
            {
                (60, 0L, 960L), (64, 240L, 720L), (67, 480L, 480L), (83, 720L, 240L),
                (60, 960L, 960L), (64, 1440L, 480L)
            });

            return wholeOk && splitOk;
        });

        Check("legato：可以和行前缀连用", () =>
        {
            const char lf = (char)10;

            var combined = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "$ legato(1357),0,5,1"));
            var expanded = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "legato(+1+3+5+7),0,+5,+1"));

            if (!combined.CanPlay || !expanded.CanPlay) return false;

            var a = combined.Score!.Notes.Select(n => (n.Pitch, n.StartTicks, n.DurationTicks)).ToArray();
            var b = expanded.Score!.Notes.Select(n => (n.Pitch, n.StartTicks, n.DurationTicks)).ToArray();

            Console.WriteLine($"      $ legato -> {string.Join(" ", a.Select(n => $"{n.Pitch}@{n.StartTicks}+{n.DurationTicks}"))}");
            return a.SequenceEqual(b) && a.Length == 6;
        });

        Check("高亮：行前缀和 legato 有专门类别", () =>
        {
            var result = AttmParser.Parse(J(
                "bpm = 120", "1 = C 4/4", "",
                "$ legato(12),3,4,5",
                "% 1,2,3,4"));

            if (!result.CanPlay) return false;

            var runs = EditorHighlighter.Build(result);
            var up = runs[3].Select(r => r.Class).Distinct().ToList();
            var down = runs[4].Select(r => r.Class).Distinct().ToList();

            Console.WriteLine($"      $ 行：{string.Join(",", up)}  % 行：{string.Join(",", down)}");

            return up.Contains(EditorTokenClass.Octave)
                   && up.Contains(EditorTokenClass.Keyword)
                   && !up.Contains(EditorTokenClass.Unknown)
                   && down.Contains(EditorTokenClass.OctaveDown)
                   && !down.Contains(EditorTokenClass.Unknown);
        });

        Check("legato：括号缺失 / 为空 / 个数不是 2 的幂都要报错", () =>
        {
            const char lf = (char)10;

            var noParen = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "legato 1234"));
            var empty = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "legato()"));
            var odd = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "legato(102)"));

            Console.WriteLine($"      3 个音：{odd.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.Message}");
            return noParen.Diagnostics.Any(d => d.Message.Contains("必须紧跟括号"))
                   && empty.Diagnostics.Any(d => d.Message.Contains("不能为空"))
                   && odd.Diagnostics.Any(d => d.Message.Contains("必须是 1、2、4、8"));
        });

        Check("legato：括号里可以用休止符 0", () =>
        {
            const char lf = (char)10;

            var result = AttmParser.Parse(J("bpm = 120", "1 = C 4/4", "", "legato(1020)"));
            if (!result.CanPlay) return false;

            // 四个拍位：1 在 0、休止在 480、2 在 960；两个音都撑到段末 1920
            var notes = result.Score!.Notes
                .Select(n => (n.Pitch, n.StartTicks, n.DurationTicks)).ToArray();

            Console.WriteLine($"      legato(1020) -> {string.Join(" ", notes.Select(n => $"{n.Pitch}@{n.StartTicks}+{n.DurationTicks}"))}");

            return notes.SequenceEqual(new[] { (60, 0L, 1920L), (62, 960L, 960L) });
        });

        Check("音符：+/- 可以叠加表示多个八度", () =>
        {
            const char lf = (char)10;

            var result = AttmParser.Parse("bpm = 120" + lf + "1 = C 4/4" + lf + lf
                + "++1,--1,+++#1,--#1");

            if (!result.CanPlay) return false;

            var pitches = result.Score!.Notes.Select(n => n.Pitch).ToArray();
            Console.WriteLine($"      ++1,--1,+++#1,--#1 -> {string.Join(",", pitches)}");

            return pitches.SequenceEqual(new[] { 84, 36, 97, 37 });
        });

        Check("音符：混用 +/- 会报错", () =>
        {
            const char lf = (char)10;

            var mixed = AttmParser.Parse("bpm = 120" + lf + "1 = C 4/4" + lf + lf + "+-1,2,3,4");
            var rest = AttmParser.Parse("bpm = 120" + lf + "1 = C 4/4" + lf + lf + "++0,1,1,1");

            return mixed.Diagnostics.Any(d => d.Message.Contains("不能混用"))
                   && rest.Diagnostics.Any(d => d.Message.Contains("休止符 0"));
        });

        PlaybackChecks.Run();

        Console.WriteLine();
        // ---------------------------------------------------------- MIDI 导入

        Check("MIDI 导入：4/4 四分音符往返后音高与时值一致", () =>
{
            const char lf = (char)10;
            var source = "bpm = 120" + lf + "1 = C 4/4" + lf + lf + "1,2,3,4" + lf;
            var imported = ImportAttmViaMidi(source);

            var before = AttmParser.Parse(source).Score;
            var after = AttmParser.Parse(imported);

            if (before is null || !after.CanPlay)
            {
                Console.WriteLine(imported);
                return false;
            }

            Console.WriteLine("      导入结果：" + imported.Split(lf)[^2]);

            var a = before.Notes.Select(n => (n.Pitch, n.StartTicks, n.DurationTicks)).ToArray();
            var b = after.Score!.Notes.Select(n => (n.Pitch, n.StartTicks, n.DurationTicks)).ToArray();
            return a.SequenceEqual(b);
});

        Check("MIDI 导入：和弦转成括号", () =>
{
            const char lf = (char)10;
            var source = "bpm = 120" + lf + "1 = C 4/4" + lf + lf + "(135),(246)" + lf;
            var imported = ImportAttmViaMidi(source);

            Console.WriteLine("      " + imported.Split(lf).FirstOrDefault(l => l.Contains('(')));

            var result = AttmParser.Parse(imported);
            return result.CanPlay && imported.Contains('(') && result.Score!.Notes.Count == 6;
});

        Check("MIDI 导入：3/4 保持三拍", () =>
{
            const char lf = (char)10;
            var source = "bpm = 100" + lf + "1 = C 3/4" + lf + lf + "1,2,3" + lf;
            var imported = ImportAttmViaMidi(source);

            var result = AttmParser.Parse(imported);
            return result.CanPlay
                   && result.Score!.BeatsPerMeasure == 3
                   && result.Score.MeasureCount == 1
                   && result.Score.Notes.Count == 3;
});

        Check("MIDI 导入：长音不被拆碎（半音符 + 休止）", () =>
{
            const char lf = (char)10;
            var source = "bpm = 120" + lf + "1 = C 4/4" + lf + lf + "1,0" + lf;
            var imported = ImportAttmViaMidi(source);

            var result = AttmParser.Parse(imported);
            return result.CanPlay
                   && result.Score!.Notes.Count == 1
                   && result.Score.Notes[0].DurationTicks == 960;
});

        Check("MIDI 导入：调式推断为 C 大调", () =>
{
            const char lf = (char)10;
            var source = "bpm = 120" + lf + "1 = C 4/4" + lf + lf + "1,2,3,4" + lf + lf + "5,6,7,+1" + lf;
            var imported = ImportAttmViaMidi(source);

            var result = AttmParser.Parse(imported);
            return result.CanPlay && result.Score!.KeyRoot == 'C' && !result.Score.KeySharp;
});

        Check("MIDI 导入：仓库示例 .mid 能转换回可播放谱面", () =>
{
            var root = FindRepoRoot();
            if (root is null) return false;

            var midi = Path.Combine(root, "samples", "小星星 - 演示谱.mid");
            if (!File.Exists(midi)) return false;

            var result = MidiToAttm.Convert(SmfReader.Read(File.ReadAllBytes(midi)), "小星星.mid");
            var parsed = AttmParser.Parse(result.Text);

            Console.WriteLine($"      {result.MeasureCount} 小节 / {result.NoteCount} 音 / {result.KeyName} / 精确={result.RhythmExact}");

            return parsed.CanPlay && result.NoteCount > 20;
});

        Check("MIDI 导入：坏文件给出明确异常", () =>
{
            try
            {
                SmfReader.Read(new byte[] { 1, 2, 3, 4 });
                return false;
            }
            catch (SmfFormatException)
            {
                return true;
            }
});
        // ---------------------------------------------------------- 插件系统

        Check("插件：注册后进入导入列表", () =>
{
            using var host = new PluginHost(TempRoot, Path.Combine(TempRoot, "data"), Array.Empty<string>());
            var entry = host.Register(new FakeImporter("test.one"));

            return entry.IsLoaded
                   && host.Entries.Count == 1
                   && host.Importers.Count == 1
                   && host.Importers[0].Plugin!.Id == "test.one";
});

        Check("插件：重复 Id 只记一次", () =>
{
            using var host = new PluginHost(TempRoot, Path.Combine(TempRoot, "data"), Array.Empty<string>());
            host.Register(new FakeImporter("test.dup"));
            host.Register(new FakeImporter("test.dup"));

            return host.Entries.Count == 1;
});

        Check("插件：初始化失败不会拖垮宿主", () =>
{
            using var host = new PluginHost(TempRoot, Path.Combine(TempRoot, "data"), Array.Empty<string>());
            var bad = host.Register(new FakeImporter("test.bad", throwOnInitialize: true));
            var good = host.Register(new FakeImporter("test.good"));

            return !bad.IsLoaded
                   && bad.Error is not null
                   && good.IsLoaded
                   && host.Importers.Count == 1;
});

        Check("插件：导入结果可以直接被解析", () =>
{
            using var host = new PluginHost(TempRoot, Path.Combine(TempRoot, "data"), Array.Empty<string>());
            host.Register(new FakeImporter("test.import"));

            var importer = (IImporterPlugin)host.Importers[0].Plugin!;
            var result = importer.Import("x.fake");

            return AttmParser.Parse(result.Text).CanPlay;
});

        Check("插件：从目录装载真实插件 dll", () =>
{
            var dll = Path.Combine(AppContext.BaseDirectory, "ATTM.Plugin.MidiImport.dll");
            if (!File.Exists(dll))
            {
                Console.WriteLine("      输出目录里没有插件 dll，跳过");
                return true;
            }

            var dir = Path.Combine(TempRoot, "plugins-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.Copy(dll, Path.Combine(dir, Path.GetFileName(dll)), true);

            using var host = new PluginHost(TempRoot, Path.Combine(TempRoot, "data"), new[] { dir });
            host.LoadAll();

            Console.WriteLine($"      装载 {host.Entries.Count} 个，导入插件 {host.Importers.Count} 个");

            return host.Importers.Count == 1
                   && host.Importers[0].Plugin!.Id == "attm.midi-import"
                   && host.Importers[0].Plugin is IImporterPlugin;
});
        Check("插件：命令与界面贡献被聚合", () =>
{
            using var host = new PluginHost(TempRoot, Path.Combine(TempRoot, "data"), Array.Empty<string>());
            host.Register(new FakeUiPlugin());

            var commands = host.Commands;
            var items = host.UiItems;

            Console.WriteLine($"      命令 {commands.Count}，控件 {items.Count}");

            return commands.Count == 1
                   && commands[0].Command.Id == "hello"
                   && items.Count == 2
                   && items[0].Item.Slot == PluginUiSlot.StatusBar;   // Order 更小
});

        Check("插件：被禁用的 dll 不装载", () =>
{
            var dll = Path.Combine(AppContext.BaseDirectory, "ATTM.Plugin.MidiImport.dll");
            if (!File.Exists(dll)) return true;

            var dir = Path.Combine(TempRoot, "plugins-off-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var fileName = Path.GetFileName(dll);
            File.Copy(dll, Path.Combine(dir, fileName), true);

            using var host = new PluginHost(TempRoot, Path.Combine(TempRoot, "data"), new[] { dir },
                bridge: null, disabledFiles: new[] { fileName });
            host.LoadAll();

            return host.Entries.Count == 1
                   && host.Entries[0].State == PluginState.Disabled
                   && host.Entries[0].Plugin is null
                   && host.Importers.Count == 0;
});

        Check("架构：主程序不引用任何插件程序集", () =>
{
            var referenced = typeof(MainWindow).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(name => name.StartsWith("ATTM.Plugin", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Console.WriteLine("      引用插件程序集：" + (referenced.Count == 0 ? "无" : string.Join(",", referenced)));

            return referenced.Count == 0;
});
        // ---------------------------------------------------------- 沙箱与热重载

        Check("沙箱：扫描器能发现被禁止的 API", () =>
{
            // 测试程序自己就用了 System.IO.File / Directory，拿它当「不合规样本」
            var hits = PluginSecurityScanner.Scan(typeof(Program).Assembly.Location);

            Console.WriteLine("      命中：" + string.Join("、", hits.Take(4)));

            return hits.Any(h => h.Contains("System.IO.File", StringComparison.Ordinal)
                              || h.Contains("System.IO.Directory", StringComparison.Ordinal));
});

        Check("沙箱：内置 MIDI 插件本身合规", () =>
{
            var dll = Path.Combine(AppContext.BaseDirectory, "ATTM.Plugin.MidiImport.dll");
            if (!File.Exists(dll)) return true;

            var hits = PluginSecurityScanner.Scan(dll);

            Console.WriteLine("      命中：" + (hits.Count == 0 ? "无" : string.Join("、", hits)));

            return hits.Count == 0;
});

        Check("沙箱：申请无沙箱的插件在授权前不初始化", () =>
{
            using var host = new PluginHost(TempRoot, Path.Combine(TempRoot, "data"), Array.Empty<string>());
            var entry = host.Register(new FakeTrustPlugin());

            return entry.State == PluginState.PendingApproval
                   && entry.Plugin is not null
                   && host.PendingApprovals.Count == 1
                   && host.Importers.Count == 0;
});

        Check("沙箱：授权后按无沙箱装载", () =>
{
            using var host = new PluginHost(
                TempRoot, Path.Combine(TempRoot, "data"), Array.Empty<string>(),
                bridge: null, disabledFiles: null, approvedPlugins: new[] { "test.trust@1.0.0" });

            var entry = host.Register(new FakeTrustPlugin());

            return entry.IsLoaded && entry.Trust == PluginTrust.Unrestricted;
});

        Check("热重载：Reload 后插件重新装载", () =>
{
            var dll = Path.Combine(AppContext.BaseDirectory, "ATTM.Plugin.MidiImport.dll");
            if (!File.Exists(dll)) return true;

            var dir = Path.Combine(TempRoot, "plugins-reload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.Copy(dll, Path.Combine(dir, Path.GetFileName(dll)), true);

            using var host = new PluginHost(TempRoot, Path.Combine(TempRoot, "data"), new[] { dir });

            host.LoadAll();

            // 注意：重载前不要碰 host.Importers —— 它返回的列表会临时持有旧插件实例，
            // 在 Debug 构建里这些临时引用留在栈上，GC 就收不掉旧的加载上下文了。
            var clean = host.Reload();
            var after = host.Importers.Count;

            Console.WriteLine($"      重载后 {after} 个，上下文完全回收 {clean}");

            return after == 1 && host.Importers[0].Plugin!.Id == "attm.midi-import";
});
        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ 工具

    /// <summary>用换行拼一份谱面文本。</summary>
    private static string J(params string[] lines) => string.Join((char)10, lines);

    /// <summary>向上找仓库根目录（有 ATTM.csproj 的那一层），便于读 samples。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ATTM.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    private static ParseResult Parse(string header, string body) => AttmParser.Parse(header + body);

    private static bool HasCode(ParseResult result, DiagnosticCode code)
        => result.Diagnostics.Any(d => d.Code == code);

    private static bool Notes(ParseResult result, params (int Pitch, long Start, long Duration)[] expected)
    {
        var notes = result.Score?.Notes;
        if (notes is null || notes.Count != expected.Length)
        {
            if (notes is not null)
            {
                Console.WriteLine($"      实际音符数 {notes.Count}，期望 {expected.Length}");
                foreach (var note in notes)
                    Console.WriteLine($"      {note.Pitch}@{note.StartTicks}/{note.DurationTicks}");
            }

            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (notes[i].Pitch == expected[i].Pitch
                && notes[i].StartTicks == expected[i].Start
                && notes[i].DurationTicks == expected[i].Duration)
            {
                continue;
            }

            Console.WriteLine(
                $"      第 {i} 个音符：实际 {notes[i].Pitch}@{notes[i].StartTicks}/{notes[i].DurationTicks}，" +
                $"期望 {expected[i].Pitch}@{expected[i].Start}/{expected[i].Duration}");
            return false;
        }

        return true;
    }

    /// <summary>测试用的「申请无沙箱」插件。</summary>
    private sealed class FakeTrustPlugin : ITrustRequestPlugin
    {
        public string Id => "test.trust";
        public string Name => "要权限的插件";
        public string Version => "1.0.0";
        public string Description => "只用于测试";
        public string TrustJustification => "需要读写用户指定的谱面目录";

        public void Initialize(IPluginContext context) { }
        public void Shutdown() { }
    }

    /// <summary>测试用的「命令 + 界面」插件。</summary>
    private sealed class FakeUiPlugin : ICommandPlugin, IUiPlugin
    {
        public string Id => "test.ui";
        public string Name => "假界面插件";
        public string Version => "0.0.1";
        public string Description => "只用于测试";

        public IReadOnlyList<PluginCommand> Commands { get; } = new[]
        {
            new PluginCommand("hello", "打招呼", null, null, static context => context.Log("hi"))
        };

        public IReadOnlyList<PluginUiItem> UiItems { get; } = new[]
        {
            new PluginUiItem(PluginUiSlot.Toolbar, 20, static _ => new System.Windows.Controls.TextBlock { Text = "T" }),
            new PluginUiItem(PluginUiSlot.StatusBar, 10, static _ => new System.Windows.Controls.TextBlock { Text = "S" })
        };

        public void Initialize(IPluginContext context) { }
        public void Shutdown() { }
    }

    /// <summary>测试用的导入插件。</summary>
    private sealed class FakeImporter : IImporterPlugin
    {
        private readonly bool _throwOnInitialize;

        public FakeImporter(string id, bool throwOnInitialize = false)
        {
            Id = id;
            _throwOnInitialize = throwOnInitialize;
        }

        public string Id { get; }
        public string Name => "假插件";
        public string Version => "0.0.1";
        public string Description => "只用于测试";
        public IReadOnlyList<string> Extensions { get; } = new[] { ".fake" };
        public string FileFilter => "假文件 (*.fake)|*.fake";

        public void Initialize(IPluginContext context)
        {
            if (_throwOnInitialize) throw new InvalidOperationException("故意失败");
        }

        public void Shutdown() { }

        public PluginImportResult Import(string path)
            => new("# 假插件" + (char)10 + "bpm = 120" + (char)10 + "1 = C 4/4" + (char)10 + (char)10 + "1,2,3,4" + (char)10);
    }

    /// <summary>把一段 ATTM 文本写成 .mid，再经导入器转回文本（往返验证）。</summary>
    private static string ImportAttmViaMidi(string attmText)
    {
        var parsed = AttmParser.Parse(attmText);
        if (parsed.Score is null) throw new InvalidOperationException("示例谱面解析失败");

        var path = Path.Combine(TempRoot, "import-" + Guid.NewGuid().ToString("N") + ".mid");
        try
        {
            MidiFileWriter.Write(parsed.Score, path);
            var document = SmfReader.Read(File.ReadAllBytes(path));
            return MidiToAttm.Convert(document, "roundtrip.mid").Text;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    internal static void Check(string name, Func<bool> test)
    {
        try
        {
            if (test())
            {
                _passed++;
                Console.WriteLine($"  ok   {name}");
            }
            else
            {
                _failed++;
                Console.WriteLine($"  FAIL {name}");
            }
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL {name}：{ex.GetType().Name} {ex.Message}");
        }
    }
}
