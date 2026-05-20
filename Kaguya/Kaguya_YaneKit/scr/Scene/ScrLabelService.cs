// ============================================================================
// ScrLabelService.cs
// 标签偏移映射服务: 计算标签名到代码段字节偏移的映射表
//
// GetLabelOffsets: 遍历 ScriptDocument.Elements, 累加 Instruction 和
//                  Tail 的字节长度, 记录每个 Label 出现时的当前偏移
// MakeOffsetLabel: 生成标准格式标签名 "loc_XXXXXXXX" (8 位大写十六进制)
//
// 依赖: ScriptDocument, ScriptLabel, ScriptInstruction, ScriptTail
// 被依赖: ScrContainerCodec (写出时解析标签), ScrSemanticPass (读取时建立映射)
// ============================================================================
using Kaguya_YaneKit.Scr.Model;

namespace Kaguya_YaneKit.Scr;

public static class ScrLabelService
{
    public static Dictionary<string, int> GetLabelOffsets(ScriptDocument document)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var offset = 0;

        foreach (var element in document.Elements)
        {
            switch (element)
            {
                case ScriptLabel label:
                    result[label.Name] = offset;
                    break;
                case ScriptInstruction instruction:
                    offset += instruction.DeclaredLength;
                    break;
                case ScriptTail tail:
                    offset += tail.Data.Length;
                    break;
            }
        }

        return result;
    }

    public static string MakeOffsetLabel(int offset) => $"loc_{offset:X8}";
}
