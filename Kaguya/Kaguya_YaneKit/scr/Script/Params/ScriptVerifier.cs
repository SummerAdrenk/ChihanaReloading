// ============================================================================
// ScriptVerifier.cs
// 脚本完整性验证器: 检查 ScriptDocument 中的结构合法性
//
// 验证规则:
//   - 标签名不得为空或空白
//   - 标签名不得重复
//   - 指令的 DeclaredLength 不得超过 u16 上限 (65535)
//
// 依赖: ScriptDocument, ScriptLabel, ScriptInstruction, ValidationResult
// 被依赖: 上层命令/交互会话调用以确保文档合法
// ============================================================================
using Kaguya_YaneKit.Core.Validation;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Script.Params;

public sealed class ScriptVerifier
{
    public ValidationResult Verify(ScriptDocument document)
    {
        var result = new ValidationResult();
        var labels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in document.Elements)
        {
            switch (element)
            {
                case ScriptLabel label:
                    if (string.IsNullOrWhiteSpace(label.Name))
                    {
                        result.AddError("Label name cannot be empty.");
                    }
                    else if (!labels.Add(label.Name))
                    {
                        result.AddError($"Duplicate label: {label.Name}");
                    }
                    break;
                case ScriptInstruction instruction:
                    if (instruction.DeclaredLength > ushort.MaxValue)
                    {
                        result.AddError($"Instruction {instruction.Opcode} is too large.");
                    }
                    break;
            }
        }

        return result;
    }
}
