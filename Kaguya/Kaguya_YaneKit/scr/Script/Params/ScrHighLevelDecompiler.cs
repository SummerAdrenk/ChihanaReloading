using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Kaguya_YaneKit.Formats.Params;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Script.Params;

public sealed class ScrHighLevelDecompiler
{
    private readonly ParamsDatDocument? _paramsDocument;
    private readonly Encoding _readEncoding;

    public ScrHighLevelDecompiler(string? readEncoding = null, ParamsDatDocument? paramsDocument = null)
    {
        _paramsDocument = paramsDocument;
        _readEncoding = ScrInstructionTextCodec.ResolveEncoding(readEncoding);
    }

    public string Write(ScrFileDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine(".file kind=params_scr_hls");
        builder.AppendLine($".source \"{Escape(document.Script.SourceName ?? string.Empty)}\"");
        builder.AppendLine($".header \"{Escape(document.Header)}\"");
        builder.AppendLine();
        builder.AppendLine(".code");
        builder.AppendLine();

        foreach (var element in document.Script.Elements)
        {
            switch (element)
            {
                case ScriptLabel label:
                    builder.AppendLine($"{label.Name}:");
                    break;
                case ScriptComment comment:
                    builder.AppendLine($"    ; {comment.Text}");
                    break;
                case ScriptInstruction instruction:
                    builder.Append("    ");
                    builder.AppendLine(RenderInstruction(instruction));
                    break;
                case ScriptTail tail:
                    builder.AppendLine($"    DATA_TAIL {FormatBytes(tail.Data)}");
                    break;
            }
        }

        builder.AppendLine();
        builder.AppendLine("save_entries {");
        foreach (var entry in document.SaveOffsets)
        {
            builder.AppendLine($"    {FormatOffset(entry)};");
        }
        builder.AppendLine("}");

        builder.AppendLine("pattern_entries {");
        foreach (var entry in document.LayerOffsets)
        {
            builder.AppendLine($"    {FormatPatternOffset(entry, document.Tail)};");
        }
        builder.AppendLine("}");

        if (document.Tail.Length > 0)
        {
            if (!AppendPatternTailRecords(builder, document.Tail, document.LayerOffsets))
            {
                throw new InvalidDataException("Unsupported pattern tail layout in Params HLS decompiler.");
            }
        }

        return builder.ToString();
    }

    private void WriteInstruction(StringBuilder builder, ScriptInstruction instruction)
    {
        builder.Append(RenderInstruction(instruction));
    }

    private string RenderInstruction(ScriptInstruction instruction)
    {
        var local = new StringBuilder();
        WriteInstructionBody(local, instruction);
        var line = local.ToString().TrimEnd();
        if (line.EndsWith(';'))
        {
            line = line[..^1];
        }

        return UppercaseMnemonic(line);
    }

    private static string UppercaseMnemonic(string line)
    {
        var split = line.IndexOf(' ');
        return split < 0
            ? line.ToUpperInvariant()
            : line[..split].ToUpperInvariant() + line[split..];
    }

