// ============================================================================
// ScrInstructionTextCodec.cs
// 指令级文本编解码器: 处理单条指令在 SCRASM 文本格式中的读写
//
// TryWrite: 将 ScriptInstruction 格式化为文本行
//   - 每种 opcode (1~28) 有专用格式, 如:
//     assign dst=N src08=N src0c=N
//     jump @label / if_true flags=N value=N @label
//     title "文本" (使用 _readEncoding 解码 body 中的 Shift_JIS 字节)
//   - 未匹配的 opcode/body 长度组合返回 false, 由上层回退为通用格式
//
// TryRead: 解析文本行并构建 ScriptInstruction 添加到 ScriptDocument
//   - 支持 key=value 格式参数, @label 跳转目标, "quoted" 字符串
//   - title 指令用 _writeEncoding 编码文本 (默认 Shift_JIS CP932)
//   - 未知指令使用 "op N name bytes=[...]" 通用格式
//
// 编码处理: 构造时接收 readEncoding / writeEncoding (默认 CP932)
//           用于 title 指令的 Shift_JIS 文本 <-> 字节转换
//
// 依赖: ScriptDocument, ScriptInstruction, ScrOpcodeInfo
// 被依赖: ScrTextCodec (文本汇编), ScrListingFormatter (列表格式化)
// ============================================================================
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Script.Params;

public sealed class ScrInstructionTextCodec
{
    public static void EnsureEncodingProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    static ScrInstructionTextCodec() => EnsureEncodingProvider();

    private readonly Encoding _readEncoding;
    private readonly Encoding _writeEncoding;

    public ScrInstructionTextCodec(Encoding readEncoding, Encoding writeEncoding)
    {
        _readEncoding = readEncoding ?? throw new ArgumentNullException(nameof(readEncoding));
        _writeEncoding = writeEncoding ?? throw new ArgumentNullException(nameof(writeEncoding));
    }

