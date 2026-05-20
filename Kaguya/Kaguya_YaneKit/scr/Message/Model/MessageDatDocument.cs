// ============================================================================
// MessageDatDocument.cs
// message.dat 完整文档模型
//
// 数据结构:
//   Magic      - 固定魔数 "[SCR-MESSAGE]ver4.0"
//   Encrypted  - 是否启用 XOR 加密
//   XorKey     - XOR 加密密钥 (单字节)
//   Names      - 角色名列表 (解码后)
//   RawNameBytes    - 角色名原始字节 (用于往返保真)
//   Choices         - 选项文本列表 (解码后)
//   RawChoiceBytes  - 选项原始字节
//   Messages   - 消息条目列表 (MessageEntry)
//   Commands   - 命令列表 (MessageCommand, 建立角色名与消息的映射)
//   RawTail    - 文件尾部原始数据 (保留未解析部分)
//
// 设计说明:
//   本类为纯数据容器, 不含序列化逻辑
//   Raw*字段用于写入时保持与原始文件的二进制一致性
//
// 依赖: MessageEntry, MessageCommand
// 被依赖: MessageDatCodec, MessageTextCodec, MessageScriptLinker,
//          MessageWorkflowProcessor
// ============================================================================
namespace Kaguya_YaneKit.Message.Model;

public sealed class MessageDatDocument
{
    public const string Magic = "[SCR-MESSAGE]ver4.0";

    public bool Encrypted { get; set; }
    public byte XorKey { get; set; }
    public List<string> Names { get; } = [];
    public List<byte[]> RawNameBytes { get; } = [];
    public List<string> Choices { get; } = [];
    public List<byte[]> RawChoiceBytes { get; } = [];
    public List<MessageEntry> Messages { get; } = [];
    public List<MessageCommand> Commands { get; } = [];
    public byte[] RawTail { get; set; } = [];
}
