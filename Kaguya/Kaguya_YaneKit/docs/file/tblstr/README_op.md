# TBLSTR系 opcode 逆向记录

本文用于记录 TBLSTR系 `.scr` opcode。这里出现的 `sub_xxxxx` 都是当前样本的逆向证据地址，不是通用格式字段，也不会进入工具 HLS/scan/disasm 输出。

## 阅读顺序

这页按“格式边界 -> opcode 总览 -> 分组细节 -> 调度返回码 -> 未收口项”的顺序写：

1. `读取规则`：只说明 TBLSTR系 `.scr` 指令头、base length 和内联字符串消费规则。
2. `Opcode 总览`：只放 `.scr` opcode，不混入 `TBLSTR.ARC`、`.tbl` 或导出工具的临时字段。
3. `.scr opcode 详细说明`：按文本、跳转、ADV、音频、对象/keyframe 等业务组展开。
4. `Handler 返回码`：单独记录调度状态机返回值，避免把返回码误写成 opcode。
5. `当前边界与未收口项`：只列还需要样本或调用链继续确认的点。

## 读取规则

TBLSTR系 `.scr` 指令头：

```text
u8 opcode
u8 base_length
byte[base_length - 2] fixed_operands
byte[] inline_strings
```

`base_length` 只覆盖固定字段。handler 若需要字符串，会调用内联字符串 helper 继续从当前 pc 读取并推进 pc。字符串长度槽位于固定字段中；字符串正文在固定字段后按顺序排列。

`sub_523100` 已确认是内联字符串读取 helper：调用方传入字符串字节数，它从 payload 当前 pc 读取对应字节数，每字节按 bitwise NOT 解码，遇到原始字节 `0xFF` 结束，并把 pc 增加传入长度。

## Opcode 总览

