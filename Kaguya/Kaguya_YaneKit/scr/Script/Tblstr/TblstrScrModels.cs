using System.Text;

namespace Kaguya_YaneKit.Script.Tblstr;

public sealed class TblstrScrDocument
{
    public string SourceName { get; set; } = "";
    public uint Magic0 { get; set; }
    public uint Magic1 { get; set; }
    public int PayloadSize { get; set; }
    public byte[] Payload { get; set; } = [];
    public List<TblstrScrInstruction> Instructions { get; set; } = [];
    public Dictionary<int, List<string>> LabelsByOffset { get; set; } = [];
}

public sealed class TblstrScrInstruction
{
    public int Offset { get; set; }
    public int Opcode { get; set; }
    public int BaseLength { get; set; }
    public int ExtraLength { get; set; }
    public int TotalLength => BaseLength + ExtraLength;
    public byte[] RawBytes { get; set; } = [];
    public byte[] BaseOperandBytes { get; set; } = [];
    public List<TblstrScrStringImmediate> Strings { get; set; } = [];
    public TblstrScrOpcodeDescriptor Descriptor { get; set; } = TblstrScrOpcodeTable.Unknown;
}

public sealed class TblstrScrStringImmediate
{
    public string Name { get; set; } = "";
    public int LengthOffset { get; set; }
    public int DeclaredLength { get; set; }
    public int DataOffset { get; set; }
    public byte[] EncodedBytes { get; set; } = [];
    public string Text { get; set; } = "";
}

public sealed class TblstrScrScanSummary
{
    public int FileCount { get; set; }
    public int InstructionCount { get; set; }
    public Dictionary<int, int> OpcodeCounts { get; } = [];
    public Dictionary<int, int> BaseLengthCounts { get; } = [];
    public List<string> Issues { get; } = [];

    public void AddInstruction(TblstrScrInstruction instruction)
    {
        InstructionCount++;
        OpcodeCounts[instruction.Opcode] = OpcodeCounts.GetValueOrDefault(instruction.Opcode) + 1;
        BaseLengthCounts[instruction.BaseLength] = BaseLengthCounts.GetValueOrDefault(instruction.BaseLength) + 1;
    }
}

public readonly record struct TblstrLabelEntry(string ScriptName, string Label, int TargetOffset);

internal static class TblstrScrText
{
    public static string DecodeInlineString(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var decoded = new byte[bytes.Length];
        var count = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0xFF)
            {
                break;
            }

            decoded[count++] = unchecked((byte)~bytes[i]);
        }

        return encoding.GetString(decoded, 0, count);
    }
}
