// ============================================================================
// ParamsDatCodec.cs
// params.dat 二进制序列化/反序列化编解码器 (版本 [SCR-PARAMS]v02/v03/v04/v05, v05.1, v05.3 ~ v05.8)
//
// 读取流程 (Read):
//   1. 验证 ASCII 头 "[SCR-PARAMS]v05.x"
//   2. ReadGameSystem  -- 解析游戏系统配置区段
//      - 基本信息: VersionMarker, Width, Height, ConfigBytes
//      - 字符串字段: GameTitle, DisplayTitle, Brand, StaffName1/2
//      - InstallTable: u8 计数 -> (File, Media) 对
//      - v05.1: 3xu32 + legacy voice/byte/sound groups
//      - v05.3+: V5Scalars (4xu32) + V5TailByte (v5.5+)
//      - SettingTags: 3 个可选树形标签 (递归 ReadSettingTag)
//      - RawBlob: LINK6 XOR key, stored as u32 length + raw key bytes
//      - Demos: [Demo3.0] 命令流 (ReadDemoData -> ReadDemoCommand, 10 种类型)
//      - V51StringList, V51PlaceCount, V54NestedList
//      - Thumbnails: 每组 11 个 TypedValue (8 string + 3 int)
//      - SceneNames, RegistCg, RegistScene (TypedValue 序列化)
//   3. ReadPattern     -- 解析 CG/立绘资源引用
//      - Items: u32 计数 -> (Name, Kind, 按 Kind 的附加字段)
//      - IntArrays: u32 计数 -> (u8 长度 -> u32[])
//      - GroupTable1/2: u32 组数 -> (Name, u16 索引数 -> u32[])
//   4. ReadSceneLabels -- u32 计数 -> (Name, Value1, Value2)
//   5. 验证无尾部字节
//
// 写入流程 (Write): 与读取完全对称, 所有计数使用 Checked* 溢出检查
//
// 内部工具:
//   ParamsBinaryReader -- 低级小端读取器 (U8/U16/U32/String16/TypedValue)
//   ParamsBinaryWriter -- 低级小端写入器
//   Hex                -- 十六进制编解码工具
//
// 字符串编码: UTF-16LE (String16: u16 字节长 + payload)
//              ASCII (头部 / Demo 魔数)
// 字节序: 全部 Little-Endian
//
// 依赖: ParamsModels (数据模型)
// 被依赖: ParamsFormatModule, LinkArchiveCodec (加密密钥提取)
// ============================================================================
using System.Buffers.Binary;
using System.Text;

namespace Kaguya_YaneKit.Formats.Params;

public sealed class ParamsDatCodec
{
    public const string ExpectedHeader = "[SCR-PARAMS]v05.8";
    public const string V02Header = "[SCR-PARAMS]v02";
    public const string V03Header = "[SCR-PARAMS]v03";
    public const string V04Header = "[SCR-PARAMS]v04";
    public const string V50Header = "[SCR-PARAMS]v05";
    public const string V51Header = "[SCR-PARAMS]v05.1";
    public const string V53Header = "[SCR-PARAMS]v05.3";
    public const string V54Header = "[SCR-PARAMS]v05.4";
    public const string V55Header = "[SCR-PARAMS]v05.5";
    public const string V56Header = "[SCR-PARAMS]v05.6";
    public const string V57Header = "[SCR-PARAMS]v05.7";
    public const string V58Header = ExpectedHeader;
    private const int HeaderLength = 17;
    private const int V50HeaderLength = 15;

    private static readonly Encoding Utf16Le = Encoding.Unicode;
    private readonly Encoding _legacyReadEncoding;
    private readonly Encoding _legacyWriteEncoding;

    public ParamsDatCodec(string? legacyReadEncoding = null, string? legacyWriteEncoding = null)
    {
        _legacyReadEncoding = CreateLegacyEncoding(legacyReadEncoding);
        _legacyWriteEncoding = CreateLegacyEncoding(legacyWriteEncoding ?? legacyReadEncoding);
    }

    public ParamsDatDocument Read(byte[] data)
    {
        var reader = new ParamsBinaryReader(data);
        var header = ReadHeader(reader, data);
        if (!IsSupportedHeader(header))
        {
            throw new InvalidDataException($"Unsupported params.dat header: {header}");
        }

        if (IsV04(header))
        {
            return ReadV04Document(reader, header);
        }

        ParamsGameSystem gameSystem;
        try
        {
            reader.Context = "GameSystem";
            gameSystem = IsV51(header) ? ReadGameSystemV51(reader, header) : ReadGameSystem(reader, header);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException($"Failed to read GameSystem at 0x{reader.Offset:X}: {ex.Message}", ex);
        }

        ParamsPattern pattern;
        try
        {
            reader.Context = "Pattern";
            pattern = ReadPattern(reader, header);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException($"Failed to read Pattern at 0x{reader.Offset:X}: {ex.Message}", ex);
        }

        List<ParamsSceneLabel> sceneLabels;
        try
        {
            reader.Context = "SceneLabels";
            sceneLabels = ReadSceneLabels(reader);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException($"Failed to read SceneLabels at 0x{reader.Offset:X}: {ex.Message}", ex);
        }

        var document = new ParamsDatDocument
        {
            Header = header,
            GameSystem = gameSystem,
            Pattern = pattern,
            SceneLabels = sceneLabels
        };

        if (!reader.End)
        {
            throw new InvalidDataException($"Trailing params.dat bytes at 0x{reader.Offset:X}: {reader.Remaining} bytes.");
        }

        return document;
    }

    public byte[] Write(ParamsDatDocument document)
    {
        if (!IsSupportedHeader(document.Header))
        {
            throw new InvalidDataException($"Unsupported params.dat header: {document.Header}");
        }

        var writer = new ParamsBinaryWriter();
        writer.WriteAscii(document.Header);
        if (IsV04(document.Header))
        {
            if (IsV02(document.Header))
            {
                WriteV02GameSystem(writer, document.GameSystem);
            }
            else
            {
                WriteV04GameSystem(writer, document.GameSystem);
            }
            if (IsV02(document.Header) || IsV03(document.Header))
            {
                WriteV03Pattern(writer, document.Pattern);
            }
            else
            {
                WriteV04Pattern(writer, document.Pattern);
            }
            WriteV04SceneLabels(writer, document.SceneLabels, document.V04SceneLabelXorKey ?? 0);
            return writer.ToArray();
        }

        if (IsV51(document.Header))
        {
            WriteGameSystemV51(writer, document.Header, document.GameSystem);
        }
        else
        {
            WriteGameSystem(writer, document.Header, document.GameSystem);
        }
        WritePattern(writer, document.Header, document.Pattern);
        WriteSceneLabels(writer, document.SceneLabels);
        return writer.ToArray();
    }

    private static bool IsSupportedHeader(string header) =>
        string.Equals(header, V02Header, StringComparison.Ordinal) ||
        string.Equals(header, V04Header, StringComparison.Ordinal) ||
        string.Equals(header, V03Header, StringComparison.Ordinal) ||
        string.Equals(header, V51Header, StringComparison.Ordinal) ||
        string.Equals(header, V50Header, StringComparison.Ordinal) ||
        string.Equals(header, V54Header, StringComparison.Ordinal) ||
        string.Equals(header, V53Header, StringComparison.Ordinal) ||
        string.Equals(header, V55Header, StringComparison.Ordinal) ||
        string.Equals(header, V56Header, StringComparison.Ordinal) ||
        string.Equals(header, V57Header, StringComparison.Ordinal) ||
        string.Equals(header, V58Header, StringComparison.Ordinal);

    private static string ReadHeader(ParamsBinaryReader reader, byte[] data)
    {
        if (data.Length >= V50HeaderLength)
        {
            var shortHeader = Encoding.ASCII.GetString(data, 0, V50HeaderLength);
            if (shortHeader == V02Header || shortHeader == V03Header || shortHeader == V04Header)
            {
                return reader.ReadAscii(V50HeaderLength);
            }

            // v05 is the only 15-byte v05 header. v05.1/v05.3+ continue with
            // ".x", while v05's next two bytes are the GameSystem version marker.
            if (shortHeader == V50Header &&
                data.Length >= HeaderLength &&
                data[V50HeaderLength] == 0 &&
                data[V50HeaderLength + 1] == 0)
            {
                return reader.ReadAscii(V50HeaderLength);
            }
        }

        return reader.ReadAscii(HeaderLength);
    }

    public static string DescribeVersion(string header) =>
        header.TrimEnd('\0').StartsWith("[SCR-PARAMS]v", StringComparison.Ordinal) &&
        header.TrimEnd('\0').Length > "[SCR-PARAMS]v".Length
            ? header.TrimEnd('\0')["[SCR-PARAMS]v".Length..]
            : header.TrimEnd('\0');

