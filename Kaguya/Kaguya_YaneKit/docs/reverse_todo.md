# 未完成逆向清单

更新时间：2026-06-01

这份清单只记录当前还没有真正收尾的内容。已经能稳定解析、只剩“是否要做更高级控制流美化”的项目不放进阻塞项。

## TBLSTR系 SCR

当前状态：

- 当前 CC 样本扫描结果：50 个 `.scr`，35204 条指令，unknown opcode 0，取指边界 issues 0。
- HLS 输出没有 `raw=` 大块残留；`raw=` 只保留在调试反汇编输出里。
- opcode 表、base length、内联字符串消费规则已经闭合。

未完成项：

| 项目 | 当前已确认 | 未完成原因 |
| --- | --- | --- |
| `opcode 143/144` | 与 `121` 同系资源组加载；143 写 `record+136/+140/+144`，144 额外写 `+152/+156`。 | 当前 CC 样本无实际指令覆盖；`control0/control1/secondary_control` 还不能命名为 duration/transition/frame。 |
| `opcode 152/153/154` | 152/154 是 24-bit RGB message color 槽，153 是同组 mode/flag。 | 当前 CC 样本无实际指令覆盖；`color0/color1/mode` 的 UI 层称呼未证死。 |
| 未覆盖 handler | 155..172 多数已按 handler 记录语义。 | 当前 CC 样本覆盖不足，需要其他 TBLSTR系样本验证实际脚本用法。 |

已收口但保留证据边界：

| 项目 | 收口结果 | 说明 |
| --- | --- | --- |
| `opcode 3..8` | `IF_EQ/IF_NE/IF_GT/IF_LT/IF_GE/IF_LE` | 已确认比较关系和 flags 操作数来源。旧命名里 `opcode 6/7` 的 `<` / `>=` 曾写反，当前工具和文档已修正。 |
| `opcode 1` | `add_value` | 已确认是 opcode 0 的加法对应指令：同样选 local/scenario value 表，写法为 `value[index] += addend`。没有证据显示另有 UI 层专名。 |
| `opcode 39` | `stop_script` | handler 直接返回 0；调度器走当前脚本结束/暂停清理路径。旧 HLS 名 `STOP_OR_PAUSE` 仅保留为兼容输入。 |
| `opcode 44` | `set_current_display_name` | 复用 opcode 19 speaker/name 分支的 `sub_57B250` 刷新路径，不再按普通文本、caption 或 debug text 记录。 |
| `opcode 80` | `play_bgm` | 已确认 `bgm/<name>` 路径、`inst[2]` 格式选择和 `inst[4]` 播放 mode/state；HLS 输出 `PLAY_BGM`，必要时附 `format=` / `mode=`。 |
| `opcode 82` | `play_wave` | 已确认 `wav/<name>.ogg` 路径，以及 group/slot 对 voice、SE channel、loop slot 的分流。旧 HLS 名 `PLAY_SOUND` 仅保留为兼容输入。 |
| `opcode 117` | `set_adv_view_sprite_index` | `*(this+28)+424` 会在进入/恢复 `AdvView` 时传给 `SpriteChange(..., index)` 路径。 |
| `opcode 137` | `set_scene_title` | 已确认写场景标题/副标题状态，HLS 输出 `TITLE "..." subtitle="..."`。 |
| `opcode 142` | `wait_wave_slot` | 检查默认 SE 或 0..2 loop slot 的音频对象 busy 状态；busy 时回拨 pc 并返回 3。 |
| `opcode 146/147` | `set_voice_group_prefix` / `append_voice_group_entry` | 已确认是一组 voice group 前缀与条目追加路径；旧 `PENDING_TEXT_A` / `APPEND_PENDING_TEXT_PAIR` 仅保留为兼容输入。 |
| 返回码 | 已整理到 `docs/file/tblstr/README_op.md` 的 Handler 返回码表 | 只对 `.scr` handler 直接返回码命名；外部场景函数投递的 17..32 状态不混入 opcode 语义。 |

## TBLSTR 文本与 `.tbl`

