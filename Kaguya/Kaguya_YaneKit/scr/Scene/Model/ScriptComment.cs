// ============================================================================
// ScriptComment.cs
// 脚本注释元素: 存放独立的注释行 (以 ; 开头)
//
// 注释元素不参与二进制编码, 仅在文本格式 (SCRASM) 中保留
// 与 ScriptElement.Comment (行尾注释) 不同, 这是独立的整行注释
//
// 依赖: ScriptElement (基类)
// 被依赖: ScriptDocument, ScrTextCodec (文本格式读写)
// ============================================================================
namespace Kaguya_YaneKit.Scr.Model;

public sealed class ScriptComment : ScriptElement
{
    public required string Text { get; set; }
}