    private static bool IsV51(string header) =>
        string.Equals(header, V50Header, StringComparison.Ordinal) ||
        string.Equals(header, V51Header, StringComparison.Ordinal);
    private static bool IsV02(string header) => string.Equals(header, V02Header, StringComparison.Ordinal);
    private static bool IsV03(string header) => string.Equals(header, V03Header, StringComparison.Ordinal);
    private static bool IsV04(string header) =>
        string.Equals(header, V02Header, StringComparison.Ordinal) ||
        string.Equals(header, V03Header, StringComparison.Ordinal) ||
        string.Equals(header, V04Header, StringComparison.Ordinal);
    private static bool IsV50(string header) => string.Equals(header, V50Header, StringComparison.Ordinal);
    private static bool IsV53(string header) => string.Equals(header, V53Header, StringComparison.Ordinal);
    private static bool IsBeforeV55(string header) =>
        string.Equals(header, V50Header, StringComparison.Ordinal) ||
        string.Equals(header, V51Header, StringComparison.Ordinal) ||
        string.Equals(header, V53Header, StringComparison.Ordinal) ||
        string.Equals(header, V54Header, StringComparison.Ordinal);
    private static bool IsV53OrV54OrV55OrV56(string header) =>
        string.Equals(header, V50Header, StringComparison.Ordinal) ||
        string.Equals(header, V51Header, StringComparison.Ordinal) ||
        string.Equals(header, V53Header, StringComparison.Ordinal) ||
        string.Equals(header, V54Header, StringComparison.Ordinal) ||
        string.Equals(header, V55Header, StringComparison.Ordinal) ||
        string.Equals(header, V56Header, StringComparison.Ordinal);
    private static bool IsV53OrV54OrV55(string header) =>
        string.Equals(header, V50Header, StringComparison.Ordinal) ||
        string.Equals(header, V51Header, StringComparison.Ordinal) ||
        string.Equals(header, V53Header, StringComparison.Ordinal) ||
        string.Equals(header, V54Header, StringComparison.Ordinal) ||
        string.Equals(header, V55Header, StringComparison.Ordinal);
    private static bool IsLegacyRegistScene(string header) =>
        IsV53OrV54OrV55OrV56(header) || string.Equals(header, V57Header, StringComparison.Ordinal);

    private static Encoding CreateLegacyEncoding(string? encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        return encodingName.Trim().ToLowerInvariant() switch
        {
            "cp932" or "sjis" or "shift-jis" or "shift_jis" => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "cp936" or "gbk" => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "utf8" or "utf-8" => Encoding.GetEncoding(65001, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => int.TryParse(encodingName, out var codePage)
                ? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                : Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        };
    }

    private ParamsDatDocument ReadV04Document(ParamsBinaryReader reader, string header)
    {
        ParamsGameSystem gameSystem;
        try
        {
            reader.Context = "GameSystem(v04)";
            gameSystem = IsV02(header) ? ReadV02GameSystem(reader) : ReadV04GameSystem(reader);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException($"Failed to read v04 GameSystem at 0x{reader.Offset:X}: {ex.Message}", ex);
        }

        ParamsPattern pattern;
        try
        {
            reader.Context = "Pattern(v04)";
            pattern = IsV02(header) || IsV03(header) ? ReadV03Pattern(reader) : ReadV04Pattern(reader);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException($"Failed to read v04 Pattern at 0x{reader.Offset:X}: {ex.Message}", ex);
        }

        List<ParamsSceneLabel> sceneLabels;
        byte sceneLabelKey;
        try
        {
            reader.Context = "SceneLabels(v04)";
            sceneLabelKey = reader.ReadU8();
            sceneLabels = ReadV04SceneLabels(reader, sceneLabelKey);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException($"Failed to read v04 SceneLabels at 0x{reader.Offset:X}: {ex.Message}", ex);
        }

        if (!reader.End)
        {
            throw new InvalidDataException($"Trailing params.dat bytes at 0x{reader.Offset:X}: {reader.Remaining} bytes.");
        }

        return new ParamsDatDocument
        {
            Header = header,
            LegacyReadEncoding = _legacyReadEncoding.WebName,
            LegacyWriteEncoding = _legacyWriteEncoding.WebName,
            GameSystem = gameSystem,
            Pattern = pattern,
            SceneLabels = sceneLabels,
            V04SceneLabelXorKey = sceneLabelKey
        };
    }

    private ParamsGameSystem ReadV04GameSystem(ParamsBinaryReader reader)
    {
        var result = new ParamsGameSystem
        {
            VersionMarker = reader.ReadU16(),
            Width = reader.ReadU32(),
            Height = reader.ReadU32()
        };

        result.ConfigBytes = reader.ReadBytes(reader.ReadU8()).ToList();
        result.GameTitle = reader.ReadLegacyString(encoding: _legacyReadEncoding);
        result.DisplayTitle = reader.ReadLegacyString(encoding: _legacyReadEncoding);
        result.Brand = reader.ReadLegacyString(encoding: _legacyReadEncoding);
        result.StaffFlag = reader.ReadU8();
        result.StaffName1 = reader.ReadLegacyString(encoding: _legacyReadEncoding);
        result.StaffName2 = reader.ReadLegacyString(encoding: _legacyReadEncoding);

        var installCount = reader.ReadU8();
        for (var i = 0; i < installCount; i++)
        {
            result.InstallTable.Add(new ParamsInstallEntry
            {
                File = reader.ReadLegacyString(),
                Media = reader.ReadLegacyString()
            });
        }

        result.V5Scalars = [reader.ReadU32(), reader.ReadU32(), reader.ReadU32()];
        var xorKey = reader.ReadU8();
        result.V04XorKey = xorKey;

        var voiceCount = reader.ReadU8();
        for (var i = 0; i < voiceCount; i++)
        {
            result.V51VoiceEntries.Add(new ParamsV51VoiceEntry
            {
                Flag = reader.ReadU8(),
                Name = reader.ReadLegacyString(xorKey),
                Primary = ReadV04StringList8(reader, xorKey),
                Secondary = ReadV04StringList8(reader, null)
            });
        }

        var byteGroupCount = reader.ReadU8();
        for (var i = 0; i < byteGroupCount; i++)
        {
            result.V51ByteGroups.Add(new ParamsV51ByteGroup
            {
                Name = reader.ReadLegacyString(xorKey),
                Values = reader.ReadBytes(reader.ReadU8()).ToList()
            });
        }

        var soundGroupCount = reader.ReadU8();
        for (var i = 0; i < soundGroupCount; i++)
        {
            result.V51SoundGroups.Add(new ParamsV51SoundGroup
            {
                Name = reader.ReadLegacyString(xorKey),
                Primary = ReadV04StringList8(reader, xorKey),
                Secondary = ReadV04StringList8(reader, null)
            });
        }

        var rawBlob = reader.ReadBytes(CheckedInt(reader.ReadU32(), "v04 raw blob length"));
        result.RawBlob = new ParamsRawBlob
        {
            ExpectedWidth = result.Width,
            ExpectedHeight = result.Height,
            ExpectedBytesPerPixel = CalculateBytesPerPixel(rawBlob.Length, result.Width, result.Height),
            KeyByteLength = rawBlob.Length,
            LinkXorKeyBase64 = Convert.ToBase64String(rawBlob)
        };

        return result;
    }

    private ParamsGameSystem ReadV02GameSystem(ParamsBinaryReader reader)
    {
        var result = new ParamsGameSystem
        {
            VersionMarker = 0,
            Width = reader.ReadU32(),
            Height = reader.ReadU32()
        };

        result.ConfigBytes = reader.ReadBytes(reader.ReadU8()).ToList();
        result.GameTitle = reader.ReadLegacyString(encoding: _legacyReadEncoding);
        result.DisplayTitle = reader.ReadLegacyString(encoding: _legacyReadEncoding);
        result.Brand = reader.ReadLegacyString(encoding: _legacyReadEncoding);
        result.V02Copyright = reader.ReadLegacyString();
        result.StaffFlag = reader.ReadU8();
        result.StaffName1 = reader.ReadLegacyString(encoding: _legacyReadEncoding);
        result.StaffName2 = reader.ReadLegacyString(encoding: _legacyReadEncoding);

        var installCount = reader.ReadU8();
        for (var i = 0; i < installCount; i++)
        {
            result.InstallTable.Add(new ParamsInstallEntry
            {
                File = reader.ReadLegacyString(),
                Media = reader.ReadLegacyString()
            });
        }

        var xorKey = reader.ReadU8();
        result.V04XorKey = xorKey;

        var voiceCount = reader.ReadU8();
        for (var i = 0; i < voiceCount; i++)
        {
            result.V51VoiceEntries.Add(new ParamsV51VoiceEntry
            {
                Flag = reader.ReadU8(),
                Name = reader.ReadLegacyString(xorKey),
                Primary = ReadV04StringList8(reader, xorKey),
                Secondary = ReadV04StringList8(reader, null)
            });
        }

        var byteGroupCount = reader.ReadU8();
        for (var i = 0; i < byteGroupCount; i++)
        {
            result.V51ByteGroups.Add(new ParamsV51ByteGroup
            {
                Name = reader.ReadLegacyString(xorKey),
                Values = reader.ReadBytes(reader.ReadU8()).ToList()
            });
        }

        var soundGroupCount = reader.ReadU8();
        for (var i = 0; i < soundGroupCount; i++)
        {
            result.V51SoundGroups.Add(new ParamsV51SoundGroup
            {
                Name = reader.ReadLegacyString(xorKey),
                Primary = ReadV04StringList8(reader, xorKey),
                Secondary = ReadV04StringList8(reader, null)
            });
        }

        var rawBlob = reader.ReadBytes(CheckedInt(reader.ReadU32(), "v02 raw blob length"));
        result.RawBlob = new ParamsRawBlob
        {
            ExpectedWidth = result.Width,
            ExpectedHeight = result.Height,
            ExpectedBytesPerPixel = CalculateBytesPerPixel(rawBlob.Length, result.Width, result.Height),
            KeyByteLength = rawBlob.Length,
            LinkXorKeyBase64 = Convert.ToBase64String(rawBlob)
        };

        return result;
    }

    private static List<string> ReadV04StringList8(ParamsBinaryReader reader, byte? xorKey)
    {
        var count = reader.ReadU8();
        var values = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add(reader.ReadLegacyString(xorKey));
        }

        return values;
    }