    public bool TryWrite(StringBuilder builder, ScriptInstruction instruction)
    {
        var body = instruction.Body;
        switch (instruction.Opcode)
        {
            case 1 when body.Length == 12:
                builder.Append("assign");
                AppendU8(builder, "flags", body, 0);
                AppendU8(builder, "op", body, 1);
                AppendU16(builder, "dst", body, 2);
                AppendI32(builder, "src08", body, 4);
                AppendI32(builder, "src0c", body, 8);
                return true;
            case 2 when body.Length == 4:
                builder.Append("flag_set");
                AppendU16(builder, "var", body, 0);
                return true;
            case 3 when body.Length == 4:
                builder.Append("flag_clear");
                AppendU16(builder, "var", body, 0);
                return true;
            case 4 when body.Length == 0:
                builder.Append("end");
                return true;
            case 5 when body.Length >= 3:
                builder.Append("wait");
                AppendU8(builder, "flags", body, 0);
                AppendU16(builder, "value", body, 1);
                if (body.Length >= 5)
                {
                    AppendU16(builder, "aux", body, 3);
                }
                AppendBytes(builder, "extra", body.AsSpan(body.Length >= 5 ? 5 : 3));
                return true;
            case 6 when body.Length >= 8:
                builder.Append("update_layer");
                AppendU32(builder, "layer", body, 0);
                AppendU32(builder, "ref", body, 4);
                AppendBytes(builder, "extra", body.AsSpan(8));
                return true;
            case 7 when body.Length == 44:
                builder.Append("text");
                AppendI32(builder, "cmd", body, 0);
                AppendI32(builder, "arg08", body, 4);
                AppendI32(builder, "arg0c", body, 8);
                AppendBytes(builder, "payload", body.AsSpan(12, 28));
                AppendI32(builder, "tail", body, 40);
                return true;
            case 8 when body.Length >= 4:
                builder.Append("menu");
                AppendU32(builder, "id", body, 0);
                AppendBytes(builder, "extra", body.AsSpan(4));
                return true;
            case 9 when body.Length >= 4:
                builder.Append("sound");
                AppendU32(builder, "id", body, 0);
                AppendBytes(builder, "extra", body.AsSpan(4));
                return true;
            case 10 when body.Length == 4:
                builder.Append("bgm");
                AppendU32(builder, "track", body, 0);
                return true;
            case 11 when body.Length == 4:
                builder.Append("jump ");
                AppendTarget(builder, instruction, body, 0);
                return true;
            case 12 when body.Length >= 5:
                builder.Append("file_jump");
                AppendU8(builder, "flags", body, 0);
                AppendU32(builder, "target", body, 1);
                AppendBytes(builder, "extra", body.AsSpan(5));
                return true;
            case 13 when body.Length == 12:
                builder.Append("compare");
                AppendU32(builder, "lhs", body, 0);
                AppendU32(builder, "rhs", body, 4);
                AppendU32(builder, "mode", body, 8);
                return true;
            case 14 when body.Length == 7:
                builder.Append("if_true");
                AppendConditional(builder, instruction, body);
                return true;
            case 15 when body.Length == 7:
                builder.Append("if_false");
                AppendConditional(builder, instruction, body);
                return true;
            case 16 when body.Length == 5:
                builder.Append("file_call");
                AppendU8(builder, "flags", body, 0);
                AppendU32(builder, "target", body, 1);
                return true;
            case 17 when body.Length == 0:
                builder.Append("file_return");
                return true;
            case 18 when body.Length == 4:
                builder.Append("call ");
                AppendTarget(builder, instruction, body, 0);
                return true;
            case 19 when body.Length == 0:
                builder.Append("return");
                return true;
            case 20 when body.Length == 3:
                builder.Append("program");
                AppendU8(builder, "flags", body, 0);
                var programId = ReadU16(body, 1);
                AppendU16(builder, "id", body, 1);
                builder.Append(" name=");
                builder.Append(GetProgramName(programId));
                return true;
            case 21 when body.Length >= 1 && body.Length == 1 + body[0]:
                builder.Append("title ");
                builder.Append(Quote(_readEncoding.GetString(body, 1, body[0])));
                return true;
            case 22 when body.Length == 2:
                builder.Append("scene");
                AppendU16(builder, "index", body, 0);
                return true;
            case 23 when body.Length == 0:
                builder.Append("date_window");
                return true;
            case 24 when body.Length == 0:
                builder.Append("date_place_reset");
                return true;
            case 25 when body.Length == 4:
                builder.Append("save");
                AppendU32(builder, "slot", body, 0);
                return true;
            case 26 when body.Length >= 5:
                builder.Append("follow_jump");
                AppendU8(builder, "flags", body, 0);
                AppendU32(builder, "target", body, 1);
                AppendBytes(builder, "extra", body.AsSpan(5));
                return true;
            case 27 when body.Length >= 1 && body.Length == 1 + body[0] * 4:
                builder.Append("voice");
                for (var i = 0; i < body[0]; i++)
                {
                    builder.Append(' ');
                    builder.Append(ReadI32(body, 1 + i * 4).ToString(CultureInfo.InvariantCulture));
                }
                return true;
            case 28 when body.Length == 0:
                builder.Append("nop");
                return true;
            default:
                return false;
        }
    }

