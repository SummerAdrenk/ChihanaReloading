using System.Buffers.Binary;
using System.Text;

namespace Kaguya_YaneKit.Script.Tblstr;

public sealed class TblstrScrTextFormatter
{
    public string WriteHls(TblstrScrDocument document) => WriteReadableHls(document);

    public string WriteDisasm(TblstrScrDocument document) => WriteDocument(document, "TBLSTR-SCR-DISASM", includeDebugFields: true);

    private static string WriteReadableHls(TblstrScrDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine(".file kind=tblstr_scr_hls");
        sb.AppendLine($".source {Quote(document.SourceName)}");
        sb.AppendLine($".magic 0x{document.Magic0:X8}, 0x{document.Magic1:X8}");
        sb.AppendLine($".payload_size {document.PayloadSize}");
        sb.AppendLine();
        sb.AppendLine(".code");
        sb.AppendLine();

        foreach (var instruction in document.Instructions)
        {
            if (document.LabelsByOffset.TryGetValue(instruction.Offset, out var labels))
            {
                foreach (var label in labels)
                {
                    sb.AppendLine($"{FormatLabelName(label)}:");
                }
            }

            sb.Append("    ");
            sb.AppendLine(RenderReadableInstruction(instruction));
        }

        return sb.ToString();
    }

