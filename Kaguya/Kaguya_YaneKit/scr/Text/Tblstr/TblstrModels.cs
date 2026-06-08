using System.Text.Json.Serialization;

namespace Kaguya_YaneKit.Text.Tblstr;

public sealed class TblstrDocument
{
    public string Format { get; set; } = "TBLSTR";
    public string Magic { get; set; } = "";
    public string Version { get; set; } = "";
    public TblstrHeader Header { get; set; } = new();
    public List<TblstrEntry> Entries { get; set; } = [];
}

public sealed class TblstrHeader
{
    public uint RecordTableOffset { get; set; }
    public uint HeaderReserved08 { get; set; }
    public uint HeaderField0C { get; set; }
    public int EntryCount { get; set; }
}

public sealed class TblstrEntry
{
    public int Index { get; set; }
    public uint RelativeOffset { get; set; }
    public long AbsoluteOffset { get; set; }
    public int ByteLength { get; set; }
    public uint Meta0 { get; set; }
    public uint Meta1 { get; set; }
    public string Text { get; set; } = "";
    [JsonIgnore]
    public string OriginalText { get; set; } = "";
    [JsonIgnore]
    public byte[] OriginalTextBytes { get; set; } = [];
}