    public bool TryRead(ScriptDocument script, string line, int lineNo)
    {
        var tokens = Tokenize(line);
        if (tokens.Count == 0)
        {
            return false;
        }

        switch (tokens[0])
        {
            case "assign":
                script.AddInstruction(1, BuildAssignBody(tokens, lineNo));
                return true;
            case "flag_set":
                script.AddInstruction(2, BuildFlagBody(tokens, lineNo));
                return true;
            case "flag_clear":
                script.AddInstruction(3, BuildFlagBody(tokens, lineNo));
                return true;
            case "end":
                RequireCount(tokens, 1, lineNo);
                script.AddInstruction(4);
                return true;
            case "wait":
                script.AddInstruction(5, BuildWaitBody(tokens, lineNo));
                return true;
            case "update_layer":
                script.AddInstruction(6, BuildLayerBody(tokens, lineNo));
                return true;
            case "text":
                script.AddInstruction(7, BuildTextBody(tokens, lineNo));
                return true;
            case "menu":
                script.AddInstruction(8, BuildIdExtraBody(tokens, lineNo, 4));
                return true;
            case "sound":
                script.AddInstruction(9, BuildIdExtraBody(tokens, lineNo, 4));
                return true;
            case "bgm":
                script.AddInstruction(10, BuildSingleU32Body(tokens, lineNo, "track"));
                return true;
            case "jump":
                script.AddInstruction(11, BuildTargetBody(tokens, lineNo), targetLabel: ReadTargetLabel(tokens, lineNo));
                return true;
            case "file_jump":
                script.AddInstruction(12, BuildFileJumpBody(tokens, lineNo));
                return true;
            case "compare":
                script.AddInstruction(13, BuildCompareBody(tokens, lineNo));
                return true;
            case "if_true":
                script.AddInstruction(14, BuildConditionalBody(tokens, lineNo), targetLabel: ReadTargetLabel(tokens, lineNo));
                return true;
            case "if_false":
                script.AddInstruction(15, BuildConditionalBody(tokens, lineNo), targetLabel: ReadTargetLabel(tokens, lineNo));
                return true;
            case "file_call":
                script.AddInstruction(16, BuildFileCallBody(tokens, lineNo));
                return true;
            case "file_return":
                RequireCount(tokens, 1, lineNo);
                script.AddInstruction(17);
                return true;
            case "call":
                script.AddInstruction(18, BuildTargetBody(tokens, lineNo), targetLabel: ReadTargetLabel(tokens, lineNo));
                return true;
            case "return":
                RequireCount(tokens, 1, lineNo);
                script.AddInstruction(19);
                return true;
            case "program":
                script.AddInstruction(20, BuildProgramBody(tokens, lineNo));
                return true;
            case "title":
                script.AddInstruction(21, BuildTitleBody(tokens, lineNo));
                return true;
            case "scene":
                script.AddInstruction(22, BuildSceneBody(tokens, lineNo));
                return true;
            case "date_window":
                RequireCount(tokens, 1, lineNo);
                script.AddInstruction(23);
                return true;
            case "date_place_reset":
                RequireCount(tokens, 1, lineNo);
                script.AddInstruction(24);
                return true;
            case "save":
                script.AddInstruction(25, BuildSaveBody(tokens, lineNo));
                return true;
            case "follow_jump":
                script.AddInstruction(26, BuildFileJumpBody(tokens, lineNo));
                return true;
            case "voice":
                script.AddInstruction(27, BuildVoiceBody(tokens, lineNo));
                return true;
            case "nop":
                RequireCount(tokens, 1, lineNo);
                script.AddInstruction(28);
                return true;
            case "op":
                script.AddInstruction(ParseUnknownOpcode(tokens, lineNo), ParseUnknownBody(tokens, lineNo));
                return true;
            default:
                return false;
        }
    }

    public static Encoding ResolveEncoding(string? value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (string.IsNullOrWhiteSpace(value))
        {
            return Encoding.GetEncoding(932);
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "cp932" or "sjis" or "shift-jis" or "shift_jis" => Encoding.GetEncoding(932),
            "cp936" or "gbk" => Encoding.GetEncoding(936),
            "utf8" or "utf-8" => Encoding.UTF8,
            _ => int.TryParse(value, out var codePage)
                ? Encoding.GetEncoding(codePage)
                : Encoding.GetEncoding(value)
        };
    }

    private byte[] BuildTextBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[44];
        WriteI32(body, 0, ReadI32(values, "cmd", lineNo));
        WriteI32(body, 4, ReadI32(values, "arg08", lineNo));
        WriteI32(body, 8, ReadI32(values, "arg0c", lineNo));
        var payload = ParseByteList(Require(values, "payload", lineNo), lineNo);
        if (payload.Length != 28)
        {
            throw new FormatException($"Line {lineNo}: text payload must be 28 bytes.");
        }