    private void WriteInstructionBody(StringBuilder builder, ScriptInstruction instruction)
    {
        ValidateInstruction(instruction);
        var body = instruction.Body;
        switch (instruction.Opcode)
        {
            case 1 when body.Length == 12:
                builder.Append($"assign dst={U16(body, 2)} flags={body[0]} op={body[1]} src08={I32(body, 4)} src0c={I32(body, 8)};");
                return;
            case 2 when body.Length == 4:
                builder.Append($"flag_set var[{U16(body, 0)}];");
                AppendRawSuffix(builder, body, 2);
                return;
            case 3 when body.Length == 4:
                builder.Append($"flag_clear var[{U16(body, 0)}];");
                AppendRawSuffix(builder, body, 2);
                return;
            case 4 when body.Length == 0:
                builder.Append("end;");
                return;
            case 5 when body.Length >= 3:
                AppendWaitCommand(builder, body);
                return;
            case 6 when body.Length == 5:
                builder.Append($"update pattern_entry={I32(body, 0)} flags={body[4]} flag_ops={FormatUpdateFlagOps(body[4])};");
                return;
            case 6 when body.Length > 5 && body.Length < 9:
                builder.Append($"update pattern_entry={I32(body, 0)} flags={body[4]} flag_ops={FormatUpdateFlagOps(body[4])} payload={FormatBytes(body.AsSpan(5))};");
                return;
            case 6 when body.Length >= 9 && CanRenderStructuredUpdate(body):
                builder.Append($"update pattern_entry={I32(body, 0)} aux_entry={I32(body, 4)} flags={body[8]} flag_ops={FormatUpdateFlagOps(body[8])}");
                AppendUpdatePayload(builder, body);
                builder.Append(';');
                return;
            case 6 when body.Length >= 9:
                builder.Append($"update pattern_entry={I32(body, 0)} aux_entry={I32(body, 4)} flags={body[8]} flag_ops={FormatUpdateFlagOps(body[8])} payload={FormatBytes(body.AsSpan(9))};");
                return;
            case 6:
                builder.Append($"update payload={FormatBytes(body)};");
                return;
            case 7 when body.Length >= 16 && (body.Length - 12) % 4 == 0:
                builder.Append($"text command={I32(body, 0)} pattern_entry={I32(body, 4)} message_resource={I32(body, 8)} resource_slots={FormatResourceSlots(body.AsSpan(12, body.Length - 12))};");
                return;
            case 8 when body.Length >= 4:
                AppendMenuCommand(builder, body);
                builder.Append(';');
                return;
            case 9 when body.Length >= 4:
                builder.Append($"sound id={U32(body, 0)}");
                AppendExtra(builder, body, 4);
                builder.Append(';');
                return;
            case 10 when body.Length == 4:
                builder.Append($"bgm track={U32(body, 0)};");
                return;
            case 11 when body.Length == 4:
                builder.Append($"goto {FormatTarget(instruction, body, 0)};");
                return;
            case 12 when body.Length >= 5:
                AppendFileJumpCommand(builder, body);
                return;
            case 13 when body.Length == 12:
                builder.Append($"compare lhs={U32(body, 0)} rhs={U32(body, 4)} mode={U32(body, 8)};");
                return;
            case 14 when body.Length == 7:
                builder.Append($"if_true flags={body[0]} value={U16(body, 1)} goto {FormatTarget(instruction, body, 3)};");
                return;
            case 15 when body.Length == 7:
                builder.Append($"if_false flags={body[0]} value={U16(body, 1)} goto {FormatTarget(instruction, body, 3)};");
                return;
            case 16 when body.Length == 5:
                AppendFileCallCommand(builder, body);
                return;
            case 17 when body.Length == 0:
                builder.Append("file_return;");
                return;
            case 18 when body.Length == 4:
                builder.Append($"call {FormatTarget(instruction, body, 0)};");
                return;
            case 19 when body.Length == 0:
                builder.Append("return;");
                return;
            case 20 when body.Length == 3:
                builder.Append($"program flags={body[0]} id={U16(body, 1)} name={ProgramName(U16(body, 1))};");
                return;
            case 21 when body.Length >= 1 && body.Length == 1 + body[0]:
                builder.Append($"title {Quote(_readEncoding.GetString(body, 1, body[0]))};");
                return;
            case 22 when body.Length == 2:
                builder.Append($"scene index={U16(body, 0)};");
                return;
            case 23 when body.Length == 0:
                builder.Append("date_window;");
                return;
            case 24 when body.Length == 0:
                builder.Append("date_place_reset;");
                return;
            case 25 when body.Length == 4:
                builder.Append($"save slot={U32(body, 0)};");
                return;
            case 26 when body.Length >= 5:
                AppendFollowJumpCommand(builder, body);
                return;
            case 27 when body.Length >= 1 && body.Length == 1 + body[0] * 4:
                builder.Append("voice");
                for (var i = 0; i < body[0]; i++)
                {
                    builder.Append(i == 0 ? " " : ", ");
                    builder.Append(I32(body, 1 + i * 4).ToString(CultureInfo.InvariantCulture));
                }
                builder.Append(';');
                return;
            case 28 when body.Length == 0:
                builder.Append("nop;");
                return;
            default:
                throw new InvalidDataException(
                    $"SCR-HLS has no emitter for opcode/body variant at {FormatInstructionOffset(instruction)}: " +
                    $"opcode={instruction.Opcode}, bodyLength={body.Length}.");
        }
    }

