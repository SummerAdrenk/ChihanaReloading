// ============================================================================
// ScrTextCodec.cs
// 文本汇编编解码器: 在 SCRASM 文本格式与 ScrFileDocument 之间互相转换
//
// SCRASM 文本格式结构:
//   "; Kaguya_YaneKit SCRASM v2"  魔数行
//   ".header [SCR-Ver5.3]"        文件头声明
//   ".code"                        代码段 (标签 @name: / 指令 / tail)
//   ".save"                        存档偏移表 (@label 或 0xHHHHHHHH)
//   ".layer"                       图层偏移表
//   ".container-tail"              容器尾部字节列表
//
// Write: 遍历 ScrFileDocument 各段, 使用 ScrInstructionTextCodec 格式化指令
// Read: 逐行解析, 按段分发; 代码段调用 ScrInstructionTextCodec 反汇编
// 支持 ; 行注释 (引号内的分号不视为注释)
//
// 依赖: ScrInstructionTextCodec, ScrOpcodeInfo, ScrFileDocument, ScrOffsetReference
// 被依赖: 上层命令 (ScrCommands) 的 disassemble/assemble 操作
// ============================================================================
using System.Globalization;
using System.Text;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Script.Params;

public sealed class ScrTextCodec
{
    private const string Magic = "; Kaguya_YaneKit SCRASM v2";
    private readonly ScrInstructionTextCodec _instructionCodec;

    public ScrTextCodec(string? readEncoding = null, string? writeEncoding = null)
    {
        var read = ScrInstructionTextCodec.ResolveEncoding(readEncoding);
        var write = ScrInstructionTextCodec.ResolveEncoding(writeEncoding);
        _instructionCodec = new ScrInstructionTextCodec(read, write);
    }

    public string Write(ScrFileDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Magic);
        builder.AppendLine($".header {document.Header}");
        builder.AppendLine(".code");

        foreach (var element in document.Script.Elements)
        {
            switch (element)
            {
                case ScriptLabel label:
                    builder.AppendLine($"@{label.Name}:");
                    break;
                case ScriptComment comment:
                    builder.AppendLine($"; {comment.Text}");
                    break;
                case ScriptInstruction instruction:
                    WriteInstruction(builder, instruction);
                    if (!string.IsNullOrWhiteSpace(instruction.Comment))
                    {
                        builder.Append(" ; ");
                        builder.Append(instruction.Comment);
                    }
                    builder.AppendLine();
                    break;
                case ScriptTail tail:
                    builder.Append("tail bytes=");
                    builder.AppendLine(FormatByteList(tail.Data));
                    break;
            }
        }

        builder.AppendLine(".save");
        foreach (var reference in document.SaveOffsets)
        {
            builder.AppendLine(FormatOffsetReference(reference));
        }

        builder.AppendLine(".layer");
        foreach (var reference in document.LayerOffsets)
        {
            builder.AppendLine(FormatOffsetReference(reference));
        }

        if (document.Tail.Length > 0)
        {
            builder.AppendLine(".container-tail");
            builder.AppendLine(FormatByteList(document.Tail));
        }

        return builder.ToString();
    }

    public ScrFileDocument Read(string text)
    {
        using var reader = new StringReader(text);
        var document = new ScrFileDocument();
        var section = "preamble";
        string? line;
        var lineNo = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var trimmed = StripComment(line).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith(".header ", StringComparison.Ordinal))
            {
                document.Header = trimmed[".header ".Length..].Trim();
                continue;
            }

            if (trimmed.StartsWith(".", StringComparison.Ordinal))
            {
                section = trimmed;
                continue;
            }

            switch (section)
            {
                case ".code":
                    ReadCodeLine(document.Script, trimmed, lineNo);
                    break;
                case ".save":
                    document.SaveOffsets.Add(ParseOffsetReference(trimmed, lineNo, ScrOffsetEncoding.FileAbsolute));
                    break;
                case ".layer":
                    document.LayerOffsets.Add(ParseOffsetReference(trimmed, lineNo, ScrOffsetEncoding.CodeRelative));
                    break;
                case ".container-tail":
                    document.Tail = ParseByteList(trimmed, lineNo);
                    break;
                default:
                    throw new FormatException($"Line {lineNo}: content outside a known section.");
            }
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

        if (_instructionCodec.TryRead(script, line, lineNo))
        {
            return;
        }

        if (line.StartsWith("tail ", StringComparison.Ordinal))
        {
            var body = line["tail ".Length..].Trim();
            if (body.StartsWith("bytes=", StringComparison.Ordinal))
            {
                body = body["bytes=".Length..];
            }
            script.AddTail(ParseByteList(body, lineNo));
            return;
        }

        throw new FormatException($"Line {lineNo}: unknown code directive.");
    }

    private void WriteInstruction(StringBuilder builder, ScriptInstruction instruction)
    {
        if (_instructionCodec.TryWrite(builder, instruction))
        {
            return;
        }

        var descriptor = ScrOpcodeInfo.Get(instruction.Opcode);
        builder.Append("op ");
        builder.Append(instruction.Opcode.ToString(CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(descriptor.Name);
        builder.Append(" bytes=");
        builder.Append(FormatByteList(instruction.Body));
    }

    private static ScrOffsetReference ParseOffsetReference(string line, int lineNo, ScrOffsetEncoding encoding)
    {
        if (line.StartsWith('@'))
        {
            return ScrOffsetReference.FromLabel(line[1..].TrimEnd(':'), encoding);
        }

        if (line.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ScrOffsetReference.FromRaw(Convert.ToUInt32(line[2..], 16), encoding);
        }

        if (uint.TryParse(line, CultureInfo.InvariantCulture, out var raw))
        {
            return ScrOffsetReference.FromRaw(raw, encoding);
        }

        throw new FormatException($"Line {lineNo}: expected label reference or raw uint.");
    }

    private static string FormatOffsetReference(ScrOffsetReference reference)
    {
        if (reference.Label is not null)
        {
            return $"@{reference.Label}";
        }

        return $"0x{reference.RawValue.GetValueOrDefault():X8}";
    }

    private static string FormatByteList(byte[] data)
    {
        if (data.Length == 0)
        {
            return "[]";
        }

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

    private static byte[] ParseByteList(string text, int lineNo)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed == "[]")
        {
            return [];
        }

        if (trimmed.StartsWith("bytes=", StringComparison.Ordinal))
        {
            trimmed = trimmed["bytes=".Length..];
        }

        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            throw new FormatException($"Line {lineNo}: expected byte list like [1,2,3].");
        }

        var inner = trimmed[1..^1];
        if (inner.Trim().Length == 0)
        {
            return [];
        }

        var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var value = int.Parse(parts[i], CultureInfo.InvariantCulture);
            if (value is < 0 or > 255)
            {
                throw new FormatException($"Line {lineNo}: byte value out of range.");
            }

            result[i] = (byte)value;
        }

        return result;
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        var escape = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (escape)
            {
                escape = false;
                continue;
            }

            if (ch == '\\')
            {
                escape = true;
                continue;
            }

            if (ch == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (ch == ';' && !inQuote)
            {
                return line[..i];
            }
        }

        return line;
    }
}