    private static string RenderReadableInstruction(TblstrScrInstruction instruction)
    {
        var args = DescribeHlsArgs(instruction);
        foreach (var str in instruction.Strings)
        {
            AddHlsArg(args, str.Name, Quote(str.Text));
        }

        var named = ToNamedArgs(args);
        var rendered = instruction.Opcode switch
        {
            0 => $"SET_VALUE {Arg(named, "value_table")} [{Arg(named, "value_index")}] = {Arg(named, "value")} flags={Arg(named, "value_flags")}",
            1 => $"ADD_VALUE {Arg(named, "value_table")} [{Arg(named, "value_index")}] += {Arg(named, "value")} flags={Arg(named, "value_flags")}",
            2 => $"JUMP {Arg(named, "goto")}",
            3 => RenderCompareJump("IF_EQ", named),
            4 => RenderCompareJump("IF_NE", named),
            5 => RenderCompareJump("IF_GT", named),
            6 => RenderCompareJump("IF_LT", named),
            7 => RenderCompareJump("IF_GE", named),
            8 => RenderCompareJump("IF_LE", named),
            9 => $"MENU_BEGIN flags={Arg(named, "menu_flags")} source={Arg(named, "menu_source_index")}",
            10 => $"MENU_CHOICE id={Arg(named, "choice_id")} text={Arg(named, "text")}",
            11 => $"MENU_COMMIT result={Arg(named, "result_slot")}",
            12 => $"JUMP_SCRIPT_START {Arg(named, "script")}",
            18 => $"PLAY_MOVIE {Arg(named, "movie")}",
            19 => RenderMessage(named),
            20 => "CLOSE_MESSAGE",
            21 => $"LOAD_LAYER {Arg(named, "target")} {Arg(named, "resource")}",
            22 => $"WAIT mode={Arg(named, "mode")} duration={Arg(named, "duration")}",
            23 => $"CLEAR_LAYER {Arg(named, "target")}",
            24 => $"SET_STATE_27 {Arg(named, "state_27_value")}",
            33 => "AUTO_WAIT_CHECKPOINT",
            34 => "ALT_WAIT_CHECKPOINT",
            39 => "STOP_SCRIPT",
            44 => $"SET_DISPLAY_NAME {Arg(named, "display_name")}",
            61 => $"SET_STATE_BYTES {Arg(named, "state_byte_0")}, {Arg(named, "state_byte_1")}, {Arg(named, "state_byte_2")}",
            63 => "PUSH_KAISOU_SYSTEM_BUTTON",
            71 => "MARK_CURRENT_RESOURCE_ACTIVE",
            74 => $"RANDOM_VALUE mod={Arg(named, "random_mod")} -> scenario_value_table[{Arg(named, "value_index")}]",
            80 => RenderBgm(named),
            82 => $"PLAY_WAVE group={Arg(named, "sound_group")} slot={Arg(named, "sound_slot")} {Arg(named, "wave")}",
            83 => $"CLEAR_AUDIO {Arg(named, "target")} ; group={Arg(named, "audio_group")} channel={Arg(named, "audio_channel")}",
            84 => $"EMIT_SYSTEM_EVENT {Arg(named, "scr_event_action")} ; group={Arg(named, "scr_event_group")} arg={Arg(named, "scr_event_arg")}",
            85 => "CLEAR_MOVIE_STATE",
            87 => $"SET_RETURN_VALUE {Arg(named, "value")}",
            88 => $"SET_LAYER_COLOR_FILTER {Arg(named, "layer")} mode={Arg(named, "color_filter_mode")} arg={Arg(named, "color_filter_arg")}",
            89 => $"SET_SCROLL {Arg(named, "target")} x={Arg(named, "x")} y={Arg(named, "y")} duration={Arg(named, "duration_or_state")}",
            90 => $"APPLY_SCROLL {Arg(named, "target")} x={Arg(named, "x")} y={Arg(named, "y")}",
            91 => $"COPY_VALUE {Arg(named, "value_copy_destination")}[{Arg(named, "destination_index")}] <- {Arg(named, "value_copy_source")}[{Arg(named, "source_index")}]",
            93 => "CLEAR_DISPLAY_NAME",
            94 => $"JUMP_SCRIPT {Arg(named, "script")} label_index={Arg(named, "label_index")}",
            95 => $"SET_WAIT_RESUME {Arg(named, "wait_resume_value")}",
            96 => $"RESET_ADV_LAYERS mode={Arg(named, "adv_back_mode")}",
            112 => $"CALL_SCRIPT {Arg(named, "script")} label_index={Arg(named, "label_index")}",
            113 => "RETURN_SCRIPT",
            114 => $"SET_SPRITE_POS {Arg(named, "layer")} x={Arg(named, "x")} y={Arg(named, "y")}",
            115 => $"SET_SPRITE_FRAME {Arg(named, "layer")} frame={Arg(named, "frame_index")}",
            117 => $"SET_ADV_VIEW_SPRITE_INDEX {Arg(named, "adv_view_sprite_index")}",
            119 => $"SET_RUN_STATE {Arg(named, "run_state_value")}",
            120 => "CLEAR_RUN_STATE",
            121 => RenderSpriteBundle(named),
            122 => $"CLEAR_LAYER_COLOR_FILTER {Arg(named, "layer")}",
            134 => "STOP_MOVIE_IF_ACTIVE",
            135 => "CLEAR_MOVIE_AND_FADE_OUT",
            136 => "WAIT_RESOURCE_EFFECT",
            137 => RenderTitle(named),
            140 => $"FADE_IN_WAVE_LOOP {Arg(named, "audio_action")} ; group={Arg(named, "audio_group")} slot={Arg(named, "audio_slot")}",
            141 => $"FADE_OUT_WAVE_LOOP {Arg(named, "audio_action")} ; group={Arg(named, "audio_group")} slot={Arg(named, "audio_slot")}",
            142 => $"WAIT_WAVE_SLOT {Arg(named, "audio_wait_target")} ; group={Arg(named, "audio_group")} slot={Arg(named, "audio_slot")}",
            143 => RenderSpriteBundleControlled("LOAD_SPRITE_CONTROLLED", named, includeSecondaryControl: false),
            144 => RenderSpriteBundleControlled("LOAD_SPRITE_CONTROLLED_EX", named, includeSecondaryControl: true),
            145 => $"RANGE {Arg(named, "range")} {Arg(named, "range_start")}..{Arg(named, "range_end")}",
            146 => $"VOICE_GROUP_PREFIX slot={Arg(named, "voice_group_slot")} {Arg(named, "prefix")}",
            147 => $"VOICE_GROUP_ENTRY {Arg(named, "entry")}",
            148 => "NOP_148",
            150 => $"ADD_SPRITE_KEYDATA {Arg(named, "layer")} {Arg(named, "keydata_value_0")}, {Arg(named, "keydata_value_1")}, {Arg(named, "keydata_value_2")}",
            151 => $"ENABLE_SPRITE_KEYDATA {Arg(named, "layer")} value={Arg(named, "keydata_enable_value")}",
            152 => $"SET_MESSAGE_COLOR0 {Arg(named, "message_color0")}",
            153 => $"SET_MESSAGE_COLOR_MODE {Arg(named, "message_color_mode")}",
            154 => $"SET_MESSAGE_COLOR1 {Arg(named, "message_color1")}",
            155 => $"INIT_RESOURCE_OBJECT {Arg(named, "object")} arg={Arg(named, "init_arg")}",
            156 => $"SET_OBJECT_POS {Arg(named, "object")} x={Arg(named, "x")} y={Arg(named, "y")}",
            157 => $"SET_OBJECT_FRAME {Arg(named, "object")} frame={Arg(named, "frame")}",
            158 => $"CLEAR_OBJECT {Arg(named, "object")}",
            159 => $"ADD_OBJECT_POS_KEY {Arg(named, "object")} key={Arg(named, "key")} x={Arg(named, "x")} y={Arg(named, "y")}",
            160 => $"ENABLE_OBJECT_KEYFRAMES {Arg(named, "object")} value={Arg(named, "value")}",
            161 => $"SET_OBJECT_ANM {Arg(named, "object")} anm={Arg(named, "anm")}",
            162 => $"SET_ADV_EVENT_STATE_124 {Arg(named, "adv_event_state_124")}",
            163 => "VALIDATE_ADV_SP_KEYFRAMES",
            164 => $"ADD_OBJECT_ANM_KEY {Arg(named, "object")} key={Arg(named, "key")} anm={Arg(named, "anm")}",
            165 => $"ADD_OBJECT_ALPHA_KEY {Arg(named, "object")} key={Arg(named, "key")} alpha={Arg(named, "alpha")}",
            166 => $"SET_OBJECT_ALPHA {Arg(named, "object")} alpha={Arg(named, "alpha")}",
            167 => $"ANM_PAUSE {Arg(named, "target")}",
            168 => $"ANM_START {Arg(named, "target")}",
            169 => $"ANM_RESTART {Arg(named, "target")}",
            170 => $"ANM_WAITCOUNT {Arg(named, "target")} value={Arg(named, "value")}",
            171 => $"ANM_SPEED {Arg(named, "target")} value={Arg(named, "value")}",
            172 => "NOP_172",
            _ => RenderFallback(instruction.Descriptor.Name, args)
        };

        if (!instruction.Descriptor.Status.StartsWith("confirmed", StringComparison.Ordinal))
        {
            rendered += $" ; status={instruction.Descriptor.Status}";
        }

        return rendered;
    }

    private static string RenderCompareJump(string mnemonic, Dictionary<string, List<string>> args) =>
        $"{mnemonic} flags={Arg(args, "flags")} {Arg(args, "left")} {CompareSymbol(mnemonic)} {Arg(args, "right")} -> {Arg(args, "goto")}";