| 项目 | 当前已确认 | 未完成原因 |
| --- | --- | --- |
| `UF01` / `TBLSTR.ARC` | 当前最新样本可导出文本。 | 完整结构、旧版差异还没全部整理；不能把旧版字段混进最新版说明。 |
| `.scr -> tblstr` 引用 | 主链路已接入：opcode `19` 已接入 name/msg 分类；opcode `10` 已接入 choice 文本分类；已确认的 `IF_NE + JUMP` 菜单分支形态会输出真实 `choice-range`；TBLSTR 文本 import/merge 已接入并通过 UF01 字节级往返；TBLSTR 独立 ini 已接入编码、占位符、GBK 检查和 msg 行宽检查/修复。 | 主链路不再算待逆；剩余只是其他菜单控制流形态的样本验证。 |
| 字体大小 / ruby | 当前只看到 `OutlineFontTexture::SetFontInf`、`OutlineFontTexture::DrawChar`、`CreateFontIndirectA` 等引擎渲染层符号；TBLSTR opcode、HLS 输出和文本样本里未证到字体大小或 ruby/ルビ 控制入口。 | 需要继续追 message parser 到字体渲染/文本管理调用链，确认是否存在行内控制码或外部表驱动。 |
| `partitionInfo.tbl` | 目前只做了样本级观察。 | 样本不足，暂不定格式。 |
| `readfg.idx` | 确认不是 `.tbl` 主表。 | 业务用途未确认。 |
| TBLSTR HLS 少见 opcode 字段命名 | 当前已支持 `binary -> hls -> binary` 全量 50 个 CC SCR 样本通过；常用可读字段可编辑后重新编码，无法识别的改写会报错；新生成 HLS 不再输出 `bytes=` / `raw=`。 | HLS 回编不再算阻塞项；剩余是继续补更少见 opcode 的业务级字段名，并用更多 TBLSTR系样本验证。 |

## Params系 SCR

当前状态：

- 已按 `params/v5.8/export-for-ai` 重新核对分发器：`4B0DB0.c` 初始化 handler 表，`4B8A70.c` 运行分发；主 VM 只允许 opcode `1..28`，没有漏掉 `29+` 隐藏 opcode。
- `scr/Script/Params/ScrOpcodeInfo.cs` 与 `docs/file/params/README_op.md` 均覆盖 opcode `1..28`。
- v5.8 主要结构已经闭合。
- 没有常态“大块 raw”输出。
- `menu raw=` / `UPDATE raw=[...]` 不再作为当前主线输出；Params HLS 现在优先落到结构字段或 `payload=[...]` 保真层。

未完成项：

| 项目 | 当前已确认 | 未完成原因 |
| --- | --- | --- |
| `opcode 6 / update_cmd.aux_entry` | 结构和位置已确认；v5.8 全量样本均为 `-1`。 | 消费语义未确认，不能命名成窗口/资源/状态字段。 |
| `opcode 6 / update_cmd.flags` | `0x01/0x04/0x08` 已确认；`0x08` payload 已解析；低版本短 body 现在以 `UPDATE payload=[...]` 保真。 | `0x02/0x10` 是 handler 路径确认但样本未覆盖；异常组合不强行猜业务名。 |
| `resource_slots.primary/secondary` | 进入不同 handler，选择结果会被后续读取。 | 每个槽位的业务名还没证死。 |
| `reserved/message_sequence` | `message_sequence` 统计稳定；`reserved` 在 v5.8 全量为 `-1`。 | 其他版本是否复用 reserved 还需要样本。 |
| `opcode 5 wait` | flags 路由到 countdown/sound/surface 等 wait mode 已确认。 | v5.8 只覆盖 `0` 和 `0x80`；其他分支缺样本。 |
| `opcode 12/16/26 flags` | file jump/call/follow jump 的 target/file id 结构已确认。 | 部分 flag 是 handler 确认但样本未触发。 |
| `opcode 20 / program` | 子 opcode 表和 handler 行为已整理；未命名 program id 会保留数值并输出 `name=unknown`。 | 部分子 opcode 只有 handler 证据，缺实际脚本覆盖。 |
| message 联动 | `message.dat` split/merge 不依赖 HLS 字段名。 | 若要让 msg 输出直接引用 SCR 的结构化字段，需要单独做 linker 适配。 |

## 暂不作为当前目标

| 项目 | 原因 |
| --- | --- |
| 自动还原 `if/else/while/switch` | 用户已确认控制流结构化不是当前必要目标。 |
| 把所有保守技术名强行改成业务名 | 没有样本或调用链证据时容易误导，暂不做。 |
| TBLSTR HLS 任意语义编辑回编 | 已接入常用可读字段语义写回；剩余工作是继续拔高少见 opcode 的业务字段命名，不再是阻塞项。 |

## 下一步建议顺序

1. 继续 TBLSTR系：优先找包含 `143/144/152/153/154/155..172` 的样本，补实际脚本覆盖。
2. 继续逆 TBLSTR 选项分支范围：主链路已完成，剩余是追其他跳转/状态选项如何进入后续文本段。
3. 追 TBLSTR message parser：确认是否存在字体大小、ruby/ルビ、行内富文本控制码。
4. 补 Params系更多样本覆盖，重点是 `opcode 6 flags`、`opcode 5 wait`、`opcode 12/16/26 flags` 的未触发分支；当前 handler 层已经记录，不再当作 opcode 表缺口。
5. 继续补 TBLSTR HLS 少见 opcode 的业务级字段名，并用更多 TBLSTR系样本验证。
