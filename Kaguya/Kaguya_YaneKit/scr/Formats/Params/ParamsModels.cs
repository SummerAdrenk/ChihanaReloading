// ============================================================================
// ParamsModels.cs
// params.dat 二进制格式的数据模型定义 (支持 [SCR-PARAMS]v05.4 ~ v05.8)
//
// 顶层文档: ParamsDatDocument
//   - Header          : 版本头 "[SCR-PARAMS]v05.x"
//   - GameSystem       : 游戏系统配置 (分辨率/标题/品牌/安装表/Demo/缩略图等)
//   - Pattern          : CG/立绘资源引用模式 (Items + IntArrays + GroupTable1/2)
//   - SceneLabels      : 场景标签列表 (名称 + 两个 u32 值)
//
// GameSystem 子结构:
//   ParamsInstallEntry     -- 安装文件条目 (File + Media)
//   ParamsRawBlob          -- 原始字节载荷 (base64, 可能为位图数据)
//   ParamsOptionalSettingTag / ParamsSettingTag / ParamsSettingPair
//                          -- 可选设置树 (Name + key-value 对 + 子节点)
//   ParamsDemo / ParamsDemoCommand
//                          -- Demo 演示序列 ([Demo3.0] 命令流, 10 种命令类型)
//   ParamsThumbnail        -- 缩略图条目 (8 TypedString + 3 TypedInt)
//   ParamsRegistCgGroup / ParamsRegistCgItem
//                          -- CG 注册表 (组名 -> 项名/坐标/值)
//   ParamsRegistSceneGroup / ParamsRegistSceneItem
//                          -- 场景注册表 (组名 -> 项名/场景名列表)
//
// Pattern 子结构:
//   ParamsPatternItem      -- 资源项 (Kind: 0=单名, 1=名称列表, 2=子名+坐标, 3=子名+值)
//   ParamsPatternGroupTable / ParamsPatternGroup
//                          -- 分组表 (组名 -> IntArrays 索引列表)
//
// 依赖: System.Text.Json (序列化注解)
// 被依赖: ParamsDatCodec (读写), CharacterComposer (Pattern 消费)
// ============================================================================
using System.Text.Json.Serialization;

namespace Kaguya_YaneKit.Formats.Params;

public sealed class ParamsDatDocument
{
    public string Header { get; set; } = ParamsDatCodec.ExpectedHeader;
    public ParamsGameSystem GameSystem { get; set; } = new();
    public ParamsPattern Pattern { get; set; } = new();
    public List<ParamsSceneLabel> SceneLabels { get; set; } = [];
}

