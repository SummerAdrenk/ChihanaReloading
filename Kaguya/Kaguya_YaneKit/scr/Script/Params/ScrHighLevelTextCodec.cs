using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Script.Params;

public sealed class ScrHighLevelTextCodec
{
    private readonly Encoding _writeEncoding;

    public ScrHighLevelTextCodec(string? writeEncoding = null)
    {
        _writeEncoding = ScrInstructionTextCodec.ResolveEncoding(writeEncoding);
    }

    public ScrFileDocument Read(string text)
    {
        var document = new ScrFileDocument();
        PatternBuilder? currentPattern = null;
        var section = HlsSection.Code;
        var lineNo = 0;

        using var reader = new StringReader(text);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            lineNo++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith(".file ", StringComparison.Ordinal) || line == ".code")
            {
                section = HlsSection.Code;
                continue;
            }

            if (line.StartsWith(".source ", StringComparison.Ordinal))
            {
                document.Script.SourceName = Unquote(TrimSemicolon(line[".source ".Length..].Trim()));
                continue;
            }

            if (line.StartsWith(".header ", StringComparison.Ordinal))
            {
                document.Header = Unquote(TrimSemicolon(line[".header ".Length..].Trim()));
                continue;
            }

            if (line.StartsWith("script ", StringComparison.Ordinal))
            {
                document.Script.SourceName = ReadScriptName(line);
                continue;
            }

            if (line == "{")
            {
                continue;
            }

            if (line == "}")
            {
                if (currentPattern is not null)
                {
                    FinishPattern(document, currentPattern, lineNo);
                    currentPattern = null;
                    section = HlsSection.Patterns;
                    continue;
                }

                section = section == HlsSection.Patterns ? HlsSection.Code : HlsSection.Code;
                continue;
            }

            if (line.StartsWith("header ", StringComparison.Ordinal))
            {
                document.Header = Unquote(TrimSemicolon(line["header ".Length..].Trim()));
                continue;
            }

            if (line == "save_entries {")
            {
                section = HlsSection.SaveEntries;
                continue;
            }

            if (line == "pattern_entries {")
            {
                section = HlsSection.PatternEntries;
                continue;
            }

            if (line == "patterns {")
            {
                section = HlsSection.Patterns;
                continue;
            }

            if (line.StartsWith("container_tail ", StringComparison.Ordinal))
            {
                document.Tail = ParseByteList(TrimSemicolon(line)["container_tail ".Length..], lineNo);
                continue;
            }

