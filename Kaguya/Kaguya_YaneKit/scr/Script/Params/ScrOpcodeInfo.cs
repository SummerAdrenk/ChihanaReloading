using System.Collections.ObjectModel;

namespace Kaguya_YaneKit.Script.Params;

public enum ScrOperandKind
{
    Raw,
    PcTarget
}

public enum ScrOperandType
{
    U8,
    U16,
    U32,
    I32,
    Bytes,
    TextBytes,
    PcTarget,
    CountedI32Array
}

public enum ScrLengthKind
{
    Fixed,
    Variable,
    CountedString,
    CountedI32Array
}

public sealed record ScrOperandSchema(
    string Name,
    ScrOperandType Type,
    int Offset,
    int? Size = null,
    string? LengthFrom = null);

public sealed record ScrOpcodeVariant(
    string Name,
    string Condition,
    IReadOnlyList<ScrOperandSchema> Operands);

public sealed record ScrSubOpcode(
    int Value,
    string Name,
    string Semantics);

public sealed record ScrOpcodeDescriptor(
    ushort Opcode,
    string Name,
    ScrOperandKind OperandKind,
    int? ExpectedBodyLength = null,
    ScrLengthKind LengthKind = ScrLengthKind.Fixed,
    string LengthRule = "instrLen",
    IReadOnlyList<ScrOperandSchema>? Operands = null,
    IReadOnlyList<ScrOpcodeVariant>? Variants = null,
    IReadOnlyList<ScrSubOpcode>? SubOpcodes = null)
{
    public IReadOnlyList<ScrOperandSchema> Operands { get; init; } = Operands ?? [];
    public IReadOnlyList<ScrOpcodeVariant> Variants { get; init; } = Variants ?? [];
    public IReadOnlyList<ScrSubOpcode> SubOpcodes { get; init; } = SubOpcodes ?? [];

    public bool IsKnown => Opcode is >= 1 and <= 28;
}

public static class ScrOpcodeInfo
{
    private static readonly IReadOnlyDictionary<ushort, ScrOpcodeDescriptor> Table =
        new ReadOnlyDictionary<ushort, ScrOpcodeDescriptor>(
            new Dictionary<ushort, ScrOpcodeDescriptor>
            {
                [1] = Fixed(1, "assign", 12, U8("flags", 0), U8("op", 1), U16("dst", 2), I32("src08", 4), I32("src0c", 8)),
                [2] = Fixed(2, "flag_set", 4, U16("var", 0), Bytes("reserved", 2, 2)),
                [3] = Fixed(3, "flag_clear", 4, U16("var", 0), Bytes("reserved", 2, 2)),
                [4] = Fixed(4, "end", null),
                [5] = Variable(5, "wait", "u8 flags + u16 value; if flags & 0x10, u16 aux follows",
                    [U8("flags", 0), U16("value", 1), Bytes("aux_and_extra", 3, null)],
                    variants: WaitVariants()),
                [6] = Variable(6, "update", "i32 pattern_entry + i32 aux_entry + u8 flags + variant payload",
                    I32("pattern_entry", 0), I32("aux_entry", 4), U8("flags", 8), Bytes("variant_payload", 9, null)),
                [7] = Variable(7, "text", "i32 command + i32 pattern_entry + i32 message_resource + resource_slots[i32]; body length = 12 + 4*N",
                    I32("command", 0), I32("pattern_entry", 4), I32("message_resource", 8),
                    Bytes("resource_slots_and_message_sequence", 12, null)),
                [8] = Variable(8, "menu", "u8 mode + u8 choice_count + choice_count*i32 choices + i32 command + i32 pattern_entry + i32 message_resource + i32 resource_slots[6] + i32 reserved + i32 message_sequence",
                    U8("mode", 0), U8("choice_count", 1), Bytes("choices_and_text_state", 2, null)),
                [9] = Variable(9, "sound", "body length is 4 plus optional extra bytes",
                    U32("id", 0), Bytes("extra", 4, null)),
                [10] = Fixed(10, "bgm", 4, U32("track", 0)),
                [11] = PcFixed(11, "jump", 4, Pc("target", 0)),
                [12] = Variable(12, "file_jump", "u8 flags + u32 target file id; flags & 0x10 adds u32 entry operand",
                    [U8("flags", 0), U32("target", 1), Bytes("entry_or_extra", 5, null)],
                    variants: FileJumpVariants()),
                [13] = Fixed(13, "compare", 12, U32("lhs", 0), U32("rhs", 4), U32("mode", 8)),
                [14] = PcFixed(14, "if_true", 7, U8("flags", 0), U16("value", 1), Pc("target", 3)),
                [15] = PcFixed(15, "if_false", 7, U8("flags", 0), U16("value", 1), Pc("target", 3)),
                [16] = Variable(16, "file_call", "u8 flags + u32 target file id; pushes return file/pc before load",
                    [U8("flags", 0), U32("target", 1)],
                    variants: FileTargetVariants()),
                [17] = Fixed(17, "file_return", null),
                [18] = PcFixed(18, "call", 4, Pc("target", 0)),
                [19] = Fixed(19, "return", null),
                [20] = Fixed(20, "program", 3,
                    [U8("flags", 0), U16("id", 1)],
                    variants: ProgramVariants(),
                    subOpcodes: ProgramSubOpcodes()),
                [21] = CountedString(21, "title"),
                [22] = Fixed(22, "scene", 2, U16("index", 0)),
                [23] = Fixed(23, "date_window", null),
                [24] = Fixed(24, "date_place_reset", null),
                [25] = Fixed(25, "save", 4, U32("slot", 0)),
                [26] = Variable(26, "follow_jump", "u8 flags + u32 target file id; saves follow_return/follow_point/game_end before load",
                    [U8("flags", 0), U32("target", 1), Bytes("extra", 5, null)],
                    variants: FileTargetVariants()),
                [27] = CountedI32Array(27, "voice"),
                [28] = Fixed(28, "nop", null)
            });

