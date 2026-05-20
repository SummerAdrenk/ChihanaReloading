// ============================================================================
// ScrContainerCodec.cs
// SCR 容器格式编解码: 处理完整的 .scr 文件读写
//
// SCR 文件二进制结构:
//   [Header]         ASCII 文件头, 如 "[SCR-Ver5.3]", 以 ']' 结尾
//   [u32 codeSize]   代码段字节长度
//   [Code section]   指令序列 (4 字节头: opcode u16 + length u16 + body)
//   [Save section]   "[SAVE]" 魔数 + u32 数量 + u32[] 偏移 (FileAbsolute)
//   [Layer section]  "[LAYER]" 魔数 + u32 数量 + u32[] 偏移 (CodeRelative)
//   [Container tail] 容器尾部数据
//
// Read 流程: 解析头 -> ScriptBinaryCodec 解码代码段 -> 读偏移表并创建
//            标签引用 -> ScrSemanticPass 附加指令跳转标签
// Write 流程: ScrSemanticPass 物化标签偏移 -> ScriptBinaryCodec 编码
//             代码段 -> ScrLabelService 计算标签映射 -> 写出各段
//
// 依赖: ScriptBinaryCodec, ScrSemanticPass, ScrLabelService
// 被依赖: ScrTextCodec (文本格式转换), 上层命令/交互会话
// ============================================================================
using System.Buffers.Binary;
using System.Text;
using Kaguya_YaneKit.Scr.Model;

namespace Kaguya_YaneKit.Scr;

public sealed class ScrContainerCodec
{
    private static readonly byte[] SaveMagic = Encoding.ASCII.GetBytes("[SAVE]");
    private static readonly byte[] LayerMagic = Encoding.ASCII.GetBytes("[LAYER]");
    private readonly ScriptBinaryCodec _scriptCodec = new();

    public ScrFileDocument Read(ReadOnlySpan<byte> source, string? sourceName = null)
    {
        var headerEnd = source.IndexOf((byte)']');
        if (headerEnd < 0)
        {
            throw new InvalidDataException("SCR header terminator was not found.");
        }

        var header = Encoding.ASCII.GetString(source[..(headerEnd + 1)]);
        var offset = headerEnd + 1;
        EnsureAvailable(source, offset, 4, "code size");
        var codeSize = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
        offset += 4;

        EnsureAvailable(source, offset, checked((int)codeSize), "bytecode");
        var codeBytes = source.Slice(offset, checked((int)codeSize));
        offset += checked((int)codeSize);

        var document = new ScrFileDocument
        {
            Header = header,
            Script = _scriptCodec.Read(codeBytes, sourceName)
        };

        ReadOffsetTable(source, ref offset, SaveMagic, document.SaveOffsets, document.Script, ScrOffsetEncoding.FileAbsolute, offset - checked((int)codeSize));
        ReadOffsetTable(source, ref offset, LayerMagic, document.LayerOffsets, document.Script, ScrOffsetEncoding.CodeRelative, 0);
        ScrSemanticPass.AttachInstructionLabels(document.Script);

        if (offset < source.Length)
        {
            document.Tail = source[offset..].ToArray();
        }

        return document;
    }

    public byte[] Write(ScrFileDocument document)
    {
        ScrSemanticPass.MaterializeInstructionTargets(document.Script);
        var codeBytes = _scriptCodec.Write(document.Script);
        var labelOffsets = ScrLabelService.GetLabelOffsets(document.Script);

        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(document.Header));
        WriteU32(stream, checked((uint)codeBytes.Length));
        stream.Write(codeBytes);
        var codeStart = Encoding.ASCII.GetByteCount(document.Header) + 4;
        WriteOffsetTable(stream, SaveMagic, document.SaveOffsets, labelOffsets, codeStart);
        WriteOffsetTable(stream, LayerMagic, document.LayerOffsets, labelOffsets, 0);
        if (document.Tail.Length > 0)
        {
            stream.Write(document.Tail);
        }

        return stream.ToArray();
    }

    private static void ReadOffsetTable(
        ReadOnlySpan<byte> source,
        ref int offset,
        ReadOnlySpan<byte> magic,
        List<ScrOffsetReference> target,
        ScriptDocument script,
        ScrOffsetEncoding encoding,
        int baseOffset)
    {
        EnsureAvailable(source, offset, magic.Length + 4, Encoding.ASCII.GetString(magic));
        if (!source.Slice(offset, magic.Length).SequenceEqual(magic))
        {
            throw new InvalidDataException($"Expected {Encoding.ASCII.GetString(magic)} section.");
        }

        offset += magic.Length;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
        offset += 4;

        EnsureAvailable(source, offset, checked((int)count * 4), "offset table");
        for (var i = 0; i < count; i++)
        {
            var raw = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
            offset += 4;
            var pc = checked((int)raw - baseOffset);
            if (pc >= 0 && EnsureLabel(script, ScrLabelService.MakeOffsetLabel(pc), pc))
            {
                target.Add(ScrOffsetReference.FromLabel(ScrLabelService.MakeOffsetLabel(pc), encoding));
            }
            else
            {
                target.Add(ScrOffsetReference.FromRaw(raw, encoding));
            }
        }
    }

    private static void WriteOffsetTable(
        Stream stream,
        ReadOnlySpan<byte> magic,
        IEnumerable<ScrOffsetReference> refs,
        IReadOnlyDictionary<string, int> labelOffsets,
        int baseOffset)
    {
        stream.Write(magic);
        var materialized = refs.ToList();
        WriteU32(stream, checked((uint)materialized.Count));
        foreach (var reference in materialized)
        {
            if (reference.RawValue is { } raw)
            {
                WriteU32(stream, raw);
                continue;
            }

            if (reference.Label is null || !labelOffsets.TryGetValue(reference.Label, out var offset))
            {
                throw new InvalidDataException($"Label was referenced but not defined: {reference.Label}");
            }

            WriteU32(stream, checked((uint)(offset + baseOffset)));
        }
    }

    private static bool EnsureLabel(ScriptDocument script, string label, int targetOffset)
    {
        var offset = 0;
        for (var i = 0; i < script.Elements.Count; i++)
        {
            if (offset == targetOffset)
            {
                if (script.Elements[i] is ScriptLabel current && current.Name == label)
                {
                    return true;
                }

                if (i == 0 || script.Elements[i - 1] is not ScriptLabel existing || existing.Name != label)
                {
                    script.Elements.Insert(i, new ScriptLabel { Name = label });
                }

                return true;
            }

            switch (script.Elements[i])
            {
                case ScriptInstruction instruction:
                    offset += instruction.DeclaredLength;
                    break;
                case ScriptTail tail:
                    offset += tail.Data.Length;
                    break;
            }
        }

        if (offset == targetOffset)
        {
            script.AddLabel(label);
            return true;
        }

        return false;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> source, int offset, int count, string name)
    {
        if (count < 0 || source.Length - offset < count)
        {
            throw new EndOfStreamException($"Unexpected EOF while reading {name}.");
        }
    }
}
