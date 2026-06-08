// ============================================================================
// MessageEntry.cs
// 单条消息条目模型
//
// 数据结构:
//   Text          - 消息文本 (解码后的字符串, 可含占位符)
//   RawTextBytes  - 原始字节 (用于往返写入时保持二进制一致性)
//   Voices        - 关联语音文件名列表 (如 "ev101_01")
//
// 设计说明:
//   RawTextBytes 在读取时由 MessageDatCodec 填充,
//   写入时若 Text 未变则优先使用 RawTextBytes, 避免编码差异导致数据变化
//
// 依赖: 无外部依赖
// 被依赖: MessageDatDocument, MessageDatCodec, MessageTextCodec,
//          MessageDatWorkflowProcessor
// ============================================================================
namespace Kaguya_YaneKit.Text.MessageDat.Model;

public sealed class MessageEntry
{
    public string Text { get; set; } = string.Empty;
    public byte[] RawTextBytes { get; set; } = [];
    public List<string> Voices { get; } = [];
}