    private void WriteV04GameSystem(ParamsBinaryWriter writer, ParamsGameSystem value)
    {
        writer.WriteU16(value.VersionMarker);
        writer.WriteU32(value.Width);
        writer.WriteU32(value.Height);
        writer.WriteU8(CheckedByte(value.ConfigBytes.Count, "v04 config byte count"));
        writer.WriteBytes(value.ConfigBytes.ToArray());
        writer.WriteLegacyString(value.GameTitle, encoding: _legacyWriteEncoding);
        writer.WriteLegacyString(value.DisplayTitle, encoding: _legacyWriteEncoding);
        writer.WriteLegacyString(value.Brand, encoding: _legacyWriteEncoding);
        writer.WriteU8(value.StaffFlag);
        writer.WriteLegacyString(value.StaffName1, encoding: _legacyWriteEncoding);
        writer.WriteLegacyString(value.StaffName2, encoding: _legacyWriteEncoding);

        writer.WriteU8(CheckedByte(value.InstallTable.Count, "v04 install count"));
        foreach (var entry in value.InstallTable)
        {
            writer.WriteLegacyString(entry.File);
            writer.WriteLegacyString(entry.Media);
        }

        if (value.V5Scalars.Length != 3)
        {
            throw new InvalidDataException("v04 V5Scalars must contain exactly three u32 values.");
        }

        foreach (var item in value.V5Scalars)
        {
            writer.WriteU32(item);
        }

        var xorKey = value.V04XorKey ?? 0;
        writer.WriteU8(xorKey);
        writer.WriteU8(CheckedByte(value.V51VoiceEntries.Count, "v04 voice entry count"));
        foreach (var entry in value.V51VoiceEntries)
        {
            writer.WriteU8(entry.Flag);
            writer.WriteLegacyString(entry.Name, xorKey);
            WriteV04StringList8(writer, entry.Primary, xorKey);
            WriteV04StringList8(writer, entry.Secondary, null);
        }

        writer.WriteU8(CheckedByte(value.V51ByteGroups.Count, "v04 byte group count"));
        foreach (var group in value.V51ByteGroups)
        {
            writer.WriteLegacyString(group.Name, xorKey);
            writer.WriteU8(CheckedByte(group.Values.Count, "v04 byte group value count"));
            writer.WriteBytes(group.Values.ToArray());
        }

        writer.WriteU8(CheckedByte(value.V51SoundGroups.Count, "v04 sound group count"));
        foreach (var group in value.V51SoundGroups)
        {
            writer.WriteLegacyString(group.Name, xorKey);
            WriteV04StringList8(writer, group.Primary, xorKey);
            WriteV04StringList8(writer, group.Secondary, null);
        }

        var rawBlob = Convert.FromBase64String(value.RawBlob.LinkXorKeyBase64);
        writer.WriteU32(CheckedU32(rawBlob.Length, "v04 raw blob length"));
        writer.WriteBytes(rawBlob);
    }

    private void WriteV02GameSystem(ParamsBinaryWriter writer, ParamsGameSystem value)
    {
        writer.WriteU32(value.Width);
        writer.WriteU32(value.Height);
        writer.WriteU8(CheckedByte(value.ConfigBytes.Count, "v02 config byte count"));
        writer.WriteBytes(value.ConfigBytes.ToArray());
        writer.WriteLegacyString(value.GameTitle, encoding: _legacyWriteEncoding);
        writer.WriteLegacyString(value.DisplayTitle, encoding: _legacyWriteEncoding);
        writer.WriteLegacyString(value.Brand, encoding: _legacyWriteEncoding);
        writer.WriteLegacyString(value.V02Copyright);
        writer.WriteU8(value.StaffFlag);
        writer.WriteLegacyString(value.StaffName1, encoding: _legacyWriteEncoding);
        writer.WriteLegacyString(value.StaffName2, encoding: _legacyWriteEncoding);

        writer.WriteU8(CheckedByte(value.InstallTable.Count, "v02 install count"));
        foreach (var entry in value.InstallTable)
        {
            writer.WriteLegacyString(entry.File);
            writer.WriteLegacyString(entry.Media);
        }

        var xorKey = value.V04XorKey ?? 0;
        writer.WriteU8(xorKey);
        writer.WriteU8(CheckedByte(value.V51VoiceEntries.Count, "v02 voice entry count"));
        foreach (var entry in value.V51VoiceEntries)
        {
            writer.WriteU8(entry.Flag);
            writer.WriteLegacyString(entry.Name, xorKey);
            WriteV04StringList8(writer, entry.Primary, xorKey);
            WriteV04StringList8(writer, entry.Secondary, null);
        }

        writer.WriteU8(CheckedByte(value.V51ByteGroups.Count, "v02 byte group count"));
        foreach (var group in value.V51ByteGroups)
        {
            writer.WriteLegacyString(group.Name, xorKey);
            writer.WriteU8(CheckedByte(group.Values.Count, "v02 byte group value count"));
            writer.WriteBytes(group.Values.ToArray());
        }

        writer.WriteU8(CheckedByte(value.V51SoundGroups.Count, "v02 sound group count"));
        foreach (var group in value.V51SoundGroups)
        {
            writer.WriteLegacyString(group.Name, xorKey);
            WriteV04StringList8(writer, group.Primary, xorKey);
            WriteV04StringList8(writer, group.Secondary, null);
        }

        var rawBlob = Convert.FromBase64String(value.RawBlob.LinkXorKeyBase64);
        writer.WriteU32(CheckedU32(rawBlob.Length, "v02 raw blob length"));
        writer.WriteBytes(rawBlob);
    }

    private static void WriteV04StringList8(ParamsBinaryWriter writer, List<string> values, byte? xorKey)
    {
        writer.WriteU8(CheckedByte(values.Count, "v04 string list count"));
        foreach (var value in values)
        {
            writer.WriteLegacyString(value, xorKey);
        }
    }

    private static ParamsPattern ReadV03Pattern(ParamsBinaryReader reader)
    {
        var result = new ParamsPattern
        {
            V04XorKey = reader.ReadU8()
        };
        var xorKey = result.V04XorKey.Value;

        var itemCount = reader.ReadU32();
        for (var i = 0u; i < itemCount; i++)
        {
            result.Items.Add(new ParamsPatternItem
            {
                Name = reader.ReadLegacyString(xorKey),
                Kind = 0
            });
        }

        var arrayCount = reader.ReadU32();
        for (var i = 0u; i < arrayCount; i++)
        {
            var indexCount = reader.ReadU8();
            var values = new List<uint>(indexCount);
            for (var j = 0; j < indexCount; j++)
            {
                values.Add(reader.ReadU32());
            }

            result.IntArrays.Add(values);
        }

        result.GroupTable1 = ReadV04GroupTable(reader, xorKey);
        result.GroupTable2 = ReadV04GroupTable(reader, xorKey);
        return result;
    }

    private static void WriteV03Pattern(ParamsBinaryWriter writer, ParamsPattern value)
    {
        var xorKey = value.V04XorKey ?? 0;
        writer.WriteU8(xorKey);
        writer.WriteU32(CheckedU32(value.Items.Count, "v03 pattern item count"));
        foreach (var item in value.Items)
        {
            if (item.Strings.Count != 0)
            {
                throw new InvalidDataException("v03 PatternItem only supports a single legacy name.");
            }

            writer.WriteLegacyString(item.Name, xorKey);
        }

        writer.WriteU32(CheckedU32(value.IntArrays.Count, "v03 int array count"));
        foreach (var values in value.IntArrays)
        {
            writer.WriteU8(CheckedByte(values.Count, "v03 int array length"));
            foreach (var item in values)
            {
                writer.WriteU32(item);
            }
        }

        WriteV04GroupTable(writer, value.GroupTable1, xorKey);
        WriteV04GroupTable(writer, value.GroupTable2, xorKey);
    }

