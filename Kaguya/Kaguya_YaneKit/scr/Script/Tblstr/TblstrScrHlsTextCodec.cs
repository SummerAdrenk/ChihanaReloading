using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kaguya_YaneKit.Script.Tblstr;

public sealed class TblstrScrHlsTextCodec
{
    private const string BytesMarker = "; bytes=";
    private readonly Encoding _encoding;

    public TblstrScrHlsTextCodec(string? encodingName = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _encoding = string.IsNullOrWhiteSpace(encodingName)
            ? Encoding.GetEncoding(932)
            : Encoding.GetEncoding(encodingName);
    }

    public TblstrScrDocument Read(string text, string? sourceName = null)
    {
        var magic0 = TblstrScrCodec.Magic0;
        var magic1 = TblstrScrCodec.Magic1;
        var payload = new List<byte>();
        foreach (var rawLine in ReadLogicalLines(text))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(".magic ", StringComparison.Ordinal))
            {
                var values = line[".magic ".Length..].Split(',', StringSplitOptions.TrimEntries);
                if (values.Length == 2)
                {
                    magic0 = ParseU32(values[0]);
                    magic1 = ParseU32(values[1]);
                }
                continue;
            }

            var marker = line.IndexOf(BytesMarker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                var statement = line[..marker].Trim();
                var instructionBytes = ParseHexBytes(line[(marker + BytesMarker.Length)..].Trim());
                var rebuilt = ApplyReadableStatement(statement, instructionBytes);
                if (rebuilt.SequenceEqual(instructionBytes)
                    && !SameStatement(statement, RenderCanonicalStatement(instructionBytes)))
                {
                    throw new InvalidDataException(
                        "TBLSTR HLS line was changed but no semantic writer accepted it: " + statement);
                }

                payload.AddRange(rebuilt);
                continue;
            }

            if (line.StartsWith(".", StringComparison.Ordinal) ||
                line.EndsWith(":", StringComparison.Ordinal) ||
                line.StartsWith("//", StringComparison.Ordinal) ||
                line.Length == 0)
            {
                continue;
            }