            switch (section)
            {
                case HlsSection.SaveEntries:
                    document.SaveOffsets.Add(ParseOffsetReference(TrimSemicolon(line), ScrOffsetEncoding.FileAbsolute, lineNo));
                    break;
                case HlsSection.PatternEntries:
                    document.LayerOffsets.Add(ParsePatternOffsetReference(TrimSemicolon(line), lineNo));
                    break;
                case HlsSection.Patterns:
                    ReadPatternLine(document, ref currentPattern, line, lineNo);
                    break;
                default:
                    ReadCodeLine(document.Script, line, lineNo);
                    break;
            }
        }

        if (currentPattern is not null)
        {
            FinishPattern(document, currentPattern, lineNo);
        }

        return document;
    }

    private void ReadCodeLine(ScriptDocument script, string line, int lineNo)
    {
        if (line.StartsWith('@') && line.EndsWith(':'))
        {
            script.AddLabel(line[1..^1]);
            return;
        }

        if (IsBareLabel(line))
        {
            script.AddLabel(line[..^1]);
            return;
        }

        var statement = TrimSemicolon(line);
        var lowerStatement = statement.ToLowerInvariant();
        if (lowerStatement.StartsWith("data_tail ", StringComparison.Ordinal))
        {
            script.AddTail(ParseByteList(statement["data_tail ".Length..], lineNo));
            return;
        }

        if (lowerStatement.StartsWith("container_tail ", StringComparison.Ordinal))
        {
            throw new FormatException($"Line {lineNo}: container_tail belongs after pattern_entries, not in code.");
        }

        var tokens = Tokenize(statement);
        if (tokens.Count == 0)
        {
            return;
        }

        switch (tokens[0].ToLowerInvariant())
        {
            case "assign":
                AddAssign(script, tokens, lineNo);
                break;
            case "flag_set":
                AddFlag(script, 2, tokens, lineNo);
                break;
            case "flag_clear":
                AddFlag(script, 3, tokens, lineNo);
                break;
            case "end":
                script.AddInstruction(4);
                break;
            case "wait":
                script.AddInstruction(5, BuildWait(tokens, lineNo));
                break;
            case "update":
                script.AddInstruction(6, BuildUpdate(tokens, lineNo));
                break;
            case "text":
                script.AddInstruction(7, BuildText(tokens, lineNo));
                break;
            case "menu":
                script.AddInstruction(8, BuildMenu(tokens, lineNo));
                break;
            case "sound":
                script.AddInstruction(9, BuildIdExtra(tokens, lineNo));
                break;
            case "bgm":
                script.AddInstruction(10, BuildSingleU32(tokens, "track", lineNo));
                break;
            case "goto":
                script.AddInstruction(11, BuildTarget(tokens, lineNo), targetLabel: ReadTargetLabel(tokens, lineNo));
                break;
            case "file_jump":
                script.AddInstruction(12, BuildFileJump(tokens, lineNo));
                break;
            case "compare":
                script.AddInstruction(13, BuildCompare(tokens, lineNo));
                break;
            case "if_true":
                script.AddInstruction(14, BuildConditional(tokens, lineNo), targetLabel: ReadGotoLabel(tokens, lineNo));
                break;
            case "if_false":
                script.AddInstruction(15, BuildConditional(tokens, lineNo), targetLabel: ReadGotoLabel(tokens, lineNo));
                break;
            case "file_call":
                script.AddInstruction(16, BuildFileCall(tokens, lineNo));
                break;
            case "file_return":
                script.AddInstruction(17);
                break;
            case "call":
                script.AddInstruction(18, BuildTarget(tokens, lineNo), targetLabel: ReadTargetLabel(tokens, lineNo));
                break;
            case "return":
                script.AddInstruction(19);
                break;
            case "program":
                script.AddInstruction(20, BuildProgram(tokens, lineNo));
                break;
            case "title":
                script.AddInstruction(21, BuildTitle(tokens, lineNo));
                break;
            case "scene":
                script.AddInstruction(22, BuildU16(tokens, "index", lineNo));
                break;
            case "date_window":
                script.AddInstruction(23);
                break;
            case "date_place_reset":
                script.AddInstruction(24);
                break;
            case "save":
                script.AddInstruction(25, BuildSingleU32(tokens, "slot", lineNo));
                break;
            case "follow_jump":
                script.AddInstruction(26, BuildFileJump(tokens, lineNo));
                break;
            case "voice":
                script.AddInstruction(27, BuildVoice(tokens, lineNo));
                break;
            case "nop":
                script.AddInstruction(28);
                break;
            default:
                throw new FormatException($"Line {lineNo}: unknown SCR-HLS statement '{tokens[0]}'.");
        }
    }

    private static bool IsBareLabel(string line)
    {
        if (!line.EndsWith(':') || line.Contains(' ', StringComparison.Ordinal) || line.Contains('\t', StringComparison.Ordinal))
        {
            return false;
        }

        var name = line[..^1];
        return name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_');
    }

    private void ReadPatternLine(ScrFileDocument document, ref PatternBuilder? currentPattern, string line, int lineNo)
    {
        var lowerLine = line.ToLowerInvariant();
        if (line.StartsWith('@') && line.EndsWith(':'))
        {
            if (currentPattern is not null)
            {
                FinishPattern(document, currentPattern, lineNo);
            }

            currentPattern = new PatternBuilder(line[1..^1], checked((uint)document.Tail.Length));
            return;
        }

        if (IsBareLabel(line))
        {
            if (currentPattern is not null)
            {
                FinishPattern(document, currentPattern, lineNo);
            }

            currentPattern = new PatternBuilder(line[..^1], checked((uint)document.Tail.Length));
            return;
        }

        if (lowerLine.StartsWith("pattern ", StringComparison.Ordinal) && line.EndsWith("{", StringComparison.Ordinal))
        {
            if (currentPattern is null)
            {
                throw new FormatException($"Line {lineNo}: pattern block has no @pattern label.");
            }

            var values = ReadKeyValues(Tokenize(line[..^1].Trim()).Skip(1), lineNo);
            currentPattern.ExpectedCount = ReadByte(values, "count", lineNo);
            return;
        }

        if (lowerLine.StartsWith("pattern_layer ", StringComparison.Ordinal))
        {
            if (currentPattern is null)
            {
                throw new FormatException($"Line {lineNo}: pattern_layer is outside a pattern block.");
            }

            currentPattern.Layers.Add(BuildPatternLayer(TrimSemicolon(line), lineNo));
            return;
        }

        if (lowerLine.StartsWith("container_tail ", StringComparison.Ordinal))
        {
            document.Tail = ParseByteList(TrimSemicolon(line)["container_tail ".Length..], lineNo);
            return;
        }

        if (line == "}")
        {
            if (currentPattern is not null)
            {
                FinishPattern(document, currentPattern, lineNo);
                currentPattern = null;
            }
            return;
        }

        throw new FormatException($"Line {lineNo}: unknown patterns statement.");
    }

    private static void FinishPattern(ScrFileDocument document, PatternBuilder pattern, int lineNo)
    {
        if (pattern.ExpectedCount is null)
        {
            throw new FormatException($"Line {lineNo}: pattern @{pattern.Label} has no count header.");
        }

        if (pattern.ExpectedCount.Value != pattern.Layers.Count)
        {
            throw new FormatException(
                $"Line {lineNo}: pattern @{pattern.Label} count={pattern.ExpectedCount.Value} but has {pattern.Layers.Count} layer records.");
        }

        var bytes = new List<byte> { pattern.ExpectedCount.Value };
        foreach (var layer in pattern.Layers)
        {
            bytes.AddRange(layer);
        }

        var tail = new byte[document.Tail.Length + bytes.Count];
        document.Tail.CopyTo(tail, 0);
        bytes.CopyTo(tail.AsSpan(document.Tail.Length));
        document.Tail = tail;
    }

    private static byte[] BuildPatternLayer(string statement, int lineNo)
    {
        var values = ReadKeyValues(Tokenize(statement).Skip(1), lineNo);
        var filterParams = values.TryGetValue("filter_params", out var paramText)
            ? ParseIntList(paramText, lineNo)
            : [];
        var itemLength = ReadU16(values, "len", lineNo);
        var oldLayout = itemLength >= 13 && (itemLength - 13) % 4 == 0;
        var expectedLength = oldLayout
            ? checked((ushort)(13 + filterParams.Length * 4))
            : checked((ushort)(14 + filterParams.Length * 4));
        if (itemLength != expectedLength)
        {
            throw new FormatException($"Line {lineNo}: pattern_layer len={itemLength} but filter_params require len={expectedLength}.");
        }

        var position = ReadStruct(values, "position", lineNo);
        var body = new byte[itemLength];
        WriteU16(body, 0, itemLength);
        WriteU32(body, 2, ReadU32(values, "resource_ref", lineNo));
        body[6] = ReadByte(values, "layer", lineNo);
        WriteI16(body, 7, ReadI16(position, "x", lineNo));
        WriteI16(body, 9, ReadI16(position, "y", lineNo));
        body[11] = ReadByte(values, "absolute_position", lineNo);
        body[12] = unchecked((byte)ReadI32(values, "filter", lineNo));
        var filterParamOffset = oldLayout ? 13 : 14;
        if (!oldLayout)
        {
            body[13] = checked((byte)filterParams.Length);
        }

        for (var i = 0; i < filterParams.Length; i++)
        {
            WriteI32(body, filterParamOffset + i * 4, filterParams[i]);
        }

        return body;
    }

    private static void AddAssign(ScriptDocument script, IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[12];
        body[0] = ReadByte(values, "flags", lineNo);
        body[1] = ReadByte(values, "op", lineNo);
        WriteU16(body, 2, ReadU16(values, "dst", lineNo));
        WriteI32(body, 4, ReadI32(values, "src08", lineNo));
        WriteI32(body, 8, ReadI32(values, "src0c", lineNo));
        script.AddInstruction(1, body);
    }

    private static void AddFlag(ScriptDocument script, ushort opcode, IReadOnlyList<string> tokens, int lineNo)
    {
        if (tokens.Count != 2 || !tokens[1].StartsWith("var[", StringComparison.Ordinal) || !tokens[1].EndsWith("]", StringComparison.Ordinal))
        {
            throw new FormatException($"Line {lineNo}: flag statement must be flag_* var[NN].");
        }

        var body = new byte[4];
        WriteU16(body, 0, checked((ushort)ParseU32(tokens[1][4..^1], lineNo)));
        script.AddInstruction(opcode, body);
    }

    private static byte[] BuildWait(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var extra = values.TryGetValue("extra", out var extraText) ? ParseByteList(extraText, lineNo) : [];
        var hasAux = values.ContainsKey("aux") || values.ContainsKey("progress_threshold");
        var body = new byte[(hasAux ? 5 : 3) + extra.Length];
        body[0] = ReadByte(values, "flags", lineNo);
        WriteU16(body, 1, ReadU16(values, "value", lineNo));
        if (hasAux)
        {
            var key = values.ContainsKey("aux") ? "aux" : "progress_threshold";
            WriteU16(body, 3, ReadU16(values, key, lineNo));
        }
        extra.CopyTo(body.AsSpan(hasAux ? 5 : 3));
        return body;
    }

    private static byte[] BuildUpdate(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        if (values.TryGetValue("raw", out var rawText))
        {
            return ParseByteList(rawText, lineNo);
        }

        var payload = new List<byte>();
        var flags = ReadByte(values, "flags", lineNo);
        var hasAuxEntry = values.ContainsKey("aux_entry");

        if (values.TryGetValue("payload", out var payloadText))
        {
            payload.AddRange(ParseByteList(payloadText, lineNo));
            var payloadBody = new byte[(hasAuxEntry ? 9 : 5) + payload.Count];
            WriteI32(payloadBody, 0, ReadI32(values, "pattern_entry", lineNo));
            if (hasAuxEntry)
            {
                WriteI32(payloadBody, 4, ReadI32(values, "aux_entry", lineNo));
                payloadBody[8] = flags;
                payload.CopyTo(payloadBody.AsSpan(9));
            }
            else
            {
                payloadBody[4] = flags;
                payload.CopyTo(payloadBody.AsSpan(5));
            }

            return payloadBody;
        }

        if ((flags & 0x08) != 0)
        {
            var entries = ParseUpdatePositionOverrides(Require(values, "position_overrides", lineNo), lineNo);
            payload.Add(checked((byte)entries.Count));
            var temp = new byte[4];
            foreach (var entry in entries)
            {
                payload.Add(checked((byte)entry.Layer));
                BinaryPrimitives.WriteInt16LittleEndian(temp.AsSpan(0, 2), checked((short)entry.X));
                BinaryPrimitives.WriteInt16LittleEndian(temp.AsSpan(2, 2), checked((short)entry.Y));
                payload.AddRange(temp);
            }
        }
        else if ((flags & 0x07) != 0)
        {
            var key = (flags & 0x02) != 0
                ? "variable_value"
                : (flags & 0x04) != 0 ? "reference_value" : "immediate_value";
            Span<byte> temp = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(temp, ReadI32(values, key, lineNo));
            payload.AddRange(temp.ToArray());
        }

        if ((flags & 0x10) != 0 && (flags & 0x0F) == 0)
        {
            payload.Add(ReadByte(values, "submode", lineNo));
        }

        if (values.TryGetValue("extra", out var extraText))
        {
            payload.AddRange(ParseByteList(extraText, lineNo));
        }

        if (!hasAuxEntry)
        {
            var shortBody = new byte[5 + payload.Count];
            WriteI32(shortBody, 0, ReadI32(values, "pattern_entry", lineNo));
            shortBody[4] = flags;
            payload.CopyTo(shortBody.AsSpan(5));
            return shortBody;
        }

        var body = new byte[9 + payload.Count];
        WriteI32(body, 0, ReadI32(values, "pattern_entry", lineNo));
        WriteI32(body, 4, ReadI32(values, "aux_entry", lineNo));
        body[8] = flags;
        payload.CopyTo(body.AsSpan(9));
        return body;
    }

    private static byte[] BuildText(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var slots = ParseResourceSlots(Require(values, "resource_slots", lineNo), lineNo);
        var body = new byte[12 + slots.Length * 4];
        WriteI32(body, 0, ReadI32(values, "command", lineNo));
        WriteI32(body, 4, ReadI32(values, "pattern_entry", lineNo));
        WriteI32(body, 8, ReadI32(values, "message_resource", lineNo));
        WriteResourceSlots(body, 12, slots);
        return body;
    }

    private static byte[] BuildMenu(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        if (values.TryGetValue("raw", out var raw))
        {
            return ParseByteList(raw, lineNo);
        }

        var choices = ParseChoiceList(Require(values, "choices", lineNo), lineNo);
        if (choices.Length > byte.MaxValue)
        {
            throw new FormatException($"Line {lineNo}: menu has too many choices.");
        }

        var slots = ParseResourceSlots(Require(values, "resource_slots", lineNo), lineNo);
        var fixedOffset = 2 + choices.Length * 4;
        var body = new byte[fixedOffset + 12 + slots.Length * 4];
        body[0] = unchecked((byte)ReadI32(values, "mode", lineNo));
        body[1] = (byte)choices.Length;
        for (var i = 0; i < choices.Length; i++)
        {
            WriteI32(body, 2 + i * 4, choices[i]);
        }

        WriteI32(body, fixedOffset, ReadI32(values, "command", lineNo));
        WriteI32(body, fixedOffset + 4, ReadI32(values, "pattern_entry", lineNo));
        WriteI32(body, fixedOffset + 8, ReadI32(values, "message_resource", lineNo));
        WriteResourceSlots(body, fixedOffset + 12, slots);
        return body;
    }

    private static byte[] BuildIdExtra(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var extra = values.TryGetValue("extra", out var extraText) ? ParseByteList(extraText, lineNo) : [];
        var body = new byte[4 + extra.Length];
        WriteU32(body, 0, ReadU32(values, "id", lineNo));
        extra.CopyTo(body.AsSpan(4));
        return body;
    }

    private static byte[] BuildSingleU32(IReadOnlyList<string> tokens, string key, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[4];
        WriteU32(body, 0, ReadU32(values, key, lineNo));
        return body;
    }

    private static byte[] BuildTarget(IReadOnlyList<string> tokens, int lineNo)
    {
        if (tokens.Count != 2)
        {
            throw new FormatException($"Line {lineNo}: target statement requires one operand.");
        }

        var body = new byte[4];
        if (!tokens[1].StartsWith('@'))
        {
            WriteU32(body, 0, ParseU32(tokens[1], lineNo));
        }

        return body;
    }

    private static byte[] BuildFileJump(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var extra = values.TryGetValue("extra", out var extraText) ? ParseByteList(extraText, lineNo) : [];
        var hasEntry = values.ContainsKey("entry_pc") || values.ContainsKey("entry_ref") || values.ContainsKey("entry_value");
        var body = new byte[5 + (hasEntry ? 4 : 0) + extra.Length];
        body[0] = ReadByte(values, "flags", lineNo);
        WriteU32(body, 1, ReadU32(values, "target", lineNo));
        if (hasEntry)
        {
            var key = values.ContainsKey("entry_pc") ? "entry_pc" : values.ContainsKey("entry_ref") ? "entry_ref" : "entry_value";
            WriteI32(body, 5, ReadI32(values, key, lineNo));
        }
        extra.CopyTo(body.AsSpan(5 + (hasEntry ? 4 : 0)));
        return body;
    }

    private static byte[] BuildCompare(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[12];
        WriteU32(body, 0, ReadU32(values, "lhs", lineNo));
        WriteU32(body, 4, ReadU32(values, "rhs", lineNo));
        WriteU32(body, 8, ReadU32(values, "mode", lineNo));
        return body;
    }

    private static byte[] BuildConditional(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1).Where(x => x != "goto"), lineNo);
        var body = new byte[7];
        body[0] = ReadByte(values, "flags", lineNo);
        WriteU16(body, 1, ReadU16(values, "value", lineNo));
        var target = tokens[^1];
        if (!target.StartsWith('@'))
        {
            WriteU32(body, 3, ParseU32(target, lineNo));
        }
        return body;
    }

    private static byte[] BuildFileCall(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[5];
        body[0] = ReadByte(values, "flags", lineNo);
        WriteU32(body, 1, ReadU32(values, "target", lineNo));
        return body;
    }

    private static byte[] BuildProgram(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[3];
        body[0] = ReadByte(values, "flags", lineNo);
        WriteU16(body, 1, ReadU16(values, "id", lineNo));
        return body;
    }

    private byte[] BuildTitle(IReadOnlyList<string> tokens, int lineNo)
    {
        if (tokens.Count != 2)
        {
            throw new FormatException($"Line {lineNo}: title requires one quoted string.");
        }

        var bytes = _writeEncoding.GetBytes(Unquote(tokens[1]));
        if (bytes.Length > byte.MaxValue)
        {
            throw new FormatException($"Line {lineNo}: title text is too long.");
        }

        var body = new byte[1 + bytes.Length];
        body[0] = (byte)bytes.Length;
        bytes.CopyTo(body.AsSpan(1));
        return body;
    }

    private static byte[] BuildU16(IReadOnlyList<string> tokens, string key, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[2];
        WriteU16(body, 0, ReadU16(values, key, lineNo));
        return body;
    }

    private static byte[] BuildVoice(IReadOnlyList<string> tokens, int lineNo)
    {
        var count = tokens.Count - 1;
        var body = new byte[1 + count * 4];
        body[0] = checked((byte)count);
        for (var i = 0; i < count; i++)
        {
            WriteI32(body, 1 + i * 4, ReadI32(tokens[i + 1], lineNo));
        }
        return body;
    }

    private static string? ReadTargetLabel(IReadOnlyList<string> tokens, int lineNo)
    {
        if (tokens.Count != 2)
        {
            throw new FormatException($"Line {lineNo}: target statement requires one operand.");
        }
        return tokens[1].StartsWith('@') ? tokens[1][1..].TrimEnd(':') : null;
    }

    private static string? ReadGotoLabel(IReadOnlyList<string> tokens, int lineNo)
    {
        if (tokens.Count < 2 || tokens[^2] != "goto")
        {
            throw new FormatException($"Line {lineNo}: conditional must end with 'goto <target>'.");
        }
        return tokens[^1].StartsWith('@') ? tokens[^1][1..].TrimEnd(':') : null;
    }

    private static int[] ParseResourceSlots(string text, int lineNo)
    {
        if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            var flatSlots = ParseIntList(text, lineNo);
            if (flatSlots.Length == 0)
            {
                throw new FormatException($"Line {lineNo}: flat resource_slots must contain at least one value.");
            }

            return flatSlots;
        }

        var values = ReadStructText(text, lineNo);
        var primary = ParseIntList(Require(values, "primary", lineNo), lineNo);
        var secondary = ParseIntList(Require(values, "secondary", lineNo), lineNo);
        if (primary.Length == 0 || secondary.Length == 0)
        {
            throw new FormatException($"Line {lineNo}: resource_slots primary/secondary must each contain at least one value.");
        }

        var slots = new List<int>(primary.Length + secondary.Length + 2);
        slots.AddRange(primary);
        slots.AddRange(secondary);

        if (values.ContainsKey("reserved") || values.ContainsKey("message_sequence"))
        {
            slots.Add(ReadI32(values, "reserved", lineNo));
            slots.Add(ReadI32(values, "message_sequence", lineNo));
        }
        else if (values.TryGetValue("extra", out var extraText))
        {
            slots.AddRange(ParseIntList(extraText, lineNo));
        }

        return slots.ToArray();
    }

    private static void WriteResourceSlots(byte[] body, int offset, IReadOnlyList<int> slots)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            WriteI32(body, offset + i * 4, slots[i]);
        }
    }

    private static List<PositionOverride> ParseUpdatePositionOverrides(string text, int lineNo)
    {
        var trimmed = RequireWrapped(text.Trim(), '[', ']', lineNo);
        var result = new List<PositionOverride>();
        var i = 0;
        while (i < trimmed.Length)
        {
            while (i < trimmed.Length && (char.IsWhiteSpace(trimmed[i]) || trimmed[i] == ','))
            {
                i++;
            }
            if (i >= trimmed.Length)
            {
                break;
            }
            if (trimmed[i] != '{')
            {
                throw new FormatException($"Line {lineNo}: expected position override object.");
            }

            var end = FindMatching(trimmed, i, '{', '}', lineNo);
            var fields = ReadStructText(trimmed[i..(end + 1)], lineNo);
            result.Add(new PositionOverride(
                ReadI32(fields, "layer", lineNo),
                ReadI32(fields, "x", lineNo),
                ReadI32(fields, "y", lineNo)));
            i = end + 1;
        }

        return result;
    }

    private static int[] ParseChoiceList(string text, int lineNo)
    {
        var inner = RequireWrapped(text.Trim(), '[', ']', lineNo).Trim();
        if (inner.Length == 0)
        {
            return [];
        }

        return SplitTopLevel(inner, ',')
            .Select(x => ParseChoiceId(x.Trim(), lineNo))
            .ToArray();
    }

    private static int ParseChoiceId(string text, int lineNo)
    {
        if (text.StartsWith('B') && text.Length > 1)
        {
            return unchecked((int)Convert.ToUInt32(text[1..], 16));
        }

        return ReadI32(text, lineNo);
    }

    private static int[] ParseIntList(string text, int lineNo)
    {
        var inner = RequireWrapped(text.Trim(), '[', ']', lineNo).Trim();
        if (inner.Length == 0)
        {
            return [];
        }

        return SplitTopLevel(inner, ',')
            .Select(x => ReadI32(x.Trim(), lineNo))
            .ToArray();
    }

    private static byte[] ParseByteList(string text, int lineNo)
    {
        var values = ParseIntList(text, lineNo);
        var result = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = checked((byte)values[i]);
        }
        return result;
    }

    private static Dictionary<string, string> ReadStruct(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        ReadStructText(Require(values, key, lineNo), lineNo);

    private static Dictionary<string, string> ReadStructText(string text, int lineNo)
    {
        var inner = RequireWrapped(text.Trim(), '{', '}', lineNo);
        return ReadKeyValues(SplitTopLevel(inner, ',').Select(x => x.Trim()).Where(x => x.Length > 0), lineNo);
    }

    private static Dictionary<string, string> ReadKeyValues(IEnumerable<string> tokens, int lineNo)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            var index = token.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            result[token[..index]] = token[(index + 1)..];
        }

        return result;
    }

    private static ScrOffsetReference ParseOffsetReference(string text, ScrOffsetEncoding encoding, int lineNo)
    {
        if (text.StartsWith('@'))
        {
            return ScrOffsetReference.FromLabel(text[1..].TrimEnd(':'), encoding);
        }

        return ScrOffsetReference.FromRaw(ParseU32(text, lineNo), encoding);
    }

    private static ScrOffsetReference ParsePatternOffsetReference(string text, int lineNo)
    {
        if (text.StartsWith("@pattern_", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text["@pattern_".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var patternOffset))
        {
            return ScrOffsetReference.FromRaw(patternOffset, ScrOffsetEncoding.CodeRelative);
        }

        return ParseOffsetReference(text, ScrOffsetEncoding.CodeRelative, lineNo);
    }

    private static string ReadScriptName(string line)
    {
        var firstQuote = line.IndexOf('"');
        if (firstQuote < 0)
        {
            return string.Empty;
        }

        var end = FindQuotedEnd(line, firstQuote);
        return Unquote(line[firstQuote..(end + 1)]);
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var start = -1;
        var depthSquare = 0;
        var depthCurly = 0;
        var inQuote = false;
        var escape = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (start < 0 && !char.IsWhiteSpace(ch))
            {
                start = i;
            }

            if (inQuote)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inQuote = false;
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuote = true;
                    break;
                case '[':
                    depthSquare++;
                    break;
                case ']':
                    depthSquare--;
                    break;
                case '{':
                    depthCurly++;
                    break;
                case '}':
                    depthCurly--;
                    break;
            }

            if (char.IsWhiteSpace(ch) && depthSquare == 0 && depthCurly == 0 && start >= 0)
            {
                tokens.Add(line[start..i]);
                start = -1;
            }
        }

        if (start >= 0)
        {
            tokens.Add(line[start..]);
        }

        return tokens;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var depthSquare = 0;
        var depthCurly = 0;
        var inQuote = false;
        var escape = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuote)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inQuote = false;
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuote = true;
                    break;
                case '[':
                    depthSquare++;
                    break;
                case ']':
                    depthSquare--;
                    break;
                case '{':
                    depthCurly++;
                    break;
                case '}':
                    depthCurly--;
                    break;
                default:
                    if (ch == separator && depthSquare == 0 && depthCurly == 0)
                    {
                        parts.Add(text[start..i]);
                        start = i + 1;
                    }
                    break;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static string TrimSemicolon(string line) =>
        line.EndsWith(';') ? line[..^1].TrimEnd() : line;

    private static string RequireWrapped(string text, char open, char close, int lineNo)
    {
        if (text.Length < 2 || text[0] != open || text[^1] != close)
        {
            throw new FormatException($"Line {lineNo}: expected {open}...{close}.");
        }

        return text[1..^1];
    }

    private static int FindMatching(string text, int start, char open, char close, int lineNo)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close && --depth == 0)
            {
                return i;
            }
        }

        throw new FormatException($"Line {lineNo}: unmatched {open}.");
    }

    private static string Unquote(string value)
    {
        var text = value.Trim();
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return text;
        }

        var builder = new StringBuilder();
        for (var i = 1; i < text.Length - 1; i++)
        {
            var ch = text[i];
            if (ch == '\\' && i + 1 < text.Length - 1)
            {
                var next = text[++i];
                builder.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => next
                });
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static int FindQuotedEnd(string text, int start)
    {
        var escape = false;
        for (var i = start + 1; i < text.Length; i++)
        {
            if (escape)
            {
                escape = false;
                continue;
            }

            if (text[i] == '\\')
            {
                escape = true;
                continue;
            }

            if (text[i] == '"')
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string key, int lineNo)
    {
        if (!values.TryGetValue(key, out var value))
        {
            throw new FormatException($"Line {lineNo}: missing key '{key}'.");
        }

        return value;
    }

    private static byte ReadByte(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        checked((byte)ParseU32(Require(values, key, lineNo), lineNo));

    private static ushort ReadU16(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        checked((ushort)ParseU32(Require(values, key, lineNo), lineNo));

    private static uint ReadU32(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        ParseU32(Require(values, key, lineNo), lineNo);

    private static short ReadI16(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        checked((short)ReadI32(values, key, lineNo));

    private static int ReadI32(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        ReadI32(Require(values, key, lineNo), lineNo);

    private static int ReadI32(string value, int lineNo)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return unchecked((int)Convert.ToUInt32(value[2..], 16));
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException($"Line {lineNo}: invalid int value '{value}'.");
        }

        return result;
    }

    private static uint ParseU32(string value, int lineNo)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToUInt32(value[2..], 16);
        }

        if (!uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException($"Line {lineNo}: invalid uint value '{value}'.");
        }

        return result;
    }

    private static void WriteU16(byte[] body, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(offset, 2), value);

    private static void WriteI16(byte[] body, int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(offset, 2), value);

    private static void WriteU32(byte[] body, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset, 4), value);

    private static void WriteI32(byte[] body, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset, 4), value);

    private enum HlsSection
    {
        Code,
        SaveEntries,
        PatternEntries,
        Patterns
    }

    private sealed class PatternBuilder(string label, uint offset)
    {
        public string Label { get; } = label;
        public uint Offset { get; } = offset;
        public byte? ExpectedCount { get; set; }
        public List<byte[]> Layers { get; } = [];
    }

    private readonly record struct PositionOverride(int Layer, int X, int Y);
}