| opcode | 当前工具名 / 状态 | 字符串槽 | 关键立即数 / 行为 |
| ---: | --- | --- | --- |
| 0 | `set_value_immediate` | - | 写 value 表；`inst[2] & 0x04` 选表，`u16+6` 是索引，`i32+8` 是值 |
| 1 | `add_value` | - | `inst[2] & 0x04` 选表，`u16+6` 是 value index，`i32+8` 是加数；是 opcode 0 的加法对应指令 |
| 2 | `jump_pc` | - | 直接设置 pc |
| 3 | `jump_if_equal` | - | 比较左右操作数，相等时跳转 |
| 4 | `jump_if_not_equal` | - | 比较左右操作数，不相等时跳转 |
| 5 | `jump_if_greater` | - | 左操作数大于右操作数时跳转 |
| 6 | `jump_if_less` | - | 左操作数小于右操作数时跳转 |
| 7 | `jump_if_greater_equal` | - | 左操作数大于等于右操作数时跳转 |
| 8 | `jump_if_less_equal` | - | 左操作数小于等于右操作数时跳转 |
| 9 | `menu_begin_or_resume` | - | menu 状态路径 |
| 10 | `menu_add_choice` | - | choice id + 文本索引 |
| 11 | `menu_commit_choice` | - | choice result 写回 |
| 12 | `jump_script_start` | `inst[3]` | 读取脚本名，走 `.sct` 入口解析但 label 长度为 0，最终加载 `scr/<script>.scr` 并从 pc `0` 开始 |
| 18 | `play_movie` | `inst[3]` | 组装 `Movie/<movie_name>.mpg` 并播放 |
| 19 | `message_window` | `inst[2]` / `inst[3]` | speaker/message/voice 路径；第二字符串受 `i32+12` 影响 |
| 20 | `close_message_window` | - | 关闭/清理当前 message 状态 |
| 21 | `set_adv_layer_resource` | `inst[4]` | `inst[3]` 是 ADV layer/resource mode |
| 22 | `set_wait_mode_duration` | - | `inst[2]` mode，`u32+4` duration，0 时默认 500 |
| 23 | `clear_adv_layer` | - | 清理 `adv_back` / `adv_event` / `adv_spN` |
| 24 | `set_state_27_or_return` | - | `i32+4` 为 0 返回 3，否则写 `this+27` 并返回 12 |
| 33 | `set_auto_wait_checkpoint` | - | auto/skip 等待 checkpoint |
| 34 | `set_alt_wait_checkpoint` | - | 写 checkpoint flag `0x40000000` |
| 39 | `stop_script` | - | handler 直接返回 0，交给调度器结束/暂停当前脚本并做清理 |
| 44 | `set_current_display_name` | `inst[3]` | 读取内联字符串，写当前显示名/nameplate 槽，并走 message window 共用的名字刷新路径 |
| 61 | `set_byte_triplet_state` | - | `inst[2..4]` 写 `this+64/+65/+66` |
| 63 | `push_kaisou_system_button` | - | `kaisou_fg` 非 0 时投递系统按钮事件 |
| 71 | `mark_current_resource_active` | - | 按当前 resource name 标记资源 active |
| 74 | `set_value_random` | - | 随机值写入 scenario value table |
| 80 | `play_bgm` | `inst[3]` | `bgm/` + 曲名 + 格式扩展名；`inst[2]` 选择格式，常见为 `2=.ogg` |
| 82 | `play_wave` | `inst[4]` | `wav/` + wave 名 + `.ogg`；`inst[2]/[3]` 选择 voice/SE/loop slot 路径 |
| 83 | `clear_audio_channel` | - | 清理 sound/voice channel |
| 84 | `emit_system_scr_event` | - | 投递 `GL::EvtData_ScrEvent` |
| 85 | `clear_movie_state` | - | 清 movie/resource 字符串和状态 |
| 87 | `set_pending_return_value` | - | `i32+4` 写 `this+27`，返回 11 |
| 88 | `set_adv_layer_color_filter` | - | 设置 ADV layer color filter |
| 89 | `set_adv_scroll_position` | - | 写 adv scroll/camera 目标坐标 |
| 90 | `apply_adv_scroll_position` | - | 直接应用 adv scroll/camera 坐标 |
| 91 | `copy_value` | - | value 表之间复制 |
| 93 | `clear_current_display_name` | - | 清当前显示/资源名字符串 |
| 94 | `jump_script_label_index` | `inst[2]` | 读取脚本名；`i32+4` 是 `label.tbl` 记录索引，经 label 表取 targetOffset 后切换到目标脚本 |
| 95 | `set_wait_resume_checkpoint` | - | 等待恢复 checkpoint |
| 96 | `reset_adv_layers` | - | 重置 adv_back/adv_event/adv_sp 和 transition |
| 112 | `call_script_label_index` | `inst[2]` | 先把当前脚本名和当前 pc 压入 gosub 栈，再按 `i32+4` 的 `label.tbl` 记录索引切换脚本 |
| 113 | `return_from_script_call` | - | 从 call stack 弹出 script/pc |
| 114 | `set_adv_sp_position` | - | ADV SP x/y |
| 115 | `set_adv_sp_frame` | - | ADV SP frame |
| 117 | `set_adv_view_sprite_index` | - | `i32+4` 写 `*(this+28)+424`；进入/恢复 `AdvView` 时作为 `SpriteChange(..., index)` 的 index 使用 |
| 119 | `set_run_state` | - | `i32+4` 写 `this+46`，并设置运行状态 |
| 120 | `clear_run_state` | - | 清运行状态 |
| 121 | `set_adv_sp_resource_bundle` | `inst[4/8/12/16]` | `adv_sp1..5` 四字符串资源组 |
| 122 | `clear_adv_layer_color_filter` | - | 清 color filter 并同步子项 |
| 134 | `stop_movie_if_active` | - | 非 `this+44` 状态下停止 movie/resource |
| 135 | `clear_movie_state_and_fade_out` | - | 清 movie/resource 状态并停止或 fade-out |
| 136 | `wait_current_resource_effect` | - | 等待当前资源对象 effect/动作完成 |
| 137 | `set_scene_title` | `inst[2]` / `inst[3]` | 写当前场景标题/副标题状态；第二槽为 0 时为空 |
| 140 | `fade_in_wave_loop` | - | wave loop fade-in |
| 141 | `fade_out_wave_loop` | - | wave loop fade-out；`this+44` 下有特殊清理分支 |
| 142 | `wait_wave_slot` | - | 检查默认 SE 或 0..2 loop slot 的音频对象 busy 状态；busy 时回拨 pc 并返回等待码 |
| 143 | `load_adv_sp_resource_bundle_controlled` | `inst[4/8/12/16]` | opcode 121 同系资源组加载；额外把资源记录控制字段 `+136/+140/+144` 置为启用和值 |
| 144 | `load_adv_sp_resource_bundle_controlled_ex` | `inst[4/8/12/16]` | opcode 143 的扩展；额外把第二控制字段 `+152/+156` 置为启用和值 |
| 145 | `register_named_range` | `inst[3]` | 登记 name + start/end 范围 |
| 146 | `set_voice_group_prefix` | `inst[3]` | 暂存 voice group slot 和角色/前缀字符串 |
| 147 | `append_voice_group_entry` | `inst[3]` | 读取 voice/wave 文件名，并和 opcode 146 暂存前缀组成 voice group entry |
| 148 | `nop` | - | handler 只返回 2 |
| 150 | `add_adv_sp_keydata` | - | 向 ADV SP keydata vector 追加记录 |
| 151 | `enable_adv_sp_keydata` | - | 启用 ADV SP keydata |
| 152 | `set_message_color0` | - | `inst[4..6]` 组成 24-bit RGB，写 `*(this+28)+156`；默认值 `0xFFFFFF` |
| 153 | `set_message_color_mode` | - | `inst[3]` 写 `*(this+28)+164`；与 message color 槽同组 |
| 154 | `set_message_color1` | - | `inst[4..6]` 组成 24-bit RGB，写 `*(this+28)+160`；默认值 `0xFFFFFF` |
| 155 | `init_resource_object` | `inst[2]` / `inst[3]` | 初始化具名资源对象 |
| 156 | `set_resource_object_position` | `inst[2]` | 写对象 x/y |
| 157 | `set_resource_object_frame` | `inst[2]` | 写对象 frame |
| 158 | `clear_resource_object` | `inst[2]` | 清对象状态 |
| 159 | `add_resource_object_position_keyframe` | `inst[2]` | 追加位置 keyframe |
| 160 | `enable_resource_object_keyframes` | `inst[2]` | 启用对象 keyframe |
| 161 | `set_resource_object_anm` | `inst[2]` | 写对象 ANM |
| 162 | `set_adv_event_state_124` | - | 写 `adv_event` record `+124` |
| 163 | `validate_adv_sp_keyframes` | - | 校验 ADV SP keyframe 数据 |
| 164 | `add_resource_object_anm_keyframe` | `inst[2]` | 追加 ANM keyframe |
| 165 | `add_resource_object_alpha_keyframe` | `inst[2]` | 追加 alpha keyframe |
| 166 | `set_resource_object_alpha` | `inst[2]` | 写对象 alpha |
| 167 | `anm_ctl_pause` | - | 写 ANM pause |
| 168 | `anm_ctl_start` | - | 写 ANM start |
| 169 | `anm_ctl_restart` | - | 写 ANM restart |
| 170 | `anm_ctl_waitcount` | - | 写 ANM waitcount |
| 171 | `anm_ctl_speed` | - | 写 ANM speed |
| 172 | `nop` | - | handler 只返回 2 |

