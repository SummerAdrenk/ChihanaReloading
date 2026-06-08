namespace Kaguya_YaneKit.Formats.Pe;

public sealed class PeStringDumpDocument
{
    public string Format { get; set; } = "kaguya-pe-strings-v1";
    public string SourcePath { get; set; } = "";
    public string ImageBase { get; set; } = "";
    public string EncodingName { get; set; } = "cp932";
    public int MinBytes { get; set; } = 4;
    public List<PeSectionInfo> Sections { get; set; } = [];
    public List<PeStringEntry> Entries { get; set; } = [];
}

public sealed class PeSectionInfo
{
    public string Name { get; set; } = "";
    public string Rva { get; set; } = "";
    public string VirtualSize { get; set; } = "";
    public string RawOffset { get; set; } = "";
    public string RawSize { get; set; } = "";
    public string Characteristics { get; set; } = "";
}

public sealed class PeStringEntry
{
    public string Id { get; set; } = "";
    public string Section { get; set; } = "";
    public string Rva { get; set; } = "";
    public string Va { get; set; } = "";
    public string FileOffset { get; set; } = "";
    public int ByteLength { get; set; }
    public string RawHex { get; set; } = "";
    public string Original { get; set; } = "";
    public string Translated { get; set; } = "";
    public List<PeStringReference> Refs { get; set; } = [];
    public bool NeedsLengthPatch { get; set; }
    public string Status { get; set; } = "confirmed";
}

public sealed class PeStringReference
{
    public string Section { get; set; } = "";
    public string Rva { get; set; } = "";
    public string Va { get; set; } = "";
    public string FileOffset { get; set; } = "";
    public string Kind { get; set; } = "absolute_va";
    public PeLengthPatch? LengthPatch { get; set; }
}

public sealed class PeLengthPatch
{
    public string FileOffset { get; set; } = "";
    public string Rva { get; set; } = "";
    public string Va { get; set; } = "";
    public string Encoding { get; set; } = "push_imm8";
    public int OriginalLength { get; set; }
}
