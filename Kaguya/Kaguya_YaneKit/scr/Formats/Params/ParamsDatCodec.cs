// ============================================================================
// ParamsDatCodec.cs
// params.dat 二进制序列化/反序列化编解码器 (版本 [SCR-PARAMS]v05.8)
//
// 读取流程 (Read):
//   1. 验证 ASCII 头 "[SCR-PARAMS]v05.8"
//   2. ReadGameSystem  -- 解析游戏系统配置区段
//      - 基本信息: VersionMarker, Width, Height, ConfigBytes
//      - 字符串字段: GameTitle, DisplayTitle, Brand, StaffName1/2
//      - InstallTable: u8 计数 -> (File, Media) 对
//      - V5Scalars (4xu32) + V5TailByte (v5.5+)
//      - SettingTags: 3 个可选树形标签 (递归 ReadSettingTag)
//      - RawBlob: u32 长度 + 原始字节 (base64 存储)
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
    public const string V54Header = "[SCR-PARAMS]v05.4";
    public const string V55Header = "[SCR-PARAMS]v05.5";
    public const string V56Header = "[SCR-PARAMS]v05.6";
    public const string V57Header = "[SCR-PARAMS]v05.7";
    public const string V58Header = ExpectedHeader;
    private const int HeaderLength = 17;

    private static readonly Encoding Utf16Le = Encoding.Unicode;

    public ParamsDatDocument Read(byte[] data)
    {
        var reader = new ParamsBinaryReader(data);
        var header = reader.ReadAscii(HeaderLength);
        if (!IsSupportedHeader(header))
        {
            throw new InvalidDataException($"Unsupported params.dat header: {header}");
        }

        var document = new ParamsDatDocument
        {
            Header = header,
            GameSystem = ReadGameSystem(reader, header),
            Pattern = ReadPattern(reader, header),
            SceneLabels = ReadSceneLabels(reader)
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
        WriteGameSystem(writer, document.Header, document.GameSystem);
        WritePattern(writer, document.Header, document.Pattern);
        WriteSceneLabels(writer, document.SceneLabels);
        return writer.ToArray();
    }

    private static bool IsSupportedHeader(string header) =>
        string.Equals(header, V54Header, StringComparison.Ordinal) ||
        string.Equals(header, V55Header, StringComparison.Ordinal) ||
        string.Equals(header, V56Header, StringComparison.Ordinal) ||
        string.Equals(header, V57Header, StringComparison.Ordinal) ||
        string.Equals(header, V58Header, StringComparison.Ordinal);

    public static string DescribeVersion(string header) =>
        header.StartsWith("[SCR-PARAMS]v", StringComparison.Ordinal) && header.Length > "[SCR-PARAMS]v".Length
            ? header["[SCR-PARAMS]v".Length..]
            : header;

    private static bool IsV54OrV55OrV56(string header) =>
        string.Equals(header, V54Header, StringComparison.Ordinal) ||
        string.Equals(header, V55Header, StringComparison.Ordinal) ||
        string.Equals(header, V56Header, StringComparison.Ordinal);
    private static bool IsV54OrV55(string header) =>
        string.Equals(header, V54Header, StringComparison.Ordinal) ||
        string.Equals(header, V55Header, StringComparison.Ordinal);
    private static bool IsV54(string header) => string.Equals(header, V54Header, StringComparison.Ordinal);
    private static bool IsLegacyRegistScene(string header) =>
        IsV54OrV55OrV56(header) || string.Equals(header, V57Header, StringComparison.Ordinal);

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

        result.V5Scalars = [reader.ReadU32(), reader.ReadU32(), reader.ReadU32(), reader.ReadU32()];
        result.V5TailByte = IsV54(header) ? (byte)0 : reader.ReadU8();

        for (var i = 0; i < 3; i++)
        {
            var present = reader.ReadU8() != 0;
            result.SettingTags.Add(new ParamsOptionalSettingTag
            {
                Present = present,
                Root = present ? ReadSettingTag(reader) : null
            });
        }

        result.V53TripleRawCount = reader.ReadU32();
        var rawBlob = reader.ReadBytes(CheckedInt(reader.ReadU32(), "raw blob length"));
        result.RawBlob = new ParamsRawBlob
        {
            ExpectedWidth = result.Width,
            ExpectedHeight = result.Height,
            ExpectedBytesPerPixel = CalculateBytesPerPixel(rawBlob.Length, result.Width, result.Height),
            DataBase64 = Convert.ToBase64String(rawBlob)
        };

        var demoCount = reader.ReadU8();
        for (var i = 0; i < demoCount; i++)
        {
            result.Demos.Add(new ParamsDemo
            {
                Name = reader.ReadString16(),
                Commands = ReadDemoData(reader)
            });
        }

        var stringCount = reader.ReadU32();
        for (var i = 0u; i < stringCount; i++)
        {
            result.V51StringList.Add(reader.ReadString16());
        }
        result.V51PlaceCount = reader.ReadU32();
        if (result.V51PlaceCount != 0)
        {
            throw new InvalidDataException("params.dat v5.1 place table is present; this sample branch is not implemented yet.");
        }

        result.V54NestedListName = reader.ReadString16();
        result.V54NestedOuterCount = reader.ReadU32();
        if (result.V54NestedOuterCount != 0)
        {
            throw new InvalidDataException("params.dat v5.4 nested list is present; this sample branch is not implemented yet.");
        }

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

        var sceneNameCount = reader.ReadU32();
        for (var i = 0u; i < sceneNameCount; i++)
        {
            result.SceneNames.Add(reader.ReadTypedString());
        }

        result.RegistCg = ReadRegistCg(reader);
        result.RegistScene = ReadRegistScene(reader, header);
        return result;
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
        if (!IsV54(header))
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

        writer.WriteU32(value.V53TripleRawCount);
        var rawBlob = Convert.FromBase64String(value.RawBlob.DataBase64);
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
        writer.WriteU32(value.V51PlaceCount);
        writer.WriteString16(value.V54NestedListName);
        writer.WriteU32(value.V54NestedOuterCount);

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
            if (IsV54OrV55OrV56(header))
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
            if (IsV54OrV55OrV56(header))
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
            var indexCount = IsV54OrV55(header) ? reader.ReadU8() : reader.ReadU16();
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
            if (IsV54OrV55(header))
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

    private sealed class ParamsBinaryReader(byte[] data)
    {
        private readonly byte[] _data = data;
        public int Offset { get; private set; }
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
                throw new EndOfStreamException($"params.dat read past EOF at 0x{Offset:X}, need {count} bytes.");
            }
        }
    }

    private sealed class ParamsBinaryWriter
    {
        private readonly MemoryStream _stream = new();

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