## `.scr` opcode 详细说明

### 菜单与文本

| opcode | 工具名 | 已确认路径 |
| ---: | --- | --- |
| 9 | `menu_begin_or_resume` | 重置 menu 临时状态 `this+196/+200/+204`；`u16+2` 为 flags；非 0 时读取 `menu_source_index=i32+4` 对应项，结果 1/2 会写入 pending menu state；否则清理 menu command 容器并返回 16 |
| 10 | `menu_add_choice` | `inst[2]` 是 choice id；`i32+4` 是 choice 文本索引，走 `sub_523950` 解出文本后加入 menu choice 列表；同时保存 choice id 和文本索引 |
| 11 | `menu_commit_choice` | `u16+2` 是 choice result slot。普通阶段写入 menu state 并返回 7；menu 回调阶段会把当前选择结果写回结果表/历史记录 |
| 19 | `message_window` | `inst[2]` 是 voice 文件名长度；`inst[3]` 是 alternate voice 文件名长度；`i32+4` 走 `sub_523950` 作为 speaker/name 文本索引；`i32+8` 走 `sub_5237A0` 作为 message 文本索引；`i32+12 == -1` 时不启用 alternate message |
| 20 | `close_message_window` | 清理当前 message window 状态：调用 `sub_524EF0(this+124)`，设置当前 message object active/state 为 0 |
| 44 | `set_current_display_name` | `inst[3]` 是内联字符串长度。handler 把字符串复制到 `*(this+28)+224`，随后调用 `sub_57B250(name, 0, 0)`；opcode 19 的 speaker/name 分支也走同一个刷新 helper。因此它不是普通正文、caption 或 debug text，而是当前显示名/nameplate 字符串 |
| 152 | `set_message_color0` | handler 读取 `inst[4..6]` 组成 24-bit RGB，写入 `*(this+28)+156`。状态初始化时该字段为 `0xFFFFFF`，因此当前工具按 message color 槽输出为 `SET_MESSAGE_COLOR0 0xRRGGBB` |
| 153 | `set_message_color_mode` | handler 读取 `inst[3]` 写入 `*(this+28)+164`。该字段初始化为 0，和 `+156/+160` 同组；目前只证到 color mode/flag 作用域，不再保留裸 `state_164` 命名 |
| 154 | `set_message_color1` | handler 读取 `inst[4..6]` 组成 24-bit RGB，写入 `*(this+28)+160`。状态初始化时该字段同样为 `0xFFFFFF`，当前工具输出为 `SET_MESSAGE_COLOR1 0xRRGGBB` |

补充：当前 CC 样本扫描没有出现 opcode 152/153/154，所以这里的 `color0/color1/mode` 是基于 handler、初始化默认值和 message 邻近分派得到的保守业务名；`color0` 与 `color1` 的 UI 层称呼还没有进一步证死。

HLS 中可以用这组 opcode 改一整条或连续多条 `MESSAGE` 的显示颜色。典型写法是在目标文本前设置颜色，在目标文本后恢复默认色：

