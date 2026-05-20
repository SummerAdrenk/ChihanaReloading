// ============================================================================
// ScrOpcodeInfo.cs
// 操作码信息表: 定义 SCR 脚本引擎的全部 28 种操作码及其属性
//
// ScrOpcodeDescriptor 记录:
//   Opcode            - 操作码编号 (u16, 1~28)
//   Name              - 助记符 (如 "jump", "if_true", "text")
//   OperandKind       - 操作数类型: Raw (原始数据) / PcTarget (PC 跳转目标)
//   ExpectedBodyLength - 预期 body 长度 (null 表示可变长)
//
// 关键方法:
//   Get(opcode)             - 查表返回描述符, 未知操作码返回 "op_N"
//   TryGetPcTargetOffset    - 判断指令是否含 PC 目标, 并返回 body 内偏移
//     opcode 11/18/25 (jump/call/save): 目标在 body[0..4]
//     opcode 14/15 (if_true/if_false):  目标在 body[3..7]
//
// 依赖: 无
// 被依赖: ScrSemanticPass, ScrInstructionTextCodec, ScrTextCodec, ScrListingFormatter
// ============================================================================
namespace Kaguya_YaneKit.Scr;

public enum ScrOperandKind
{
    Raw,
    PcTarget
}

public sealed record ScrOpcodeDescriptor(
    ushort Opcode,
    string Name,
    ScrOperandKind OperandKind,
    int? ExpectedBodyLength = null);

public static class ScrOpcodeInfo
{
    private static readonly IReadOnlyDictionary<ushort, ScrOpcodeDescriptor> Table =
        new Dictionary<ushort, ScrOpcodeDescriptor>
        {
            [1] = new(1, "assign", ScrOperandKind.Raw, 12),
            [2] = new(2, "flag_set", ScrOperandKind.Raw, 4),
            [3] = new(3, "flag_clear", ScrOperandKind.Raw, 4),
            [4] = new(4, "end", ScrOperandKind.Raw),
            [5] = new(5, "wait", ScrOperandKind.Raw),
            [6] = new(6, "update_layer", ScrOperandKind.Raw),
            [7] = new(7, "text", ScrOperandKind.Raw, 44),
            [8] = new(8, "menu", ScrOperandKind.Raw),
            [9] = new(9, "sound", ScrOperandKind.Raw),
            [10] = new(10, "bgm", ScrOperandKind.Raw, 4),
            [11] = new(11, "jump", ScrOperandKind.PcTarget, 4),
            [12] = new(12, "file_jump", ScrOperandKind.Raw),
            [13] = new(13, "compare", ScrOperandKind.Raw, 12),
            [14] = new(14, "if_true", ScrOperandKind.PcTarget, 7),
            [15] = new(15, "if_false", ScrOperandKind.PcTarget, 7),
            [16] = new(16, "file_call", ScrOperandKind.Raw),
            [17] = new(17, "file_return", ScrOperandKind.Raw, 0),
            [18] = new(18, "call", ScrOperandKind.PcTarget, 4),
            [19] = new(19, "return", ScrOperandKind.Raw, 0),
            [20] = new(20, "program", ScrOperandKind.Raw),
            [21] = new(21, "title", ScrOperandKind.Raw),
            [22] = new(22, "scene", ScrOperandKind.Raw, 2),
            [23] = new(23, "date_window", ScrOperandKind.Raw, 0),
            [24] = new(24, "date_place_reset", ScrOperandKind.Raw, 0),
            [25] = new(25, "save", ScrOperandKind.PcTarget, 4),
            [26] = new(26, "follow_jump", ScrOperandKind.Raw),
            [27] = new(27, "voice", ScrOperandKind.Raw),
            [28] = new(28, "nop", ScrOperandKind.Raw, 0)
        };

    public static ScrOpcodeDescriptor Get(ushort opcode) =>
        Table.TryGetValue(opcode, out var descriptor)
            ? descriptor
            : new ScrOpcodeDescriptor(opcode, $"op_{opcode}", ScrOperandKind.Raw);

    public static bool IsPcTarget(ushort opcode, int bodyLength) =>
        TryGetPcTargetOffset(opcode, bodyLength, out _);

    public static bool TryGetPcTargetOffset(ushort opcode, int bodyLength, out int operandOffset)
    {
        switch (opcode, bodyLength)
        {
            case (11, 4):
            case (18, 4):
            case (25, 4):
                operandOffset = 0;
                return true;
            case (14, 7):
            case (15, 7):
                operandOffset = 3;
                return true;
            default:
                operandOffset = 0;
                return false;
        }
    }
}