    private static ParamsPattern ReadV04Pattern(ParamsBinaryReader reader)
    {
        var result = new ParamsPattern
        {
            V04XorKey = reader.ReadU8()
        };
        var xorKey = result.V04XorKey.Value;

        var itemCount = reader.ReadU32();
        for (var i = 0u; i < itemCount; i++)
        {
            var item = new ParamsPatternItem
            {
                Name = reader.ReadLegacyString(xorKey)
            };
            var fileNameCount = reader.ReadU8();
            for (var j = 0; j < fileNameCount; j++)
            {
                item.Strings.Add(reader.ReadLegacyString(xorKey));
            }

            item.Kind = item.Strings.Count == 0 ? (byte)0 : (byte)1;
            result.Items.Add(item);
        }

        var arrayCount = reader.ReadU32();
        for (var i = 0u; i < arrayCount; i++)
        {
            var indexCount = reader.ReadU8();
            var values = new List<uint>(indexCount);
            for (var j = 0; j < indexCount; j++)
            {
                values.Add(reader.ReadU32());
            }

            result.IntArrays.Add(values);
        }

        result.GroupTable1 = ReadV04GroupTable(reader, xorKey);
        result.GroupTable2 = ReadV04GroupTable(reader, xorKey);
        return result;
    }

    private static void WriteV04Pattern(ParamsBinaryWriter writer, ParamsPattern value)
    {
        var xorKey = value.V04XorKey ?? 0;
        writer.WriteU8(xorKey);
        writer.WriteU32(CheckedU32(value.Items.Count, "v04 pattern item count"));
        foreach (var item in value.Items)
        {
            writer.WriteLegacyString(item.Name, xorKey);
            if (item.Kind is not 0 and not 1)
            {
                throw new InvalidDataException($"v04 PatternItem only supports legacy kind 0/1, got {item.Kind}.");
            }

            writer.WriteU8(CheckedByte(item.Strings.Count, "v04 pattern file name count"));
            foreach (var fileName in item.Strings)
            {
                writer.WriteLegacyString(fileName, xorKey);
            }
        }

        writer.WriteU32(CheckedU32(value.IntArrays.Count, "v04 int array count"));
        foreach (var values in value.IntArrays)
        {
            writer.WriteU8(CheckedByte(values.Count, "v04 int array length"));
            foreach (var item in values)
            {
                writer.WriteU32(item);
            }
        }

        WriteV04GroupTable(writer, value.GroupTable1, xorKey);
        WriteV04GroupTable(writer, value.GroupTable2, xorKey);
    }

    private static ParamsPatternGroupTable ReadV04GroupTable(ParamsBinaryReader reader, byte xorKey)
    {
        var table = new ParamsPatternGroupTable();
        var count = reader.ReadU32();
        for (var i = 0u; i < count; i++)
        {
            var group = new ParamsPatternGroup
            {
                Name = reader.ReadLegacyString(xorKey)
            };
            var indexCount = reader.ReadU8();
            for (var j = 0; j < indexCount; j++)
            {
                group.Indices.Add(reader.ReadU32());
            }

            table.Groups.Add(group);
        }

        return table;
    }

    private static void WriteV04GroupTable(ParamsBinaryWriter writer, ParamsPatternGroupTable table, byte xorKey)
    {
        writer.WriteU32(CheckedU32(table.Groups.Count, "v04 group table count"));
        foreach (var group in table.Groups)
        {
            writer.WriteLegacyString(group.Name, xorKey);
            writer.WriteU8(CheckedByte(group.Indices.Count, "v04 group index count"));
            foreach (var index in group.Indices)
            {
                writer.WriteU32(index);
            }
        }
    }

    private static List<ParamsSceneLabel> ReadV04SceneLabels(ParamsBinaryReader reader, byte xorKey)
    {
        var count = reader.ReadU32();
        var labels = new List<ParamsSceneLabel>(CheckedInt(count, "v04 scene label count"));
        for (var i = 0u; i < count; i++)
        {
            labels.Add(new ParamsSceneLabel
            {
                Name = reader.ReadLegacyString(xorKey),
                Value1 = reader.ReadU32(),
                Value2 = reader.ReadU32()
            });
        }

        return labels;
    }

    private static void WriteV04SceneLabels(ParamsBinaryWriter writer, List<ParamsSceneLabel> labels, byte xorKey)
    {
        writer.WriteU8(xorKey);
        writer.WriteU32(CheckedU32(labels.Count, "v04 scene label count"));
        foreach (var label in labels)
        {
            writer.WriteLegacyString(label.Name, xorKey);
            writer.WriteU32(label.Value1);
            writer.WriteU32(label.Value2);
        }
    }

    private static ParamsGameSystem ReadGameSystemV51(ParamsBinaryReader reader, string header)
    {
        var result = new ParamsGameSystem
        {
            VersionMarker = reader.ReadU16(),
            Width = reader.ReadU32(),
            Height = reader.ReadU32()
        };
        result.ConfigBytes = reader.ReadBytes(reader.ReadU8()).ToList();
        result.GameTitle = reader.ReadString16();
        result.DisplayTitle = reader.ReadString16();
        result.Brand = reader.ReadString16();
        result.StaffFlag = reader.ReadU8();
        result.StaffName1 = reader.ReadString16();
        result.StaffName2 = reader.ReadString16();

        var installCount = reader.ReadU8();
        for (var i = 0; i < installCount; i++)
        {
            result.InstallTable.Add(new ParamsInstallEntry
            {
                File = reader.ReadString16(),
                Media = reader.ReadString16()
            });
        }

        result.V5Scalars = [reader.ReadU32(), reader.ReadU32(), reader.ReadU32()];

        var voiceCount = reader.ReadU8();
        for (var i = 0; i < voiceCount; i++)
        {
            result.V51VoiceEntries.Add(new ParamsV51VoiceEntry
            {
                Flag = reader.ReadU8(),
                Name = reader.ReadString16(),
                Primary = ReadString16List8(reader),
                Secondary = ReadString16List8(reader)
            });
        }

        var byteGroupCount = reader.ReadU8();
        for (var i = 0; i < byteGroupCount; i++)
        {
            result.V51ByteGroups.Add(new ParamsV51ByteGroup
            {
                Name = reader.ReadString16(),
                Values = reader.ReadBytes(reader.ReadU8()).ToList()
            });
        }

        var soundGroupCount = reader.ReadU8();
        for (var i = 0; i < soundGroupCount; i++)
        {
            result.V51SoundGroups.Add(new ParamsV51SoundGroup
            {
                Name = reader.ReadString16(),
                Primary = ReadString16List8(reader),
                Secondary = ReadString16List8(reader)
            });
        }

        var rawBlob = reader.ReadBytes(CheckedInt(reader.ReadU32(), "raw blob length"));
        result.RawBlob = new ParamsRawBlob
        {
            ExpectedWidth = result.Width,
            ExpectedHeight = result.Height,
            ExpectedBytesPerPixel = CalculateBytesPerPixel(rawBlob.Length, result.Width, result.Height),
            KeyByteLength = rawBlob.Length,
            LinkXorKeyBase64 = Convert.ToBase64String(rawBlob)
        };

        if (IsV50(header))
        {
            return result;
        }

        var stringCount = reader.ReadU32();
        for (var i = 0u; i < stringCount; i++)
        {
            result.V51StringList.Add(reader.ReadString16());
        }

        result.V51PlaceCount = reader.ReadU32();
        for (var i = 0u; i < result.V51PlaceCount; i++)
        {
            result.V51Places.Add(new ParamsV51Place
            {
                Name = reader.ReadString16(),
                Value = reader.ReadU32()
            });
        }

        return result;
    }