        payload.CopyTo(body.AsSpan(12, 28));
        WriteI32(body, 40, ReadI32(values, "tail", lineNo));
        return body;
    }

    private byte[] BuildAssignBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[12];
        body[0] = values.TryGetValue("flags", out _) ? ReadU8(values, "flags", lineNo) : (byte)0;
        body[1] = values.TryGetValue("op", out _) ? ReadU8(values, "op", lineNo) : (byte)0;
        WriteU16(body, 2, ReadU16(values, "dst", lineNo));
        WriteI32(body, 4, ReadI32(values, "src08", lineNo));
        WriteI32(body, 8, ReadI32(values, "src0c", lineNo));
        return body;
    }

    private byte[] BuildFlagBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[4];
        WriteU16(body, 0, ReadU16(values, "var", lineNo));
        return body;
    }

    private byte[] BuildWaitBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var extra = values.TryGetValue("extra", out var extraText) ? ParseByteList(extraText, lineNo) : [];
        var hasAux = values.ContainsKey("aux");
        var body = new byte[(hasAux ? 5 : 3) + extra.Length];
        body[0] = ReadU8(values, "flags", lineNo);
        WriteU16(body, 1, ReadU16(values, "value", lineNo));
        if (hasAux)
        {
            WriteU16(body, 3, ReadU16(values, "aux", lineNo));
        }
        if (extra.Length > 0)
        {
            extra.CopyTo(body.AsSpan(hasAux ? 5 : 3));
        }
        return body;
    }

    private byte[] BuildSingleU32Body(IReadOnlyList<string> tokens, int lineNo, string key)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[4];
        WriteU32(body, 0, ReadU32(values, key, lineNo));
        return body;
    }

    private byte[] BuildSaveBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var body = new byte[4];
        if (tokens.Count == 2 && !tokens[1].Contains('='))
        {
            WriteU32(body, 0, ParseU32(tokens[1], lineNo));
            return body;
        }

        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        WriteU32(body, 0, ReadU32(values, "slot", lineNo));
        return body;
    }

    private byte[] BuildLayerBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var extra = values.TryGetValue("extra", out var extraText) ? ParseByteList(extraText, lineNo) : [];
        var body = new byte[8 + extra.Length];
        WriteU32(body, 0, ReadU32(values, "layer", lineNo));
        WriteU32(body, 4, ReadU32(values, "ref", lineNo));
        if (extra.Length > 0)
        {
            extra.CopyTo(body.AsSpan(8));
        }
        return body;
    }

    private byte[] BuildIdExtraBody(IReadOnlyList<string> tokens, int lineNo, int baseLength)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var extra = values.TryGetValue("extra", out var extraText) ? ParseByteList(extraText, lineNo) : [];
        var body = new byte[baseLength + extra.Length];
        WriteU32(body, 0, ReadU32(values, "id", lineNo));
        if (extra.Length > 0)
        {
            extra.CopyTo(body.AsSpan(baseLength));
        }
        return body;
    }

    private byte[] BuildFileJumpBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var extra = values.TryGetValue("extra", out var extraText) ? ParseByteList(extraText, lineNo) : [];
        var body = new byte[5 + extra.Length];
        body[0] = ReadU8(values, "flags", lineNo);
        WriteU32(body, 1, ReadU32(values, "target", lineNo));
        if (extra.Length > 0)
        {
            extra.CopyTo(body.AsSpan(5));
        }
        return body;
    }

    private byte[] BuildCompareBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[12];
        WriteU32(body, 0, ReadU32(values, "lhs", lineNo));
        WriteU32(body, 4, ReadU32(values, "rhs", lineNo));
        WriteU32(body, 8, ReadU32(values, "mode", lineNo));
        return body;
    }

    private byte[] BuildTargetBody(IReadOnlyList<string> tokens, int lineNo)
    {
        RequireCount(tokens, 2, lineNo);
        var body = new byte[4];
        if (!tokens[1].StartsWith('@'))
        {
            WriteU32(body, 0, ParseU32(tokens[1], lineNo));
        }

        return body;
    }

    private byte[] BuildConditionalBody(IReadOnlyList<string> tokens, int lineNo)
    {
        if (tokens.Count != 4)
        {
            throw new FormatException($"Line {lineNo}: conditional format is '<op> flags=<u8> value=<u16> <target>'.");
        }

        var values = ReadKeyValues(tokens.Skip(1).Take(2), lineNo);
        var body = new byte[7];
        body[0] = ReadU8(values, "flags", lineNo);
        WriteU16(body, 1, ReadU16(values, "value", lineNo));
        if (!tokens[3].StartsWith('@'))
        {
            WriteU32(body, 3, ParseU32(tokens[3], lineNo));
        }

        return body;
    }

    private byte[] BuildFileCallBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[5];
        body[0] = ReadU8(values, "flags", lineNo);
        WriteU32(body, 1, ReadU32(values, "target", lineNo));
        return body;
    }

    private byte[] BuildProgramBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[3];
        body[0] = ReadU8(values, "flags", lineNo);
        WriteU16(body, 1, ReadU16(values, "id", lineNo));
        return body;
    }

    private byte[] BuildTitleBody(IReadOnlyList<string> tokens, int lineNo)
    {
        if (tokens.Count != 2)
        {
            throw new FormatException($"Line {lineNo}: title requires one quoted string.");
        }

        var text = Unquote(tokens[1]);
        var bytes = _writeEncoding.GetBytes(text);
        if (bytes.Length > byte.MaxValue)
        {
            throw new FormatException($"Line {lineNo}: title text is too long.");
        }

        var body = new byte[1 + bytes.Length];
        body[0] = (byte)bytes.Length;
        bytes.CopyTo(body.AsSpan(1));
        return body;
    }

    private byte[] BuildSceneBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var values = ReadKeyValues(tokens.Skip(1), lineNo);
        var body = new byte[2];
        WriteU16(body, 0, ReadU16(values, "index", lineNo));
        return body;
    }

    private byte[] BuildVoiceBody(IReadOnlyList<string> tokens, int lineNo)
    {
        var count = tokens.Count - 1;
        if (count > byte.MaxValue)
        {
            throw new FormatException($"Line {lineNo}: too many voice indices.");
        }

        var body = new byte[1 + count * 4];
        body[0] = (byte)count;
        for (var i = 0; i < count; i++)
        {
            WriteI32(body, 1 + i * 4, int.Parse(tokens[i + 1], CultureInfo.InvariantCulture));
        }

        return body;
    }

    private string? ReadTargetLabel(IReadOnlyList<string> tokens, int lineNo)
    {
        var target = tokens[^1];
        if (target.StartsWith('@'))
        {
            return target[1..].TrimEnd(':');
        }

        ParseU32(target, lineNo);
        return null;
    }

    private static void AppendConditional(StringBuilder builder, ScriptInstruction instruction, byte[] body)
    {
        AppendU8(builder, "flags", body, 0);
        AppendU16(builder, "value", body, 1);
        builder.Append(' ');
        AppendTarget(builder, instruction, body, 3);
    }

    private static void AppendTarget(StringBuilder builder, ScriptInstruction instruction, byte[] body, int offset)
    {
        if (instruction.TargetLabel is not null)
        {
            builder.Append('@');
            builder.Append(instruction.TargetLabel);
        }
        else
        {
            builder.Append(ReadU32(body, offset).ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendU8(StringBuilder builder, string name, byte[] body, int offset)
    {
        builder.Append(' ');
        builder.Append(name);
        builder.Append('=');
        builder.Append(body[offset].ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendU16(StringBuilder builder, string name, byte[] body, int offset)
    {
        builder.Append(' ');
        builder.Append(name);
        builder.Append('=');
        builder.Append(ReadU16(body, offset).ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendU32(StringBuilder builder, string name, byte[] body, int offset)
    {
        builder.Append(' ');
        builder.Append(name);
        builder.Append('=');
        builder.Append(ReadU32(body, offset).ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendI32(StringBuilder builder, string name, byte[] body, int offset)
    {
        builder.Append(' ');
        builder.Append(name);
        builder.Append('=');
        builder.Append(ReadI32(body, offset).ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendBytes(StringBuilder builder, string name, ReadOnlySpan<byte> data)
    {
        builder.Append(' ');
        builder.Append(name);
        builder.Append('=');
        builder.Append(FormatByteList(data));
    }

    private static Dictionary<string, string> ReadKeyValues(IEnumerable<string> tokens, int lineNo)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            var index = token.IndexOf('=');
            if (index <= 0)
            {
                throw new FormatException($"Line {lineNo}: expected key=value token.");
            }

            result[token[..index]] = token[(index + 1)..];
        }

        return result;
    }

    private static byte[] ParseUnknownBody(IReadOnlyList<string> tokens, int lineNo)
    {
        if (tokens.Count <= 2)
        {
            return [];
        }

        var token = tokens.Count >= 4 && tokens[2] == "bytes=" ? tokens[3] : tokens[^1];
        if (token.StartsWith("bytes=", StringComparison.Ordinal))
        {
            token = token["bytes=".Length..];
        }

        return ParseByteList(token, lineNo);
    }

    private static ushort ParseUnknownOpcode(IReadOnlyList<string> tokens, int lineNo)
    {
        if (tokens.Count < 2)
        {
            throw new FormatException($"Line {lineNo}: expected opcode number.");
        }

        return checked((ushort)ParseU32(tokens[1], lineNo));
    }

    private static byte[] ParseByteList(string text, int lineNo)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed == "[]")
        {
            return [];
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

    private static string FormatByteList(ReadOnlySpan<byte> data)
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

    private static string GetProgramName(ushort id) => id switch
    {
        0 => "play_game_param_media",
        1 => "set_system_word_flag",
        2 => "set_replay_or_skip_flag",
        3 => "return_state_10",
        4 => "enable_subsystem_3_4",
        5 => "disable_subsystem_3_4",
        6 => "noop",
        7 => "random_prog_result",
        8 => "wait_subsystem1_ready",
        9 => "set_subsystem2_flag",
        10 => "read_subsystem1_flag111",
        11 => "return_state_12",
        12 => "read_system_word_array",
        13 => "write_system_word_array",
        14 => "return_state_13",
        15 => "subsystem4_null_call",
        16 => "set_subsystem1_bool",
        17 => "read_subsystem1_bool",
        18 => "read_mage_gauge_flag",
        19 => "restart_point_dispatch",
        20 => "return_state_15",
        21 => "read_gauge_display_flag",
        22 => "noop",
        23 => "return_state_16",
        _ => "unknown"
    };

    private static byte ReadU8(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        checked((byte)ParseU32(Require(values, key, lineNo), lineNo));

    private static ushort ReadU16(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        checked((ushort)ParseU32(Require(values, key, lineNo), lineNo));

    private static uint ReadU32(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        ParseU32(Require(values, key, lineNo), lineNo);

    private static int ReadI32(IReadOnlyDictionary<string, string> values, string key, int lineNo) =>
        int.Parse(Require(values, key, lineNo), CultureInfo.InvariantCulture);

    private static string Require(IReadOnlyDictionary<string, string> values, string key, int lineNo)
    {
        if (!values.TryGetValue(key, out var value))
        {
            throw new FormatException($"Line {lineNo}: missing '{key}'.");
        }

        return value;
    }

    private static uint ParseU32(string value, int lineNo)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToUInt32(value[2..], 16);
        }

        if (uint.TryParse(value, CultureInfo.InvariantCulture, out var raw))
        {
            return raw;
        }

        throw new FormatException($"Line {lineNo}: invalid uint '{value}'.");
    }

    private static void RequireCount(IReadOnlyList<string> tokens, int count, int lineNo)
    {
        if (tokens.Count != count)
        {
            throw new FormatException($"Line {lineNo}: expected {count} token(s).");
        }
    }

    private static List<string> Tokenize(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var escape = false;

        foreach (var ch in line)
        {
            if (escape)
            {
                current.Append(ch);
                escape = false;
                continue;
            }

            if (ch == '\\')
            {
                current.Append(ch);
                escape = true;
                continue;
            }

            if (ch == '"')
            {
                current.Append(ch);
                inQuote = !inQuote;
                continue;
            }

            if (!inQuote && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return text;
        }

        var inner = text[1..^1];
        var builder = new StringBuilder();
        var escape = false;
        foreach (var ch in inner)
        {
            if (escape)
            {
                builder.Append(ch);
                escape = false;
            }
            else if (ch == '\\')
            {
                escape = true;
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static ushort ReadU16(byte[] body, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2));
    private static uint ReadU32(byte[] body, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));
    private static int ReadI32(byte[] body, int offset) => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4));
    private static void WriteU16(byte[] body, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(offset, 2), value);
    private static void WriteU32(byte[] body, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset, 4), value);
    private static void WriteI32(byte[] body, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset, 4), value);
}
