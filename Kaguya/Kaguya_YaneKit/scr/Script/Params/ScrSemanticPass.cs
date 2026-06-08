// ============================================================================
// ScrSemanticPass.cs
// 语义分析遍: 在指令与标签之间建立跳转关系
//
// AttachInstructionLabels (反汇编方向):
//   遍历所有指令, 对含 PC 目标的操作码 (jump/call/save/if_true/if_false),
//   读取 body 中的目标偏移, 在对应位置插入标签 (如 loc_00001234),
//   并设置 instruction.TargetLabel 指向该标签
//   算法: ScrLabelService 建立 offset->label 映射, 未命中则 EnsureLabel
//         在 Elements 列表中按字节偏移定位并插入新标签
//
// MaterializeInstructionTargets (汇编方向):
//   遍历所有指令, 将 TargetLabel 通过 ScrLabelService 解析为字节偏移,
//   写回 body 中对应位置的 u32 值
//
// 依赖: ScrLabelService, ScrOpcodeInfo, ScriptDocument, ScriptInstruction
// 被依赖: ScrContainerCodec (Read 时调用 Attach, Write 时调用 Materialize)
// ============================================================================
using System.Buffers.Binary;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Script.Params;

public static class ScrSemanticPass
{
    public static void AttachInstructionLabels(ScriptDocument script, int codeStart)
    {
        var knownOffsets = ScrLabelService.GetLabelOffsets(script);
        var labelsByOffset = knownOffsets.ToDictionary(kv => kv.Value, kv => kv.Key);

        foreach (var instruction in script.Instructions.ToList())
        {
            if (!ScrOpcodeInfo.TryGetPcTargetOffset(instruction.Opcode, instruction.Body.Length, out var operandOffset))
            {
                continue;
            }

            var targetPc = BinaryPrimitives.ReadUInt32LittleEndian(instruction.Body.AsSpan(operandOffset, 4));
            if (targetPc > int.MaxValue || targetPc < codeStart)
            {
                continue;
            }

            var pc = checked((int)targetPc - codeStart);
            if (!labelsByOffset.TryGetValue(pc, out var label))
            {
                label = ScrLabelService.MakeOffsetLabel(pc);
                if (!EnsureLabel(script, label, pc))
                {
                    continue;
                }
                labelsByOffset[pc] = label;
            }

            instruction.TargetLabel = label;
        }
    }

    public static void MaterializeInstructionTargets(ScriptDocument script, int codeStart)
    {
        var labelOffsets = ScrLabelService.GetLabelOffsets(script);
        foreach (var instruction in script.Instructions.ToList())
        {
            if (instruction.TargetLabel is null)
            {
                continue;
            }

            if (!ScrOpcodeInfo.TryGetPcTargetOffset(instruction.Opcode, instruction.Body.Length, out var operandOffset))
            {
                continue;
            }

            if (!labelOffsets.TryGetValue(instruction.TargetLabel, out var offset))
            {
                throw new InvalidDataException($"Instruction target label was not defined: {instruction.TargetLabel}");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(instruction.Body.AsSpan(operandOffset, 4), checked((uint)(offset + codeStart)));
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
}