```text
SET_MESSAGE_COLOR0 0xFF0000
MESSAGE speaker=123 text=456
SET_MESSAGE_COLOR0 0xFFFFFF
```

若需要同时调整同组第二颜色槽和模式，可以写成：

```text
SET_MESSAGE_COLOR0 0xFF0000
SET_MESSAGE_COLOR1 0xFFFFFF
SET_MESSAGE_COLOR_MODE 1
MESSAGE speaker=123 text=456 voice="sample.voi"
SET_MESSAGE_COLOR_MODE 0
SET_MESSAGE_COLOR0 0xFFFFFF
SET_MESSAGE_COLOR1 0xFFFFFF
```

注意：这属于“脚本层 message 颜色状态”，适合改某条 `MESSAGE` 或一段连续 `MESSAGE`。一句话内部只改几个字的颜色，当前尚未确认 TBLSTR 文本存在可用的行内颜色控制码；不要把上面的 opcode 当成行内富文本标签使用。

### 字体大小与 ruby

当前没有确认到 TBLSTR系 `.scr` 可直接调整 message 字体大小的 opcode，也没有在 TBLSTR 文本正文中确认到可用的字体大小控制码。现有证据只到引擎渲染层：CC 样本字符串表里能看到 `OutlineFontTexture::SetFontInf`、`OutlineFontTexture::GetMaxRect`、`OutlineFontTexture::DrawChar`、`CreateFontIndirectA` 等字体对象/绘制相关符号；这只能说明引擎内部有字体渲染配置，不能等价为脚本或 TBLSTR 文本可编辑字体大小。

当前也没有确认到 ruby/ルビ 功能入口。已扫过的 TBLSTR opcode、HLS 输出、TBLSTR 文本样本，以及 CC 样本字符串表中均未发现明确的 `ruby` / `ルビ` / 振り仮名控制链路。后续如果要证死，需要继续追 message 文本 parser 到 `OutlineFontTexture` / `text_mgr` / message manager 的调用链，确认是否存在行内控制码或外部表驱动。

因此现在可编辑的文字显示属性是 message 颜色状态：`SET_MESSAGE_COLOR0`、`SET_MESSAGE_COLOR1`、`SET_MESSAGE_COLOR_MODE`。它们不是字体大小，也不是 ruby。

### 条件跳转

opcode `3..8` 是同一组比较跳转 handler，固定字段均为：

```text
u8 opcode
u8 base_length = 16
u8 compare_flags
u8 padding/reserved
u32 left
u32 right
u32 target_offset
```

比较成立时设置 pc 到 `target_offset`，否则继续下一条。已确认的 opcode 与 HLS 输出如下：

| opcode | HLS | 比较关系 |
| ---: | --- | --- |
| 3 | `IF_EQ` | `left == right` |
| 4 | `IF_NE` | `left != right` |
| 5 | `IF_GT` | `left > right` |
| 6 | `IF_LT` | `left < right` |
| 7 | `IF_GE` | `left >= right` |
| 8 | `IF_LE` | `left <= right` |

`compare_flags` 的操作数来源已确认：

| bit | 含义 |
| ---: | --- |
| `0x01` | 左操作数从 scenario value table 读取 |
| `0x02` | 右操作数从 scenario value table 读取 |
| `0x04` | 左操作数从 local value table 读取 |
| `0x08` | 右操作数从 local value table 读取 |
| 未置位 | 对应操作数按立即数使用 |

注意：旧工具里曾把 opcode 6/7 的业务名写反；当前已按 handler 证据修正为 `6=IF_LT`、`7=IF_GE`，HLS 回编也按这个关系生成原 opcode。

### 脚本跳转、等待与 value

