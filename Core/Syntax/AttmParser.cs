using System.Globalization;
using System.Text;

namespace ATTM.Core.Syntax;

/// <summary>ATTM 解析入口：纯函数，输入文本，输出乐谱 + 诊断 + token（供高亮）。</summary>
public static class AttmParser
{
    public static ParseResult Parse(string text) => new Parser(text ?? string.Empty).Run();
}

internal sealed class Parser
{
    private readonly string _text;
    private readonly List<Token> _tokens;
    private readonly List<List<Token>> _lines = new();
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly List<NoteEvent> _notes = new();
    private readonly List<VoiceLine> _group = new();
    private readonly List<int> _measureStartLines = new();

    private double? _bpm;
    private char? _keyRoot;
    private bool _keySharp;
    private int _beatsPerMeasure = AttmSpec.DefaultBeatsPerMeasure;
    private int _beatUnit = AttmSpec.DefaultBeatUnit;
    private bool _timeSignatureKnown;
    private int _velocityBase = AttmSpec.DefaultVelocity;
    private int _velocityJitter;
    private int _groupStartLine;
    private bool _sawBpm;
    private bool _sawKey;
    private bool _sawVelocity;
    private bool _contentStarted;
    private int _measureIndex;
    private readonly List<int> _lineStarts = new() { 0 };

    public Parser(string text)
    {
        _text = text;
        _tokens = AttmLexer.Lex(text);
        SplitLines();
        ComputeLineStarts();
    }

    private int MeasureTicks => AttmSpec.TicksPerQuarter * 4 * _beatsPerMeasure / _beatUnit;

    private void SplitLines()
    {
        var current = new List<Token>();
        foreach (var token in _tokens)
        {
            if (token.Kind == TokenKind.NewLine)
            {
                _lines.Add(current);
                current = new List<Token>();
            }
            else
            {
                current.Add(token);
            }
        }

        _lines.Add(current);
    }

