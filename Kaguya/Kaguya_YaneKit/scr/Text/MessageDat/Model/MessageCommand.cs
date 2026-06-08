// ============================================================================
// MessageCommand.cs
// 消息命令模型
//
// 数据结构:
//   Id     - 命令 ID (通常对应角色名索引, 即 Names[Id])
//   Params - 参数列表 (通常为消息索引, 即 Messages[param] 的引用)
//
// 语义说明:
//   Command.Id 指向 Names 列表中的说话者
//   Command.Params 列出该说话者的消息条目索引
//   当 Params.Count >= 2 时, 表示分支选项 (branch) 消息
//
// 依赖: 无外部依赖
// 被依赖: MessageDatDocument, MessageDatCodec, MessageTextCodec,
//          MessageScriptLinker, MessageDatWorkflowProcessor
// ============================================================================
namespace Kaguya_YaneKit.Text.MessageDat.Model;

public sealed class MessageCommand
{
    public int Id { get; set; }
    public List<int> Params { get; } = [];
}
