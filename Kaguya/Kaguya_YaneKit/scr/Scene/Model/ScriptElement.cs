// ============================================================================
// ScriptElement.cs
// 脚本元素抽象基类: 所有 SCR 脚本文档中的元素均继承此类
//
// 提供可选的 Comment 属性, 用于附加行尾注释
// 派生类: ScriptInstruction, ScriptLabel, ScriptComment, ScriptTail
//
// 被依赖: ScriptDocument (元素列表容器), 所有派生类型
// ============================================================================
namespace Kaguya_YaneKit.Scr.Model;

public abstract class ScriptElement
{
    public string? Comment { get; set; }
}
