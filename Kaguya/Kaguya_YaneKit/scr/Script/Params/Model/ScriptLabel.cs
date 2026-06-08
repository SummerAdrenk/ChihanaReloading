// ============================================================================
// ScriptLabel.cs
// 脚本标签元素: 表示代码中的命名跳转目标 (如 loc_00001234)
//
// 标签不产生二进制输出, 仅作为逻辑锚点存在于元素列表中
// 由 ScrSemanticPass 根据 PC 目标地址自动生成
// 由 ScrLabelService 计算标签到字节偏移的映射
//
// 依赖: ScriptElement (基类)
// 被依赖: ScrLabelService, ScrSemanticPass, ScrContainerCodec, ScrTextCodec
// ============================================================================
namespace Kaguya_YaneKit.Script.Paramsipt.Params.Model;

public sealed class ScriptLabel : ScriptElement
{
    public required string Name { get; set; }
}