    private static ParamsGameSystem ReadGameSystem(ParamsBinaryReader reader, string header)
    {
        var result = new ParamsGameSystem
        {
            VersionMarker = reader.ReadU16(),
            Width = reader.ReadU32(),
            Height = reader.ReadU32()
        };
        result.ConfigBytes = reader.ReadBytes(reader.ReadU8()).ToList();
        result.GameTitle = reader.ReadString16();
        result.DisplayTitle = reader.ReadString16();
        result.Brand = reader.ReadString16();
        result.StaffFlag = reader.ReadU8();
        result.StaffName1 = reader.ReadString16();
        result.StaffName2 = reader.ReadString16();

        var installCount = reader.ReadU8();
        for (var i = 0; i < installCount; i++)
        {
            result.InstallTable.Add(new ParamsInstallEntry
            {
                File = reader.ReadString16(),
                Media = reader.ReadString16()
            });
        }

        reader.Context = "GameSystem.V5Scalars";
        result.V5Scalars = [reader.ReadU32(), reader.ReadU32(), reader.ReadU32(), reader.ReadU32()];
        result.V5TailByte = IsBeforeV55(header) ? (byte)0 : reader.ReadU8();

        reader.Context = "GameSystem.SettingTags";
        for (var i = 0; i < 3; i++)
        {
            var present = reader.ReadU8() != 0;
            result.SettingTags.Add(new ParamsOptionalSettingTag
            {
                Present = present,
                Root = present ? ReadSettingTag(reader) : null
            });
        }

        reader.Context = "GameSystem.V53Triples";
        result.V53TripleRawCount = reader.ReadU32();
        if (result.V53TripleRawCount != 0)
        {
            for (var i = 0u; i < result.V53TripleRawCount; i++)
            {
                result.V53Triples.Add(new ParamsV53Triple
                {
                    Value1 = reader.ReadU32(),
                    Value2 = reader.ReadU32(),
                    Value3 = reader.ReadU32()
                });
            }
        }
        reader.Context = "GameSystem.RawBlob";
        var rawBlob = reader.ReadBytes(CheckedInt(reader.ReadU32(), "raw blob length"));
        result.RawBlob = new ParamsRawBlob
        {
            ExpectedWidth = result.Width,
            ExpectedHeight = result.Height,
            ExpectedBytesPerPixel = CalculateBytesPerPixel(rawBlob.Length, result.Width, result.Height),
            KeyByteLength = rawBlob.Length,
            LinkXorKeyBase64 = Convert.ToBase64String(rawBlob)
        };

        reader.Context = "GameSystem.Demos";
        var demoCount = reader.ReadU8();
        for (var i = 0; i < demoCount; i++)
        {
            result.Demos.Add(new ParamsDemo
            {
                Name = reader.ReadString16(),
                Commands = ReadDemoData(reader)
            });
        }

        reader.Context = "GameSystem.StringList";
        var stringCount = reader.ReadU32();
        for (var i = 0u; i < stringCount; i++)
        {
            result.V51StringList.Add(reader.ReadString16());
        }
        reader.Context = "GameSystem.Places";
        result.V51PlaceCount = reader.ReadU32();
        for (var i = 0u; i < result.V51PlaceCount; i++)
        {
            result.V51Places.Add(new ParamsV51Place
            {
                Name = reader.ReadString16(),
                Value = reader.ReadU32()
            });
        }

        if (!IsV53(header))
        {
            reader.Context = "GameSystem.V54NestedList";
            result.V54NestedListName = reader.ReadString16();
            result.V54NestedOuterCount = reader.ReadU32();
            if (result.V54NestedOuterCount != 0)
            {
                throw new InvalidDataException("params.dat v5.4 nested list is present; this sample branch is not implemented yet.");
            }
        }

        reader.Context = "GameSystem.Thumbnails";
        var thumbnailUnitCount = reader.ReadU32();
        if (thumbnailUnitCount % 11 != 0)
        {
            throw new InvalidDataException($"Invalid thumbnail unit count: {thumbnailUnitCount}");
        }

        for (var used = 0u; used < thumbnailUnitCount; used += 11)
        {
            var thumbnail = new ParamsThumbnail();
            for (var i = 0; i < 8; i++)
            {
                thumbnail.Strings.Add(reader.ReadTypedString());
            }

            for (var i = 0; i < 3; i++)
            {
                thumbnail.Ints.Add(reader.ReadTypedInt());
            }

            result.Thumbnails.Add(thumbnail);
        }

        reader.Context = "GameSystem.SceneNames";
        var sceneNameCount = reader.ReadU32();
        for (var i = 0u; i < sceneNameCount; i++)
        {
            result.SceneNames.Add(reader.ReadTypedString());
        }

        reader.Context = "GameSystem.RegistCg";
        result.RegistCg = ReadRegistCg(reader);
        reader.Context = "GameSystem.RegistScene";
        result.RegistScene = ReadRegistScene(reader, header);
        return result;
    }

    private static void WriteGameSystemV51(ParamsBinaryWriter writer, string header, ParamsGameSystem value)
    {
        writer.WriteU16(value.VersionMarker);
        writer.WriteU32(value.Width);
        writer.WriteU32(value.Height);
        var configBytes = value.ConfigBytes.Count > 0
            ? value.ConfigBytes.ToArray()
            : Hex.Decode(value.ConfigBytesHex ?? "");
        writer.WriteU8(CheckedByte(configBytes.Length, "config byte count"));
        writer.WriteBytes(configBytes);
        writer.WriteString16(value.GameTitle);
        writer.WriteString16(value.DisplayTitle);
        writer.WriteString16(value.Brand);
        writer.WriteU8(value.StaffFlag);
        writer.WriteString16(value.StaffName1);
        writer.WriteString16(value.StaffName2);

        writer.WriteU8(CheckedByte(value.InstallTable.Count, "install entry count"));
        foreach (var entry in value.InstallTable)
        {
            writer.WriteString16(entry.File);
            writer.WriteString16(entry.Media);
        }

        if (value.V5Scalars.Length != 3)
        {
            throw new InvalidDataException("v05.1 V5Scalars must contain exactly three u32 values.");
        }

        foreach (var scalar in value.V5Scalars)
        {
            writer.WriteU32(scalar);
        }

        writer.WriteU8(CheckedByte(value.V51VoiceEntries.Count, "v05.1 voice entry count"));
        foreach (var entry in value.V51VoiceEntries)
        {
            writer.WriteU8(entry.Flag);
            writer.WriteString16(entry.Name);
            WriteString16List8(writer, entry.Primary, "v05.1 voice primary count");
            WriteString16List8(writer, entry.Secondary, "v05.1 voice secondary count");
        }

        writer.WriteU8(CheckedByte(value.V51ByteGroups.Count, "v05.1 byte group count"));
        foreach (var group in value.V51ByteGroups)
        {
            writer.WriteString16(group.Name);
            writer.WriteU8(CheckedByte(group.Values.Count, "v05.1 byte group value count"));
            writer.WriteBytes(group.Values.ToArray());
        }

        writer.WriteU8(CheckedByte(value.V51SoundGroups.Count, "v05.1 sound group count"));
        foreach (var group in value.V51SoundGroups)
        {
            writer.WriteString16(group.Name);
            WriteString16List8(writer, group.Primary, "v05.1 sound primary count");
            WriteString16List8(writer, group.Secondary, "v05.1 sound secondary count");
        }

        var rawBlob = Convert.FromBase64String(value.RawBlob.LinkXorKeyBase64);
        writer.WriteU32(CheckedU32(rawBlob.Length, "raw blob length"));
        writer.WriteBytes(rawBlob);

        if (IsV50(header))
        {
            if (value.V51StringList.Count != 0 || value.V51PlaceCount != 0 || value.V51Places.Count != 0)
            {
                throw new InvalidDataException("v05 has no v05.1 string/place tables.");
            }

            return;
        }

        writer.WriteU32(CheckedU32(value.V51StringList.Count, "v5.1 string count"));
        foreach (var item in value.V51StringList)
        {
            writer.WriteString16(item);
        }

        var placeCount = CheckedU32(value.V51Places.Count, "v5.1 place count");
        if (value.V51PlaceCount != 0 && value.V51PlaceCount != placeCount)
        {
            throw new InvalidDataException($"v5.1 place count mismatch: raw={value.V51PlaceCount}, items={placeCount}");
        }

        writer.WriteU32(placeCount);
        foreach (var place in value.V51Places)
        {
            writer.WriteString16(place.Name);
            writer.WriteU32(place.Value);
        }
    }

