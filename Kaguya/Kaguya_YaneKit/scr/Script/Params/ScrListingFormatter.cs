// ============================================================================
// ScrListingFormatter.cs
// 人类可读列表格式化器: 将 ScrFileDocument 格式化为带偏移地址的列表输出
//
// 输出格式 (非可逆, 仅用于阅读/调试):
//   header: [SCR-Ver5.3]
//   code:
//     @label:
//     00001234: jump @loc_00005678                        ; op=11 len=8 jump -> @loc_00005678
//   save:
//     @loc_XXXXXXXX
//   layer:
//     @loc_XXXXXXXX
//
// 每行显示: 十六进制偏移 + 指令文本 + 行尾注释 (opcode/长度/助记符/跳转目标)
// 使用 ScrInstructionTextCodec (CP932) 格式化指令文本
//
// 依赖: ScrInstructionTextCodec, ScrOpcodeInfo, ScrFileDocument
// 被依赖: 上层命令 (ScrCommands) 的 listing 操作
// ============================================================================
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;
using System.Text;

namespace Kaguya_YaneKit.Script.Params;

public sealed class ScrListingFormatter
{
    private readonly ScrInstructionTextCodec _instructionCodec;

    public ScrListingFormatter()
    {
        ScrInstructionTextCodec.EnsureEncodingProvider();
        var encoding = Encoding.GetEncoding(932);
        _instructionCodec = new ScrInstructionTextCodec(encoding, encoding);
    }

    public string Format(ScrFileDocument document)
    {
        using var writer = new StringWriter();
        writer.WriteLine($"header: {document.Header}");
        writer.WriteLine("code:");

        var offset = 0;
        foreach (var element in document.Script.Elements)
        {
            switch (element)
            {
                case ScriptLabel label:
                    writer.WriteLine($"  @{label.Name}:");
                    break;
                case ScriptInstruction instruction:
                    var descriptor = ScrOpcodeInfo.Get(instruction.Opcode);
                    var target = instruction.TargetLabel is null ? string.Empty : $" -> @{instruction.TargetLabel}";
                    var text = FormatInstruction(instruction);
                    writer.WriteLine($"  {offset:X8}: {text,-80} ; op={instruction.Opcode} len={instruction.DeclaredLength} {descriptor.Name}{target}");
                    offset += instruction.DeclaredLength;
                    break;
                case ScriptTail tail:
                    writer.WriteLine($"  {offset:X8}: tail len={tail.Data.Length}");
                    offset += tail.Data.Length;
                    break;
            }
        }

        writer.WriteLine("save:");
        foreach (var reference in document.SaveOffsets)
        {
            writer.WriteLine($"  {FormatReference(reference)}");
        }

        writer.WriteLine("layer:");
        foreach (var reference in document.LayerOffsets)
        {
            writer.WriteLine($"  {FormatReference(reference)}");
        }

        return writer.ToString();
    }

    private static string FormatReference(ScrOffsetReference reference)
    {
        if (reference.Label is not null)
        {
            return $"@{reference.Label}";
        }

        return $"0x{reference.RawValue.GetValueOrDefault():X8}";
    }

    private string FormatInstruction(ScriptInstruction instruction)
    {
        var builder = new System.Text.StringBuilder();
        if (_instructionCodec.TryWrite(builder, instruction))
        {
            return builder.ToString();
        }

        return $"op {instruction.Opcode} bytes=[{string.Join(",", instruction.Body)}]";
    }
}