    private static string CompareSymbol(string mnemonic) =>
        mnemonic switch
        {
            "IF_EQ" => "==",
            "IF_NE" => "!=",
            "IF_GT" => ">",
            "IF_GE" => ">=",
            "IF_LT" => "<",
            "IF_LE" => "<=",
            _ => "?"
        };

    private static string RenderMessage(Dictionary<string, List<string>> args)
    {
        var line = $"MESSAGE speaker={Arg(args, "speaker")} text={Arg(args, "message")}";
        if (HasArg(args, "voice"))
        {
            line += $" voice={Arg(args, "voice")}";
        }

        if (HasArg(args, "alternate_message"))
        {
            line += $" alt={Arg(args, "alternate_message")}";
        }

        return line;
    }

    private static string RenderBgm(Dictionary<string, List<string>> args)
    {
        var line = $"PLAY_BGM {Arg(args, "track")}";
        if (HasArg(args, "bgm_format") && Arg(args, "bgm_format") != "ogg")
        {
            line += $" format={Arg(args, "bgm_format")}";
        }

        if (HasArg(args, "bgm_play_mode") && Arg(args, "bgm_play_mode") != "0")
        {
            line += $" mode={Arg(args, "bgm_play_mode")}";
        }

        return line;
    }

    private static string RenderSpriteBundle(Dictionary<string, List<string>> args)
    {
        var line = $"LOAD_SPRITE {Arg(args, "layer")} object={Arg(args, "object")} pattern={Arg(args, "pattern")}";
        if (HasArg(args, "arg2"))
        {
            line += $" arg2={Arg(args, "arg2")}";
        }

        if (HasArg(args, "arg3"))
        {
            line += $" arg3={Arg(args, "arg3")}";
        }

        return line;
    }

    private static string RenderSpriteBundleControlled(
        string mnemonic,
        Dictionary<string, List<string>> args,
        bool includeSecondaryControl)
    {
        var line = $"{mnemonic} {Arg(args, "layer")} object={Arg(args, "object")} pattern={Arg(args, "pattern")}";
        if (HasArg(args, "arg2"))
        {
            line += $" arg2={Arg(args, "arg2")}";
        }

        if (HasArg(args, "arg3"))
        {
            line += $" arg3={Arg(args, "arg3")}";
        }

        line += $" control0={Arg(args, "control0")} control1={Arg(args, "control1")}";
        if (includeSecondaryControl)
        {
            line += $" secondary_control={Arg(args, "secondary_control")}";
        }

        return line;
    }

    private static string RenderTitle(Dictionary<string, List<string>> args)
    {
        var line = $"TITLE {Arg(args, "title")}";
        if (HasArg(args, "subtitle"))
        {
            line += $" subtitle={Arg(args, "subtitle")}";
        }

        return line;
    }

    private static string RenderResourceObject(string mnemonic, Dictionary<string, List<string>> args)
    {
        var line = $"{mnemonic} object={Arg(args, "object")} pattern={Arg(args, "pattern")}";
        if (HasArg(args, "arg2"))
        {
            line += $" arg2={Arg(args, "arg2")}";
        }

        if (HasArg(args, "arg3"))
        {
            line += $" arg3={Arg(args, "arg3")}";
        }

        return line;
    }

    private static string RenderResourceObjectOp(string name, Dictionary<string, List<string>> args)
    {
        var mnemonic = name.ToUpperInvariant();
        return HasArg(args, "resource_object_name")
            ? $"{mnemonic} {Arg(args, "resource_object_name")}"
            : mnemonic;
    }

    private static string RenderFallback(string name, List<HlsArg> args)
    {
        var mnemonic = name.ToUpperInvariant();
        return args.Count == 0
            ? mnemonic
            : $"{mnemonic} {string.Join(" ", args.Select(FormatArg))}";
    }

    private static string FormatArg(HlsArg arg) =>
        arg.Value is null ? arg.Key : $"{arg.Key}={arg.Value}";

