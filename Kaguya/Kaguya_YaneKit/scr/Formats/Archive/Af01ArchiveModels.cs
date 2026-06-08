namespace Kaguya_YaneKit.Formats.Archive;

public sealed class Af01ArchiveHeader
{
    public string Magic { get; set; } = "AF01";
    public uint Version { get; set; } = 1;
    public uint IndexBaseOffset { get; set; }
    public long IndexOffset { get; set; }
}

public sealed class Af01ArchiveEntry
{
    public string Name { get; set; } = "";
    public string StoredName { get; set; } = "";
    public ushort Flags { get; set; }
    public bool IsPacked { get; set; }
    public long EntryHeaderOffset { get; set; }
    public long DataOffset { get; set; }
    public uint PackedSize { get; set; }
    public uint UnpackedSize { get; set; }
    public uint StoredSize { get; set; }
}

public sealed class Af01ArchiveManifest
{
    public string Format { get; set; } = "AF01";
    public Af01ArchiveHeader Header { get; set; } = new();
    public List<Af01ArchiveEntry> Entries { get; set; } = [];
}
