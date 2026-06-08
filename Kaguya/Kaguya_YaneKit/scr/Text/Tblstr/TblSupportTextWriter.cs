using System.Text;

namespace Kaguya_YaneKit.Text.Tblstr;

public static class TblSupportTextWriter
{
    public static string Write(TblSupportDocument document)
    {
        return document.Kind switch
        {
            "value" or "globalvalue" => WriteLineTable(document),
            "label" => WriteLabelTable(document),
            "eventfg" => WriteEventFgTable(document),
            _ => throw new InvalidDataException($"Unsupported TBL kind: {document.Kind}")
        };
    }

    private static string WriteLineTable(TblSupportDocument document)
    {
        var table = document.LineTable ?? throw new InvalidDataException("Missing line table.");
        var builder = new StringBuilder();
        builder.AppendLine("//==================================================");
        builder.AppendLine($"; TBL line table: {document.FileName}");
        builder.AppendLine("; binary rule: line bytes are bitwise NOT, CRLF is plain");
        builder.AppendLine("//==================================================");
        foreach (var entry in table.Entries)
        {
            var id = $"T{entry.Index:X8}";
            var text = Escape(entry.Name);
            builder.AppendLine($"◇{id}◇{document.Kind}◇{text}");
            builder.AppendLine($"◆{id}◆{document.Kind}◆{text}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string WriteLabelTable(TblSupportDocument document)
    {
        var table = document.LabelTable ?? throw new InvalidDataException("Missing label table.");
        var builder = new StringBuilder();
        builder.AppendLine("# label.tbl");
        builder.AppendLine("# index\tscript_file\tlabel\ttarget_offset");
        foreach (var entry in table.Entries)
        {
            builder.Append(entry.Index.ToString("D8"));
            builder.Append('\t');
            builder.Append(EscapeCell(entry.ScriptFile));
            builder.Append('\t');
            builder.Append(EscapeCell(entry.Label));
            builder.Append('\t');
            builder.Append(entry.TargetOffset);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string WriteEventFgTable(TblSupportDocument document)
    {
        var table = document.EventFgTable ?? throw new InvalidDataException("Missing EventFg table.");
        var builder = new StringBuilder();
        builder.AppendLine($"# EventFg.tbl");
        builder.AppendLine($"# xor_key=0x{table.XorKey:X2}");
        builder.AppendLine("# field0/field1 are confirmed structural i32 fields; business names are not fixed yet.");
        builder.AppendLine("[Characters]");
        foreach (var character in table.Characters)
        {
            builder.AppendLine($"Character\t{character.Index:D4}\t{EscapeCell(character.Name)}");
            foreach (var slot in character.Slots)
            {
                builder.AppendLine($"Slot\t{character.Index:D4}\t{slot.Index:D4}\t{EscapeCell(slot.Name)}");
                foreach (var evt in slot.Events)
                {
                    builder.AppendLine($"Event\t{character.Index:D4}\t{slot.Index:D4}\t{evt.Index:D4}\t{evt.Field0}\t{evt.Field1}\t{EscapeCell(evt.Name)}");
                }
            }
        }

        builder.AppendLine("[Kaisou]");
        foreach (var kaisou in table.Kaisou)
        {
            builder.AppendLine($"Kaisou\t{kaisou.Index:D4}\t{EscapeCell(kaisou.Name)}");
            foreach (var slot in kaisou.Slots)
            {
                builder.AppendLine($"KaiSlot\t{kaisou.Index:D4}\t{slot.Index:D4}\t{slot.Field0}\t{EscapeCell(slot.SlotName)}\t{EscapeCell(slot.ScriptName)}");
            }
        }

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeCell(string value) =>
        Escape(value).Replace("\t", "\\t", StringComparison.Ordinal);
}
