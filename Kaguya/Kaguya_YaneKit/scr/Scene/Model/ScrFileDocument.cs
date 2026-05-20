// ============================================================================
// ScrFileDocument.cs
// SCR 文件完整文档模型: 对应一个 .scr 文件的全部内容
//
// 组成部分:
//   Header       - 文件头字符串, 如 "[SCR-Ver5.3]"
//   Script       - 代码段 (ScriptDocument, 包含指令/标签/注释/尾部)
//   SaveOffsets  - 存档点偏移表 ([SAVE] 段, FileAbsolute 编码)
//   LayerOffsets - 图层偏移表 ([LAYER] 段, CodeRelative 编码)
//   Tail         - 容器尾部原始字节
//
// 依赖: ScriptDocument, ScrOffsetReference
// 被依赖: ScrContainerCodec, ScrTextCodec, ScrListingFormatter
// ============================================================================
namespace Kaguya_YaneKit.Scr.Model;

public sealed class ScrFileDocument
{
    public string Header { get; set; } = "[SCR-Ver5.3]";

    public ScriptDocument Script { get; set; } = new();

    public List<ScrOffsetReference> SaveOffsets { get; } = [];

    public List<ScrOffsetReference> LayerOffsets { get; } = [];

    public byte[] Tail { get; set; } = [];
}
