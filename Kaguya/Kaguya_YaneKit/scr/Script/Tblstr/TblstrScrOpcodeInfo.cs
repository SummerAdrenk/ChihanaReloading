using System.Buffers.Binary;

namespace Kaguya_YaneKit.Script.Tblstr;

public sealed record TblstrScrOpcodeDescriptor(
    int Opcode,
    string Name,
    string Status);

public static class TblstrScrOpcodeTable
{
    public static readonly TblstrScrOpcodeDescriptor Unknown = new(-1, "unknown", "unknown");

    private static readonly Dictionary<int, TblstrScrOpcodeDescriptor> Descriptors = new()
    {
        [0] = D(0, "set_value_immediate", "confirmed-field-path"),
        [1] = D(1, "add_value", "confirmed-field-path"),
        [2] = D(2, "jump_absolute", "confirmed"),
        [3] = D(3, "jump_if_equal", "confirmed-branch-shape"),
        [4] = D(4, "jump_if_not_equal", "confirmed-branch-shape"),
        [5] = D(5, "jump_if_greater", "confirmed-branch-shape"),
        [6] = D(6, "jump_if_less", "confirmed-branch-shape"),
        [7] = D(7, "jump_if_greater_equal", "confirmed-branch-shape"),
        [8] = D(8, "jump_if_less_equal", "confirmed-branch-shape"),
        [9] = D(9, "menu_begin_or_resume", "confirmed-menu-path"),
        [10] = D(10, "menu_add_choice", "confirmed-menu-path"),
        [11] = D(11, "menu_commit_choice", "confirmed-menu-path"),
        [12] = D(12, "jump_script_start", "confirmed-script-load-start"),
        [18] = D(18, "play_movie", "confirmed-resource-path"),
        [19] = D(19, "message_window", "confirmed-message-path"),
        [20] = D(20, "close_message_window", "confirmed-message-state"),
        [21] = D(21, "set_adv_layer_resource", "confirmed-adv-layer-path"),
        [22] = D(22, "set_wait_mode_duration", "confirmed-field-path"),
        [23] = D(23, "clear_adv_layer", "confirmed-adv-layer-path"),
        [24] = D(24, "set_state_27_or_return", "confirmed-field-path"),
        [33] = D(33, "set_auto_wait_checkpoint", "confirmed-field-path"),
        [34] = D(34, "set_alt_wait_checkpoint", "confirmed-field-path"),
        [39] = D(39, "stop_script", "confirmed-return0"),
        [44] = D(44, "set_current_display_name", "confirmed-nameplate-path"),
        [61] = D(61, "set_byte_triplet_state", "confirmed-field-path"),
        [63] = D(63, "push_kaisou_system_button", "confirmed-event-path"),
        [71] = D(71, "mark_current_resource_active", "confirmed-resource-path"),
        [74] = D(74, "set_value_random", "confirmed-field-path"),
        [80] = D(80, "play_bgm", "confirmed-resource-path"),
        [82] = D(82, "play_wave", "confirmed-resource-path"),
        [83] = D(83, "clear_audio_channel", "confirmed-audio-channel-path"),
        [84] = D(84, "emit_system_scr_event", "confirmed-event-path"),
        [85] = D(85, "clear_movie_state", "confirmed-field-path"),
        [87] = D(87, "set_pending_return_value", "confirmed-field-path"),
        [88] = D(88, "set_adv_layer_color_filter", "confirmed-adv-layer-path"),
        [89] = D(89, "set_adv_scroll_position", "confirmed-adv-layer-path"),
        [90] = D(90, "apply_adv_scroll_position", "confirmed-adv-layer-path"),
        [91] = D(91, "copy_value", "confirmed-field-path"),
        [93] = D(93, "clear_current_display_name", "confirmed-field-path"),
        [94] = D(94, "jump_script_label_index", "confirmed-label-table-load"),
        [95] = D(95, "set_wait_resume_checkpoint", "confirmed-field-path"),
        [96] = D(96, "reset_adv_layers", "confirmed-adv-layer-path"),
        [112] = D(112, "call_script_label_index", "confirmed-gosub-label-table"),
        [113] = D(113, "return_from_script_call", "confirmed-call-stack-path"),
        [114] = D(114, "set_adv_sp_position", "confirmed-adv-sp-path"),
        [115] = D(115, "set_adv_sp_frame", "confirmed-adv-sp-path"),
        [117] = D(117, "set_adv_view_sprite_index", "confirmed-adv-view-sprite-path"),
        [119] = D(119, "set_run_state", "confirmed-field-path"),
        [120] = D(120, "clear_run_state", "confirmed-field-path"),
        [121] = D(121, "set_adv_sp_resource_bundle", "confirmed-adv-sp-path"),
        [122] = D(122, "clear_adv_layer_color_filter", "confirmed-adv-layer-path"),
        [134] = D(134, "stop_movie_if_active", "confirmed-resource-path"),
        [135] = D(135, "clear_movie_state_and_fade_out", "confirmed-resource-path"),
        [136] = D(136, "wait_current_resource_effect", "confirmed-resource-path"),
        [137] = D(137, "set_scene_title", "confirmed-scene-title-path"),
        [140] = D(140, "fade_in_wave_loop", "confirmed-audio-fade-path"),
        [141] = D(141, "fade_out_wave_loop", "confirmed-audio-fade-path"),
        [142] = D(142, "wait_wave_slot", "confirmed-audio-wait-path"),
        [143] = D(143, "load_adv_sp_resource_bundle_controlled", "confirmed-record-control-path"),
        [144] = D(144, "load_adv_sp_resource_bundle_controlled_ex", "confirmed-record-control-path"),
        [145] = D(145, "register_named_range", "confirmed-named-range-path"),
        [146] = D(146, "set_voice_group_prefix", "confirmed-voice-group-path"),
        [147] = D(147, "append_voice_group_entry", "confirmed-voice-group-path"),
        [148] = D(148, "nop", "confirmed-noop"),
        [150] = D(150, "add_adv_sp_keydata", "confirmed-adv-sp-path"),
        [151] = D(151, "enable_adv_sp_keydata", "confirmed-adv-sp-path"),
        [152] = D(152, "set_message_color0", "confirmed-message-color-path"),
        [153] = D(153, "set_message_color_mode", "confirmed-message-color-path"),
        [154] = D(154, "set_message_color1", "confirmed-message-color-path"),
        [155] = D(155, "init_resource_object", "confirmed-string-layout"),
        [156] = D(156, "set_resource_object_position", "confirmed-resource-object-path"),
        [157] = D(157, "set_resource_object_frame", "confirmed-resource-object-path"),
        [158] = D(158, "clear_resource_object", "confirmed-resource-object-path"),
        [159] = D(159, "add_resource_object_position_keyframe", "confirmed-keyframe-path"),
        [160] = D(160, "enable_resource_object_keyframes", "confirmed-keyframe-path"),
        [161] = D(161, "set_resource_object_anm", "confirmed-resource-object-path"),
        [162] = D(162, "set_adv_event_state_124", "confirmed-adv-layer-path"),
        [163] = D(163, "validate_adv_sp_keyframes", "confirmed-adv-sp-path"),
        [164] = D(164, "add_resource_object_anm_keyframe", "confirmed-keyframe-path"),
        [165] = D(165, "add_resource_object_alpha_keyframe", "confirmed-keyframe-path"),
        [166] = D(166, "set_resource_object_alpha", "confirmed-resource-object-path"),
        [167] = D(167, "anm_ctl_pause", "confirmed-adv-layer-path"),
        [168] = D(168, "anm_ctl_start", "confirmed-adv-layer-path"),
        [169] = D(169, "anm_ctl_restart", "confirmed-adv-layer-path"),
        [170] = D(170, "anm_ctl_waitcount", "confirmed-adv-layer-path"),
        [171] = D(171, "anm_ctl_speed", "confirmed-adv-layer-path"),
        [172] = D(172, "nop", "confirmed-noop"),
    };