    public static IEnumerable<ScrOpcodeDescriptor> All() => Table.Values.OrderBy(x => x.Opcode);

    public static bool IsKnown(ushort opcode) => Table.ContainsKey(opcode);

    public static ScrOpcodeDescriptor Get(ushort opcode) =>
        Table.TryGetValue(opcode, out var descriptor)
            ? descriptor
            : new ScrOpcodeDescriptor(opcode, $"op_{opcode}", ScrOperandKind.Raw, LengthKind: ScrLengthKind.Variable);

    public static bool IsPcTarget(ushort opcode, int bodyLength) =>
        TryGetPcTargetOffset(opcode, bodyLength, out _);

    public static bool TryGetPcTargetOffset(ushort opcode, int bodyLength, out int operandOffset)
    {
        switch (opcode, bodyLength)
        {
            case (11, 4):
            case (18, 4):
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

    private static ScrOpcodeDescriptor Fixed(ushort opcode, string name, int? bodyLength, params ScrOperandSchema[] operands) =>
        new(opcode, name, ScrOperandKind.Raw, bodyLength, ScrLengthKind.Fixed, bodyLength is null ? "body length = 0" : $"body length = {bodyLength}", operands);

    private static ScrOpcodeDescriptor Fixed(
        ushort opcode,
        string name,
        int bodyLength,
        ScrOperandSchema[] operands,
        IReadOnlyList<ScrOpcodeVariant>? variants = null,
        IReadOnlyList<ScrSubOpcode>? subOpcodes = null) =>
        new(opcode, name, ScrOperandKind.Raw, bodyLength, ScrLengthKind.Fixed, $"body length = {bodyLength}", operands, variants, subOpcodes);

    private static ScrOpcodeDescriptor PcFixed(ushort opcode, string name, int bodyLength, params ScrOperandSchema[] operands) =>
        new(opcode, name, ScrOperandKind.PcTarget, bodyLength, ScrLengthKind.Fixed, $"body length = {bodyLength}", operands);

    private static ScrOpcodeDescriptor Variable(
        ushort opcode,
        string name,
        string lengthRule,
        params ScrOperandSchema[] operands) =>
        new(opcode, name, ScrOperandKind.Raw, null, ScrLengthKind.Variable, lengthRule, operands);

    private static ScrOpcodeDescriptor Variable(
        ushort opcode,
        string name,
        string lengthRule,
        ScrOperandSchema[] operands,
        IReadOnlyList<ScrOpcodeVariant>? variants = null) =>
        new(opcode, name, ScrOperandKind.Raw, null, ScrLengthKind.Variable, lengthRule, operands, variants);

    private static ScrOpcodeDescriptor CountedString(ushort opcode, string name) =>
        new(opcode, name, ScrOperandKind.Raw, null, ScrLengthKind.CountedString,
            "body[0] is byte count; body length must be 1 + body[0]",
            [U8("byte_count", 0), TextBytes("text", 1, "byte_count")]);

    private static ScrOpcodeDescriptor CountedI32Array(ushort opcode, string name) =>
        new(opcode, name, ScrOperandKind.Raw, null, ScrLengthKind.CountedI32Array,
            "body[0] is i32 item count; body length must be 1 + 4 * body[0]",
            [U8("count", 0), new ScrOperandSchema("indices", ScrOperandType.CountedI32Array, 1, LengthFrom: "count")]);

    private static ScrOperandSchema U8(string name, int offset) => new(name, ScrOperandType.U8, offset, 1);
    private static ScrOperandSchema U16(string name, int offset) => new(name, ScrOperandType.U16, offset, 2);
    private static ScrOperandSchema U32(string name, int offset) => new(name, ScrOperandType.U32, offset, 4);
    private static ScrOperandSchema I32(string name, int offset) => new(name, ScrOperandType.I32, offset, 4);
    private static ScrOperandSchema Pc(string name, int offset) => new(name, ScrOperandType.PcTarget, offset, 4);
    private static ScrOperandSchema Bytes(string name, int offset, int? size) => new(name, ScrOperandType.Bytes, offset, size);
    private static ScrOperandSchema TextBytes(string name, int offset, string lengthFrom) => new(name, ScrOperandType.TextBytes, offset, LengthFrom: lengthFrom);

    private static IReadOnlyList<ScrOpcodeVariant> FileTargetVariants() =>
    [
        new("file_id_from_vm_value", "flags & 0x02", [U8("flags", 0), U32("file_id_var", 1)]),
        new("file_id_immediate", "flags & 0x04", [U8("flags", 0), U32("file_id", 1)])
    ];

    private static IReadOnlyList<ScrOpcodeVariant> FileJumpVariants() =>
    [
        new("file_id_from_vm_value", "flags & 0x02", [U8("flags", 0), U32("file_id_var", 1)]),
        new("file_id_immediate", "flags & 0x04", [U8("flags", 0), U32("file_id", 1)]),
        new("entry_pc_present", "flags & 0x10", [U8("flags", 0), U32("file_id_or_var", 1), U32("entry_pc_or_var", 5)]),
        new("entry_pc_immediate", "flags & 0x10 && flags & 0x20", [U8("flags", 0), U32("file_id_or_var", 1), U32("entry_pc", 5)]),
        new("entry_pc_from_vm_value", "flags & 0x10 && flags & 0x40", [U8("flags", 0), U32("file_id_or_var", 1), U32("entry_pc_var", 5)]),
        new("clear_call_stack", "flags & 0x80", [U8("flags", 0), U32("file_id_or_var", 1)])
    ];

    private static IReadOnlyList<ScrOpcodeVariant> WaitVariants() =>
    [
        new("engine_mode_1", "flags & 0x40", [U8("flags", 0), U16("value", 1)]),
        new("sound", "flags & 0x80", [U8("flags", 0), U16("sound_target", 1)]),
        new("surface_complete", "flags & 0x20", [U8("flags", 0), U16("surface_selector", 1)]),
        new("surface_progress", "flags & 0x10", [U8("flags", 0), U16("surface_selector", 1), U16("progress_threshold", 3)]),
        new("countdown", "otherwise", [U8("flags", 0), U16("count", 1)]),
        new("vm_value_ref", "flags & 0x02", [U8("flags", 0), U16("value_ref", 1)])
    ];
    private static IReadOnlyList<ScrOpcodeVariant> ProgramVariants() =>
    [
        new("program_id_from_var", "flags & 0x02", [U8("flags", 0), U16("program_id_var", 1)]),
        new("program_id_immediate", "flags & 0x01", [U8("flags", 0), U16("program_id", 1)])
    ];

    private static IReadOnlyList<ScrSubOpcode> ProgramSubOpcodes() =>
    [
        new(0, "playGameParamMedia", "play media resource selected through prog_param00"),
        new(1, "setSystemWordFlag", "write subsystem7 word flag"),
        new(2, "setReplayOrSkipFlag", "update replay/skip mode flag"),
        new(3, "return10", "return execution state 10"),
        new(4, "enableSubsystem3And4", "enable subsystem 3 and 4"),
        new(5, "disableSubsystem3And4", "disable subsystem 3 and 4"),
        new(6, "noop", "successful no-op"),
        new(7, "randomProgResult", "prog_result = random bounded by current result"),
        new(8, "waitSubsystem1Ready", "wait until subsystem1 ready flag is set"),
        new(9, "setSubsystem2Flag", "write subsystem2 flag"),
        new(10, "readSubsystem1Flag111", "read subsystem1 byte 111 into prog_result"),
        new(11, "return12", "return execution state 12"),
        new(12, "readSystemWordArray", "read subsystem7 word array by prog_param00"),
        new(13, "writeSystemWordArray", "write subsystem7 word array by prog_param00"),
        new(14, "return13", "return execution state 13"),
        new(15, "subsystem4NullCall", "subsystem4 null call"),
        new(16, "setSubsystem1Bool", "set subsystem1 bool array"),
        new(17, "readSubsystem1Bool", "read subsystem1 bool array"),
        new(18, "readMageGaugeFlag", "read gauge display flag"),
        new(19, "restartPointDispatch", "dispatch restart/follow/game-end point"),
        new(20, "return15", "return execution state 15"),
        new(21, "readGaugeDisplayFlag", "read gauge display flag"),
        new(22, "noop", "successful no-op"),
        new(23, "return16", "return execution state 16")
    ];
}