    public ParseResult Run()
    {
        for (var lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
        {
            var line = _lines[lineIndex];
            var meaningful = new List<Token>();
            var hasComment = false;

            foreach (var token in line)
            {
                switch (token.Kind)
                {
                    case TokenKind.Comment:
                        hasComment = true;
                        break;
                    case TokenKind.Whitespace:
                    case TokenKind.NewLine:
                        break;
                    default:
                        meaningful.Add(token);
                        break;
                }
            }

            if (meaningful.Count == 0)
            {
                // 空行分隔并行小节组；纯注释行不影响分组
                if (!hasComment) FlushGroup();
                continue;
            }

            if (IsDirectiveLine(meaningful))
            {
                ParseDirective(line, meaningful);
                continue;
            }

            var (body, shift) = ReadLinePrefix(line, meaningful);
            if (body.Count == 0) continue;

            _contentStarted = true;
            if (_group.Count == 0) _groupStartLine = lineIndex;
            _group.Add(new VoiceLine(body, shift));
        }

        FlushGroup();

        if (!_sawBpm)
        {
            AddDiagnostic(DiagnosticCode.MissingBpm, 1, 1, SourceSpan.Empty,
                "缺少 bpm 声明，例如 \"bpm = 120\"");
        }

        if (!_sawKey)
        {
            AddDiagnostic(DiagnosticCode.MissingKeySignature, 1, 1, SourceSpan.Empty,
                "缺少调式与拍号声明，例如 \"1 = C 4/4\"");
        }

        Score? score = null;
        if (_bpm is { } bpm && _keyRoot is { } key && _timeSignatureKnown)
        {
            score = new Score
            {
                Bpm = bpm,
                KeyRoot = key,
                KeySharp = _keySharp,
                BeatsPerMeasure = _beatsPerMeasure,
                BeatUnit = _beatUnit,
                VelocityBase = Math.Clamp(_velocityBase, AttmSpec.MinVelocity, AttmSpec.MaxVelocity),
                VelocityJitter = _velocityJitter,
                TicksPerQuarter = AttmSpec.TicksPerQuarter,
                MeasureCount = _measureIndex,
                Notes = NoteMerger.Apply(_notes)
            };
        }

        var diagnostics = _diagnostics
            .OrderBy(d => d.Span.Start)
            .ThenBy(d => d.Code)
            .ToList();

        return new ParseResult(score, diagnostics, _tokens, _lineStarts, _measureStartLines);
    }

    // ---------------------------------------------------------------- 指令

    private static bool IsDirectiveLine(List<Token> meaningful)
    {
        if (meaningful.Count >= 2 && meaningful[0].Kind == TokenKind.Identifier)
        {
            var name = meaningful[0].Text;
            if (name.Equals("bpm", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("vel", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return meaningful.Count >= 2
               && meaningful[0].Kind == TokenKind.Number
               && meaningful[0].Text == "1"
               && meaningful[1].Kind == TokenKind.Equals;
    }

    private void ParseDirective(List<Token> rawLine, List<Token> meaningful)
    {
        var first = meaningful[0];

        if (_contentStarted)
        {
            AddDiagnostic(DiagnosticCode.DirectiveAfterContent, first, SpanOf(meaningful),
                "指令必须写在所有小节之前");
            return;
        }

        var equalsIndex = -1;
        for (var i = 1; i < meaningful.Count; i++)
        {
            if (meaningful[i].Kind == TokenKind.Equals)
            {
                equalsIndex = i;
                break;
            }
        }

        if (equalsIndex < 0)
        {
            AddDiagnostic(DiagnosticCode.DirectiveSyntax, first, SpanOf(meaningful),
                "指令格式错误，应为 \"名称 = 值\"");
            return;
        }

        var equals = meaningful[equalsIndex];
        if (!HasSpaceAfterEquals(rawLine, equals))
        {
            AddDiagnostic(DiagnosticCode.DirectiveSyntax, equals, equals.Span,
                "等号右侧必须有空格，例如 \"bpm = 120\"");
            return;
        }

        var name = first.Kind == TokenKind.Identifier ? first.Text : "key";
        if (name.Equals("bpm", StringComparison.OrdinalIgnoreCase))
        {
            ParseBpmDirective(meaningful, equalsIndex);
        }
        else if (name.Equals("vel", StringComparison.OrdinalIgnoreCase))
        {
            ParseVelocityDirective(meaningful, equalsIndex);
        }
        else
        {
            ParseKeyDirective(meaningful, equalsIndex);
        }
    }

    private static bool HasSpaceAfterEquals(List<Token> rawLine, Token equals)
    {
        for (var i = 0; i < rawLine.Count; i++)
        {
            if (rawLine[i].Span.Start != equals.Span.Start || rawLine[i].Span.Length != equals.Span.Length)
                continue;

            return i + 1 < rawLine.Count && rawLine[i + 1].Kind == TokenKind.Whitespace;
        }

        return false;
    }

    private void ParseBpmDirective(List<Token> tokens, int equalsIndex)
    {
        if (_sawBpm)
        {
            AddDiagnostic(DiagnosticCode.DuplicateDirective, tokens[0], SpanOf(tokens),
                "\"bpm\" 只能声明一次");
            return;
        }

        _sawBpm = true;

        var index = equalsIndex + 1;
        if (!TryReadNumber(tokens, index, out var literal, out var next) || next != tokens.Count)
        {
            AddDiagnostic(DiagnosticCode.DirectiveSyntax, tokens[0], SpanOf(tokens),
                "bpm 值必须是数字，例如 \"bpm = 120\"");
            return;
        }

        if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm))
        {
            AddDiagnostic(DiagnosticCode.DirectiveSyntax, tokens[0], SpanOf(tokens),
                "bpm 值必须是数字，例如 \"bpm = 120\"");
            return;
        }

        if (bpm < AttmSpec.MinBpm || bpm > AttmSpec.MaxBpm)
        {
            AddDiagnostic(DiagnosticCode.BpmOutOfRange, tokens[index], tokens[index].Span,
                "bpm 必须在 1 到 1000 之间");
            return;
        }

        _bpm = bpm;
    }

    private void ParseKeyDirective(List<Token> tokens, int equalsIndex)
    {
        if (_sawKey)
        {
            AddDiagnostic(DiagnosticCode.DuplicateDirective, tokens[0], SpanOf(tokens),
                "调式与拍号只能声明一次");
            return;
        }

        _sawKey = true;

        var index = equalsIndex + 1;

        // 允许前置升号：1 = #C 4/4
        var keySharp = false;
        if (index < tokens.Count && tokens[index].Kind == TokenKind.Sharp)
        {
            keySharp = true;
            index++;
        }

        if (index >= tokens.Count || tokens[index].Kind != TokenKind.Identifier)
        {
            var at = tokens[Math.Min(index, tokens.Count - 1)];
            AddDiagnostic(DiagnosticCode.InvalidKeyRoot, at, at.Span,
                "调式必须是 C、D、E、F、G、A、B（可加 #，例如 1 = #C 4/4）");
            return;
        }

        var keyToken = tokens[index];
        var keyText = keyToken.Text;
        if (keyText.Length != 1
            || !PitchMapper.TryGetRootOffset(keyText[0], out _)
            || char.ToUpperInvariant(keyText[0]) > 'G')
        {
            AddDiagnostic(DiagnosticCode.InvalidKeyRoot, keyToken, keyToken.Span,
                "调式必须是 C、D、E、F、G、A、B（可加 #，例如 1 = #C 4/4）");
            return;
        }

        index++;

        // 也接受后置写法：1 = C# 4/4
        if (index < tokens.Count && tokens[index].Kind == TokenKind.Sharp)
        {
            keySharp = true;
            index++;
        }

        if (index + 2 >= tokens.Count
            || tokens[index].Kind != TokenKind.Number
            || tokens[index + 1].Kind != TokenKind.Slash
            || tokens[index + 2].Kind != TokenKind.Number)
        {
            AddDiagnostic(DiagnosticCode.DirectiveSyntax, keyToken, SpanOf(tokens),
                "拍号格式错误，仅支持 4/4 或 3/4");
            return;
        }

        var beatsToken = tokens[index];
        var unitToken = tokens[index + 2];
        var beats = beatsToken.Text[0] - '0';
        var unit = unitToken.Text[0] - '0';

        if (index + 3 != tokens.Count)
        {
            var extra = tokens[index + 3];
            AddDiagnostic(DiagnosticCode.DirectiveSyntax, extra, extra.Span,
                "拍号后面不能有多余内容");
            return;
        }

        if (!((beats == 4 && unit == 4) || (beats == 3 && unit == 4)))
        {
            AddDiagnostic(DiagnosticCode.InvalidTimeSignature, beatsToken, SpanOf(tokens),
                "仅支持 4/4 和 3/4 拍");
            return;
        }

        _keyRoot = char.ToUpperInvariant(keyText[0]);
        _keySharp = keySharp;
        _beatsPerMeasure = beats;
        _beatUnit = unit;
        _timeSignatureKnown = true;
    }

    private void ParseVelocityDirective(List<Token> tokens, int equalsIndex)
    {
        if (_sawVelocity)
        {
            AddDiagnostic(DiagnosticCode.DuplicateDirective, tokens[0], SpanOf(tokens),
                "\"Vel\" 只能声明一次");
            return;
        }

        _sawVelocity = true;

        var index = equalsIndex + 1;
        if (!TryReadNumber(tokens, index, out var baseLiteral, out var next) ||
            !int.TryParse(baseLiteral, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baseVelocity))
        {
            AddDiagnostic(DiagnosticCode.DirectiveSyntax, tokens[0], SpanOf(tokens),
                "力度基准必须是整数，例如 \"Vel = 90 +-10\"");
            return;
        }

        if (baseVelocity is < AttmSpec.MinVelocity or > AttmSpec.MaxVelocity)
        {
            AddDiagnostic(DiagnosticCode.VelocityOutOfRange, tokens[index], tokens[index].Span,
                "力度基准必须在 1 到 127 之间");
            return;
        }

        var jitter = 0;
        if (next < tokens.Count)
        {
            if (next + 2 >= tokens.Count
                || tokens[next].Kind != TokenKind.Plus
                || tokens[next + 1].Kind != TokenKind.Minus
                || !TryReadNumber(tokens, next + 2, out var jitterLiteral, out var end)
                || end != tokens.Count
                || !int.TryParse(jitterLiteral, NumberStyles.Integer, CultureInfo.InvariantCulture, out jitter))
            {
                AddDiagnostic(DiagnosticCode.DirectiveSyntax, tokens[next], SpanOf(tokens),
                    "随机力度格式错误，例如 \"Vel = 90 +-10\"");
                return;
            }

            if (jitter is < 0 or > AttmSpec.MaxVelocityJitter)
            {
                AddDiagnostic(DiagnosticCode.VelocityOutOfRange, tokens[next + 2], tokens[next + 2].Span,
                    "随机力度范围必须在 0 到 127 之间");
                return;
            }
        }

        _velocityBase = baseVelocity;
        _velocityJitter = jitter;
    }

    private static bool TryReadNumber(List<Token> tokens, int start, out string literal, out int next)
    {
        var builder = new StringBuilder();
        var i = start;

        while (i < tokens.Count && tokens[i].Kind is TokenKind.Number or TokenKind.Dot)
        {
            if (i > start && tokens[i].Span.Start != tokens[i - 1].Span.End) break;
            builder.Append(tokens[i].Text);
            i++;
        }

        literal = builder.ToString();
        next = i;
        return builder.Length > 0;
    }

    // ---------------------------------------------------------------- 小节

    private void FlushGroup()
    {
        if (_group.Count == 0) return;

        _measureStartLines.Add(_groupStartLine);
        var measureTicks = MeasureTicks;
        for (var voice = 0; voice < _group.Count; voice++)
        {
            ParseVoiceLine(_group[voice], measureTicks, voice);
        }

        _group.Clear();
        _measureIndex++;
    }

    /// <summary>
    /// 剥掉行首的 $ / % 前缀：每个 $ 整行升一个八度，每个 % 降一个八度，只影响这一行。
    /// </summary>
    private (List<Token> Tokens, int Shift) ReadLinePrefix(List<Token> rawLine, List<Token> meaningful)
    {
        var ups = 0;
        var downs = 0;
        var index = 0;

        while (index < meaningful.Count
               && meaningful[index].Kind is TokenKind.LineOctaveUp or TokenKind.LineOctaveDown)
        {
            if (meaningful[index].Kind == TokenKind.LineOctaveUp) ups++;
            else downs++;
            index++;
        }

        // 前缀只允许出现在行首
        for (var i = index; i < meaningful.Count; i++)
        {
            if (meaningful[i].Kind is TokenKind.LineOctaveUp or TokenKind.LineOctaveDown)
            {
                AddDiagnostic(DiagnosticCode.LineOctaveNotAtStart, meaningful[i], meaningful[i].Span,
                    "八度前缀 $ / % 只能写在行首，例如 \"$ 1,2,3,4\"");
                break;
            }
        }

        if (index == 0) return (meaningful, 0);

        var rest = meaningful.GetRange(index, meaningful.Count - index);

        if (ups > 0 && downs > 0)
        {
            AddDiagnostic(DiagnosticCode.LineOctaveMixed, meaningful[0], SpanOf(meaningful),
                "八度前缀不能混用 $ 和 %");
            return (rest, 0);
        }

        // 前缀后面必须跟空格
        var lastPrefix = meaningful[index - 1];
        var hasSpace = false;
        for (var i = 0; i < rawLine.Count; i++)
        {
            if (rawLine[i].Span.Start != lastPrefix.Span.Start) continue;

            hasSpace = i + 1 < rawLine.Count && rawLine[i + 1].Kind == TokenKind.Whitespace;
            break;
        }

        if (!hasSpace)
        {
            AddDiagnostic(DiagnosticCode.LineOctaveWithoutSpace, lastPrefix, lastPrefix.Span,
                "八度前缀 $ / % 后面必须跟一个空格，例如 \"$ 1,2,3,4\"");
        }

        return (rest, ups - downs);
    }

    private void ParseVoiceLine(VoiceLine voiceLine, int measureTicks, int voiceIndex)
    {
        var tokens = voiceLine.Tokens;
        var lineShift = voiceLine.OctaveShift;

        // 按逗号切段（保留逗号 token 以便报错定位）
        var segments = new List<(List<Token> Tokens, Token At)>();
        var start = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Comma) continue;
            segments.Add((tokens.GetRange(start, i - start), tokens[i]));
            start = i + 1;
        }

        segments.Add((tokens.GetRange(start, tokens.Count - start), tokens[^1]));

        foreach (var (segmentTokens, at) in segments)
        {
            if (segmentTokens.Count > 0) continue;

            AddDiagnostic(DiagnosticCode.EmptySegment, at, at.Span,
                "空的节拍段：逗号前后必须有音符或休止符");
            return;
        }

        var segmentCount = segments.Count;
        var segmentCountValid = _beatsPerMeasure == 4
            ? segmentCount is 1 or 2 or 4
            : segmentCount is 1 or 2 or 3 or 4;

        if (_timeSignatureKnown && !segmentCountValid)
        {
            AddDiagnostic(DiagnosticCode.InvalidSegmentCount, tokens[0], SpanOf(tokens),
                _beatsPerMeasure == 4
                    ? "4/4 拍每行只能有 0、1 或 3 个逗号"
                    : "3/4 拍每行最多 3 个逗号");
            return;
        }

        var parsedSegments = new List<List<Unit>>(segmentCount);
        foreach (var (segmentTokens, _) in segments)
        {
            var units = ParseSegment(segmentTokens, lineShift);
            if (units.Count == 0) return;

            if ((units.Count & (units.Count - 1)) != 0)
            {
                AddDiagnostic(DiagnosticCode.UnitCountNotPowerOfTwo, segmentTokens[0], SpanOf(segmentTokens),
                    "同一个节拍段里的音符个数必须是 1、2、4、8……");
                return;
            }

            parsedSegments.Add(units);
        }

        // 计算每个单元在行内的小节偏移
        var segmentTicks = measureTicks / segmentCount;
        var flat = new List<(long Start, long SegmentEnd, Unit Unit)>();

        for (var s = 0; s < parsedSegments.Count; s++)
        {
            var units = parsedSegments[s];
            var segmentStart = (long)s * segmentTicks;

            for (var u = 0; u < units.Count; u++)
            {
                var unitStart = segmentStart + (long)Math.Round((double)segmentTicks * u / units.Count);
                var unitEnd = segmentStart + (long)Math.Round((double)segmentTicks * (u + 1) / units.Count);

                if (unitEnd - unitStart < AttmSpec.MinUnitTicks)
                {
                    AddDiagnostic(DiagnosticCode.UnitTooShort, units[u].Span,
                        "音符时值过短（小于三十二分之一拍）");
                    return;
                }

                var segmentEnd = (long)Math.Round((double)measureTicks * (s + 1) / segmentCount);
                flat.Add((unitStart, segmentEnd, units[u]));
            }
        }

        var measureOffset = (long)_measureIndex * measureTicks;

        for (var i = 0; i < flat.Count; i++)
        {
            var (unitStart, segmentEnd, unit) = flat[i];
            var nextStart = i + 1 < flat.Count ? flat[i + 1].Start : measureTicks;
            var unitTicks = Math.Max(1, nextStart - unitStart);

            if (unit.IsRest) continue;

            // 连奏：组内音符依次开始，但每个音的时值都撑到本段末尾才断开
            if (unit.IsLegato && unit.Notes.Count > 0)
            {
                for (var n = 0; n < unit.Notes.Count; n++)
                {
                    var noteStart = unitStart + (long)Math.Round((double)unitTicks * n / unit.Notes.Count);
                    if (noteStart >= measureTicks) break;

                    // 组内的 0 不发声，只占掉自己的那一份时间
                    if (unit.Notes[n].IsRest) continue;

                    var noteEnd = Math.Min(Math.Max(noteStart + 1, segmentEnd), measureTicks);
                    if (noteEnd <= noteStart) continue;

                    _notes.Add(new NoteEvent(
                        unit.Notes[n].Pitch,
                        _velocityBase,
                        measureOffset + noteStart,
                        noteEnd - noteStart,
                        _measureIndex,
                        voiceIndex,
                        unit.Span));
                }

                continue;
            }

            var duration = nextStart - unitStart;

            if (duration <= 0) continue;

            foreach (var note in unit.Notes)
            {
                _notes.Add(new NoteEvent(
                    note.Pitch,
                    _velocityBase,
                    measureOffset + unitStart,
                    duration,
                    _measureIndex,
                    voiceIndex,
                    unit.Span));
            }
        }
    }

    // ---------------------------------------------------------------- 音符

    private List<Unit> ParseSegment(List<Token> tokens, int lineShift)
    {
        var units = new List<Unit>();
        var i = 0;

        while (i < tokens.Count)
        {
            var token = tokens[i];

            if (token.Kind == TokenKind.Identifier
                && token.Text.Equals("legato", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseLegato(tokens, ref i, lineShift, units)) return units;
                continue;
            }

            if (token.Kind == TokenKind.LParen)
            {
                var close = -1;
                for (var j = i + 1; j < tokens.Count; j++)
                {
                    if (tokens[j].Kind == TokenKind.RParen) { close = j; break; }
                    if (tokens[j].Kind == TokenKind.LParen) break;
                }

                if (close < 0)
                {
                    AddDiagnostic(DiagnosticCode.UnclosedParenthesis, token,
                        SourceSpan.FromBounds(token.Span.Start, tokens[^1].Span.End),
                        "和弦括号没有闭合");
                    return units;
                }

                var span = SourceSpan.FromBounds(token.Span.Start, tokens[close].Span.End);
                var inner = tokens.GetRange(i + 1, close - i - 1);

                if (inner.Count == 0)
                {
                    AddDiagnostic(DiagnosticCode.EmptyChord, token, span, "和弦括号内不能为空");
                    i = close + 1;
                    continue;
                }

                var notes = new List<NoteInfo>();
                var k = 0;
                while (k < inner.Count)
                {
                    if (!TryParseNote(inner, ref k, lineShift, out var note)) continue;

                    if (note.IsRest)
                    {
                        AddDiagnostic(DiagnosticCode.RestInChord, inner[Math.Max(0, k - 1)], note.Span,
                            "和弦括号内不能使用休止符 0");
                        continue;
                    }

                    notes.Add(note);
                }

                units.Add(new Unit(notes, span));
                i = close + 1;
            }
            else
            {
                if (!TryParseNote(tokens, ref i, lineShift, out var note)) continue;

                units.Add(note.IsRest
                    ? new Unit(Array.Empty<NoteInfo>(), note.Span, true)
                    : new Unit(new[] { note }, note.Span));
            }
        }

        return units;
    }

    /// <summary>解析 legato(...)：一组依次演奏、时值都撑到本段末尾的音符。</summary>
    private bool TryParseLegato(List<Token> tokens, ref int i, int lineShift, List<Unit> units)
    {
        var keyword = tokens[i];
        i++;

        if (i >= tokens.Count || tokens[i].Kind != TokenKind.LParen)
        {
            AddDiagnostic(DiagnosticCode.LegatoSyntax, keyword, keyword.Span,
                "legato 后面必须紧跟括号，例如 \"legato(1234)\"");
            return true;
        }

        var open = tokens[i];
        var close = -1;

        for (var j = i + 1; j < tokens.Count; j++)
        {
            if (tokens[j].Kind == TokenKind.RParen) { close = j; break; }
            if (tokens[j].Kind == TokenKind.LParen) break;
        }

        if (close < 0)
        {
            AddDiagnostic(DiagnosticCode.UnclosedParenthesis, open,
                SourceSpan.FromBounds(keyword.Span.Start, tokens[^1].Span.End),
                "legato 括号没有闭合");
            return false;
        }

        var span = SourceSpan.FromBounds(keyword.Span.Start, tokens[close].Span.End);
        var inner = tokens.GetRange(i + 1, close - i - 1);
        i = close + 1;

        if (inner.Count == 0)
        {
            AddDiagnostic(DiagnosticCode.LegatoEmpty, open, span, "legato 括号内不能为空");
            return true;
        }

        var notes = new List<NoteInfo>();
        var k = 0;

        while (k < inner.Count)
        {
            if (inner[k].Kind == TokenKind.LParen)
            {
                AddDiagnostic(DiagnosticCode.LegatoNested, inner[k], inner[k].Span,
                    "legato 括号内不能再用和弦括号（连奏是依次演奏，不是同时）");
                k++;
                continue;
            }

            if (inner[k].Kind == TokenKind.Comma)
            {
                AddDiagnostic(DiagnosticCode.LegatoNested, inner[k], inner[k].Span,
                    "legato 括号内不能用逗号，音符直接连着写，例如 legato(1234)");
                k++;
                continue;
            }

            if (!TryParseNote(inner, ref k, lineShift, out var note)) continue;

            // 休止符也算一个"拍位"：不发声，但占掉这一份时间
            notes.Add(note);
        }

        if ((notes.Count & (notes.Count - 1)) != 0)
        {
            AddDiagnostic(DiagnosticCode.UnitCountNotPowerOfTwo, open, span,
                "legato 里的音符个数（含休止符）必须是 1、2、4、8……");
            return true;
        }

        units.Add(new Unit(notes, span, false, true));
        return true;
    }

    private bool TryParseNote(List<Token> tokens, ref int i, int lineShift, out NoteInfo note)
    {
        note = default;
        var start = i;
        var octave = lineShift;
        var direction = 0;

        // + 可以叠加：++1 高两个八度，+++#1 高三个八度的 #1；- 同理
        while (i < tokens.Count && tokens[i].Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var current = tokens[i].Kind == TokenKind.Plus ? 1 : -1;

            if (direction != 0 && current != direction)
            {
                AddDiagnostic(DiagnosticCode.InvalidCharacter, tokens[i], tokens[i].Span,
                    "八度标记不能混用 + 和 -，例如 \"+-1\"");
            }
            else
            {
                direction = current;
                octave += current;
            }

            var markerEnd = tokens[i].Span.End;
            i++;

            if (i >= tokens.Count || tokens[i].Span.Start != markerEnd)
            {
                AddDiagnostic(DiagnosticCode.OctaveMarkerWithoutDigit, tokens[i - 1], tokens[i - 1].Span,
                    "八度标记 +/- 后面必须紧跟数字");
                return false;
            }
        }

        var sawOctave = direction != 0;

        var sharp = false;
        if (i < tokens.Count && tokens[i].Kind == TokenKind.Sharp)
        {
            sharp = true;
            var sharpEnd = tokens[i].Span.End;
            i++;

            if (i >= tokens.Count || tokens[i].Span.Start != sharpEnd)
            {
                AddDiagnostic(DiagnosticCode.SharpWithoutDigit, tokens[i - 1], tokens[i - 1].Span,
                    "升号 # 后面必须紧跟数字");
                return false;
            }
        }

        if (i >= tokens.Count || tokens[i].Kind != TokenKind.Number)
        {
            var at = tokens[Math.Min(start, tokens.Count - 1)];
            AddDiagnostic(DiagnosticCode.InvalidCharacter, at, at.Span, "无法识别的音符");
            if (i == start) i++;
            return false;
        }

        var digitToken = tokens[i];
        var digit = digitToken.Text[0] - '0';

        if (digit == 0)
        {
            if (sawOctave || sharp)
            {
                AddDiagnostic(DiagnosticCode.InvalidDigit, digitToken, digitToken.Span,
                    "休止符 0 不能带升降号或八度标记");
            }

            i++;
            note = new NoteInfo(0, SourceSpan.FromBounds(tokens[start].Span.Start, digitToken.Span.End), true);
            return true;
        }

        if (digit is < 1 or > 7)
        {
            AddDiagnostic(DiagnosticCode.InvalidDigit, digitToken, digitToken.Span,
                "只允许数字 1 到 7（0 表示休止）");
            i++;
            return false;
        }

        var key = _keyRoot ?? 'C';
        if (!PitchMapper.TryMap(key, _keySharp, digit, octave, sharp, out var pitch))
        {
            AddDiagnostic(DiagnosticCode.PitchOutOfRange, digitToken,
                SourceSpan.FromBounds(tokens[start].Span.Start, digitToken.Span.End),
                "音高超出 MIDI 范围（0 到 127）");
            i++;
            return false;
        }

        i++;
        note = new NoteInfo(pitch,
            SourceSpan.FromBounds(tokens[start].Span.Start, digitToken.Span.End), false);
        return true;
    }

    // ---------------------------------------------------------------- 工具

    private static SourceSpan SpanOf(List<Token> tokens)
        => tokens.Count == 0
            ? SourceSpan.Empty
            : SourceSpan.FromBounds(tokens[0].Span.Start, tokens[^1].Span.End);

    private IReadOnlyList<int> ComputeLineStarts()
    {
        _lineStarts.Clear();
        _lineStarts.Add(0);
        for (var i = 0; i < _text.Length; i++)
        {
            if (_text[i] == '\r')
            {
                if (i + 1 < _text.Length && _text[i + 1] == '\n') i++;
                _lineStarts.Add(i + 1);
            }
            else if (_text[i] == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }

        return _lineStarts;
    }

    private (int Line, int Column) GetLineColumn(int offset)
    {
        var index = 0;
        var low = 0;
        var high = _lineStarts.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (_lineStarts[mid] <= offset) { index = mid; low = mid + 1; }
            else high = mid - 1;
        }

        return (index + 1, offset - _lineStarts[index] + 1);
    }

    private void AddDiagnostic(DiagnosticCode code, Token at, SourceSpan span, string message)
        => _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, at.Line, at.Column, span));

    private void AddDiagnostic(DiagnosticCode code, SourceSpan span, string message)
    {
        var (line, column) = GetLineColumn(Math.Clamp(span.Start, 0, Math.Max(0, _text.Length)));
        _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, line, column, span));
    }

    private void AddDiagnostic(DiagnosticCode code, int line, int column, SourceSpan span, string message)
        => _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, line, column, span));

    // ---------------------------------------------------------------- 内部模型

    private readonly record struct NoteInfo(int Pitch, SourceSpan Span, bool IsRest = false);

    private readonly record struct Unit(
        IReadOnlyList<NoteInfo> Notes,
        SourceSpan Span,
        bool IsRest = false,
        bool IsLegato = false);

    private readonly record struct VoiceLine(List<Token> Tokens, int OctaveShift);
}
