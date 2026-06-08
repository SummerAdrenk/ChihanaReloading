// ============================================================================
// ScriptBinaryCodec.cs
// 脚本二进制编解码器: 将原始字节码与 ScriptDocument 模型互相转换
//
// 指令二进制格式 (每条指令):
//   [u16 opcode] [u16 length] [body: length-4 bytes]
//   length 包含 4 字节头自身, 最小值为 4
//
// Read: 顺序扫描字节流, 每次读取 4 字节头解析 opcode 和 length,
//       不足 4 字节或 length 异常时将剩余数据存为 ScriptTail
// Write: 遍历 Elements, 依次写出 ScriptInstruction 和 ScriptTail
// VerifyRoundTrip: 写出后重新读取, 比对指令数量以验证无损往返
//
// 依赖: ScriptDocument, ScriptInstruction, ScriptTail, ValidationResult
// 被依赖: ScrContainerCodec (容器层调用此编解码器处理代码段)
// ============================================================================
using System.Buffers.Binary;
using Kaguya_YaneKit.Core.Validation;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Script.Params;

public sealed class ScriptBinaryCodec
{
    public ScriptDocument Read(ReadOnlySpan<byte> source, string? sourceName = null)
    {
        var document = new ScriptDocument { SourceName = sourceName };
        var offset = 0;

        while (source.Length - offset >= 4)
        {
            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, 2));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset + 2, 2));

            if (length < 4 || source.Length - offset < length)
            {
                document.AddTail(source[offset..].ToArray());
                return document;
            }

            var body = source.Slice(offset + 4, length - 4).ToArray();
            var instruction = document.AddInstruction(opcode, body);
            instruction.OriginalLength = length;
            instruction.Metadata["offset"] = offset;
            offset += length;
        }

        if (offset < source.Length)
        {
            document.AddTail(source[offset..].ToArray());
        }

        return document;
    }

    public byte[] Write(ScriptDocument document)
    {
        using var stream = new MemoryStream();

        foreach (var element in document.Elements)
        {
            switch (element)
            {
                case ScriptInstruction instruction:
                    WriteInstruction(stream, instruction);
                    break;
                case ScriptTail tail:
                    stream.Write(tail.Data);
                    break;
            }
        }

        return stream.ToArray();
    }

    public ValidationResult VerifyRoundTrip(ScriptDocument document)
    {
        var result = new ValidationResult();
        var bytes = Write(document);
        var replay = Read(bytes, document.SourceName);

        var instructionCount = document.Instructions.Count();
        var replayCount = replay.Instructions.Count();
        if (instructionCount != replayCount)
        {
            result.AddError($"Instruction count changed after roundtrip: {instructionCount} -> {replayCount}.");
        }

        return result;
    }

    private static void WriteInstruction(Stream stream, ScriptInstruction instruction)
    {
        var body = instruction.Body;
        var length = checked((ushort)(4 + body.Length));
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, instruction.Opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], length);
        stream.Write(header);
        if (body.Length > 0)
        {
            stream.Write(body);
        }
    }
}
