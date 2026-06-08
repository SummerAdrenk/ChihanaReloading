// ============================================================================
// MessageVer3Document.cs
// 旧式 [SCR-MESSAGE]ver + u8 version=3 的独立数据模型
// ============================================================================
namespace Kaguya_YaneKit.Text.MessageDat.Model;

public sealed class MessageVer3Document
{
    public const string MagicPrefix = "[SCR-MESSAGE]ver";
    public const byte Version2 = 2;
    public const byte Version3 = 3;

    public byte Version { get; set; } = Version3;
    public bool HeaderHasXorKey { get; set; } = true;
    public bool Encrypted { get; set; }
    public byte EncryptionFlag { get; set; }
    public byte XorKey { get; set; }
    public List<MessageVer3Block> Blocks { get; } = [];
}

public sealed class MessageVer3Block
{
    public string FormatName { get; set; } = "";
    public byte[] RawFormatNameBytes { get; set; } = [];
    public List<MessageVer3Item> Items { get; } = [];
}

public sealed class MessageVer3Item
{
    public List<MessageVer3String> Voices { get; } = [];
    public MessageVer3String Message { get; set; } = new();
}

public sealed class MessageVer3String
{
    public string Text { get; set; } = "";
    public byte[] RawBytes { get; set; } = [];
}