    private static void ValidateInstruction(ScriptInstruction instruction)
    {
        var descriptor = ScrOpcodeInfo.Get(instruction.Opcode);
        if (!descriptor.IsKnown)
        {
            throw new InvalidDataException(
                $"Unknown SCR opcode at {FormatInstructionOffset(instruction)}: opcode={instruction.Opcode}.");
        }

        if (!IsBodyLengthValid(descriptor, instruction.Body))
        {
            throw new InvalidDataException(
                $"Invalid SCR opcode body at {FormatInstructionOffset(instruction)}: " +
                $"opcode={instruction.Opcode} ({descriptor.Name}), bodyLength={instruction.Body.Length}, expected={descriptor.LengthRule}.");
        }

        // Older Params系 samples can carry program ids not present in the current
        // reverse-named table. They are still valid bytecode and must roundtrip.
    }

    private static bool IsBodyLengthValid(ScrOpcodeDescriptor descriptor, byte[] body)
    {
        return descriptor.LengthKind switch
        {
            ScrLengthKind.Fixed => body.Length == (descriptor.ExpectedBodyLength ?? 0),
            ScrLengthKind.CountedString => body.Length >= 1 && body.Length == 1 + body[0],
            ScrLengthKind.CountedI32Array => body.Length >= 1 && body.Length == 1 + body[0] * 4,
            ScrLengthKind.Variable => HasMinimumOperands(descriptor, body),
            _ => false
        };
    }

    private static bool HasMinimumOperands(ScrOpcodeDescriptor descriptor, byte[] body)
    {
        if (descriptor.Opcode == 6 && body.Length >= 5)
        {
            return true;
        }

        var minimumLength = 0;
        foreach (var operand in descriptor.Operands)
        {
            var operandEnd = operand.Size is { } size
                ? operand.Offset + size
                : operand.Offset;
            minimumLength = Math.Max(minimumLength, operandEnd);
        }

        return body.Length >= minimumLength;
    }

    private static string FormatInstructionOffset(ScriptInstruction instruction)
    {
        if (instruction.Metadata.TryGetValue("offset", out var value) && value is int offset)
        {
            return $"bytecode offset 0x{offset:X8}";
        }

        return "unknown bytecode offset";
    }

    private void AppendMenuCommand(StringBuilder builder, byte[] body)
    {
        var count = body.Length >= 2 ? body[1] : 0;
        var fixedOffset = 2 + count * 4;
        var slotBytes = body.Length - (fixedOffset + 12);
        if (slotBytes >= 4 && slotBytes % 4 == 0)
        {
            builder.Append($"menu mode={unchecked((sbyte)body[0]).ToString(CultureInfo.InvariantCulture)} choices=[");
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append($"B{I32(body, 2 + i * 4):X8}");
            }

            builder.Append(']');
            builder.Append($" command={I32(body, fixedOffset)}");
            builder.Append($" pattern_entry={I32(body, fixedOffset + 4)}");
            builder.Append($" message_resource={I32(body, fixedOffset + 8)}");
            builder.Append($" resource_slots={FormatResourceSlots(body.AsSpan(fixedOffset + 12, slotBytes))}");
            return;
        }