public sealed class ParamsGameSystem
{
    public ushort VersionMarker { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public List<byte> ConfigBytes { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigBytesHex { get; set; }
    public string GameTitle { get; set; } = "";
    public string DisplayTitle { get; set; } = "";
    public string Brand { get; set; } = "";
    public byte StaffFlag { get; set; }
    public string StaffName1 { get; set; } = "";
    public string StaffName2 { get; set; } = "";
    public List<ParamsInstallEntry> InstallTable { get; set; } = [];
    public uint[] V5Scalars { get; set; } = [];
    public byte V5TailByte { get; set; }
    public List<ParamsOptionalSettingTag> SettingTags { get; set; } = [];
    public uint V53TripleRawCount { get; set; }
    public ParamsRawBlob RawBlob { get; set; } = new();
    public List<ParamsDemo> Demos { get; set; } = [];
    public List<string> V51StringList { get; set; } = [];
    public uint V51PlaceCount { get; set; }
    public string V54NestedListName { get; set; } = "";
    public uint V54NestedOuterCount { get; set; }
    public List<ParamsThumbnail> Thumbnails { get; set; } = [];
    public List<string> SceneNames { get; set; } = [];
    public List<ParamsRegistCgGroup> RegistCg { get; set; } = [];
    public List<ParamsRegistSceneGroup> RegistScene { get; set; } = [];
}

public sealed class ParamsInstallEntry
{
    public string File { get; set; } = "";
    public string Media { get; set; } = "";
}

public sealed class ParamsRawBlob
{
    public string Description { get; set; } = "GameSystem byte-array payload. Loader reads length + raw bytes and stores it as array<unsigned char>.";
    public string Encoding { get; set; } = "base64";
    public uint? ExpectedWidth { get; set; }
    public uint? ExpectedHeight { get; set; }
    public uint? ExpectedBytesPerPixel { get; set; }
    public string DataBase64 { get; set; } = "";
}

public sealed class ParamsOptionalSettingTag
{
    public bool Present { get; set; }
    public ParamsSettingTag? Root { get; set; }
}

public sealed class ParamsSettingTag
{
    public string Name { get; set; } = "";
    public List<ParamsSettingPair> Pairs { get; set; } = [];
    public List<ParamsSettingTag> Children { get; set; } = [];
}

public sealed class ParamsSettingPair
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class ParamsDemo
{
    public string Name { get; set; } = "";
    public List<ParamsDemoCommand> Commands { get; set; } = [];
}

public sealed class ParamsDemoCommand
{
    public byte Type { get; set; }
    public string? TypeName { get; set; }
    public string? RawPayloadHex { get; set; }

    public byte? ModeOrFlag { get; set; }
    public uint? Value { get; set; }

    public byte? FieldA { get; set; }
    public byte? FieldB { get; set; }
    public string? Name { get; set; }

    public byte? SlotOrLayer { get; set; }
    public string? Effect { get; set; }
    public string? Arg { get; set; }

    public byte? RawLayer { get; set; }
    public bool? Visible { get; set; }

    public byte? IdOrLayer { get; set; }
    public uint? DurationOrValue { get; set; }
    public uint? Value1 { get; set; }
    public uint? Value2 { get; set; }
    public uint? Value3 { get; set; }
    public uint? Value4 { get; set; }
    public uint? Value5 { get; set; }
}

public sealed class ParamsThumbnail
{
    public List<string> Strings { get; set; } = [];
    public List<uint> Ints { get; set; } = [];
}

public sealed class ParamsRegistCgGroup
{
    public string GroupName { get; set; } = "";
    public List<ParamsRegistCgItem> Items { get; set; } = [];
}

public sealed class ParamsRegistCgItem
{
    public string ItemName { get; set; } = "";
    public uint X { get; set; }
    public uint Y { get; set; }
    public uint Value { get; set; }
}

public sealed class ParamsRegistSceneGroup
{
    public string GroupName { get; set; } = "";
    public List<ParamsRegistSceneItem> Items { get; set; } = [];
}

public sealed class ParamsRegistSceneItem
{
    public string ItemName { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CgName { get; set; }
    public List<string> Scenes { get; set; } = [];
}

public sealed class ParamsPattern
{
    public List<ParamsPatternItem> Items { get; set; } = [];
    public List<List<uint>> IntArrays { get; set; } = [];
    public ParamsPatternGroupTable GroupTable1 { get; set; } = new();
    public ParamsPatternGroupTable GroupTable2 { get; set; } = new();
}

public sealed class ParamsPatternItem
{
    public string Name { get; set; } = "";
    public byte Kind { get; set; }
    public List<string> Strings { get; set; } = [];
    public string? SubName { get; set; }
    public uint? X { get; set; }
    public uint? Y { get; set; }
    public uint? Value { get; set; }
}

public sealed class ParamsPatternGroupTable
{
    public List<ParamsPatternGroup> Groups { get; set; } = [];
}

public sealed class ParamsPatternGroup
{
    public string Name { get; set; } = "";
    public List<uint> Indices { get; set; } = [];
}

public sealed class ParamsSceneLabel
{
    public string Name { get; set; } = "";
    public uint Value1 { get; set; }
    public uint Value2 { get; set; }
}
