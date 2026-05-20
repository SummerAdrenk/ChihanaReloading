// ============================================================================
// LinkArchiveModels.cs
// LINK 档案格式数据模型 (支持 LINK3/4/5/6)
//
// 模型层次:
//   LinkArchiveManifest
//     +-- LinkArchiveHeader  -- 档案头信息
//     |     Magic      : "LINK3"/"LINK4"/"LINK5"/"LINK6"
//     |     Version    : 3~6
//     |     Flags      : LINK4/5/6 的 u16 标志位
//     |     ArchiveName: 3 字节 (LINK3~5) 或变长 (LINK6)
//     |     HeaderSize : 头部总字节数
//     +-- List<LinkArchiveEntry>  -- 文件条目列表
//           Name        : 文件名 (LINK3~5: Shift-JIS, LINK6: UTF-16LE)
//           EntryOffset : 块起始偏移
//           ChunkSize   : 块总大小 (含头)
//           EntryFlags  : 条目标志 (bit0-1=压缩, bit2=加密)
//           Year~Second : 文件时间戳
//           LegacyExtra : LINK3~5 专用的 2 字节附加数据
//           DataOffset  : 数据区起始偏移
//           DataSize    : 数据区大小
//           IsCompressed: 是否需要 BMR 解压
//
// 被依赖: LinkArchiveCodec (读写), LinkArchiveManifestWriter (JSON 序列化)
// ============================================================================
namespace Kaguya_YaneKit.Formats.Archive;

public sealed class LinkArchiveHeader
{
    public string Magic { get; set; } = "";
    public int Version { get; set; }
    public ushort Flags { get; set; }
    public string ArchiveName { get; set; } = "";
    public long HeaderSize { get; set; }
}

public sealed class LinkArchiveEntry
{
    public string Name { get; set; } = "";
    public long EntryOffset { get; set; }
    public uint ChunkSize { get; set; }
    public ushort EntryFlags { get; set; }
    public ushort Year { get; set; }
    public byte Month { get; set; }
    public byte Day { get; set; }
    public byte Hour { get; set; }
    public byte Minute { get; set; }
    public byte Second { get; set; }
    public byte[] LegacyExtra { get; set; } = [];
    public long DataOffset { get; set; }
    public uint DataSize { get; set; }
    public bool IsCompressed { get; set; }
}

public sealed class LinkArchiveManifest
{
    public LinkArchiveHeader Header { get; set; } = new();
    public List<LinkArchiveEntry> Entries { get; set; } = [];
}