    public static TblstrScrOpcodeDescriptor Get(int opcode) =>
        Descriptors.TryGetValue(opcode, out var descriptor) ? descriptor : Unknown with { Opcode = opcode };

    public static IReadOnlyCollection<TblstrScrOpcodeDescriptor> All => Descriptors.Values;

    public static int[] GetInlineStringLengthOffsets(int opcode, ReadOnlySpan<byte> instruction)
    {
        return opcode switch
        {
            12 => [3],
            18 => [3],
            21 => [4],
            44 => [3],
            80 => [3],
            82 => [4],
            94 => [2],
            112 => [2],
            121 => [4, 8, 12, 16],
            137 => [2, 3],
            143 => [4, 8, 12, 16],
            144 => [4, 8, 12, 16],
            145 => [3],
            146 => [3],
            147 => [3],
            155 => [2, 3],
            156 => [2],
            157 => [2],
            158 => [2],
            159 => [2],
            160 => [2],
            161 => [2],
            164 => [2],
            165 => [2],
            166 => [2],
            19 => GetTextOpcodeStringOffsets(instruction),
            _ => []
        };
    }

    public static bool ShouldSkipString(int opcode, int lengthOffset, int declaredLength, ReadOnlySpan<byte> instruction)
    {
        if (opcode == 19 && declaredLength == 0xFF)
        {
            return true;
        }

        return false;
    }

    public static string GetStringName(int opcode, int lengthOffset)
    {
        return opcode switch
        {
            12 => "label_name",
            18 => "movie_name",
            19 => lengthOffset switch
            {
                2 => "voice_file",
                3 => "alternate_voice_file",
                _ => $"str_{lengthOffset:X2}"
            },
            21 => "resource_name",
            44 => "display_name",
            80 => "bgm_name",
            82 => "wave_name",
            94 => "script_name",
            112 => "script_name",
            121 or 143 or 144 => lengthOffset switch
            {
                4 => "object_name",
                8 => "pattern_name",
                12 => "resource_arg_2",
                16 => "resource_arg_3",
                _ => $"str_{lengthOffset:X2}"
            },
            137 => lengthOffset switch
            {
                2 => "scene_title",
                3 => "scene_subtitle",
                _ => $"str_{lengthOffset:X2}"
            },
            145 => "range_name",
            146 => "voice_group_prefix",
            147 => "voice_group_entry",
            155 => lengthOffset switch
            {
                2 => "object_name",
                3 => "init_arg",
                _ => $"str_{lengthOffset:X2}"
            },
            >= 156 and <= 166 => "resource_object_name",
            _ => $"str_{lengthOffset:X2}"
        };
    }

    private static int[] GetTextOpcodeStringOffsets(ReadOnlySpan<byte> instruction)
    {
        if (instruction.Length < 16)
        {
            return [2, 3];
        }

        var hasAlternate = BinaryPrimitives.ReadInt32LittleEndian(instruction.Slice(12, 4)) != -1;
        return hasAlternate ? [2, 3] : [2];
    }

    private static TblstrScrOpcodeDescriptor D(int opcode, string name, string status) =>
        new(opcode, name, status);
}