        throw new InvalidDataException("Unsupported menu body layout in Params HLS decompiler.");
    }

    private static void AppendWaitCommand(StringBuilder builder, byte[] body)
    {
        var flags = body[0];
        var value = U16(body, 1);
        var mode = GetWaitModeName(flags);

        builder.Append($"wait flags={flags} mode={mode} value={value} value_source=");
        builder.Append((flags & 0x02) != 0 ? "vm_value_ref" : "immediate");

        switch (mode)
        {
            case "countdown":
                builder.Append($" count={value}");
                break;
            case "sound":
                builder.Append($" sound_target={FormatWaitSoundTarget(value)}");
                break;
            case "surface_complete":
            case "surface_progress":
                builder.Append($" surface_slot={value >> 8}");
                break;
        }

        var extraOffset = 3;
        if (body.Length >= 5)
        {
            var aux = U16(body, 3);
            if (mode == "surface_progress")
            {
                builder.Append($" progress_threshold={aux}");
            }
            else
            {
                builder.Append($" aux={aux}");
            }
            extraOffset = 5;
        }

        AppendExtra(builder, body, extraOffset);
        builder.Append(';');
    }

    private static string GetWaitModeName(byte flags)
    {
        if ((flags & 0x40) != 0)
        {
            return "engine_mode_1";
        }

        if ((flags & 0x80) != 0)
        {
            return "sound";
        }

        if ((flags & 0x20) != 0)
        {
            return "surface_complete";
        }

        if ((flags & 0x10) != 0)
        {
            return "surface_progress";
        }

        return "countdown";
    }

    private static string FormatWaitSoundTarget(ushort value) => value switch
    {
        0 => "current",
        1 => "all",
        _ => "slot[" + (value - 2).ToString(CultureInfo.InvariantCulture) + "]"
    };
    private static void AppendFileJumpCommand(StringBuilder builder, byte[] body)
    {
        var flags = body[0];
        builder.Append($"file_jump flags={flags} target={U32(body, 1)} target_source={FormatFileTargetSource(flags)}");
        if ((flags & 0x80) != 0)
        {
            builder.Append(" call_stack=clear");
        }

        var extraOffset = 5;
        if ((flags & 0x10) != 0)
        {
            if (body.Length >= 9)
            {
                var entry = I32(body, 5);
                if ((flags & 0x20) != 0)
                {
                    builder.Append($" entry_source=immediate entry_pc={entry}");
                }
                else if ((flags & 0x40) != 0)
                {
                    builder.Append($" entry_source=vm_value_ref entry_ref={entry}");
                }
                else
                {
                    builder.Append($" entry_source=implicit entry_value={entry}");
                }
                extraOffset = 9;
            }
            else
            {
                builder.Append(" entry_source=missing_operand");
            }
        }

        AppendExtra(builder, body, extraOffset);
        builder.Append(';');
    }

    private static void AppendFileCallCommand(StringBuilder builder, byte[] body)
    {
        var flags = body[0];
        builder.Append($"file_call flags={flags} target={U32(body, 1)} target_source={FormatFileTargetSource(flags)} return_stack=push;");
    }

    private static void AppendFollowJumpCommand(StringBuilder builder, byte[] body)
    {
        var flags = body[0];
        builder.Append($"follow_jump flags={flags} target={U32(body, 1)} target_source={FormatFileTargetSource(flags)} follow_state=save_return_point");
        AppendExtra(builder, body, 5);
        builder.Append(';');
    }

    private static string FormatFileTargetSource(byte flags)
    {
        if ((flags & 0x02) != 0)
        {
            return "vm_value_ref";
        }

        if ((flags & 0x04) != 0)
        {
            return "immediate";
        }

        return "invalid";
    }
    private static void AppendUpdatePayload(StringBuilder builder, byte[] body)
    {
        var flags = body[8];
        var offset = 9;

        if ((flags & 0x08) != 0)
        {
            if (body.Length <= offset)
            {
                return;
            }

            var count = body[offset++];
            builder.Append(" position_overrides=[");
            for (var i = 0; i < count && offset + 5 <= body.Length; i++, offset += 5)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("{layer=");
                builder.Append(body[offset].ToString(CultureInfo.InvariantCulture));
                builder.Append(", x=");
                builder.Append(I16(body, offset + 1).ToString(CultureInfo.InvariantCulture));
                builder.Append(", y=");
                builder.Append(I16(body, offset + 3).ToString(CultureInfo.InvariantCulture));
                builder.Append('}');
            }
            builder.Append(']');
        }
        else if ((flags & 0x07) != 0 && body.Length >= offset + 4)
        {
            builder.Append((flags & 0x02) != 0
                ? " variable_value="
                : (flags & 0x04) != 0
                    ? " reference_value="
                    : " immediate_value=");
            builder.Append(I32(body, offset).ToString(CultureInfo.InvariantCulture));
            offset += 4;
        }

        if ((flags & 0x10) != 0 && body.Length > 9)
        {
            builder.Append($" submode={body[9]}");
            if ((flags & 0x0F) == 0)
            {
                offset = Math.Max(offset, 10);
            }
        }

        AppendExtra(builder, body, offset);
    }

    private static bool CanRenderStructuredUpdate(byte[] body)
    {
        if (body.Length < 9)
        {
            return false;
        }

        var flags = body[8];
        if ((flags & 0x08) == 0)
        {
            return true;
        }

        if (body.Length <= 9)
        {
            return false;
        }

        var count = body[9];
        return body.Length >= 10 + count * 5;
    }

    private static string FormatUpdateFlagOps(byte flags)
    {
        var names = new List<string>(5);
        if ((flags & 0x01) != 0)
        {
            names.Add("immediate_value");
        }
        if ((flags & 0x02) != 0)
        {
            names.Add("variable_value");
        }
        if ((flags & 0x04) != 0)
        {
            names.Add("reference_value");
        }
        if ((flags & 0x08) != 0)
        {
            names.Add("position_overrides");
        }
        if ((flags & 0x10) != 0)
        {
            names.Add("submode");
        }

        return "[" + string.Join(',', names) + "]";
    }

    private static void AppendRawSuffix(StringBuilder builder, byte[] body, int offset)
    {
        if (body.Length > offset && body.AsSpan(offset).IndexOfAnyExcept((byte)0) >= 0)
        {
            builder.Append($" raw_tail={FormatBytes(body.AsSpan(offset))}");
        }
        builder.Append(';');
    }

    private static void AppendExtra(StringBuilder builder, byte[] body, int offset)
    {
        if (body.Length > offset)
        {
            builder.Append($" extra={FormatBytes(body.AsSpan(offset))}");
        }
    }

    private static string FormatTarget(ScriptInstruction instruction, byte[] body, int offset) =>
        instruction.TargetLabel is not null
            ? "@" + instruction.TargetLabel
            : "0x" + U32(body, offset).ToString("X8", CultureInfo.InvariantCulture);

    private static string FormatOffset(ScrOffsetReference reference) =>
        reference.Label is not null
            ? "@" + reference.Label
            : "0x" + reference.RawValue.GetValueOrDefault().ToString("X8", CultureInfo.InvariantCulture);

    private static string FormatPatternOffset(ScrOffsetReference reference, byte[] tail)
    {
        if (reference.RawValue is { } raw && raw < tail.Length)
        {
            return "@pattern_" + raw.ToString("X8", CultureInfo.InvariantCulture);
        }

        return FormatOffset(reference);
    }

    private bool AppendPatternTailRecords(
        StringBuilder builder,
        byte[] tail,
        IReadOnlyList<ScrOffsetReference> patternOffsets)
    {
        var local = new StringBuilder();
        var entryOffsets = new HashSet<uint>(
            patternOffsets
                .Select(x => x.RawValue)
                .Where(x => x.HasValue)
                .Select(x => x!.Value));

        var offset = 0;
        local.AppendLine("patterns {");
        while (offset < tail.Length)
        {
            var recordOffset = offset;
            var count = tail[offset++];
            if (!entryOffsets.Contains((uint)recordOffset))
            {
                local.AppendLine($"    // unreferenced pattern offset=0x{recordOffset:X8}");
            }

            local.AppendLine($"pattern_{recordOffset:X8}:");
            local.AppendLine($"    PATTERN count={count} {{");
            for (var i = 0; i < count; i++)
            {
                if (offset + 2 > tail.Length)
                {
                    return false;
                }

                var itemStart = offset;
                var itemLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset, 2));
                if (itemStart + itemLength > tail.Length)
                {
                    return false;
                }

                int filterParamCount;
                int filterParamOffset;
                if (itemLength >= 13 && (itemLength - 13) % 4 == 0)
                {
                    filterParamCount = (itemLength - 13) / 4;
                    filterParamOffset = itemStart + 13;
                }
                else if (itemLength >= 14)
                {
                    filterParamCount = tail[itemStart + 13];
                    if (itemLength != 14 + filterParamCount * 4)
                    {
                        return false;
                    }

                    filterParamOffset = itemStart + 14;
                }
                else
                {
                    return false;
                }

                var refId = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(itemStart + 2, 4));

                local.Append("        PATTERN_LAYER");
                local.Append($" len={itemLength}");
                local.Append($" resource_ref={refId}");
                var paramsRef = ResolveParamsIntArray(refId);
                if (paramsRef is not null)
                {
                    local.Append($" params_int_array=[{string.Join(',', paramsRef.ItemIndices)}]");
                }
                local.Append($" layer={tail[itemStart + 6]}");
                local.Append($" position={{x={BinaryPrimitives.ReadInt16LittleEndian(tail.AsSpan(itemStart + 7, 2))}, y={BinaryPrimitives.ReadInt16LittleEndian(tail.AsSpan(itemStart + 9, 2))}}}");
                local.Append($" absolute_position={tail[itemStart + 11]}");
                var filter = unchecked((sbyte)tail[itemStart + 12]);
                local.Append($" filter={filter.ToString(CultureInfo.InvariantCulture)}");
                var filterParams = new int[filterParamCount];
                for (var paramIndex = 0; paramIndex < filterParamCount; paramIndex++)
                {
                    filterParams[paramIndex] = BinaryPrimitives
                        .ReadInt32LittleEndian(tail.AsSpan(filterParamOffset + paramIndex * 4, 4));
                }

                var filterOp = GetLayerFilterOperationName(filter, filterParams);
                if (filterOp is not null)
                {
                    local.Append($" filter_op={filterOp}");
                }

                if (filterParamCount > 0)
                {
                    local.Append(" filter_params=[");
                    for (var paramIndex = 0; paramIndex < filterParamCount; paramIndex++)
                    {
                        if (paramIndex > 0)
                        {
                            local.Append(',');
                        }

                        local.Append(filterParams[paramIndex].ToString(CultureInfo.InvariantCulture));
                    }

                    local.Append(']');
                }

                local.AppendLine(";");
                if (paramsRef is { Resources.Count: > 0 })
                {
                    local.AppendLine($"        // params_resources: {string.Join("; ", paramsRef.Resources.Select(EscapeComment))}");
                }
                offset += itemLength;
            }
            local.AppendLine("    }");
        }
        local.AppendLine("}");
        builder.Append(local);

        return true;
    }

    private ParamsIntArrayReference? ResolveParamsIntArray(uint refId)
    {
        if (_paramsDocument is null || refId > int.MaxValue || refId >= _paramsDocument.Pattern.IntArrays.Count)
        {
            return null;
        }

        var itemIndices = _paramsDocument.Pattern.IntArrays[(int)refId]
            .Select(x => x.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var resources = new List<string>();
        foreach (var itemIndex in _paramsDocument.Pattern.IntArrays[(int)refId])
        {
            if (itemIndex > int.MaxValue || itemIndex >= _paramsDocument.Pattern.Items.Count)
            {
                continue;
            }

            foreach (var resource in ResolvePatternItemResources(_paramsDocument.Pattern.Items[(int)itemIndex]))
            {
                if (!string.IsNullOrWhiteSpace(resource))
                {
                    resources.Add(resource);
                }
            }
        }

        return new ParamsIntArrayReference(itemIndices, resources);
    }

    private static IEnumerable<string> ResolvePatternItemResources(ParamsPatternItem item)
    {
        switch (item.Kind)
        {
            case 0 when !string.IsNullOrWhiteSpace(item.Name):
                yield return item.Name;
                break;
            case 1:
                foreach (var text in item.Strings)
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        yield return text;
                    }
                }
                break;
            case 2:
            case 3:
                if (!string.IsNullOrWhiteSpace(item.SubName))
                {
                    yield return item.SubName;
                }
                break;
        }
    }

    private sealed record ParamsIntArrayReference(IReadOnlyList<string> ItemIndices, IReadOnlyList<string> Resources);

    private static string? GetLayerFilterOperationName(sbyte filter, IReadOnlyList<int> parameters) => filter switch
    {
        1 => "SurfaceFilter",
        2 => "SurfaceFlush",
        3 => parameters.Count > 0 && parameters[0] == 1 ? "SurfaceGaussBlur" : "SurfaceBlur",
        4 => GetColorFilterOperationName(parameters),
        5 => parameters.Count > 0
            ? parameters[0] switch
            {
                0 => "SurfaceClear",
                < 255 => "SurfaceMulAlpha",
                _ => "SurfaceCopyDraw",
            }
            : null,
        6 => "SurfaceAddSubSurface",
        7 => "SurfaceMosaic",
        8 => "SurfaceMulColor",
        9 => "SurfaceFilter2",
        11 => "SurfaceFlip",
        _ => null,
    };

    private static string? GetColorFilterOperationName(IReadOnlyList<int> parameters)
    {
        var hasAdd = parameters.Any(value => value > 0);
        var hasSub = parameters.Any(value => value < 0);
        return (hasAdd, hasSub) switch
        {
            (true, true) => "SurfaceAddColor+SurfaceSubColor",
            (true, false) => "SurfaceAddColor",
            (false, true) => "SurfaceSubColor",
            _ => null,
        };
    }

    private static string FormatBytes(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append(data[i].ToString(CultureInfo.InvariantCulture));
        }
        builder.Append(']');
        return builder.ToString();
    }

    private static string FormatI32List(ReadOnlySpan<byte> data)
    {
        var values = ReadI32List(data);
        return FormatI32Values(values);
    }

    private string FormatResourceSlots(ReadOnlySpan<byte> data)
    {
        var values = ReadI32List(data);
        if (!TryGetResourceSlotGroups(out var primaryCount, out var secondaryCount))
        {
            return FormatI32Values(values);
        }

        var tailStart = primaryCount + secondaryCount;
        if (values.Length < tailStart)
        {
            return FormatI32Values(values);
        }

        var builder = new StringBuilder();
        builder.Append("{primary=");
        builder.Append(FormatI32Values(values.AsSpan(0, primaryCount)));
        builder.Append(", secondary=");
        builder.Append(FormatI32Values(values.AsSpan(primaryCount, secondaryCount)));
        if (values.Length - tailStart == 2)
        {
            builder.Append(", reserved=");
            builder.Append(values[tailStart].ToString(CultureInfo.InvariantCulture));
            builder.Append(", message_sequence=");
            builder.Append(values[tailStart + 1].ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            builder.Append(", extra=");
            builder.Append(FormatI32Values(values.AsSpan(tailStart)));
        }
        builder.Append('}');
        return builder.ToString();
    }

    private bool TryGetResourceSlotGroups(out int primaryCount, out int secondaryCount)
    {
        primaryCount = 0;
        secondaryCount = 0;

        var scalars = _paramsDocument?.GameSystem.V5Scalars;
        if (scalars is null || scalars.Length < 4)
        {
            return false;
        }

        if (scalars[3] > 8 || scalars[1] > 8 || scalars[3] + scalars[1] > 8)
        {
            return false;
        }

        primaryCount = (int)scalars[3];
        secondaryCount = (int)scalars[1];
        return true;
    }

    private static int[] ReadI32List(ReadOnlySpan<byte> data)
    {
        var values = new int[data.Length / 4];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4));
        }

        return values;
    }

    private static string FormatI32Values(ReadOnlySpan<int> values)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append(values[i].ToString(CultureInfo.InvariantCulture));
        }
        builder.Append(']');
        return builder.ToString();
    }

    private static string ProgramName(ushort id) =>
        ScrOpcodeInfo.Get(20).SubOpcodes.FirstOrDefault(x => x.Value == id)?.Name ?? "unknown";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Quote(string value) => "\"" + Escape(value) + "\"";

    private static string EscapeComment(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static ushort U16(byte[] body, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2));

    private static uint U32(byte[] body, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));

    private static int I32(byte[] body, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4));

    private static short I16(byte[] body, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(offset, 2));
}