| opcode | 工具名 | 已确认路径 |
| ---: | --- | --- |
| 0 | `set_value_immediate` | `inst[2] & 0x04` 选择 value 表：非 0 写 `this+29`，否则写 `*(this+28)+392` 指向的表；`u16+6` 是 value index；`i32+8` 是写入值 |
| 1 | `add_value` | `inst[2] & 0x04` 与 opcode 0 一样选择 local/scenario value 表；`u16+6` 是 value index；`i32+8` 会累加到目标槽，即 `value[index] += addend`。当前没有证据显示它有更高层 UI 专名，因此工具名按脚本语义保留为 `ADD_VALUE` |
| 12 | `jump_script_start` | `inst[3]` 是脚本名长度；handler 读取脚本名后调用 `.sct` 解析 helper，但传入空 label、label 长度 0，因此不会查 label 表，最终调用脚本加载路径进入 `scr/<script>.scr` 的 pc `0` |
| 24 | `set_state_27_or_return` | `i32+4` 为 0 时返回 3；非 0 时写入 `this+27` 并返回 12。业务层含义未继续拔高 |
| 33 | `set_auto_wait_checkpoint` | 在全局 auto/wait 设置允许且不在 skip 状态时，把当前 pc 写入 `this+55` 对应 checkpoint 字段，返回 14 |
| 34 | `set_alt_wait_checkpoint` | 读取 `this+4C`；条件成立时把 `*(this+0xDC)+8` 写成 pc/checkpoint，把 `*(this+0xDC)+4` 写成 `0x40000000`，返回 14；否则返回 2 |
| 74 | `set_value_random` | `i32+4 > 0` 时经 `sub_52ACF0` 取随机值，否则写 0；结果写入 scenario value table 的 `s16+8` 索引 |
| 87 | `set_pending_return_value` | 直接把 `i32+4` 写入 `this+27`，返回 11；业务名未完全证死 |
| 91 | `copy_value` | `inst[2] & 0x04` 选择目标 value 表，`inst[2] & 0x08` 选择来源 value 表；`u16+4` 是目标索引，`u16+6` 是来源索引 |
| 95 | `set_wait_resume_checkpoint` | 在全局等待条件允许且不是 `this+44` 状态时，写入当前脚本状态和 `inst[2..4]` 组成的 24-bit resume 值，返回 14 |
| 94 | `jump_script_label_index` | `inst[2]` 是脚本名长度；`i32+4` 是 `label.tbl` 的记录索引。handler 从 label 表记录 `+48` 取 targetOffset，再调用脚本加载路径进入目标脚本对应 pc |
| 112 | `call_script_label_index` | `inst[2]` 是脚本名长度；handler 先把当前脚本名 `*(this+28)+172` 和当前 pc `this+37` 写入 `GosubFMT` 栈，再按 `i32+4` 的 `label.tbl` 记录索引解析 targetOffset 并切换脚本 |
| 113 | `return_from_script_call` | 从 `this[28]+404..408` 的 call stack 弹出 saved script name 和 saved pc，再调用脚本切换路径 `sub_526E00` |
| 117 | `set_adv_view_sprite_index` | `i32+4` 写入 `*(this+28)+424`。进入/恢复 `AdvView` 时，`sub_584E60` 会读取该字段，对可用显示对象调用 `sub_56F7F0`；该 helper 越界错误字符串为 `SpriteChange(%s,%d)`，因此这里收紧为 AdvView 的 sprite change index，而不是裸 `state_424` |
| 119 | `set_run_state` | `i32+4` 写入 `this+46`，并设置 `this+47=1`、`this+48=0` |
| 120 | `clear_run_state` | 设置 `this+46=-1`、`this+47=0`、`this+48=0` |

### ADV layer / ADV SP