    private static void WriteGameSystem(ParamsBinaryWriter writer, string header, ParamsGameSystem value)
    {
        writer.WriteU16(value.VersionMarker);
        writer.WriteU32(value.Width);
        writer.WriteU32(value.Height);
        var configBytes = value.ConfigBytes.Count > 0
            ? value.ConfigBytes.ToArray()
            : Hex.Decode(value.ConfigBytesHex ?? "");
        writer.WriteU8(CheckedByte(configBytes.Length, "config byte count"));
        writer.WriteBytes(configBytes);
        writer.WriteString16(value.GameTitle);
        writer.WriteString16(value.DisplayTitle);
        writer.WriteString16(value.Brand);
        writer.WriteU8(value.StaffFlag);
        writer.WriteString16(value.StaffName1);
        writer.WriteString16(value.StaffName2);

        writer.WriteU8(CheckedByte(value.InstallTable.Count, "install entry count"));
        foreach (var entry in value.InstallTable)
        {
            writer.WriteString16(entry.File);
            writer.WriteString16(entry.Media);
        }

        if (value.V5Scalars.Length != 4)
        {
            throw new InvalidDataException("V5Scalars must contain exactly four u32 values.");
        }

        foreach (var scalar in value.V5Scalars)
        {
            writer.WriteU32(scalar);
        }
        if (!IsBeforeV55(header))
        {
            writer.WriteU8(value.V5TailByte);
        }

        if (value.SettingTags.Count != 3)
        {
            throw new InvalidDataException("SettingTags must contain exactly three optional roots.");
        }

        foreach (var optional in value.SettingTags)
        {
            writer.WriteU8((byte)(optional.Present ? 1 : 0));
            if (optional.Present)
            {
                if (optional.Root is null)
                {
                    throw new InvalidDataException("Present SettingTag has no root.");
                }

                WriteSettingTag(writer, optional.Root);
            }
        }

        var tripleCount = CheckedU32(value.V53Triples.Count, "v05.3+ triple count");
        if (value.V53TripleRawCount != 0 && value.V53TripleRawCount != tripleCount)
        {
            throw new InvalidDataException($"v05.3+ triple count mismatch: raw={value.V53TripleRawCount}, items={tripleCount}");
        }

        writer.WriteU32(tripleCount);
        foreach (var triple in value.V53Triples)
        {
            writer.WriteU32(triple.Value1);
            writer.WriteU32(triple.Value2);
            writer.WriteU32(triple.Value3);
        }

        var rawBlob = Convert.FromBase64String(value.RawBlob.LinkXorKeyBase64);
        writer.WriteU32(CheckedU32(rawBlob.Length, "raw blob length"));
        writer.WriteBytes(rawBlob);

        writer.WriteU8(CheckedByte(value.Demos.Count, "demo count"));
        foreach (var demo in value.Demos)
        {
            writer.WriteString16(demo.Name);
            WriteDemoData(writer, demo.Commands);
        }

        writer.WriteU32(CheckedU32(value.V51StringList.Count, "v5.1 string count"));
        foreach (var item in value.V51StringList)
        {
            writer.WriteString16(item);
        }
        var placeCount = CheckedU32(value.V51Places.Count, "v5.1 place count");
        if (value.V51PlaceCount != 0 && value.V51PlaceCount != placeCount)
        {
            throw new InvalidDataException($"v5.1 place count mismatch: raw={value.V51PlaceCount}, items={placeCount}");
        }

        writer.WriteU32(placeCount);
        foreach (var place in value.V51Places)
        {
            writer.WriteString16(place.Name);
            writer.WriteU32(place.Value);
        }
        if (!IsV53(header))
        {
            writer.WriteString16(value.V54NestedListName);
            writer.WriteU32(value.V54NestedOuterCount);
        }

        writer.WriteU32(CheckedU32(value.Thumbnails.Count * 11, "thumbnail unit count"));
        foreach (var thumbnail in value.Thumbnails)
        {
            if (thumbnail.Strings.Count != 8 || thumbnail.Ints.Count != 3)
            {
                throw new InvalidDataException("Each thumbnail must contain 8 typed strings and 3 typed ints.");
            }

            foreach (var text in thumbnail.Strings)
            {
                writer.WriteTypedString(text);
            }

            foreach (var number in thumbnail.Ints)
            {
                writer.WriteTypedInt(number);
            }
        }

        writer.WriteU32(CheckedU32(value.SceneNames.Count, "scene name count"));
        foreach (var name in value.SceneNames)
        {
            writer.WriteTypedString(name);
        }

        WriteRegistCg(writer, value.RegistCg);
        WriteRegistScene(writer, header, value.RegistScene);
    }

    private static ParamsSettingTag ReadSettingTag(ParamsBinaryReader reader)
    {
        var tag = new ParamsSettingTag
        {
            Name = reader.ReadString16()
        };

        var pairCount = reader.ReadU32();
        for (var i = 0u; i < pairCount; i++)
        {
            tag.Pairs.Add(new ParamsSettingPair
            {
                Key = reader.ReadString16(),
                Value = reader.ReadString16()
            });
        }

        var childCount = reader.ReadU32();
        for (var i = 0u; i < childCount; i++)
        {
            tag.Children.Add(ReadSettingTag(reader));
        }

        return tag;
    }

    private static void WriteSettingTag(ParamsBinaryWriter writer, ParamsSettingTag tag)
    {
        writer.WriteString16(tag.Name);
        writer.WriteU32(CheckedU32(tag.Pairs.Count, "setting pair count"));
        foreach (var pair in tag.Pairs)
        {
            writer.WriteString16(pair.Key);
            writer.WriteString16(pair.Value);
        }

        writer.WriteU32(CheckedU32(tag.Children.Count, "setting child count"));
        foreach (var child in tag.Children)
        {
            WriteSettingTag(writer, child);
        }
    }

    private static List<string> ReadString16List8(ParamsBinaryReader reader)
    {
        var count = reader.ReadU8();
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(reader.ReadString16());
        }