    private static Dictionary<string, List<string>> ToNamedArgs(IEnumerable<HlsArg> args)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var arg in args)
        {
            if (arg.Value is null)
            {
                continue;
            }

            if (!result.TryGetValue(arg.Key, out var values))
            {
                values = [];
                result[arg.Key] = values;
            }

            values.Add(arg.Value);
        }

        return result;
    }

    private static bool HasArg(Dictionary<string, List<string>> args, string key) =>
        args.TryGetValue(key, out var values) && values.Count > 0;

    private static string Arg(Dictionary<string, List<string>> args, string key) =>
        args.TryGetValue(key, out var values) && values.Count > 0 ? values[^1] : "?";

    private static List<HlsArg> DescribeHlsArgs(TblstrScrInstruction instruction)
    {
        var args = new List<HlsArg>();
        foreach (var field in DescribeFields(instruction))
        {
            var split = field.IndexOf('=');
            if (split < 0)
            {
                AddHlsArg(args, field, null);
                continue;
            }

            AddHlsArg(args, field[..split], field[(split + 1)..]);
        }

        return args;
    }

    private static void AddHlsArg(List<HlsArg> args, string key, string? value)
    {
        var mapped = MapHlsKey(key, value);
        if (mapped is null)
        {
            return;
        }

        var arg = mapped.Value;
        args.Add(new HlsArg(arg.ValueKey, arg.Value));
    }

    private readonly record struct HlsArg(string Key, string? Value);

    private static (string ValueKey, string? Value)? MapHlsKey(string key, string? value)
    {
        if (key.EndsWith("_len", StringComparison.Ordinal))
        {
            return null;
        }

        if (value is "\"\"" && key is "scene_subtitle" or "script_state_subtitle" or "resource_arg_2" or "resource_arg_3")
        {
            return null;
        }

        return key switch
        {
            "compare_flags" => ("flags", value),
            "target_offset" => ("goto", value),
            "speaker_index" => ("speaker", value),
            "message_index" => ("message", value),
            "alternate_message_index" => value == "-1" ? null : ("alternate_message", value),
            "message_color0_rgb" => ("message_color0", value),
            "message_color1_rgb" => ("message_color1", value),
            "voice_file" => ("voice", value),
            "display_name" => ("display_name", value),
            "label_name" => ("script", value),
            "script_name" => ("script", value),
            "scene_title" => ("title", value),
            "scene_subtitle" => ("subtitle", value),
            "script_state_title" => ("title", value),
            "script_state_subtitle" => ("subtitle", value),
            "choice_text_index" => ("text", value),
            "choice_result_slot" => ("result_slot", value),
            "pending_return_value" => ("value", value),
            "label_index" => ("label_index", value),
            "duration_ms" => ("duration", value),
            "wait_mode" => ("mode", value),
            "adv_layer_resource_mode" => null,
            "adv_layer_resource_target" => ("target", value),
            "resource_name" => ("resource", value),
            "adv_layer_clear_mode" => null,
            "adv_layer_clear_target" => ("target", value),
            "adv_scroll_target_mode" => null,
            "adv_scroll_target" => ("target", value),
            "adv_layer_slot" => null,
            "adv_layer" => ("layer", value),
            "adv_sp_slot" => null,
            "adv_sp_layer" => ("layer", value),
            "object_name" => ("object", value),
            "pattern_name" => ("pattern", value),
            "resource_arg_2" => ("arg2", value),
            "resource_arg_3" => ("arg3", value),
            "resource_record_control_0" => ("control0", value),
            "resource_record_control_1" => ("control1", value),
            "resource_record_secondary_control" => ("secondary_control", value),
            "resource_object_name" => ("object", value),
            "resource_object_x" => ("x", value),
            "resource_object_y" => ("y", value),
            "resource_object_frame" => ("frame", value),
            "resource_object_key" => ("key", value),
            "resource_object_key_x" => ("x", value),
            "resource_object_key_y" => ("y", value),
            "resource_object_keyframe_enable_value" => ("value", value),
            "resource_object_anm" => ("anm", value),
            "resource_object_alpha" => ("alpha", value),
            "audio_clear_target" => ("target", value),
            "audio_action" => ("action", value),
            "audio_wait_target" => ("audio_wait_target", value),
            "adv_view_sprite_index" => ("adv_view_sprite_index", value),
            "bgm_name" => ("track", value),
            "bgm_format" => ("bgm_format", value),
            "bgm_play_mode" => ("bgm_play_mode", value),
            "wave_name" => ("wave", value),
            "movie_name" => ("movie", value),
            "range_name" => ("range", value),
            "voice_group_slot" => ("voice_group_slot", value),
            "voice_group_prefix" => ("prefix", value),
            "voice_group_entry" => ("entry", value),
            "anm_ctl_target_mode" => null,
            "anm_ctl_target" => ("target", value),
            "anm_ctl_value" => ("value", value),
            _ => (key, value)
        };
    }

    private static string FormatLabelName(string label)
    {
        var builder = new StringBuilder(label.Length + 8);
        foreach (var ch in label)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
            else if (ch == '#')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }
            }
            else
            {
                builder.Append('_');
            }
        }

        var result = builder.ToString().Trim('_');
        return string.IsNullOrEmpty(result) ? "label" : result;
    }

    public string FormatScan(TblstrScrScanSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TBLSTR SCR opcode scan");
        sb.AppendLine($"files: {summary.FileCount}");
        sb.AppendLine($"instructions: {summary.InstructionCount}");
        sb.AppendLine($"issues: {summary.Issues.Count}");
        sb.AppendLine();

        sb.AppendLine("opcodes:");
        foreach (var (opcode, count) in summary.OpcodeCounts
                     .OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key))
        {
            var descriptor = TblstrScrOpcodeTable.Get(opcode);
            sb.AppendLine($"  {opcode,3}  {count,6}  {descriptor.Name}  status={descriptor.Status}");
        }

        sb.AppendLine();
        sb.AppendLine("base lengths:");
        foreach (var (length, count) in summary.BaseLengthCounts
                     .OrderBy(pair => pair.Key))
        {
            sb.AppendLine($"  {length,3}  {count,6}");
        }

        if (summary.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("issues:");
            foreach (var issue in summary.Issues)
            {
                sb.AppendLine($"  - {issue}");
            }
        }

        return sb.ToString();
    }

    private static string WriteDocument(TblstrScrDocument document, string title, bool includeDebugFields)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine($"source={Quote(document.SourceName)}");
        sb.AppendLine($"magic0=0x{document.Magic0:X8}");
        sb.AppendLine($"magic1=0x{document.Magic1:X8}");
        sb.AppendLine($"payload_size={document.PayloadSize}");
        sb.AppendLine();

        foreach (var instruction in document.Instructions)
        {
            if (document.LabelsByOffset.TryGetValue(instruction.Offset, out var labels))
            {
                foreach (var label in labels)
                {
                    sb.AppendLine($"label {Quote(label)}:");
                }
            }

            sb.Append($"@0x{instruction.Offset:X8} ");
            sb.Append(instruction.Descriptor.Name);
            sb.Append($" opcode={instruction.Opcode}");
            if (includeDebugFields)
            {
                sb.Append($" status={instruction.Descriptor.Status}");
            }
            sb.Append($" base_len={instruction.BaseLength}");
            sb.Append($" extra_len={instruction.ExtraLength}");
            sb.Append($" total_len={instruction.TotalLength}");
            sb.AppendLine();

            if (instruction.BaseOperandBytes.Length > 0)
            {
                sb.AppendLine($"  operands_hex={Hex(instruction.BaseOperandBytes)}");
                foreach (var field in DescribeFields(instruction))
                {
                    sb.AppendLine($"  {field}");
                }
            }

            foreach (var str in instruction.Strings)
            {
                sb.AppendLine($"  string {str.Name} inst[0x{str.LengthOffset:X2}] len={str.DeclaredLength} data=+0x{str.DataOffset:X2} text={Quote(str.Text)}");
            }

            sb.AppendLine($"  raw={Hex(instruction.RawBytes)}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static List<string> DescribeFields(TblstrScrInstruction instruction)
    {
        var fields = new List<string>();
        var raw = instruction.RawBytes.AsSpan();
        var baseSpan = raw[..instruction.BaseLength];
        switch (instruction.Opcode)
        {
            case 0 when baseSpan.Length >= 12:
                fields.Add($"value_flags=0x{baseSpan[2]:X2}");
                fields.Add($"value_table={(((baseSpan[2] & 4) != 0) ? "local_value_table" : "scenario_value_table")}");
                fields.Add($"value_index={U16(baseSpan, 6)}");
                fields.Add($"value={I32(baseSpan, 8)}");
                break;

            case 2 when baseSpan.Length >= 8:
                fields.Add($"target_offset=0x{U32(baseSpan, 4):X8}");
                break;

            case 9 when baseSpan.Length >= 8:
                fields.Add($"menu_flags=0x{U16(baseSpan, 2):X4}");
                fields.Add($"menu_source_index={I32(baseSpan, 4)}");
                break;

            case 10 when baseSpan.Length >= 8:
                fields.Add($"choice_id={S8(baseSpan[2])}");
                fields.Add($"choice_text_index={I32(baseSpan, 4)}");
                break;

            case 11 when baseSpan.Length >= 4:
                fields.Add($"choice_result_slot={U16(baseSpan, 2)}");
                break;

            case 18 when baseSpan.Length >= 4:
                fields.Add($"movie_name_len={baseSpan[3]}");
                fields.Add("movie_path=Movie/<movie_name>.mpg");
                break;

            case >= 3 and <= 8 when baseSpan.Length >= 16:
                fields.Add($"compare_flags=0x{baseSpan[2]:X2}");
                fields.Add($"left=0x{U32(baseSpan, 4):X8}");
                fields.Add($"right=0x{U32(baseSpan, 8):X8}");
                fields.Add($"target_offset=0x{U32(baseSpan, 12):X8}");
                break;

            case 24 when baseSpan.Length >= 8:
                fields.Add($"state_27_value={I32(baseSpan, 4)}");
                fields.Add("return_code=state_27_value_zero ? 3 : 12");
                break;

            case 33:
                fields.Add("auto_wait_checkpoint=use_current_pc_when_auto_wait_enabled");
                break;

            case 34:
                fields.Add("alt_wait_checkpoint=use_previous_pc_when_alt_wait_enabled");
                fields.Add("checkpoint_flag=0x40000000");
                fields.Add("return_code=enabled_and_not_modal ? 14 : 2");
                break;

            case 39:
                fields.Add("script_control=stop_or_pause");
                fields.Add("return_code=0");
                break;

            case 63:
                fields.Add("event=push_system_button_when_kaisou_fg_is_set");
                break;

            case 61 when baseSpan.Length >= 5:
                fields.Add($"state_byte_0={baseSpan[2]}");
                fields.Add($"state_byte_1={baseSpan[3]}");
                fields.Add($"state_byte_2={baseSpan[4]}");
                break;

            case 71:
                fields.Add("resource_mark=current_resource_name_from_state");
                break;

            case 74 when baseSpan.Length >= 12:
                fields.Add($"random_mod={I32(baseSpan, 4)}");
                fields.Add($"value_index={unchecked((short)U16(baseSpan, 8))}");
                fields.Add("value_table=scenario_value_table");
                break;

            case 80 when baseSpan.Length >= 8:
                fields.Add($"bgm_format_code={baseSpan[2]}");
                fields.Add($"bgm_format={DescribeBgmFormat(baseSpan[2])}");
                fields.Add($"bgm_name_len={baseSpan[3]}");
                fields.Add($"bgm_play_mode={baseSpan[4]}");
                break;

            case 19:
                AddTextFields(fields, baseSpan);
                break;

            case 20:
                fields.Add("message_state=close_current_window");
                break;

            case 21 when baseSpan.Length >= 5:
                fields.Add($"adv_layer_resource_mode={S8(baseSpan[3])}");
                fields.Add($"adv_layer_resource_target={DescribeAdvLayerMode(S8(baseSpan[3]))}");
                fields.Add($"resource_name_len={baseSpan[4]}");
                break;

            case 22 when baseSpan.Length >= 8:
                fields.Add($"wait_mode={S8(baseSpan[2])}");
                fields.Add($"duration_ms={U32(baseSpan, 4)}");
                break;

            case 23 when baseSpan.Length >= 3:
                fields.Add($"adv_layer_clear_mode={S8(baseSpan[2])}");
                fields.Add($"adv_layer_clear_target={DescribeAdvLayerMode(S8(baseSpan[2]))}");
                break;

            case 83 when baseSpan.Length >= 4:
                fields.Add($"audio_group={baseSpan[2]}");
                fields.Add($"audio_channel={baseSpan[3]}");
                fields.Add($"audio_clear_target={DescribeAudioClearTarget(baseSpan[2], baseSpan[3])}");
                break;

            case 82 when baseSpan.Length >= 5:
                fields.Add($"sound_group={baseSpan[2]}");
                fields.Add($"sound_slot={baseSpan[3]}");
                fields.Add($"wave_play_target={DescribeWavePlayTarget(baseSpan[2], baseSpan[3])}");
                break;

            case 84 when baseSpan.Length >= 4:
                fields.Add($"scr_event_group={baseSpan[2]}");
                fields.Add($"scr_event_arg={baseSpan[3]}");
                fields.Add($"scr_event_action={DescribeScrEventAction(baseSpan[2], baseSpan[3])}");
                break;

            case 85:
                fields.Add("movie_state=clear_current_movie_state");
                break;

            case 88 when baseSpan.Length >= 7:
                fields.Add($"adv_layer_slot={baseSpan[2]}");
                fields.Add($"adv_layer={DescribeAdvLayerMode(baseSpan[2])}");
                fields.Add($"color_filter_mode={S8(baseSpan[3])}");
                fields.Add($"color_filter_arg=0x{U24(baseSpan, 4):X6}");
                break;

            case 89 when baseSpan.Length >= 16:
                fields.Add($"adv_scroll_target_mode={I32(baseSpan, 4)}");
                fields.Add($"adv_scroll_target={DescribeAdvLayerMode(I32(baseSpan, 4))}");
                fields.Add($"x={unchecked((short)U16(baseSpan, 8))}");
                fields.Add($"y={unchecked((short)U16(baseSpan, 10))}");
                fields.Add($"duration_or_state={I32(baseSpan, 12)}");
                break;

            case 90 when baseSpan.Length >= 12:
                fields.Add($"adv_scroll_target_mode={I32(baseSpan, 4)}");
                fields.Add($"adv_scroll_target={DescribeAdvLayerMode(I32(baseSpan, 4))}");
                fields.Add($"x={unchecked((short)U16(baseSpan, 8))}");
                fields.Add($"y={unchecked((short)U16(baseSpan, 10))}");
                break;

            case 91 when baseSpan.Length >= 8:
                fields.Add($"value_copy_flags=0x{baseSpan[2]:X2}");
                fields.Add($"value_copy_destination={(((baseSpan[2] & 4) != 0) ? "local_value_table" : "scenario_value_table")}");
                fields.Add($"value_copy_source={(((baseSpan[2] & 8) != 0) ? "local_value_table" : "scenario_value_table")}");
                fields.Add($"destination_index={U16(baseSpan, 4)}");
                fields.Add($"source_index={U16(baseSpan, 6)}");
                break;

            case 93:
                fields.Add("display_name_state=clear_current_display_name_and_reset_object");
                break;

            case 95 when baseSpan.Length >= 5:
                fields.Add($"wait_resume_value=0x{U24(baseSpan, 2):X6}");
                fields.Add("wait_resume_state=write_current_script_and_resume_value_when_enabled");
                break;

            case 87 when baseSpan.Length >= 8:
                fields.Add($"pending_return_value={I32(baseSpan, 4)}");
                break;

            case 96 when baseSpan.Length >= 5:
                fields.Add($"adv_back_mode={U24(baseSpan, 2)}");
                break;

            case 94 when baseSpan.Length >= 8:
            case 112 when baseSpan.Length >= 8:
                fields.Add($"label_index=0x{U32(baseSpan, 4):X8}");
                break;

            case 113:
                fields.Add("call_stack_action=return_to_saved_script_and_pc");
                break;

            case 134:
                fields.Add("movie_action=stop_if_not_skipping_or_previewing");
                break;

            case 135:
                fields.Add("movie_state=clear_current_movie_name_and_state");
                fields.Add("movie_action=modal ? stop_now : fade_out");
                break;

            case 136:
                fields.Add("resource_effect_wait=call_current_resource_vfunc60");
                fields.Add("resume_pc=effect_active ? current_resume_pc : unchanged");
                fields.Add("return_code=effect_active ? 3 : 2");
                break;

            case 140 when baseSpan.Length >= 4:
                fields.Add($"audio_group={baseSpan[2]}");
                fields.Add($"audio_slot={baseSpan[3]}");
                fields.Add($"audio_action={DescribeAudioSlotFadeAction(baseSpan[2], baseSpan[3], "fade_in")}");
                fields.Add("audio_fade=volume_-5000_to_current_2000ms");
                break;

            case 141 when baseSpan.Length >= 4:
                fields.Add($"audio_group={baseSpan[2]}");
                fields.Add($"audio_slot={baseSpan[3]}");
                fields.Add($"audio_action={DescribeAudioSlotFadeAction(baseSpan[2], baseSpan[3], "fade_out")}");
                fields.Add("audio_fade=volume_current_to_-10000_2000ms");
                fields.Add("modal_branch=this+44 ? clear_script_text_state_and_close_loop_slot : normal_fade_out");
                break;

            case 142 when baseSpan.Length >= 4:
                fields.Add($"audio_group={baseSpan[2]}");
                fields.Add($"audio_slot={baseSpan[3]}");
                fields.Add($"audio_wait_target={DescribeAudioSlotWaitTarget(baseSpan[2], baseSpan[3])}");
                fields.Add("resume_pc=audio_busy ? previous_pc : unchanged");
                fields.Add("return_code=audio_busy ? 3 : 2");
                break;

            case 114 when baseSpan.Length >= 12:
                fields.Add($"adv_sp_slot={baseSpan[2]}");
                fields.Add($"adv_sp_layer={DescribeAdvSpLayer(baseSpan[2])}");
                fields.Add($"x={I32(baseSpan, 4)}");
                fields.Add($"y={I32(baseSpan, 8)}");
                break;

            case 115 when baseSpan.Length >= 8:
                fields.Add($"adv_sp_slot={baseSpan[2]}");
                fields.Add($"adv_sp_layer={DescribeAdvSpLayer(baseSpan[2])}");
                fields.Add($"frame_index={I32(baseSpan, 4)}");
                break;

            case 117 when baseSpan.Length >= 8:
                fields.Add($"adv_view_sprite_index={I32(baseSpan, 4)}");
                fields.Add("adv_view_sprite_path=AdvView_enter_resume_calls_SpriteChange");
                break;

            case 119 when baseSpan.Length >= 8:
                fields.Add($"run_state_value={I32(baseSpan, 4)}");
                fields.Add("run_state_enabled=1");
                break;

            case 120:
                fields.Add("run_state=clear");
                break;

            case 122 when baseSpan.Length >= 3:
                fields.Add($"adv_layer_slot={baseSpan[2]}");
                fields.Add($"adv_layer={DescribeAdvLayerMode(baseSpan[2])}");
                break;

            case 121 when baseSpan.Length >= 17:
                fields.Add($"adv_sp_slot={baseSpan[2]}");
                fields.Add($"adv_sp_layer={DescribeAdvSpLayer(baseSpan[2])}");
                fields.Add($"object_name_len={baseSpan[4]}");
                fields.Add($"pattern_name_len={baseSpan[8]}");
                fields.Add($"resource_arg_2_len={baseSpan[12]}");
                fields.Add($"resource_arg_3_len={baseSpan[16]}");
                break;

            case 143:
            case 144:
                AddResourceBundleFields(fields, baseSpan, instruction.Opcode);
                break;

            case 145 when baseSpan.Length >= 12:
                fields.Add($"range_name_len={baseSpan[3]}");
                fields.Add($"range_start={I32(baseSpan, 4)}");
                fields.Add($"range_end={I32(baseSpan, 8)}");
                break;

            case 146 when baseSpan.Length >= 4:
                fields.Add($"voice_group_slot={baseSpan[2]}");
                fields.Add($"voice_group_prefix_len={baseSpan[3]}");
                break;

            case 147 when baseSpan.Length >= 4:
                fields.Add($"voice_group_entry_len={baseSpan[3]}");
                fields.Add("voice_group_source=previous_opcode_146_prefix");
                break;

            case 148:
                fields.Add("nop=return_continue");
                break;

            case 150 when baseSpan.Length >= 16:
                fields.Add($"adv_sp_slot={baseSpan[2]}");
                fields.Add($"adv_sp_layer={DescribeAdvSpLayer(baseSpan[2])}");
                fields.Add($"keydata_value_0={I32(baseSpan, 4)}");
                fields.Add($"keydata_value_1={I32(baseSpan, 8)}");
                fields.Add($"keydata_value_2={U32(baseSpan, 12)}");
                break;

            case 151 when baseSpan.Length >= 8:
                fields.Add($"adv_sp_slot={baseSpan[2]}");
                fields.Add($"adv_sp_layer={DescribeAdvSpLayer(baseSpan[2])}");
                fields.Add($"keydata_enable_value={I32(baseSpan, 4)}");
                fields.Add("keydata_enable_value_used_when_state_176_is_zero=true");
                break;

            case 152 when baseSpan.Length >= 7:
                fields.Add($"message_color0_rgb=0x{U24(baseSpan, 4):X6}");
                fields.Add("message_color0_default=0xFFFFFF");
                break;

            case 153 when baseSpan.Length >= 4:
                fields.Add($"message_color_mode={baseSpan[3]}");
                break;

            case 154 when baseSpan.Length >= 7:
                fields.Add($"message_color1_rgb=0x{U24(baseSpan, 4):X6}");
                fields.Add("message_color1_default=0xFFFFFF");
                break;

            case 156 when baseSpan.Length >= 12:
                fields.Add($"resource_object_x={I32(baseSpan, 4)}");
                fields.Add($"resource_object_y={I32(baseSpan, 8)}");
                break;

            case 157 when baseSpan.Length >= 8:
                fields.Add($"resource_object_frame={I32(baseSpan, 4)}");
                break;

            case 158:
                fields.Add("resource_object_action=clear");
                break;

            case 159 when baseSpan.Length >= 16:
                fields.Add($"resource_object_key={I32(baseSpan, 4)}");
                fields.Add($"resource_object_key_x={I32(baseSpan, 8)}");
                fields.Add($"resource_object_key_y={I32(baseSpan, 12)}");
                fields.Add("resource_object_keyframe=position");
                break;

            case 160 when baseSpan.Length >= 8:
                fields.Add($"resource_object_keyframe_enable_value={I32(baseSpan, 4)}");
                fields.Add("resource_object_keyframe_enable_value_used_when_modal_state_is_zero=true");
                break;

            case 161 when baseSpan.Length >= 4:
                fields.Add($"resource_object_anm={baseSpan[3]}");
                break;

            case 162 when baseSpan.Length >= 8:
                fields.Add($"adv_event_state_124={I32(baseSpan, 4)}");
                break;

            case 163:
                fields.Add("adv_sp_keyframes=validate_all_layers_have_key0");
                fields.Add("return_code=6");
                break;

            case 164 when baseSpan.Length >= 12:
                fields.Add($"resource_object_key={I32(baseSpan, 4)}");
                fields.Add($"resource_object_anm={I32(baseSpan, 8)}");
                fields.Add("resource_object_keyframe=anm");
                break;

            case 165 when baseSpan.Length >= 12:
                fields.Add($"resource_object_key={I32(baseSpan, 4)}");
                fields.Add($"resource_object_alpha={I32(baseSpan, 8)}");
                fields.Add("resource_object_keyframe=alpha");
                break;

            case 166 when baseSpan.Length >= 8:
                fields.Add($"resource_object_alpha={I32(baseSpan, 4)}");
                break;

            case >= 167 and <= 169 when baseSpan.Length >= 3:
                fields.Add($"anm_ctl_target_mode={S8(baseSpan[2])}");
                fields.Add($"anm_ctl_target={DescribeAdvLayerMode(S8(baseSpan[2]))}");
                break;

            case >= 170 and <= 171 when baseSpan.Length >= 8:
                fields.Add($"anm_ctl_target_mode={S8(baseSpan[2])}");
                fields.Add($"anm_ctl_target={DescribeAdvLayerMode(S8(baseSpan[2]))}");
                fields.Add($"anm_ctl_value={I32(baseSpan, 4)}");
                break;

            case 172:
                fields.Add("nop=return_continue");
                break;
        }

        return fields;
    }

    private static void AddTextFields(List<string> fields, ReadOnlySpan<byte> instruction)
    {
        if (instruction.Length >= 8)
        {
            fields.Add($"speaker_index={I32(instruction, 4)}");
        }

        if (instruction.Length >= 12)
        {
            fields.Add($"message_index={I32(instruction, 8)}");
        }

        if (instruction.Length >= 16)
        {
            fields.Add($"alternate_message_index={I32(instruction, 12)}");
        }
    }

    private static void AddResourceBundleFields(List<string> fields, ReadOnlySpan<byte> instruction, int opcode)
    {
        if (instruction.Length >= 17)
        {
            fields.Add($"adv_sp_slot={instruction[2]}");
            fields.Add($"adv_sp_layer={DescribeAdvSpLayer(instruction[2])}");
            fields.Add($"object_name_len={instruction[4]}");
            fields.Add($"pattern_name_len={instruction[8]}");
            fields.Add($"resource_arg_2_len={instruction[12]}");
            fields.Add($"resource_arg_3_len={instruction[16]}");
        }

        if (instruction.Length >= 28)
        {
            fields.Add("resource_record_control_enabled=1");
            fields.Add($"resource_record_control_0={I32(instruction, 20)}");
            fields.Add($"resource_record_control_1={I32(instruction, 24)}");
        }

        if (opcode == 144 && instruction.Length >= 32)
        {
            fields.Add("resource_record_secondary_control_enabled=1");
            fields.Add($"resource_record_secondary_control={I32(instruction, 28)}");
        }
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private static int I32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    private static int U24(ReadOnlySpan<byte> bytes, int offset) =>
        bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);

    private static int S8(byte value) => unchecked((sbyte)value);

    private static string DescribeAdvSpLayer(byte slot) =>
        slot is >= 7 and <= 11 ? $"adv_sp{slot - 6}" : "unknown";

    private static string DescribeAdvLayerMode(int mode) =>
        mode switch
        {
            0 or 1 => "adv_back",
            2 => "adv_back_special",
            4 => "adv_event",
            5 => "transition_name",
            >= 7 and <= 11 => $"adv_sp{mode - 6}",
            _ => "unknown"
        };

    private static string DescribeAudioClearTarget(byte group, byte channel) =>
        group switch
        {
            0 => channel switch
            {
                0 => "voice_slot_0_and_state",
                1 => "sound_channel_0",
                2 => "sound_channel_1",
                _ => "invalid_group0_channel"
            },
            1 => channel < 3 ? $"voice_slot_{channel}" : "invalid_voice_slot",
            _ => "unknown_audio_group"
        };

    private static string DescribeScrEventAction(byte group, byte arg) =>
        (group, arg) switch
        {
            (0, 0) => "emit_scr_event_18",
            (1, 0) => "clear_audio_and_emit_scr_events_8_34",
            _ => "invalid_handler_branch"
        };

    private static string DescribeBgmFormat(byte format) =>
        format switch
        {
            1 => "mid",
            2 => "ogg",
            0 => "cdda",
            _ => $"unknown_{format}"
        };

    private static string DescribeWavePlayTarget(byte group, byte slot) =>
        group switch
        {
            0 => slot switch
            {
                0 => "voice_or_primary_wave",
                1 => "sound_channel_0",
                2 => "sound_channel_1",
                _ => slot < 8 ? "invalid_group0_channel" : "invalid_group0_large_channel"
            },
            1 => slot < 3 ? $"loop_slot_{slot}" : "invalid_loop_slot",
            _ => "unknown_audio_group"
        };

    private static string DescribeAudioSlotFadeAction(byte group, byte slot, string action) =>
        group switch
        {
            0 => slot == 0 ? $"default_se_{action}" : "invalid_se_branch",
            1 => slot < 3 ? $"loop_slot_{slot}_{action}" : "invalid_loop_slot",
            _ => "unknown_audio_group"
        };

    private static string DescribeAudioSlotWaitTarget(byte group, byte slot) =>
        group switch
        {
            0 => slot == 0 ? "default_se" : "invalid_se_branch",
            1 => slot < 3 ? $"loop_slot_{slot}" : "invalid_loop_slot",
            _ => "unknown_audio_group"
        };

    private static string Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Quote(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