| opcode | 工具名 | 已确认路径 |
| ---: | --- | --- |
| 21 | `set_adv_layer_resource` | `inst[3]` 是 ADV layer/resource 模式，`inst[4]` 是资源名长度；经 `sub_52FE80` 处理 `adv_back` / `adv_event` / transition 名等路径 |
| 22 | `set_wait_mode_duration` | `inst[2]` 是 wait/transition mode；`u32+4` 是 duration，0 时 handler 内改成默认 500；写入 `this[28]+320/+324/+328` |
| 23 | `clear_adv_layer` | `inst[2]` 是 clear mode，经 `sub_51E6A0` 清理 `adv_back` / `adv_event` / `adv_spN` 等显示层 |
| 63 | `push_kaisou_system_button` | 读取变量名 `kaisou_fg` 的当前值；非 0 时创建 `GL::EvtData_PushSystemBtnEvent`，事件码 43，返回 14 |
| 71 | `mark_current_resource_active` | 取 `this[28]+172` 的当前 resource name，在资源列表中匹配字符串，命中后把对应项 active 标志置 1 |
| 88 | `set_adv_layer_color_filter` | `inst[2]` 是 ADV layer slot；`inst[3]` 是 filter mode；`inst[4..6]` 组成 24-bit filter 参数；写入 resource record 的 color filter 字段，并同步到子项 |
| 89 | `set_adv_scroll_position` | `i32+4` 选择 `adv_back` / `adv_event` / `adv_camera`；`s16+8/+10` 是 x/y；`i32+12` 在非 `this+44` 路径写入状态字段 |
| 90 | `apply_adv_scroll_position` | `i32+4` 选择 `adv_back` / `adv_event` / `adv_camera`；`s16+8/+10` 写入当前 scroll/camera 坐标 |
| 96 | `reset_adv_layers` | 通过 `sub_51C4A0` 清理 `adv_back` / `adv_event` / `adv_spN` 和 transition 名，然后把 `inst[2..4]` 的 24-bit 值写到 `adv_back` 记录字段 |
| 114 | `set_adv_sp_position` | `inst[2]` 映射 `adv_sp1..5`；`i32+4/+8` 写入 ADV SP 资源记录 x/y |
| 115 | `set_adv_sp_frame` | `inst[2]` 映射 `adv_sp1..5`；`i32+4` 写 frame index，并同步子资源 frame |
| 121 | `set_adv_sp_resource_bundle` | `inst[2]` 映射 `adv_sp1..5`；`inst[4]/[8]/[12]/[16]` 是四个字符串长度，当前工具命名为 `object_name`、`pattern_name`、`resource_arg_2`、`resource_arg_3` |
| 122 | `clear_adv_layer_color_filter` | 调用 `sub_51E9B0` 把 color filter enabled/mode/arg 清零，并同步到子项 |
| 143 | `load_adv_sp_resource_bundle_controlled` | 和 opcode 121 一样先用 `inst[2]` 映射 `adv_sp1..5`，读取 `object_name`、`pattern_name`、`resource_arg_2`、`resource_arg_3` 四个内联字符串，再调用同一个资源组加载 helper；区别是加载前会取 `*(this+28)+320` 的对应资源记录，把 `record+136` 设为 1，`record+140=i32+20`，`record+144=i32+24`，并清 `record+152/+156` |
| 144 | `load_adv_sp_resource_bundle_controlled_ex` | 和 opcode 143 同路径，但还会把 `record+152` 设为 1，`record+156=i32+28`；因此工具输出为 `LOAD_SPRITE_CONTROLLED_EX ... control0=... control1=... secondary_control=...` |
| 150 | `add_adv_sp_keydata` | `inst[2]` 映射 `adv_sp1..5`；`i32+4`、`i32+8`、`u32+12` 组成 12 字节 keydata record，追加到该 ADV SP 对象的 keydata vector |
| 151 | `enable_adv_sp_keydata` | `inst[2]` 映射 `adv_sp1..5`；设置 keydata 对象字段 `[1]=1`、`[8]=4`、`[11]=i32+4`；当 `this+176 != 0` 时写入值会被替换为 0 |
| 162 | `set_adv_event_state_124` | 取 `adv_event` 资源对象，将 `i32+4` 写入 record `+124` |
| 163 | `validate_adv_sp_keyframes` | 调用 `this+0xE0` 的 ADV SP keyframe 管理器校验函数，遍历缩放/动画/alpha 等 keyframe 数据；缺少 `key=0` 时弹错误框，opcode 返回 6 |

补充：当前 CC 样本的 `.scr` 扫描没有出现 opcode 143/144，所以 `control0/control1/secondary_control` 仍是结构名，不硬命名为 duration、transition 或 frame。已确认的是 handler 写入的 record 偏移和与 opcode 121 共享的资源组加载链。

### 场景标题与 voice group

| opcode | 工具名 | HLS | 已确认路径 |
| ---: | --- | --- | --- |
| 137 | `set_scene_title` | `TITLE "title" subtitle="subtitle"` | handler 读取两个内联字符串并写入 `*(this+28)+96` 与 `*(this+28)+120`；字符串表存在 `SceneTitle`，第二字符串长度为 0 时表示无副标题 |
| 146 | `set_voice_group_prefix` | `VOICE_GROUP_PREFIX slot=0 "rub"` | handler 暂存 voice group slot 到 `this+57`，并暂存角色/分组前缀字符串到 `this+58` |
| 147 | `append_voice_group_entry` | `VOICE_GROUP_ENTRY "t_rub0222_0229.ogg"` | handler 读取条目字符串，再用 opcode 146 暂存的前缀调用 voice group 追加路径；样本中表现为 `rub/aqu/kag/dai` 等角色前缀加 `t_*.ogg` 条目 |

旧 HLS 名 `PENDING_TEXT_A`、`APPEND_PENDING_TEXT_PAIR` 只作为兼容输入保留；新输出统一使用 voice group 命名。

### Movie / wave / ANM