        return result;
    }

    private static void WriteString16List8(ParamsBinaryWriter writer, IReadOnlyList<string> values, string countName)
    {
        writer.WriteU8(CheckedByte(values.Count, countName));
        foreach (var value in values)
        {
            writer.WriteString16(value);
        }
    }

    private static List<ParamsDemoCommand> ReadDemoData(ParamsBinaryReader reader)
    {
        var magic = reader.ReadAscii(9);
        if (!string.Equals(magic, "[Demo3.0]", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid DemoData magic at 0x{reader.Offset - 9:X}: {magic}");
        }

        var count = reader.ReadU16();
        var commands = new List<ParamsDemoCommand>(count);
        for (var i = 0; i < count; i++)
        {
            var commandOffset = reader.Offset;
            var type = reader.ReadU8();
            var length = reader.ReadU8();
            if (length < 2)
            {
                throw new InvalidDataException($"Invalid DemoCommand length at 0x{commandOffset:X}: {length}");
            }

            var payload = reader.ReadBytes(length - 2);
            commands.Add(ReadDemoCommand(type, payload, commandOffset));
        }

        return commands;
    }

    private static ParamsDemoCommand ReadDemoCommand(byte type, byte[] payload, int commandOffset)
    {
        var reader = new ParamsBinaryReader(payload);
        var command = new ParamsDemoCommand
        {
            Type = type,
            TypeName = DemoCommandName(type)
        };

        switch (type)
        {
            case 0:
            case 1:
            case 7:
                EnsurePayloadEnd(reader, commandOffset);
                return command;
            case 2:
                command.ModeOrFlag = reader.ReadU8();
                command.Value = reader.ReadU32();
                EnsurePayloadEnd(reader, commandOffset);
                return command;
            case 3:
                command.FieldA = reader.ReadU8();
                command.FieldB = reader.ReadU8();
                command.Name = reader.ReadShortUtf16String();
                EnsurePayloadEnd(reader, commandOffset);
                return command;
            case 4:
                command.SlotOrLayer = reader.ReadU8();
                command.Name = reader.ReadShortUtf16String();
                EnsurePayloadEnd(reader, commandOffset);
                return command;
            case 5:
                command.Effect = reader.ReadShortUtf16String();
                command.Value = reader.ReadU32();
                command.Arg = reader.ReadShortUtf16String();
                EnsurePayloadEnd(reader, commandOffset);
                return command;
            case 6:
                command.RawLayer = reader.ReadU8();
                command.Visible = reader.ReadU8() != 0;
                EnsurePayloadEnd(reader, commandOffset);
                return command;
            case 8:
                command.IdOrLayer = reader.ReadU8();
                command.DurationOrValue = reader.ReadU32();
                command.Value2 = reader.ReadU32();
                command.Value3 = reader.ReadU32();
                command.Value4 = reader.ReadU32();
                command.Value5 = reader.ReadU32();
                EnsurePayloadEnd(reader, commandOffset);
                return command;
            case 9:
                command.IdOrLayer = reader.ReadU8();
                command.Value1 = reader.ReadU32();
                command.Value2 = reader.ReadU32();
                EnsurePayloadEnd(reader, commandOffset);
                return command;
            default:
                command.RawPayloadHex = Hex.Encode(payload);
                return command;
        }
    }

    private static void WriteDemoData(ParamsBinaryWriter writer, List<ParamsDemoCommand> commands)
    {
        writer.WriteAscii("[Demo3.0]");
        writer.WriteU16(CheckedU16(commands.Count, "demo command count"));
        foreach (var command in commands)
        {
            var payload = BuildDemoPayload(command);
            writer.WriteU8(command.Type);
            writer.WriteU8(CheckedByte(payload.Length + 2, "demo command length"));
            writer.WriteBytes(payload);
        }
    }

    private static byte[] BuildDemoPayload(ParamsDemoCommand command)
    {
        var writer = new ParamsBinaryWriter();
        switch (command.Type)
        {
            case 0:
            case 1:
            case 7:
                break;
            case 2:
                writer.WriteU8(Required(command.ModeOrFlag, "modeOrFlag"));
                writer.WriteU32(Required(command.Value, "value"));
                break;
            case 3:
                writer.WriteU8(Required(command.FieldA, "fieldA"));
                writer.WriteU8(Required(command.FieldB, "fieldB"));
                writer.WriteShortUtf16String(command.Name ?? "");
                break;
            case 4:
                writer.WriteU8(Required(command.SlotOrLayer, "slotOrLayer"));
                writer.WriteShortUtf16String(command.Name ?? "");
                break;
            case 5:
                writer.WriteShortUtf16String(command.Effect ?? "");
                writer.WriteU32(Required(command.Value, "value"));
                writer.WriteShortUtf16String(command.Arg ?? "");
                break;
            case 6:
                writer.WriteU8(Required(command.RawLayer, "rawLayer"));
                writer.WriteU8((byte)(Required(command.Visible, "visible") ? 1 : 0));
                break;
            case 8:
                writer.WriteU8(Required(command.IdOrLayer, "idOrLayer"));
                writer.WriteU32(Required(command.DurationOrValue, "durationOrValue"));
                writer.WriteU32(Required(command.Value2, "value2"));
                writer.WriteU32(Required(command.Value3, "value3"));
                writer.WriteU32(Required(command.Value4, "value4"));
                writer.WriteU32(Required(command.Value5, "value5"));
                break;
            case 9:
                writer.WriteU8(Required(command.IdOrLayer, "idOrLayer"));
                writer.WriteU32(Required(command.Value1, "value1"));
                writer.WriteU32(Required(command.Value2, "value2"));
                break;
            default:
                writer.WriteBytes(Hex.Decode(command.RawPayloadHex ?? ""));
                break;
        }

        return writer.ToArray();
    }

    private static ParamsPattern ReadPattern(ParamsBinaryReader reader, string header)
    {
        var result = new ParamsPattern();
        var itemCount = reader.ReadU32();
        for (var i = 0u; i < itemCount; i++)
        {
            if (IsV53OrV54OrV55OrV56(header))
            {
                var legacyItem = new ParamsPatternItem
                {
                    Name = reader.ReadString16()
                };
                var nameCount = reader.ReadU8();
                legacyItem.Kind = nameCount == 0 ? (byte)0 : (byte)1;
                for (var j = 0; j < nameCount; j++)
                {
                    legacyItem.Strings.Add(reader.ReadString16());
                }

                result.Items.Add(legacyItem);
                continue;
            }

            var item = new ParamsPatternItem
            {
                Name = reader.ReadString16(),
                Kind = reader.ReadU8()
            };
            ReadPatternItemBody(reader, item, i);

            result.Items.Add(item);
        }

        var intArrayCount = reader.ReadU32();
        for (var i = 0u; i < intArrayCount; i++)
        {
            var count = reader.ReadU8();
            var array = new List<uint>(count);
            for (var j = 0; j < count; j++)
            {
                array.Add(reader.ReadU32());
            }

            result.IntArrays.Add(array);
        }

        result.GroupTable1 = ReadGroupTable(reader, header);
        result.GroupTable2 = ReadGroupTable(reader, header);
        return result;
    }

    private static void WritePattern(ParamsBinaryWriter writer, string header, ParamsPattern value)
    {
        writer.WriteU32(CheckedU32(value.Items.Count, "pattern item count"));
        foreach (var item in value.Items)
        {
            writer.WriteString16(item.Name);
            if (IsV53OrV54OrV55OrV56(header))
            {
                writer.WriteU8(CheckedByte(item.Strings.Count, "legacy pattern file name count"));
                foreach (var text in item.Strings)
                {
                    writer.WriteString16(text);
                }

                continue;
            }

            writer.WriteU8(item.Kind);
            WritePatternItemBody(writer, item);
        }

        writer.WriteU32(CheckedU32(value.IntArrays.Count, "pattern int array count"));
        foreach (var array in value.IntArrays)
        {
            writer.WriteU8(CheckedByte(array.Count, "pattern int array length"));
            foreach (var number in array)
            {
                writer.WriteU32(number);
            }
        }

        WriteGroupTable(writer, header, value.GroupTable1);
        WriteGroupTable(writer, header, value.GroupTable2);
    }

    private static void ReadPatternItemBody(ParamsBinaryReader reader, ParamsPatternItem item, uint index)
    {
        switch (item.Kind)
        {
            case 0:
                break;
            case 1:
                var count = reader.ReadU32();
                for (var j = 0u; j < count; j++)
                {
                    item.Strings.Add(reader.ReadString16());
                }
                break;
            case 2:
                item.SubName = reader.ReadString16();
                item.X = reader.ReadU32();
                item.Y = reader.ReadU32();
                break;
            case 3:
                item.SubName = reader.ReadString16();
                item.Value = reader.ReadU32();
                break;
            default:
                throw new InvalidDataException($"Unknown PatternItem kind {item.Kind} at item {index}.");
        }
    }

    private static void WritePatternItemBody(ParamsBinaryWriter writer, ParamsPatternItem item)
    {
        switch (item.Kind)
        {
            case 0:
                break;
            case 1:
                writer.WriteU32(CheckedU32(item.Strings.Count, "pattern string list count"));
                foreach (var text in item.Strings)
                {
                    writer.WriteString16(text);
                }
                break;
            case 2:
                writer.WriteString16(item.SubName ?? "");
                writer.WriteU32(Required(item.X, "x"));
                writer.WriteU32(Required(item.Y, "y"));
                break;
            case 3:
                writer.WriteString16(item.SubName ?? "");
                writer.WriteU32(Required(item.Value, "value"));
                break;
            default:
                throw new InvalidDataException($"Unknown PatternItem kind {item.Kind}.");
        }
    }

    private static ParamsPatternGroupTable ReadGroupTable(ParamsBinaryReader reader, string header)
    {
        var table = new ParamsPatternGroupTable();
        var groupCount = reader.ReadU32();
        for (var i = 0u; i < groupCount; i++)
        {
            var group = new ParamsPatternGroup
            {
                Name = reader.ReadString16()
            };
            var indexCount = IsV53OrV54OrV55(header) ? reader.ReadU8() : reader.ReadU16();
            for (var j = 0; j < indexCount; j++)
            {
                group.Indices.Add(reader.ReadU32());
            }

            table.Groups.Add(group);
        }

        return table;
    }

    private static void WriteGroupTable(ParamsBinaryWriter writer, string header, ParamsPatternGroupTable table)
    {
        writer.WriteU32(CheckedU32(table.Groups.Count, "group table count"));
        foreach (var group in table.Groups)
        {
            writer.WriteString16(group.Name);
            if (IsV53OrV54OrV55(header))
            {
                writer.WriteU8(CheckedByte(group.Indices.Count, "group index count"));
            }
            else
            {
                writer.WriteU16(CheckedU16(group.Indices.Count, "group index count"));
            }
            foreach (var index in group.Indices)
            {
                writer.WriteU32(index);
            }
        }
    }

    private static List<ParamsSceneLabel> ReadSceneLabels(ParamsBinaryReader reader)
    {
        var count = reader.ReadU32();
        var labels = new List<ParamsSceneLabel>(CheckedInt(count, "scene label count"));
        for (var i = 0u; i < count; i++)
        {
            labels.Add(new ParamsSceneLabel
            {
                Name = reader.ReadString16(),
                Value1 = reader.ReadU32(),
                Value2 = reader.ReadU32()
            });
        }

        return labels;
    }

    private static void WriteSceneLabels(ParamsBinaryWriter writer, List<ParamsSceneLabel> labels)
    {
        writer.WriteU32(CheckedU32(labels.Count, "scene label count"));
        foreach (var label in labels)
        {
            writer.WriteString16(label.Name);
            writer.WriteU32(label.Value1);
            writer.WriteU32(label.Value2);
        }
    }

    private static List<ParamsRegistCgGroup> ReadRegistCg(ParamsBinaryReader reader)
    {
        var unitCount = reader.ReadU32();
        var groups = new List<ParamsRegistCgGroup>();
        var used = 0u;
        while (used < unitCount)
        {
            var group = new ParamsRegistCgGroup
            {
                GroupName = reader.ReadTypedString()
            };
            var itemCount = reader.ReadTypedInt();
            used += 2;
            for (var i = 0u; i < itemCount; i++)
            {
                var name = reader.ReadTypedString();
                var (x, y) = reader.ReadTypedPoint();
                group.Items.Add(new ParamsRegistCgItem
                {
                    ItemName = name,
                    X = x,
                    Y = y,
                    Value = reader.ReadTypedInt()
                });
                used += 3;
            }

            groups.Add(group);
        }

        if (used != unitCount)
        {
            throw new InvalidDataException($"_regist_cg unit mismatch: declared={unitCount}, used={used}");
        }

        return groups;
    }

    private static void WriteRegistCg(ParamsBinaryWriter writer, List<ParamsRegistCgGroup> groups)
    {
        var unitCount = groups.Aggregate(0u, (total, group) => checked(total + 2u + (uint)(group.Items.Count * 3)));
        writer.WriteU32(unitCount);
        foreach (var group in groups)
        {
            writer.WriteTypedString(group.GroupName);
            writer.WriteTypedInt(CheckedU32(group.Items.Count, "_regist_cg item count"));
            foreach (var item in group.Items)
            {
                writer.WriteTypedString(item.ItemName);
                writer.WriteTypedPoint(item.X, item.Y);
                writer.WriteTypedInt(item.Value);
            }
        }
    }

    private static List<ParamsRegistSceneGroup> ReadRegistScene(ParamsBinaryReader reader, string header)
    {
        var unitCount = reader.ReadU32();
        var groups = new List<ParamsRegistSceneGroup>();
        var used = 0u;
        while (used < unitCount)
        {
            var group = new ParamsRegistSceneGroup
            {
                GroupName = reader.ReadTypedString()
            };
            var itemCount = reader.ReadTypedInt();
            used += 2;
            for (var i = 0u; i < itemCount; i++)
            {
                if (IsLegacyRegistScene(header))
                {
                    group.Items.Add(new ParamsRegistSceneItem
                    {
                        ItemName = reader.ReadTypedString(),
                        CgName = reader.ReadTypedString()
                    });
                    used += 2;
                }
                else
                {
                    var item = new ParamsRegistSceneItem
                    {
                        ItemName = reader.ReadTypedString()
                    };
                    var nestedCount = reader.ReadTypedInt();
                    used += 2;
                    for (var j = 0u; j < nestedCount; j++)
                    {
                        item.Scenes.Add(reader.ReadTypedString());
                        used++;
                    }

                    group.Items.Add(item);
                }
            }

            groups.Add(group);
        }

        if (used != unitCount)
        {
            throw new InvalidDataException($"_regist_scene unit mismatch: declared={unitCount}, used={used}");
        }

        return groups;
    }

    private static void WriteRegistScene(ParamsBinaryWriter writer, string header, List<ParamsRegistSceneGroup> groups)
    {
        var unitCount = IsLegacyRegistScene(header)
            ? groups.Aggregate(0u, (total, group) => checked(total + 2u + (uint)(group.Items.Count * 2)))
            : groups.Aggregate(0u, (total, group) =>
            {
                total += 2;
                foreach (var item in group.Items)
                {
                    total = checked(total + 2u + (uint)item.Scenes.Count);
                }

                return total;
            });
        writer.WriteU32(unitCount);
        foreach (var group in groups)
        {
            writer.WriteTypedString(group.GroupName);
            writer.WriteTypedInt(CheckedU32(group.Items.Count, "_regist_scene item count"));
            foreach (var item in group.Items)
            {
                writer.WriteTypedString(item.ItemName);
                if (IsLegacyRegistScene(header))
                {
                    writer.WriteTypedString(item.CgName ?? "");
                }
                else
                {
                    writer.WriteTypedInt(CheckedU32(item.Scenes.Count, "_regist_scene nested scene count"));
                    foreach (var scene in item.Scenes)
                    {
                        writer.WriteTypedString(scene);
                    }
                }
            }
        }
    }

    private static void EnsurePayloadEnd(ParamsBinaryReader reader, int commandOffset)
    {
        if (!reader.End)
        {
            throw new InvalidDataException($"DemoCommand payload was not fully consumed at 0x{commandOffset:X}: {reader.Remaining} bytes left.");
        }
    }

    private static string DemoCommandName(byte type) => type switch
    {
        0 => "CmdEnd",
        1 => "CmdNext",
        2 => "CmdWait",
        3 => "CmdSound",
        4 => "CmdLoad",
        5 => "CmdTransit",
        6 => "CmdDisp",
        7 => "CmdUpdate",
        8 => "CmdMove",
        9 => "CmdPos",
        _ => "CmdUnknown"
    };

    private static byte Required(byte? value, string name) => value ?? throw new InvalidDataException($"Missing required DemoCommand field: {name}");
    private static bool Required(bool? value, string name) => value ?? throw new InvalidDataException($"Missing required DemoCommand field: {name}");
    private static uint Required(uint? value, string name) => value ?? throw new InvalidDataException($"Missing required field: {name}");

    private static uint? CalculateBytesPerPixel(int byteLength, uint width, uint height)
    {
        var pixels = (ulong)width * height;
        if (pixels == 0 || (ulong)byteLength % pixels != 0)
        {
            return null;
        }

        return (uint)((ulong)byteLength / pixels);
    }

    private static byte CheckedByte(int value, string name)
    {
        if (value is < 0 or > byte.MaxValue)
        {
            throw new InvalidDataException($"{name} does not fit in u8: {value}");
        }

        return (byte)value;
    }

    private static ushort CheckedU16(int value, string name)
    {
        if (value is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException($"{name} does not fit in u16: {value}");
        }

        return (ushort)value;
    }

    private static uint CheckedU32(int value, string name)
    {
        if (value < 0)
        {
            throw new InvalidDataException($"{name} does not fit in u32: {value}");
        }

        return (uint)value;
    }

    private static int CheckedInt(uint value, string name)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"{name} does not fit in int: {value}");
        }

        return (int)value;
    }

    private sealed class ParamsBinaryReader(byte[] data, Encoding? legacyEncoding = null)
    {
        private readonly byte[] _data = data;
        private readonly Encoding _legacyEncoding = legacyEncoding ?? CreateLegacyEncoding(null);
        public int Offset { get; private set; }
        public string Context { get; set; } = "";
        public int Remaining => _data.Length - Offset;
        public bool End => Offset == _data.Length;

        public byte ReadU8()
        {
            Ensure(1);
            return _data[Offset++];
        }

        public ushort ReadU16()
        {
            Ensure(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Offset, 2));
            Offset += 2;
            return value;
        }

        public uint ReadU32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Offset, 4));
            Offset += 4;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            Ensure(count);
            var result = _data.AsSpan(Offset, count).ToArray();
            Offset += count;
            return result;
        }

        public string ReadAscii(int count)
        {
            return Encoding.ASCII.GetString(ReadBytes(count));
        }

        public string ReadString16()
        {
            var byteCount = ReadU16();
            var bytes = ReadBytes(byteCount);
            return Utf16Le.GetString(bytes);
        }

        public string ReadShortUtf16String()
        {
            var byteCount = ReadU8();
            var bytes = ReadBytes(byteCount);
            return Utf16Le.GetString(bytes);
        }

        public string ReadLegacyString(byte? xorKey = null, Encoding? encoding = null)
        {
            var byteCount = ReadU8();
            var bytes = ReadBytes(byteCount);
            if (xorKey is not null)
            {
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] ^= xorKey.Value;
                }
            }

            return (encoding ?? _legacyEncoding).GetString(bytes);
        }

        public string ReadTypedString()
        {
            var type = ReadU32();
            if (type != 0)
            {
                throw new InvalidDataException($"Expected typed string at 0x{Offset - 4:X}, got type {type}.");
            }

            return ReadString16();
        }

        public uint ReadTypedInt()
        {
            var type = ReadU32();
            if (type != 1)
            {
                throw new InvalidDataException($"Expected typed int at 0x{Offset - 4:X}, got type {type}.");
            }

            return ReadU32();
        }

        public (uint X, uint Y) ReadTypedPoint()
        {
            var type = ReadU32();
            if (type != 2)
            {
                throw new InvalidDataException($"Expected typed point at 0x{Offset - 4:X}, got type {type}.");
            }

            return (ReadU32(), ReadU32());
        }

        private void Ensure(int count)
        {
            if (count < 0 || Offset + count > _data.Length)
            {
                var prefix = string.IsNullOrEmpty(Context) ? "params.dat" : $"params.dat {Context}";
                throw new EndOfStreamException($"{prefix} read past EOF at 0x{Offset:X}, need {count} bytes.");
            }
        }

        public byte[] GetBytes(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > _data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            return _data.AsSpan(offset, count).ToArray();
        }
    }

    private sealed class ParamsBinaryWriter(Encoding? legacyEncoding = null)
    {
        private readonly MemoryStream _stream = new();
        private readonly Encoding _legacyEncoding = legacyEncoding ?? CreateLegacyEncoding(null);

        public void WriteU8(byte value) => _stream.WriteByte(value);

        public void WriteU16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void WriteU32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void WriteBytes(byte[] value) => _stream.Write(value, 0, value.Length);
        public void WriteAscii(string value) => WriteBytes(Encoding.ASCII.GetBytes(value));

        public void WriteString16(string value)
        {
            var bytes = Utf16Le.GetBytes(value);
            WriteU16(CheckedU16(bytes.Length, "string16 byte length"));
            WriteBytes(bytes);
        }

        public void WriteShortUtf16String(string value)
        {
            var bytes = Utf16Le.GetBytes(value);
            WriteU8(CheckedByte(bytes.Length, "short UTF-16 string byte length"));
            WriteBytes(bytes);
        }

        public void WriteLegacyString(string value, byte? xorKey = null, Encoding? encoding = null)
        {
            var bytes = (encoding ?? _legacyEncoding).GetBytes(value);
            if (xorKey is not null)
            {
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] ^= xorKey.Value;
                }
            }

            WriteU8(CheckedByte(bytes.Length, "legacy ANSI string byte length"));
            WriteBytes(bytes);
        }

        public void WriteTypedString(string value)
        {
            WriteU32(0);
            WriteString16(value);
        }

        public void WriteTypedInt(uint value)
        {
            WriteU32(1);
            WriteU32(value);
        }

        public void WriteTypedPoint(uint x, uint y)
        {
            WriteU32(2);
            WriteU32(x);
            WriteU32(y);
        }

        public byte[] ToArray() => _stream.ToArray();

    }

    private static class Hex
    {
        public static string Encode(byte[] bytes) => Convert.ToHexString(bytes);

        public static byte[] Decode(string hex)
        {
            var clean = new string(hex.Where(c => !char.IsWhiteSpace(c) && c != '_').ToArray());
            if (clean.Length % 2 != 0)
            {
                throw new FormatException("Hex string must contain an even number of digits.");
            }

            return Convert.FromHexString(clean);
        }
    }
}
