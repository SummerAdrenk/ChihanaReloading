// ============================================================================
// ScriptDocument.cs
// 脚本文档容器: 持有一个 SCR 代码段中所有元素的有序列表
//
// Elements 列表包含四种元素类型:
//   ScriptInstruction - 指令 (参与二进制编码)
//   ScriptLabel       - 标签 (逻辑锚点, 不占二进制空间)
//   ScriptComment     - 注释 (仅文本格式保留)
//   ScriptTail        - 尾部数据 (剩余字节)
//
// 提供 AddLabel / AddComment / AddInstruction / AddTail 工厂方法
//
// 依赖: ScriptElement 及其所有派生类
// 被依赖: ScrFileDocument, ScriptBinaryCodec, ScrSemanticPass,
//         ScrLabelService, ScrTextCodec, ScriptVerifier, ScrListingFormatter
// ============================================================================
namespace Kaguya_YaneKit.Scr.Model;

public sealed class ScriptDocument
{
    private readonly List<ScriptElement> _elements = [];

    public string? SourceName { get; set; }

    public List<ScriptElement> Elements => _elements;

    public IEnumerable<ScriptInstruction> Instructions => _elements.OfType<ScriptInstruction>();

    public IEnumerable<ScriptLabel> Labels => _elements.OfType<ScriptLabel>();

    public ScriptLabel AddLabel(string name, string? comment = null)
    {
        var label = new ScriptLabel { Name = name, Comment = comment };
        _elements.Add(label);
        return label;
    }

    public ScriptComment AddComment(string text)
    {
        var comment = new ScriptComment { Text = text };
        _elements.Add(comment);
        return comment;
    }

    public ScriptInstruction AddInstruction(ushort opcode, byte[]? body = null, string? comment = null, string? targetLabel = null)
    {
        var instruction = new ScriptInstruction
        {
            Opcode = opcode,
            Body = body ?? [],
            Comment = comment,
            TargetLabel = targetLabel
        };
        _elements.Add(instruction);
        return instruction;
    }

    public ScriptTail AddTail(byte[]? data = null)
    {
        var tail = new ScriptTail { Data = data ?? [] };
        _elements.Add(tail);
        return tail;
    }
}