            payload.AddRange(BuildInstructionFromStatement(line));
        }

        var document = new TblstrScrDocument
        {
            SourceName = sourceName ?? "",
            Magic0 = magic0,
            Magic1 = magic1,
            PayloadSize = payload.Count,
            Payload = payload.ToArray()
        };

        return new TblstrScrCodec().Read(TblstrScrCodec.WriteRaw(document), sourceName);
    }

    private byte[] BuildInstructionFromStatement(string statement)
    {
        var opcode = InferOpcode(statement);
        var baseBytes = CreateBaseTemplate(opcode, statement);
        var rebuilt = ApplyReadableStatement(statement, baseBytes);
        var canonical = RenderCanonicalStatement(rebuilt);
        if (SameStatement(statement, canonical))
        {
            return rebuilt;
        }

        throw new InvalidDataException($"TBLSTR HLS semantic line could not be rebuilt: {statement} canonical={canonical}");
    }

    private static int InferOpcode(string statement)
    {
        if (statement.StartsWith("IF_EQ ", StringComparison.Ordinal)) return 3;
        if (statement.StartsWith("IF_NE ", StringComparison.Ordinal)) return 4;
        if (statement.StartsWith("IF_GT ", StringComparison.Ordinal)) return 5;
        if (statement.StartsWith("IF_LT ", StringComparison.Ordinal)) return 6;
        if (statement.StartsWith("IF_GE ", StringComparison.Ordinal)) return 7;
        if (statement.StartsWith("IF_LE ", StringComparison.Ordinal)) return 8;
        if (statement.StartsWith("JUMP_SCRIPT_START ", StringComparison.Ordinal)) return 12;
        if (statement.StartsWith("JUMP_SCRIPT ", StringComparison.Ordinal))
        {
            return statement.Contains("label=\"\"", StringComparison.Ordinal) ? 12 : 94;
        }

        if (statement.StartsWith("CALL_SCRIPT ", StringComparison.Ordinal)) return 112;
        if (statement.StartsWith("LOAD_SPRITE_CONTROLLED_EX ", StringComparison.Ordinal)) return 144;
        if (statement.StartsWith("LOAD_SPRITE_CONTROLLED ", StringComparison.Ordinal)) return 143;

        var split = statement.IndexOfAny([' ', '\t']);
        var mnemonic = split < 0 ? statement : statement[..split];
        return mnemonic switch
        {
            "SET_VALUE" => 0,
            "ADD_VALUE" => 1,
            "JUMP" => 2,
            "MENU_BEGIN" => 9,
            "MENU_CHOICE" => 10,
            "MENU_COMMIT" => 11,
            "PLAY_MOVIE" => 18,
            "MESSAGE" => 19,
            "CLOSE_MESSAGE" => 20,
            "LOAD_LAYER" => 21,
            "WAIT" => 22,
            "CLEAR_LAYER" => 23,
            "SET_STATE_27" => 24,
            "AUTO_WAIT_CHECKPOINT" => 33,
            "ALT_WAIT_CHECKPOINT" => 34,
            "STOP_SCRIPT" or "STOP_OR_PAUSE" => 39,
            "SET_DISPLAY_NAME" or "SHOW_TEXT" => 44,
            "SET_STATE_BYTES" => 61,
            "PUSH_KAISOU_SYSTEM_BUTTON" => 63,
            "MARK_CURRENT_RESOURCE_ACTIVE" => 71,
            "RANDOM_VALUE" => 74,
            "PLAY_BGM" => 80,
            "PLAY_WAVE" or "PLAY_SOUND" => 82,
            "CLEAR_AUDIO" => 83,
            "EMIT_SYSTEM_EVENT" => 84,
            "CLEAR_MOVIE_STATE" => 85,
            "SET_RETURN_VALUE" => 87,
            "SET_LAYER_COLOR_FILTER" => 88,
            "SET_SCROLL" => 89,
            "APPLY_SCROLL" => 90,
            "COPY_VALUE" => 91,
            "CLEAR_DISPLAY_NAME" => 93,
            "SET_WAIT_RESUME" => 95,
            "RESET_ADV_LAYERS" => 96,
            "RETURN_SCRIPT" => 113,
            "SET_SPRITE_POS" => 114,
            "SET_SPRITE_FRAME" => 115,
            "SET_ADV_VIEW_SPRITE_INDEX" or "SET_STATE_424" => 117,
            "SET_RUN_STATE" => 119,
            "CLEAR_RUN_STATE" => 120,
            "LOAD_SPRITE" => 121,
            "CLEAR_LAYER_COLOR_FILTER" => 122,
            "STOP_MOVIE_IF_ACTIVE" => 134,
            "CLEAR_MOVIE_AND_FADE_OUT" => 135,
            "WAIT_RESOURCE_EFFECT" => 136,
            "TITLE" => 137,
            "FADE_IN_WAVE_LOOP" => 140,
            "FADE_OUT_WAVE_LOOP" => 141,
            "WAIT_WAVE_SLOT" or "RESTORE_SAVED_PC" => 142,
            "RANGE" => 145,
            "VOICE_GROUP_PREFIX" or "PENDING_TEXT_A" => 146,
            "VOICE_GROUP_ENTRY" or "APPEND_PENDING_TEXT_PAIR" => 147,
            "NOP_148" or "NOP" => 148,
            "ADD_SPRITE_KEYDATA" => 150,
            "ENABLE_SPRITE_KEYDATA" => 151,
            "SET_MESSAGE_COLOR0" => 152,
            "SET_MESSAGE_COLOR_MODE" => 153,
            "SET_MESSAGE_COLOR1" => 154,
            "INIT_RESOURCE_OBJECT" => 155,
            "SET_OBJECT_POS" => 156,
            "SET_OBJECT_FRAME" => 157,
            "CLEAR_OBJECT" => 158,
            "ADD_OBJECT_POS_KEY" => 159,
            "ENABLE_OBJECT_KEYFRAMES" => 160,
            "SET_OBJECT_ANM" => 161,
            "SET_ADV_EVENT_STATE_124" => 162,
            "VALIDATE_ADV_SP_KEYFRAMES" => 163,
            "ADD_OBJECT_ANM_KEY" => 164,
            "ADD_OBJECT_ALPHA_KEY" => 165,
            "SET_OBJECT_ALPHA" => 166,
            "ANM_PAUSE" => 167,
            "ANM_START" => 168,
            "ANM_RESTART" => 169,
            "ANM_WAITCOUNT" => 170,
            "ANM_SPEED" => 171,
            "NOP_172" => 172,
            _ => throw new InvalidDataException("Unknown TBLSTR HLS mnemonic: " + mnemonic)
        };
    }

    private static byte[] CreateBaseTemplate(int opcode, string statement)
    {
        var length = opcode switch
        {
            0 or 1 or 74 or 90 or 145 or 156 or 164 or 165 => 12,
            2 or 9 or 10 or 21 or 22 or 24 or 61 or 80 or 87 or 88 or 91 or 94 or 96 or 112 or 117 or 119 or 122 or 151 or 152 or 154 or 160 or 166 or 170 or 171 => 8,
            >= 3 and <= 8 => 16,
            19 => 16,
            11 or 12 or 18 or 20 or 23 or 33 or 34 or 39 or 44 or 63 or 71 or 83 or 84 or 85 or 93 or 113 or 120 or 134 or 135 or 136 or 137 or 140 or 141 or 142 or 146 or 147 or 148 or 153 or 155 or 158 or 161 or 163 or 172 => 4,
            82 or 95 => 5,
            167 or 168 or 169 => 3,
            89 or 159 => 16,
            114 or 115 or 121 => 20,
            143 => 28,
            144 => 32,
            150 => 16,
            157 => 8,
            162 => 8,
            _ => throw new InvalidDataException($"No TBLSTR HLS base template for opcode {opcode}.")
        };

        var bytes = new byte[length];
        bytes[0] = checked((byte)opcode);
        bytes[1] = checked((byte)length);
        if (opcode == 19)
        {
            bytes[2] = 0xFF;
            bytes[3] = 0xFF;
            WriteI32(bytes, 12, -1);
        }

        if ((opcode == 0 || opcode == 1) &&
            statement.Contains("local_value_table", StringComparison.Ordinal))
        {
            bytes[2] = 4;
        }

        if (opcode == 91)
        {
            var flags = 0;
            var arrow = statement.IndexOf("<-", StringComparison.Ordinal);
            var left = arrow < 0 ? statement : statement[..arrow];
            var right = arrow < 0 ? "" : statement[(arrow + 2)..];
            if (left.Contains("local_value_table", StringComparison.Ordinal)) flags |= 4;
            if (right.Contains("local_value_table", StringComparison.Ordinal)) flags |= 8;
            bytes[2] = checked((byte)flags);
        }

        if (opcode == 80)
        {
            bytes[2] = ParseBgmFormatFromStatement(statement);
            if (TryMatch(statement, @"(?:^|\s)mode=(?<mode>\d+)(?:\s|$)", out var mbgmMode))
            {
                bytes[4] = checked((byte)ParseI32(mbgmMode, "mode"));
            }
        }

        return bytes;
    }

    private byte[] ApplyReadableStatement(string statement, byte[] rawBytes)
    {
        if (rawBytes.Length < 2)
        {
            return rawBytes;
        }

        var baseLength = rawBytes[1];
        if (baseLength < 2 || baseLength > rawBytes.Length)
        {
            return rawBytes;
        }

        var opcode = rawBytes[0];
        var baseBytes = rawBytes[..baseLength];
        switch (opcode)
        {
            case 0:
                if (TryMatch(statement, @"^SET_VALUE\s+\S+\s+\[(?<idx>-?\d+)\]\s*=\s*(?<value>-?\d+)(?:\s+flags=(?<flags>0x[0-9A-Fa-f]+|\d+))?$", out var m0))
                {
                    if (m0.Groups["flags"].Success) baseBytes[2] = checked((byte)ParseU32(m0.Groups["flags"].Value));
                    WriteU16(baseBytes, 6, ParseU16(m0, "idx"));
                    WriteI32(baseBytes, 8, ParseI32(m0, "value"));
                }
                break;
            case 1:
                if (TryMatch(statement, @"^ADD_VALUE\s+\S+\s+\[(?<idx>-?\d+)\]\s*\+=\s*(?<value>-?\d+)(?:\s+flags=(?<flags>0x[0-9A-Fa-f]+|\d+))?$", out var m1))
                {
                    if (m1.Groups["flags"].Success) baseBytes[2] = checked((byte)ParseU32(m1.Groups["flags"].Value));
                    WriteU16(baseBytes, 6, ParseU16(m1, "idx"));
                    WriteI32(baseBytes, 8, ParseI32(m1, "value"));
                }
                break;
            case 2:
                if (TryMatch(statement, @"^JUMP\s+(?<target>0x[0-9A-Fa-f]+|\d+)$", out var m2))
                {
                    WriteU32(baseBytes, 4, ParseU32(m2.Groups["target"].Value));
                }
                break;
            case >= 3 and <= 8:
                if (TryMatch(statement, @"^IF_\w+\s+flags=(?<flags>0x[0-9A-Fa-f]+|\d+)\s+(?<left>0x[0-9A-Fa-f]+|\d+)\s+.+?\s+(?<right>0x[0-9A-Fa-f]+|\d+)\s+->\s+(?<target>0x[0-9A-Fa-f]+|\d+)$", out var mc))
                {
                    baseBytes[2] = checked((byte)ParseU32(mc.Groups["flags"].Value));
                    WriteU32(baseBytes, 4, ParseU32(mc.Groups["left"].Value));
                    WriteU32(baseBytes, 8, ParseU32(mc.Groups["right"].Value));
                    WriteU32(baseBytes, 12, ParseU32(mc.Groups["target"].Value));
                }
                break;
            case 9:
                if (TryMatch(statement, @"^MENU_BEGIN\s+flags=(?<flags>0x[0-9A-Fa-f]+|\d+)\s+source=(?<source>-?\d+)$", out var m9))
                {
                    WriteU16(baseBytes, 2, checked((ushort)ParseU32(m9.Groups["flags"].Value)));
                    WriteI32(baseBytes, 4, ParseI32(m9, "source"));
                }
                break;
            case 10:
                if (TryMatch(statement, @"^MENU_CHOICE\s+id=(?<id>-?\d+)\s+text=(?<text>-?\d+)$", out var m10))
                {
                    baseBytes[2] = unchecked((byte)ParseI32(m10, "id"));
                    WriteI32(baseBytes, 4, ParseI32(m10, "text"));
                }
                break;
            case 11:
                if (TryMatch(statement, @"^MENU_COMMIT\s+result=(?<result>\d+)$", out var m11))
                {
                    WriteU16(baseBytes, 2, ParseU16(m11, "result"));
                }
                break;
            case 12:
                return ReplaceStrings(baseBytes, rawBytes, [ExtractQuoted(statement)]);
            case 18:
            case 44:
                return ReplaceStrings(baseBytes, rawBytes, [ExtractQuoted(statement)]);
            case 80:
                if (TryMatch(statement, @"^PLAY_BGM\s+(?<track>"".*"")(?:\s+format=(?<format>[A-Za-z0-9_]+))?(?:\s+mode=(?<mode>\d+))?$", out var mbgm))
                {
                    if (mbgm.Groups["format"].Success)
                    {
                        baseBytes[2] = ParseBgmFormat(mbgm.Groups["format"].Value);
                    }

                    if (mbgm.Groups["mode"].Success)
                    {
                        baseBytes[4] = checked((byte)ParseI32(mbgm, "mode"));
                    }

                    return ReplaceStrings(baseBytes, rawBytes, [Unquote(mbgm.Groups["track"].Value)]);
                }
                break;
            case 19:
                return ApplyMessage(statement, baseBytes, rawBytes);
            case 21:
                if (TryMatch(statement, @"^LOAD_LAYER\s+(?<target>\S+)\s+(?<resource>"".*"")$", out var m21))
                {
                    TryWriteLayerMode(baseBytes, 3, m21.Groups["target"].Value);
                    return ReplaceStrings(baseBytes, rawBytes, [Unquote(m21.Groups["resource"].Value)]);
                }
                break;
            case 22:
                if (TryMatch(statement, @"^WAIT\s+mode=(?<mode>-?\d+)\s+duration=(?<duration>0x[0-9A-Fa-f]+|\d+)$", out var m22))
                {
                    baseBytes[2] = unchecked((byte)ParseI32(m22, "mode"));
                    WriteU32(baseBytes, 4, ParseU32(m22.Groups["duration"].Value));
                }
                break;
            case 23:
                if (TryMatch(statement, @"^CLEAR_LAYER\s+(?<target>\S+)$", out var m23))
                {
                    TryWriteLayerMode(baseBytes, 2, m23.Groups["target"].Value);
                }
                break;
            case 24:
                if (TryMatch(statement, @"^SET_STATE_27\s+(?<value>-?\d+)$", out var m24))
                {
                    WriteI32(baseBytes, 4, ParseI32(m24, "value"));
                }
                break;
            case 61:
                if (TryMatch(statement, @"^SET_STATE_BYTES\s+(?<a>\d+),\s*(?<b>\d+),\s*(?<c>\d+)$", out var m61))
                {
                    baseBytes[2] = checked((byte)ParseI32(m61, "a"));
                    baseBytes[3] = checked((byte)ParseI32(m61, "b"));
                    baseBytes[4] = checked((byte)ParseI32(m61, "c"));
                }
                break;
            case 74:
                if (TryMatch(statement, @"^RANDOM_VALUE\s+mod=(?<mod>-?\d+)\s+->\s+\w+\[(?<idx>-?\d+)\]$", out var m74))
                {
                    WriteI32(baseBytes, 4, ParseI32(m74, "mod"));
                    WriteU16(baseBytes, 8, unchecked((ushort)ParseI32(m74, "idx")));
                }
                break;
            case 82:
                if (TryMatch(statement, @"^PLAY_(?:WAVE|SOUND)\s+group=(?<group>\d+)\s+slot=(?<slot>\d+)\s+(?<sound>"".*"")$", out var m82))
                {
                    baseBytes[2] = checked((byte)ParseI32(m82, "group"));
                    baseBytes[3] = checked((byte)ParseI32(m82, "slot"));
                    return ReplaceStrings(baseBytes, rawBytes, [Unquote(m82.Groups["sound"].Value)]);
                }
                break;
            case 83:
                if (TryMatch(statement, @"^CLEAR_AUDIO\s+\S+\s+;\s+group=(?<group>\d+)\s+channel=(?<channel>\d+)$", out var m83))
                {
                    baseBytes[2] = checked((byte)ParseI32(m83, "group"));
                    baseBytes[3] = checked((byte)ParseI32(m83, "channel"));
                }
                break;
            case 84:
                if (TryMatch(statement, @"^EMIT_SYSTEM_EVENT\s+\S+\s+;\s+group=(?<group>\d+)\s+arg=(?<arg>\d+)$", out var m84))
                {
                    baseBytes[2] = checked((byte)ParseI32(m84, "group"));
                    baseBytes[3] = checked((byte)ParseI32(m84, "arg"));
                }
                break;
            case 87:
                if (TryMatch(statement, @"^SET_RETURN_VALUE\s+(?<value>-?\d+)$", out var m87))
                {
                    WriteI32(baseBytes, 4, ParseI32(m87, "value"));
                }
                break;
            case 88:
                if (TryMatch(statement, @"^SET_LAYER_COLOR_FILTER\s+(?<layer>\S+)\s+mode=(?<mode>-?\d+)\s+arg=(?<arg>0x[0-9A-Fa-f]+|\d+)$", out var m88))
                {
                    TryWriteLayerMode(baseBytes, 2, m88.Groups["layer"].Value);
                    baseBytes[3] = unchecked((byte)ParseI32(m88, "mode"));
                    WriteU24(baseBytes, 4, ParseU32(m88.Groups["arg"].Value));
                }
                break;
            case 89:
                ApplyScroll(statement, baseBytes, includeDuration: true);
                break;
            case 90:
                ApplyScroll(statement, baseBytes, includeDuration: false);
                break;
            case 91:
                if (TryMatch(statement, @"^COPY_VALUE\s+\w+\[(?<dst>\d+)\]\s+<-\s+\w+\[(?<src>\d+)\]$", out var m91))
                {
                    WriteU16(baseBytes, 4, ParseU16(m91, "dst"));
                    WriteU16(baseBytes, 6, ParseU16(m91, "src"));
                }
                break;
            case 94:
            case 112:
                if (TryMatch(statement, @"^\w+_SCRIPT\s+(?<script>"".*"")\s+(?:label_index|label)=(?<label>0x[0-9A-Fa-f]+|\d+)$", out var mj))
                {
                    WriteU32(baseBytes, 4, ParseU32(mj.Groups["label"].Value));
                    return ReplaceStrings(baseBytes, rawBytes, [Unquote(mj.Groups["script"].Value)]);
                }
                break;
            case 95:
                if (TryMatch(statement, @"^SET_WAIT_RESUME\s+(?<value>0x[0-9A-Fa-f]+|\d+)$", out var m95))
                {
                    WriteU24(baseBytes, 2, ParseU32(m95.Groups["value"].Value));
                }
                break;
            case 96:
                if (TryMatch(statement, @"^RESET_ADV_LAYERS\s+mode=(?<mode>0x[0-9A-Fa-f]+|\d+)$", out var m96))
                {
                    WriteU24(baseBytes, 2, ParseU32(m96.Groups["mode"].Value));
                }
                break;
            case 114:
                if (TryMatch(statement, @"^SET_SPRITE_POS\s+(?<layer>\S+)\s+x=(?<x>-?\d+)\s+y=(?<y>-?\d+)$", out var m114))
                {
                    TryWriteAdvSpLayer(baseBytes, 2, m114.Groups["layer"].Value);
                    WriteI32(baseBytes, 4, ParseI32(m114, "x"));
                    WriteI32(baseBytes, 8, ParseI32(m114, "y"));
                }
                break;
            case 115:
                if (TryMatch(statement, @"^SET_SPRITE_FRAME\s+(?<layer>\S+)\s+frame=(?<frame>-?\d+)$", out var m115))
                {
                    TryWriteAdvSpLayer(baseBytes, 2, m115.Groups["layer"].Value);
                    WriteI32(baseBytes, 4, ParseI32(m115, "frame"));
                }
                break;
            case 117:
            case 119:
            case 160:
            case 166:
            case 170:
            case 171:
                ApplySingleValue(statement, baseBytes);
                break;
            case 151:
                if (TryMatch(statement, @"^ENABLE_SPRITE_KEYDATA\s+(?<layer>\S+)\s+value=(?<value>-?\d+)$", out var m151))
                {
                    TryWriteAdvSpLayer(baseBytes, 2, m151.Groups["layer"].Value);
                    WriteI32(baseBytes, 4, ParseI32(m151, "value"));
                }
                break;
            case 121:
                return ApplySpriteBundle(statement, baseBytes, rawBytes);
            case 122:
            case >= 167 and <= 169:
                ApplyLayerOnly(statement, baseBytes);
                break;
            case 140:
            case 141:
                if (TryMatch(statement, @"^FADE_\w+_WAVE_LOOP\s+\S+\s+;\s+group=(?<group>\d+)\s+slot=(?<slot>\d+)$", out var ma))
                {
                    baseBytes[2] = checked((byte)ParseI32(ma, "group"));
                    baseBytes[3] = checked((byte)ParseI32(ma, "slot"));
                }
                break;
            case 142:
                if (TryMatch(statement, @"^WAIT_WAVE_SLOT\s+\S+\s+;\s+group=(?<group>\d+)\s+slot=(?<slot>\d+)$", out var mw))
                {
                    baseBytes[2] = checked((byte)ParseI32(mw, "group"));
                    baseBytes[3] = checked((byte)ParseI32(mw, "slot"));
                }
                break;
            case 137:
                if (TryMatch(statement, @"^TITLE\s+(?<title>"".*"")(?:\s+subtitle=(?<subtitle>"".*""))?$", out var mt))
                {
                    return ReplaceStrings(baseBytes, rawBytes, [
                        Unquote(mt.Groups["title"].Value),
                        mt.Groups["subtitle"].Success ? Unquote(mt.Groups["subtitle"].Value) : ""
                    ]);
                }
                break;
            case 143:
            case 144:
                return ApplyControlledSpriteBundle(statement, baseBytes, rawBytes, opcode == 144);
            case 145:
                if (TryMatch(statement, @"^RANGE\s+(?<name>"".*"")\s+(?<start>-?\d+)\.\.(?<end>-?\d+)$", out var m145))
                {
                    WriteI32(baseBytes, 4, ParseI32(m145, "start"));
                    WriteI32(baseBytes, 8, ParseI32(m145, "end"));
                    return ReplaceStrings(baseBytes, rawBytes, [Unquote(m145.Groups["name"].Value)]);
                }
                break;
            case 146:
            case 147:
                if (TryMatch(statement, @"^(?:(?:VOICE_GROUP_PREFIX|PENDING_TEXT_A)\s+slot=(?<slot>\d+)\s+|(?:VOICE_GROUP_ENTRY|APPEND_PENDING_TEXT_PAIR)\s+)(?<text>"".*"")$", out var mp))
                {
                    if (opcode == 146 && mp.Groups["slot"].Success)
                    {
                        baseBytes[2] = checked((byte)ParseI32(mp, "slot"));
                    }
                    return ReplaceStrings(baseBytes, rawBytes, [Unquote(mp.Groups["text"].Value)]);
                }
                break;
            case 150:
                if (TryMatch(statement, @"^ADD_SPRITE_KEYDATA\s+(?<layer>\S+)\s+(?<v0>-?\d+),\s*(?<v1>-?\d+),\s*(?<v2>0x[0-9A-Fa-f]+|\d+)$", out var m150))
                {
                    TryWriteAdvSpLayer(baseBytes, 2, m150.Groups["layer"].Value);
                    WriteI32(baseBytes, 4, ParseI32(m150, "v0"));
                    WriteI32(baseBytes, 8, ParseI32(m150, "v1"));
                    WriteU32(baseBytes, 12, ParseU32(m150.Groups["v2"].Value));
                }
                break;
            case 152:
            case 154:
                if (TryMatch(statement, @"^SET_MESSAGE_COLOR[01]\s+(?<rgb>0x[0-9A-Fa-f]+|\d+)$", out var mcolor))
                {
                    WriteU24(baseBytes, 4, ParseU32(mcolor.Groups["rgb"].Value));
                }
                break;
            case 153:
                if (TryMatch(statement, @"^SET_MESSAGE_COLOR_MODE\s+(?<mode>\d+)$", out var m153))
                {
                    baseBytes[3] = checked((byte)ParseI32(m153, "mode"));
                }
                break;
            case 155:
                if (TryMatch(statement, @"^INIT_RESOURCE_OBJECT\s+(?<object>"".*"")\s+arg=(?<arg>"".*"")$", out var m155))
                {
                    return ReplaceStrings(baseBytes, rawBytes, [Unquote(m155.Groups["object"].Value), Unquote(m155.Groups["arg"].Value)]);
                }
                break;
            case >= 156 and <= 166:
                return ApplyResourceObject(statement, baseBytes, rawBytes, opcode);
        }

        return Rebuild(baseBytes, rawBytes[baseLength..]);
    }

    private static IEnumerable<string> ReadLogicalLines(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static byte[] ParseHexBytes(string value)
    {
        value = value.Replace(" ", "", StringComparison.Ordinal);
        if (value.Length == 0 || value.Length % 2 != 0)
        {
            throw new InvalidDataException($"Invalid TBLSTR HLS bytes field: {value}");
        }

        var bytes = new byte[value.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(value.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    private static string RenderCanonicalStatement(byte[] rawBytes)
    {
        var document = new TblstrScrDocument
        {
            SourceName = "",
            Magic0 = TblstrScrCodec.Magic0,
            Magic1 = TblstrScrCodec.Magic1,
            PayloadSize = rawBytes.Length,
            Payload = rawBytes
        };
        document = new TblstrScrCodec().Read(TblstrScrCodec.WriteRaw(document));
        var text = new TblstrScrTextFormatter().WriteHls(document);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var marker = line.IndexOf(BytesMarker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                return line[..marker].Trim();
            }

            var trimmed = line.Trim();
            if (trimmed.Length > 0 &&
                !trimmed.StartsWith(".", StringComparison.Ordinal) &&
                !trimmed.EndsWith(":", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return "";
    }

    private static bool SameStatement(string left, string right) =>
        NormalizeStatement(left) == NormalizeStatement(right);

    private static string NormalizeStatement(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^JUMP_SCRIPT\s+("".*"")\s+label=""""$",
            "JUMP_SCRIPT_START $1",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^((?:JUMP|CALL)_SCRIPT\s+"".*"")\s+label=(0x[0-9A-Fa-f]+|\d+)$",
            "$1 label_index=$2",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^SHOW_TEXT\s+("".*"")$",
            "SET_DISPLAY_NAME $1",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^SET_STATE_424\s+(-?\d+)$",
            "SET_ADV_VIEW_SPRITE_INDEX $1",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^RESTORE_SAVED_PC$",
            "WAIT_WAVE_SLOT",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^STOP_OR_PAUSE$",
            "STOP_SCRIPT",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^PLAY_SOUND\s+",
            "PLAY_WAVE ",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^(PLAY_BGM\s+"".*"")\s+format=ogg\s+mode=(\d+)$",
            "$1 mode=$2",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^(PLAY_BGM\s+"".*"")(?:\s+format=ogg)?\s+mode=0$",
            "$1",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^(PLAY_BGM\s+"".*"")\s+format=ogg$",
            "$1",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^PENDING_TEXT_A\s+slot=(\d+)\s+("".*"")$",
            "VOICE_GROUP_PREFIX slot=$1 $2",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^APPEND_PENDING_TEXT_PAIR\s+("".*"")$",
            "VOICE_GROUP_ENTRY $1",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^WAIT_WAVE_SLOT\s+\S+\s+;\s+group=\d+\s+slot=\d+$",
            "WAIT_WAVE_SLOT",
            RegexOptions.CultureInvariant);
        return normalized;
    }

    private byte[] ApplyMessage(string statement, byte[] baseBytes, byte[] rawBytes)
    {
        if (!TryMatch(statement, @"^MESSAGE\s+speaker=(?<speaker>-?\d+)\s+text=(?<text>-?\d+)(?:\s+voice=(?<voice>"".*""))?(?:\s+alt=(?<alt>-?\d+))?$", out var match))
        {
            return Rebuild(baseBytes, rawBytes[baseBytes.Length..]);
        }

        WriteI32(baseBytes, 4, ParseI32(match, "speaker"));
        WriteI32(baseBytes, 8, ParseI32(match, "text"));
        if (baseBytes.Length >= 16 && match.Groups["alt"].Success)
        {
            WriteI32(baseBytes, 12, ParseI32(match, "alt"));
        }

        return match.Groups["voice"].Success
            ? ReplaceStrings(baseBytes, rawBytes, [Unquote(match.Groups["voice"].Value)])
            : Rebuild(baseBytes, rawBytes[baseBytes.Length..]);
    }

    private void ApplyScroll(string statement, byte[] baseBytes, bool includeDuration)
    {
        var pattern = includeDuration
            ? @"^SET_SCROLL\s+(?<target>\S+)\s+x=(?<x>-?\d+)\s+y=(?<y>-?\d+)\s+duration=(?<duration>-?\d+)$"
            : @"^APPLY_SCROLL\s+(?<target>\S+)\s+x=(?<x>-?\d+)\s+y=(?<y>-?\d+)$";
        if (!TryMatch(statement, pattern, out var match))
        {
            return;
        }

        TryWriteLayerMode(baseBytes, 4, match.Groups["target"].Value);
        WriteU16(baseBytes, 8, unchecked((ushort)ParseI32(match, "x")));
        WriteU16(baseBytes, 10, unchecked((ushort)ParseI32(match, "y")));
        if (includeDuration)
        {
            WriteI32(baseBytes, 12, ParseI32(match, "duration"));
        }
    }

    private void ApplySingleValue(string statement, byte[] baseBytes)
    {
        if (TryMatch(statement, @"^(?:SET_ADV_VIEW_SPRITE_INDEX|SET_STATE_424|SET_RUN_STATE|ENABLE_SPRITE_KEYDATA|ENABLE_OBJECT_KEYFRAMES|SET_OBJECT_ALPHA|ANM_WAITCOUNT|ANM_SPEED)(?:\s+\S+)?\s+(?:value=|alpha=)?(?<value>-?\d+)$", out var match))
        {
            WriteI32(baseBytes, 4, ParseI32(match, "value"));
        }
    }

    private byte[] ApplySpriteBundle(string statement, byte[] baseBytes, byte[] rawBytes)
    {
        if (!TryMatch(statement, @"^LOAD_SPRITE\s+(?<layer>\S+)\s+object=(?<object>"".*"")\s+pattern=(?<pattern>"".*"")(?:\s+arg2=(?<arg2>"".*""))?(?:\s+arg3=(?<arg3>"".*""))?$", out var match))
        {
            return Rebuild(baseBytes, []);
        }

        TryWriteAdvSpLayer(baseBytes, 2, match.Groups["layer"].Value);
        return ReplaceStrings(baseBytes, rawBytes, [
            Unquote(match.Groups["object"].Value),
            Unquote(match.Groups["pattern"].Value),
            match.Groups["arg2"].Success ? Unquote(match.Groups["arg2"].Value) : "",
            match.Groups["arg3"].Success ? Unquote(match.Groups["arg3"].Value) : ""
        ]);
    }

    private byte[] ApplyControlledSpriteBundle(string statement, byte[] baseBytes, byte[] rawBytes, bool includeSecondaryControl)
    {
        if (!TryMatch(statement, @"^LOAD_SPRITE_CONTROLLED(?:_EX)?\s+(?<layer>\S+)\s+object=(?<object>"".*"")\s+pattern=(?<pattern>"".*"")(?:\s+arg2=(?<arg2>"".*""))?(?:\s+arg3=(?<arg3>"".*""))?\s+control0=(?<c0>-?\d+)\s+control1=(?<c1>-?\d+)(?:\s+secondary_control=(?<c2>-?\d+))?$", out var match))
        {
            return Rebuild(baseBytes, []);
        }

        TryWriteAdvSpLayer(baseBytes, 2, match.Groups["layer"].Value);
        if (baseBytes.Length >= 28)
        {
            WriteI32(baseBytes, 20, ParseI32(match, "c0"));
            WriteI32(baseBytes, 24, ParseI32(match, "c1"));
        }

        if (includeSecondaryControl && baseBytes.Length >= 32 && match.Groups["c2"].Success)
        {
            WriteI32(baseBytes, 28, ParseI32(match, "c2"));
        }

        return ReplaceStrings(baseBytes, rawBytes, [
            Unquote(match.Groups["object"].Value),
            Unquote(match.Groups["pattern"].Value),
            match.Groups["arg2"].Success ? Unquote(match.Groups["arg2"].Value) : "",
            match.Groups["arg3"].Success ? Unquote(match.Groups["arg3"].Value) : ""
        ]);
    }

    private void ApplyLayerOnly(string statement, byte[] baseBytes)
    {
        if (TryMatch(statement, @"^(?:CLEAR_LAYER_COLOR_FILTER|ANM_PAUSE|ANM_START|ANM_RESTART)\s+(?<target>\S+)$", out var match))
        {
            TryWriteLayerMode(baseBytes, 2, match.Groups["target"].Value);
        }
    }

    private byte[] ApplyResourceObject(string statement, byte[] baseBytes, byte[] rawBytes, int opcode)
    {
        if (!TryMatch(statement, @"^(?<mnemonic>\w+)(?:\s+(?<object>"".*""))?(?:\s+object=(?<object2>"".*""))?(?:\s+x=(?<x>-?\d+))?(?:\s+y=(?<y>-?\d+))?(?:\s+frame=(?<frame>-?\d+))?(?:\s+key=(?<key>-?\d+))?(?:\s+anm=(?<anm>-?\d+))?(?:\s+alpha=(?<alpha>-?\d+))?$", out var match))
        {
            return Rebuild(baseBytes, []);
        }

        var objectText = match.Groups["object"].Success
            ? Unquote(match.Groups["object"].Value)
            : match.Groups["object2"].Success ? Unquote(match.Groups["object2"].Value) : null;

        switch (opcode)
        {
            case 156:
                if (match.Groups["x"].Success) WriteI32(baseBytes, 4, ParseI32(match, "x"));
                if (match.Groups["y"].Success) WriteI32(baseBytes, 8, ParseI32(match, "y"));
                break;
            case 157:
                if (match.Groups["frame"].Success) WriteI32(baseBytes, 4, ParseI32(match, "frame"));
                break;
            case 159:
                if (match.Groups["key"].Success) WriteI32(baseBytes, 4, ParseI32(match, "key"));
                if (match.Groups["x"].Success) WriteI32(baseBytes, 8, ParseI32(match, "x"));
                if (match.Groups["y"].Success) WriteI32(baseBytes, 12, ParseI32(match, "y"));
                break;
            case 161:
                if (match.Groups["anm"].Success) baseBytes[3] = checked((byte)ParseI32(match, "anm"));
                break;
            case 164:
                if (match.Groups["key"].Success) WriteI32(baseBytes, 4, ParseI32(match, "key"));
                if (match.Groups["anm"].Success) WriteI32(baseBytes, 8, ParseI32(match, "anm"));
                break;
            case 165:
                if (match.Groups["key"].Success) WriteI32(baseBytes, 4, ParseI32(match, "key"));
                if (match.Groups["alpha"].Success) WriteI32(baseBytes, 8, ParseI32(match, "alpha"));
                break;
        }

        return objectText is null ? Rebuild(baseBytes, []) : ReplaceStrings(baseBytes, rawBytes, [objectText]);
    }

    private byte[] ReplaceStrings(byte[] baseBytes, byte[] originalRawBytes, IReadOnlyList<string> values)
    {
        var resultBase = baseBytes.ToArray();
        var extra = new List<byte>();
        var offsets = TblstrScrOpcodeTable.GetInlineStringLengthOffsets(resultBase[0], resultBase);
        var originalBaseLength = originalRawBytes.Length >= 2 ? originalRawBytes[1] : 0;
        var originalExtraOffset = originalBaseLength <= originalRawBytes.Length ? originalBaseLength : originalRawBytes.Length;
        var originalExtraCursor = originalExtraOffset;

        for (var i = 0; i < offsets.Length; i++)
        {
            var text = i < values.Count ? values[i] : "";
            var originalLength = offsets[i] < originalRawBytes.Length ? originalRawBytes[offsets[i]] : 0;
            var originalEncoded = originalExtraCursor + originalLength <= originalRawBytes.Length
                ? originalRawBytes.AsSpan(originalExtraCursor, originalLength).ToArray()
                : [];
            originalExtraCursor += originalLength;

            var originalText = TblstrScrText.DecodeInlineString(originalEncoded, _encoding);
            var encoded = text == originalText ? originalEncoded : EncodeInlineString(text);
            if (encoded.Length > 255)
            {
                throw new InvalidDataException($"Inline string is too long for opcode {resultBase[0]}: {encoded.Length}");
            }

            resultBase[offsets[i]] = checked((byte)encoded.Length);
            extra.AddRange(encoded);
        }

        return Rebuild(resultBase, extra.ToArray());
    }

    private byte[] EncodeInlineString(string text)
    {
        var bytes = _encoding.GetBytes(text);
        var paddedLength = ((bytes.Length + 1 + 3) / 4) * 4;
        var result = new byte[paddedLength];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = 0xFF;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            result[i] = unchecked((byte)~bytes[i]);
        }

        return result;
    }

    private static byte[] Rebuild(byte[] baseBytes, ReadOnlySpan<byte> extra)
    {
        var result = new byte[baseBytes.Length + extra.Length];
        baseBytes.CopyTo(result, 0);
        extra.CopyTo(result.AsSpan(baseBytes.Length));
        return result;
    }

    private static string ExtractQuoted(string statement)
    {
        var start = statement.IndexOf('"');
        if (start < 0)
        {
            return "";
        }

        return Unquote(statement[start..]);
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length < 2 || value[0] != '"')
        {
            return value;
        }

        var builder = new StringBuilder();
        var escaped = false;
        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (escaped)
            {
                builder.Append(ch switch
                {
                    'r' => '\r',
                    'n' => '\n',
                    't' => '\t',
                    _ => ch
                });
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                break;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool TryMatch(string text, string pattern, out Match match)
    {
        match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        return match.Success;
    }

    private static int ParseI32(Match match, string group) =>
        int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static ushort ParseU16(Match match, string group) =>
        checked((ushort)ParseI32(match, group));

    private static uint ParseU32(string value)
    {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return uint.Parse(value, CultureInfo.InvariantCulture);
    }

    private static byte ParseBgmFormatFromStatement(string statement)
    {
        if (TryMatch(statement, @"(?:^|\s)format=(?<format>[A-Za-z0-9_]+)(?:\s|$)", out var match))
        {
            return ParseBgmFormat(match.Groups["format"].Value);
        }

        return 2;
    }

    private static byte ParseBgmFormat(string value)
    {
        var lower = value.ToLowerInvariant();
        if (lower.StartsWith("unknown_", StringComparison.Ordinal) &&
            byte.TryParse(lower["unknown_".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unknownCode))
        {
            return unknownCode;
        }

        return lower switch
        {
            "mid" or "midi" => 1,
            "ogg" => 2,
            "cdda" => 0,
            _ => checked((byte)uint.Parse(value, CultureInfo.InvariantCulture))
        };
    }

    private static void WriteI32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static void WriteU16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteU24(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = checked((byte)(value & 0xFF));
        bytes[offset + 1] = checked((byte)((value >> 8) & 0xFF));
        bytes[offset + 2] = checked((byte)((value >> 16) & 0xFF));
    }

    private static void TryWriteLayerMode(byte[] bytes, int offset, string name)
    {
        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            WriteLayerValue(bytes, offset, numeric);
            return;
        }

        var mode = name switch
        {
            "adv_back_special" => 2,
            "adv_event" => 4,
            "transition_name" => 5,
            "adv_sp1" => 7,
            "adv_sp2" => 8,
            "adv_sp3" => 9,
            "adv_sp4" => 10,
            "adv_sp5" => 11,
            _ => (int?)null
        };
        if (mode is int value)
        {
            WriteLayerValue(bytes, offset, value);
        }
    }

    private static void TryWriteAdvSpLayer(byte[] bytes, int offset, string name)
    {
        var slot = name switch
        {
            "adv_sp1" => 7,
            "adv_sp2" => 8,
            "adv_sp3" => 9,
            "adv_sp4" => 10,
            "adv_sp5" => 11,
            _ => (int?)null
        };
        if (slot is int value)
        {
            bytes[offset] = checked((byte)value);
        }
    }

    private static void WriteLayerValue(byte[] bytes, int offset, int value)
    {
        if (bytes.Length - offset >= 4 && offset != 2 && offset != 3)
        {
            WriteI32(bytes, offset, value);
        }
        else
        {
            bytes[offset] = unchecked((byte)value);
        }
    }
}