| opcode | 工具名 | 已确认路径 |
| ---: | --- | --- |
| 18 | `play_movie` | `inst[3]` 是内联字符串长度；handler 组装 `Movie/<movie_name>.mpg` 并调用 movie 播放路径，成功/失败通过返回码进入后续状态处理 |
| 80 | `play_bgm` | `inst[3]` 是 BGM 名长度；handler 组装 `bgm/<bgm_name>`，再按 `inst[2]` 追加扩展名：`1=.mid`、`2=.ogg`、`0` 走 CDDA/error 分支；`inst[4]` 是传给 BGM 播放 helper 的 play mode/state |
| 82 | `play_wave` | `inst[4]` 是 wave 名长度；handler 组装 `wav/<wave_name>.ogg`。`inst[2]=0, inst[3]=0` 走 voice/primary wave 路径，`inst[2]=0, inst[3]=1/2` 走一次性 SE channel 0/1，`inst[2]=1, inst[3]=0..2` 走 loop wave slot |
| 83 | `clear_audio_channel` | `inst[2]` 是 audio group；group 0 清理 sound/voice 通道，group 1 清理 0..2 的 voice slot；会调用 `sub_57BC70` / `sub_57BDE0` |
| 84 | `emit_system_scr_event` | `(inst[2], inst[3])=(0,0)` 时投递 `GL::EvtData_ScrEvent` 事件 18；`(1,0)` 时先清 voice/sound 状态，再投递事件 8 和 34，返回 14；其他分支会报 call 参数错误 |
| 85 | `clear_movie_state` | 清空 `*(this+28)+196` 字符串和 `*(this+28)+220`，再调用 `sub_578BD0(1)` |
| 134 | `stop_movie_if_active` | 非 `this+44` 状态时调用 `sub_578730(1)`，该 helper 会清理字符串并对 movie/resource 对象走虚表停止路径 |
| 135 | `clear_movie_state_and_fade_out` | 清 `*(this+28)+196` 的当前 movie/resource 字符串，并把 `+212/+220` 状态清 0；`this+44 != 0` 时走虚表 `+24` 立即停止/关闭；`this+44 == 0` 时经 `sub_5798A0` 后走虚表 `+52` fade-out |
| 136 | `wait_current_resource_effect` | 从当前资源对象经 `sub_5797D0()` 调用虚表 `+60`；非 0 时把当前 resume pc `this+8C` 复制到 `this+94` 并返回 3；0 时返回 2 |
| 140 | `fade_in_wave_loop` | `inst[2]/[3]` 选择默认 SE 或 0..2 loop slot，最终调用音频对象虚表 `+48`；`CWaveSoundStatic +48=sub_57E3F0`，从 `-5000` 淡入当前音量，时长 `2000ms` |
| 141 | `fade_out_wave_loop` | `inst[2]/[3]` 选择默认 SE 或 0..2 loop slot，通常调用音频对象虚表 `+52`；`CWaveSoundStatic +52=sub_57E420`，从当前音量淡出到 `-10000`，时长 `2000ms`；`this+44` 状态下先清脚本文本状态，再走关闭/清理分支 |
| 142 | `wait_wave_slot` | `inst[2]/[3]` 选择默认 SE 或 0..2 loop slot，调用对应音频对象虚表 `+60` 查询 busy/active 状态；非 0 时把 `this+37` 回拨到 `this+35` 并返回 3，让调度器下一轮重跑本指令；0 时返回 2 继续执行 |
| 167 | `anm_ctl_pause` | 通过 `sub_51C6A0` 对 `adv_back` / `adv_event` / `adv_spN` 写入 ANM 控制的 `pause` 状态 |
| 168 | `anm_ctl_start` | 同上，写入 `start` 状态 |
| 169 | `anm_ctl_restart` | 同上，写入 `restart` 状态 |
| 170 | `anm_ctl_waitcount` | 同上，`i32+4` 写入 `waitcount` |
| 171 | `anm_ctl_speed` | 同上，`i32+4` 写入 `speed` |

### 具名资源对象 / keyframe

这里的“资源对象”指脚本侧通过名字在 `*(this+28)+320` 的 `ScrLayerDataFmt` 列表或 `this+56` 的 `KeyData` 列表中取得的对象，不是封包资源文件。

| opcode | 工具名 | 已确认路径 |
| ---: | --- | --- |
| 155 | `init_resource_object` | 读取 `object_name` 和 `init_arg`；用 `object_name` 在资源对象表中取 type 3 对象，把 `init_arg` 写入对象字符串字段 `+40`，清 `+92/+96/+124`，把 alpha `+128` 设为 `255`，再置 active/state `+8=1` |
| 156 | `set_resource_object_position` | 读取 `object_name`，把 `i32+4/+8` 转成 float 写入对象位置字段 `+104/+108` |
| 157 | `set_resource_object_frame` | 读取 `object_name`，把 `i32+4` 写入对象 frame 字段 `+120` |
| 158 | `clear_resource_object` | 读取 `object_name`，找到对象后清 active/name/filter/position/frame/anm/alpha/keyframe vector 等状态；找不到对象时弹 `Obj Clear` 错误 |
| 159 | `add_resource_object_position_keyframe` | 向 `KeyData +48/+52/+56` 追加 12 字节记录：`i32 key`、`i32 x`、`i32 y`；校验缺 `key=0` 时错误为“位置 keyフレームデータに key = 0 がありません” |
| 160 | `enable_resource_object_keyframes` | 设置 `KeyData+4=1`、`KeyData+32=4`、`KeyData+44=value`；当 `this+44` 非 0 时，`value` 会被替换成 0 |
| 161 | `set_resource_object_anm` | 把 `inst[3]` 写入对象 ANM 字段 `+124`；找不到对象时弹 `Obj ANM` 错误 |
| 164 | `add_resource_object_anm_keyframe` | 向 `KeyData +60/+64/+68` 追加 8 字节记录：`i32 key`、`i32 anm`；校验缺 `key=0` 时错误为“アニメ keyフレームデータに key = 0 がありません” |
| 165 | `add_resource_object_alpha_keyframe` | 向 `KeyData +72/+76/+80` 追加 8 字节记录：`i32 key`、`i32 alpha`；校验缺 `key=0` 时错误为“アルファ keyフレームデータに key = 0 がありません” |
| 166 | `set_resource_object_alpha` | 把 `i32+4` 写入对象 alpha 字段 `+128`；找不到对象时弹 `Obj Alpha` 错误 |

