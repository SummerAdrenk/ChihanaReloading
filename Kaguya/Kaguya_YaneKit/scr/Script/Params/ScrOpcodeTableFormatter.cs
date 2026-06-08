using System.Text;

namespace Kaguya_YaneKit.Script.Params;

public sealed class ScrOpcodeTableFormatter
{
    public string FormatMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SCR Opcode Schema");
        builder.AppendLine();
        builder.AppendLine("Source of truth: `scr/Scene/ScrOpcodeInfo.cs`.");
        builder.AppendLine();
        builder.AppendLine("Instruction stream format:");
        builder.AppendLine();
        builder.AppendLine("```text");
        builder.AppendLine("u16le opcode");
        builder.AppendLine("u16le instrLen       ; includes the 4-byte instruction header");
        builder.AppendLine("byte[instrLen - 4] body");
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("| opcode | mnemonic | byte_pattern | length | operand_schema | variants | sub_opcode |");
        builder.AppendLine("| ---: | --- | --- | --- | --- | --- | --- |");

        foreach (var descriptor in ScrOpcodeInfo.All())
        {
            builder.Append("| ");
            builder.Append(descriptor.Opcode);
            builder.Append(" | ");
            builder.Append(Escape(descriptor.Name));
            builder.Append(" | ");
            builder.Append($"u16le 0x{descriptor.Opcode:X4}");
            builder.Append(" | ");
            builder.Append(Escape(FormatLength(descriptor)));
            builder.Append(" | ");
            builder.Append(Escape(FormatOperands(descriptor.Operands)));
            builder.Append(" | ");
            builder.Append(Escape(FormatVariants(descriptor.Variants)));
            builder.Append(" | ");
            builder.Append(Escape(FormatSubOpcodes(descriptor.SubOpcodes)));
            builder.AppendLine(" |");
        }

        return builder.ToString();
    }

    private static string FormatLength(ScrOpcodeDescriptor descriptor)
    {
        if (descriptor.LengthKind == ScrLengthKind.Fixed)
        {
            var bodyLength = descriptor.ExpectedBodyLength ?? 0;
            return $"fixed: instrLen={bodyLength + 4}, body={bodyLength}";
        }

        return $"{descriptor.LengthKind}: {descriptor.LengthRule}";
    }

    private static string FormatOperands(IReadOnlyList<ScrOperandSchema> operands)
    {
        if (operands.Count == 0)
        {
            return "-";
        }

        return string.Join("; ", operands.Select(FormatOperand));
    }

    private static string FormatOperand(ScrOperandSchema operand)
    {
        var size = operand.Size is { } fixedSize
            ? fixedSize.ToString()
            : operand.LengthFrom is { } lengthFrom
                ? $"from {lengthFrom}"
                : "rest";
        var type = operand.Type == ScrOperandType.PcTarget
            ? "PcTarget(file_offset)"
            : operand.Type.ToString();
        return $"{operand.Name}:{type}@+{operand.Offset}[{size}]";
    }

    private static string FormatVariants(IReadOnlyList<ScrOpcodeVariant> variants)
    {
        if (variants.Count == 0)
        {
            return "-";
        }

        return string.Join("; ", variants.Select(x => $"{x.Name} when {x.Condition}: {FormatOperands(x.Operands)}"));
    }

    private static string FormatSubOpcodes(IReadOnlyList<ScrSubOpcode> subOpcodes)
    {
        if (subOpcodes.Count == 0)
        {
            return "-";
        }

        return string.Join("; ", subOpcodes.Select(x => $"{x.Value}=program.{x.Name}"));
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
