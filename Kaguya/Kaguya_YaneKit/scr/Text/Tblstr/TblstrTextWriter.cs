using System.Text;

namespace Kaguya_YaneKit.Text.Tblstr;

public static class TblstrTextWriter
{
    public static string Write(TblstrDocument document, TblstrScriptMap? map = null)
    {
        var builder = new StringBuilder();
        WriteSectionHeader(builder, "TBLSTR 文本");
        builder.AppendLine($"// Magic: {document.Magic}");
        builder.AppendLine($"// Version: {document.Version}");
        builder.AppendLine($"// Entries: {document.Entries.Count}");
        builder.AppendLine(map is null
            ? "// 未提供 SCR 引用图，无法可靠区分 name/msg/choice。"
            : $"// SCR 引用图: name={map.NameIndices.Count} msg={map.MessageIndices.Count} choice={map.ChoiceIndices.Count} unreferenced={map.UnreferencedIndices.Count}");
        builder.AppendLine();

        if (map is not null)
        {
            for (var index = 0; index < document.Entries.Count; index++)
            {
                if (map.NameIndices.Contains(index))
                {
                    WriteName(builder, document, index);
                }
                else if (map.ChoiceIndices.Contains(index))
                {
                    WriteChoice(builder, document, index);
                }
                else if (map.MessageIndices.Contains(index))
                {
                    WriteDialogueMessage(builder, document, index, "msg");
                    builder.AppendLine();
                }
                else
                {
                    WriteUnknown(builder, document, index);
                }
            }

            return builder.ToString();
        }

        foreach (var entry in document.Entries)
        {
            WriteUnknown(builder, document, entry.Index);
        }

        return builder.ToString();
    }

    public static void WriteName(StringBuilder builder, TblstrDocument document, int index)
    {
        var text = Escape(GetText(document, index));
        builder.AppendLine($"◇T{index:X8}◇name◇{text}");
        builder.AppendLine($"◆T{index:X8}◆name◆{text}");
        builder.AppendLine();
    }

    public static void WriteChoice(StringBuilder builder, TblstrDocument document, int index, TblstrChoiceBranchRange? range = null)
    {
        var text = Escape(GetText(document, index));
        if (range is not null)
        {
            builder.AppendLine($"// choice-range: T{range.StartMessageIndex:X8} -> T{range.EndMessageIndex:X8}");
        }

        builder.AppendLine($"◇T{index:X8}◇choice◇{text}");
        builder.AppendLine($"◆T{index:X8}◆choice◆{text}");
        builder.AppendLine();
    }

    public static void WriteDialogueName(StringBuilder builder, TblstrDocument document, int speakerIndex)
    {
        var text = Escape(GetText(document, speakerIndex));
        builder.AppendLine($"◇T{speakerIndex:X8}◇name◇{text}");
        builder.AppendLine($"◆T{speakerIndex:X8}◆name◆{text}");
        builder.AppendLine();
    }

    public static void WriteDialogueMessage(StringBuilder builder, TblstrDocument document, int index, string kind)
    {
        var text = Escape(GetText(document, index));
        var tag = kind == "alternate-message" ? "alt-msg" : "msg";
        builder.AppendLine($"◇T{index:X8}◇{tag}◇{text}");
        builder.AppendLine($"◆T{index:X8}◆{tag}◆{text}");
    }

    public static void WriteUnknown(StringBuilder builder, TblstrDocument document, int index)
    {
        var entry = GetEntry(document, index);
        var text = Escape(entry?.Text ?? "");
        if (entry is not null)
        {
            builder.AppendLine($"// TBLSTR[{index:D5}]: offset=0x{entry.AbsoluteOffset:X} len={entry.ByteLength} meta0={entry.Meta0} meta1={entry.Meta1}");
        }

        builder.AppendLine($"◇T{index:X8}◇unknown◇{text}");
        builder.AppendLine($"◆T{index:X8}◆unknown◆{text}");
        builder.AppendLine();
    }

    private static string GetText(TblstrDocument document, int index) =>
        GetEntry(document, index)?.Text ?? "";

    private static TblstrEntry? GetEntry(TblstrDocument document, int index) =>
        index >= 0 && index < document.Entries.Count
            ? document.Entries[index]
            : document.Entries.FirstOrDefault(entry => entry.Index == index);

    private static void WriteSectionHeader(StringBuilder builder, string title)
    {
        builder.AppendLine("//==================================================");
        builder.AppendLine($"; {title}");
        builder.AppendLine("//==================================================");
    }

    private static string Escape(string value) =>
        value.Replace("\n", "\\n", StringComparison.Ordinal);
}