对应 HLS 示例：

```text
INIT_OBJECT "obj" arg="..."
SET_OBJECT_POS "obj" x=100 y=200
SET_OBJECT_FRAME "obj" frame=7
CLEAR_OBJECT "obj"
ADD_OBJECT_POS_KEY "obj" key=0 x=100 y=200
ENABLE_OBJECT_KEYFRAMES "obj" value=500
SET_OBJECT_ANM "obj" anm=1
SET_OBJECT_ALPHA "obj" alpha=255
ADD_OBJECT_ANM_KEY "obj" key=0 anm=1
ADD_OBJECT_ALPHA_KEY "obj" key=0 alpha=255
```

## Handler 返回码

`sub_534830` 取指时会先保存当前 pc 到 `this+35`，再把 `this+37` 预推进到下一条指令。handler 返回 0 时走 `sub_51BB30` 的脚本结束/暂停清理；非 0 返回值统一交给 `sub_524170` 调度。当前已确认的返回码按行为记录如下：

| 返回码 | 已确认行为 | 典型来源 |
| ---: | --- | --- |
| 0 | 结束/暂停当前脚本并清理 display name、movie/audio 等运行状态 | opcode 39 |
| 2 | 正常继续执行下一条指令 | 大多数无等待 handler |
| 3 | 等待/重试：handler 通常会把 pc 改回保存点，下一轮重新执行本条 | opcode 24 的 0 值分支、opcode 136、opcode 142 |
| 6 | ADV SP keyframe 校验后进入对应调度状态 | opcode 163 |
| 7 | 菜单选择提交后的调度状态 | opcode 11 |
| 8 | message window 显示后的调度状态 | opcode 19 |
| 11 | 写入 pending return value 后的调度状态 | opcode 87 |
| 12 | `state_27` 非 0 分支的调度状态 | opcode 24 |
| 14 | 等待 checkpoint、系统事件或需要交回 UI/场景循环处理的状态 | opcode 33、34、63、84、95 |
| 16 | menu begin/resume 进入菜单处理状态 | opcode 9 |

外部场景函数也会直接调用 `sub_524170` 投递 17..32 等状态码；这些不是 `.scr` handler 的直接 opcode 语义，当前只记录为场景调度事件，不强行命名到 TBLSTR opcode 表里。

## 当前边界与未收口项

已收口内容：

- TBLSTR系 `.scr` opcode 表、取指边界、base length、内联字符串消费规则已闭合。
- 工具已能输出可编辑 HLS，并通过 `binary -> hls -> binary` 的全量 CC SCR 样本回环校验；新生成的 HLS 不再输出行尾 `bytes=` / `raw=`，无法识别的改写会直接报错。
- `.scr -> TBLSTR` 主文本引用链已接入：opcode `19` 负责 name/msg，opcode `10` 负责 choice；已确认的 `IF_NE + JUMP` 菜单分支形态会输出真实 `choice-range`。
- TBLSTR 文本 import/merge 已接入，TBLSTR 独立 ini 已接入编码、占位符、GBK 检查和 msg 行宽检查/修复。

真正剩余未收口项：

- `UF01` / `TBLSTR.ARC` 完整结构和旧版差异；当前最新版样本可稳定导出/回写，但不能把旧版字段混进最新版说明。
- 少见 opcode 的实际样本覆盖与 UI 层命名，尤其是当前 CC 样本没有覆盖的 `143/144`、`152/153/154` 以及 `155..172` 的少见用法。
- 其他菜单控制流形态的 `choice-range` 验证；当前只把已证实的分支形态作为真实范围输出。
- 字体大小、ruby/ルビ、行内富文本控制码：当前只看到字体渲染层符号，没有证到 TBLSTR SCR opcode 或文本控制码。
- `partitionInfo.tbl` 样本补齐后再解析。
- `readfg.idx` 的业务含义；它不是 `.tbl` 主表。

补充：本页出现的 `sub_xxxxx` 都是当前样本的逆向证据地址，不是 TBLSTR SCR 通用格式字段。工具的 HLS/scan/disasm 输出只使用 opcode、通用工具名、status 和已解析字段；不同游戏的函数地址变化不影响解析。
