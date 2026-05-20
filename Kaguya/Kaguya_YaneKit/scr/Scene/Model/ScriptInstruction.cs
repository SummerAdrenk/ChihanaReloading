// ============================================================================
// ScriptInstruction.cs
// 脚本指令元素: 表示一条 SCR 字节码指令
//
// 数据结构:
//   Opcode (u16)        - 操作码, 对应 ScrOpcodeInfo 中的 28 种指令
//   Body (byte[])       - 指令体 (不含 4 字节头)
//   OriginalLength (u16?) - 原始二进制中的声明长度 (用于验证)
//   DeclaredLength       - 计算属性: 4 + Body.Length
//   TargetLabel          - 跳转目标标签名 (由 ScrSemanticPass 填充)
//   Metadata             - 附加元数据字典 (如 "offset" 记录原始字节偏移)
//
// 依赖: ScriptElement (基类)
// 被依赖: ScriptBinaryCodec, ScrSemanticPass, ScrInstructionTextCodec,
//         ScrTextCodec, ScrListingFormatter, ScriptVerifier
// ============================================================================
namespace Kaguya_YaneKit.Scr.Model;

public sealed class ScriptInstruction : ScriptElement
{
    private byte[] _body = [];

    public ushort Opcode { get; set; }

    public byte[] Body
    {
        get => _body;
        set => _body = value ?? [];
    }

    public ushort? OriginalLength { get; set; }

    public int DeclaredLength => 4 + Body.Length;

    public string? TargetLabel { get; set; }

    public Dictionary<string, object?> Metadata { get; } = new(StringComparer.Ordinal);
}
