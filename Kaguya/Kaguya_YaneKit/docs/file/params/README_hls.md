# SCR-HLS 高阶脚本说明

当前 `scr decompile <input.scr> <output.hls.txt> [--read-encoding cp932] [--params-json params.json]` 输出的是保守的 instruction-level 高阶 IR，不是猜测式结构化反编译。也可以在主命令前传入全局 `--params params.dat` 或 `--game-root <dir>`，让 SCR HLS 取得 Params 上下文并补充资源名注释。

它会把已经确认的 SCR opcode 提升成较易读的语句，例如 `assign`、`text`、`menu`、`goto`、`if_false goto`、`program`，但仍保留显式标签和跳转。

从当前版本开始，交互模式的启动分析和 SCR 菜单默认使用 HLS：输出目录为 `analysis/scr_hls`，回编输入也是 `analysis/scr_hls/*.hls.txt`。低级 SCRASM 仍保留在 `analysis/scr_disasm`，仅作为显式调试/回退入口。

启动分析不会覆盖已有 HLS：只要 `analysis/scr_hls` 下已经存在 `.hls.txt`，该轮启动会跳过 SCR 高级解析，保护手工修改。需要重生成时，先自行整理目标目录，再从 SCR 菜单显式执行 HLS decompile。

## HLS 回编状态

HLS 现在可以走保守回编路径：

```text
Kaguya_YaneKit scr hls-asm <input.hls.txt> <output.scr> [--write-encoding cp932]
Kaguya_YaneKit [--params params.dat] scr verify-hls <input.scr> [--read-encoding cp932] [--write-encoding cp932] [--params-json params.json]
```

`hls-asm` 只接受本工具生成的保守 HLS 语法，并按原始可编码字段回写 bytecode/container tail。`mode=`、`flag_ops=`、`target_source=`、`filter_op=`、`params_int_array=`、`params_resources` 等派生说明字段不作为二进制依据；没有 Params 上下文时只保留可编码字段，也可以回编。

当前回归：

- `tmp_scr_hls_20260527/hls_all_v58` 全量 156 个 HLS：`hls-asm` 均成功。
- 对上述 156 个结果继续执行 `SCR -> HLS -> SCR`：二次 SCR 与一次 SCR 字节一致，`ok=156 fail=0`。

注意：这证明当前 HLS IR 已经可无损承载本工具生成的结构；它仍不是自动恢复 `if/else/while/switch` 的最终高级脚本语言。

## VM 与指令边界

已确认的 SCR VM 指令流格式如下：

```text
u16le opcode
u16le instrLen       ; 包含 opcode 和 instrLen 自身
byte[instrLen - 4] body
```

所有已见版本 `[SCR-Ver5]`、`[SCR-Ver5.1]`、`[SCR-Ver5.2]`、`[SCR-Ver5.3]` 共享这套指令边界。当前有效 opcode 范围为 `1..28`。

跳转类 opcode 的 PC target 在文件中以 file offset 保存。解析成脚本内标签时必须减去：

```text
codeStart = headerByteLength + 4
```

汇编回写时再把 `labelOffset + codeStart` 写回 body。`[SAVE]` 表也使用同一类 file offset；`[LAYER]` 表使用 bytecode-relative offset。

## Opcode 源表

opcode 集中定义在：

```text
scr/Script/Params/ScrOpcodeInfo.cs
```

该文件是 SCR opcode schema 的 source of truth，包含：

- `opcode`
- mnemonic
- 固定/变长长度规则
- operand schema
- opcode variants
- `program` 子 opcode `0..23`
- PC target operand 位置

可用以下命令从 C# schema 导出 Markdown 表：

```text
Kaguya_YaneKit scr opcodes [output.md]
```

可用以下命令扫描样本中是否出现 schema 未覆盖的情况：

```text
Kaguya_YaneKit scr scan-opcodes <input.scr|directory> [output.txt]
```

当前扫描项包括：

- 未知 opcode。
- opcode body 长度与 `ScrOpcodeInfo.cs` 不匹配。
- `opcode 20 / program` 的未知子 opcode会保留数值并显示为 `name=unknown`。
- 本地 PC target 不能解析到指令边界的跳转。
- opcode 使用次数，以及已见 `opcode 20 / program` 子 opcode 使用次数。

这一步只是自动发现和定位；如果出现未知 opcode，还必须回到对应宿主 exe/IDA handler 验证，修正 `ScrOpcodeInfo.cs` 和文档后再重跑全量扫描。

v5.8 全量 156 个 `.scr` 当前扫描结果已落在 `tmp_scr_hls_20260527/opcode_scan_v58_20260527.txt`：`issues=0`。已见 `program` 子 opcode 只有 `0/1/14/16/17/19`，均在 `ScrOpcodeInfo.cs` 的 `0..23` 集中表内；其余子码的 handler 行为已由 IDA `sub_4B5F40` 枚举，本批 v5.8 样本未触发。低版本样本可能出现未命名 program id，HLS 会按数值保留。

## HLS 保真规则

HLS 的主路径是结构化可读语句；为了覆盖 Params系低版本样本，允许有限 raw fallback：

- 未知 opcode：仍然报错并输出 bytecode offset，不能伪造语义。
- 已知 opcode 的低版本/异常 body 若不能安全结构化，会尽量输出同一 mnemonic 下的保真字段，例如 `payload=[...]`；只有连结构骨架都无法确认时才会退到更低层的字节保真。
- `opcode 20 / program` 的未知子 opcode 不阻塞回编；HLS 保留 `id=`，`name=unknown` 只作为提示字段。

这类 raw fallback 是可编辑的字节列表，也是无损保真入口；不要把它提升成未经验证的业务字段。

HLS parser 允许两种 `resource_slots` 形态：有 params 上下文时的 `{primary=[...], secondary=[...], reserved=..., message_sequence=...}`，以及无 params 上下文时的扁平 `[slot0,...,slot7]`。两者回编为同一组 8 个 i32。

## 已确认的 command object layout

当前 HLS 只提升已经由 v5.8 handler/accessor 伪代码确认的字段：

- `opcode 1 / value_cmd`：`flags:u8, op:u8, dst:u16, src08:i32, src0c:i32`。`op=0..0xB` 对应赋值、加减乘除、取模、位运算、取反、移位。
- `opcode 6 / update_cmd`：`pattern_entry:i32, aux_entry:i32, flags:u8, variant payload`。`pattern_entry` 与 `text/menu` 的 `pattern_entry` 一样，是 `pattern_entries` 表索引；`aux_entry` 在 v5.8 全量 6404 条中全为 `-1`，消费语义未确认，因此只给中性技术名。`flags` 是 payload bitmask，HLS 同时输出 `flag_ops=[...]`：
  - `0x01 immediate_value`：后接 `i32 immediate_value`；v5.8 出现 5553 条。
  - `0x02 variable_value`：后接 `i32 variable_value`；当前 v5.8 未出现，但 handler/长度规则保留。
  - `0x04 reference_value`：后接 `i32 reference_value`；v5.8 出现 308 条。
  - `0x08 position_overrides`：后接 `u8 count + count * {layer:u8, x:i16, y:i16}`；v5.8 出现 46 条，只见 layer `1/2/3`。
  - `0x10 submode`：后接 `u8 submode`；当前 v5.8 未出现。
  v5.8 另有 497 条 `flags=0`，表示该 command 不带额外 payload。
  低版本短 body 或 payload 位组合无法安全展开时，HLS 输出 `UPDATE payload=[...]`，只做保真，不猜业务字段。
- `opcode 7 / text_cmd`：`command:i32, pattern_entry:i32, message_resource:i32, resource_slots[6]:i32, reserved:i32, message_sequence:i32`。`command` 是 message.dat `Commands` 表入口；`pattern_entry` 会传给 `sub_4B2930`，作为 `pattern_entries` 表的索引来选择/刷新 pattern 数据；`message_resource` 会传给 `sub_4BA000`，经 `sub_46CB30` 装载一条资源字符串并写入 message state。`resource_slots` 从 handler 视角是一组连续的 i32 资源索引槽；有 params 上下文时，HLS 会按 `GameSystem.V5Scalars[3]` / `[1]` 拆成 `primary` / `secondary`，v5.8 为 `3/3`。IDA 已确认前组进入 `sub_4BABD0`，后组进入 `sub_4BA850`，并由 `sub_4BBCE0` / `sub_4BBF60` 按当前选择取结果对。剩余两个 dword 中，`reserved` 在 v5.8 全量 28922 条 text/menu 中均为 `-1`；`message_sequence` 是脚本内 message-state 序号，按 `title/text/menu` 出现顺序从 0 递增。
- `opcode 8 / menu_cmd`：`mode:i8, choice_count:u8, choices[choice_count]:i32, command:i32, pattern_entry:i32, message_resource:i32, resource_slots[6]:i32, reserved:i32, message_sequence:i32`。`choices` 会由 `sub_4BBD50` / `sub_4BA7D0` 送入菜单候选列表；`command/pattern_entry/message_resource/resource_slots/reserved/message_sequence` 复用 text/menu handler 的刷新链。该布局已在 v5.8 全量样本中验证，52 个 menu 全部符合此长度规则。
- `patterns`：来自容器 `[LAYER]` 后的 tail 流，不属于主 bytecode。`pattern_entries` 是 tail-relative pattern offset；每条 pattern 为 `u8 pattern_layer_count + pattern_layer_count * pattern_layer`。`pattern_layer` 的原始 item 结构由 IDA `sub_4B82B0` 确认：
  `u16 item_len, u32 resource_ref, u8 layer, i16 x, i16 y, u8 absolute_position, i8 filter, u8 filter_param_count, i32[filter_param_count] filter_params`。
  v5.8 全量样本中 `item_len` 只见 `14` 和 `26`，分别对应 `filter_param_count=0` 和 `3`。当前字段名保持保守：`resource_ref/layer/position/absolute_position/filter/filter_params`，其中 `layer/position` 与 `update_cmd` 的 result item 读取路径一致；`absolute_position` 由 IDA 确认从 pattern layer item 读出并复制到 `CPatternLayer+125`；渲染矩形计算中该 flag 为 1 时直接使用 pattern layer 自身坐标，为 0 时叠加父/全局偏移；`filter/filter_params` 对应 `Graphics::CFilterParams` 的读取路径。IDA `sub_4B82B0` 从 `char*` 读取 `filter` 后写入 dword，因此 HLS 按 signed i8 显示，源字节 `0xFF` 输出为 `filter=-1`。
  `resource_ref` 在 `sub_4B2930` 中会与 `CPatternLayer+20` 比较并写回；非负值传给 `sub_498870` 展开资源字符串列表，负值走清空当前图层路径。IDA `sub_498870` 与 v5.8 全量样本确认：该值不是 `Pattern.Items` 下标，而是 `Pattern.IntArrays` 下标；运行时先取 `Pattern.IntArrays[resource_ref]`，再把数组内每个值当作 `Pattern.Items` 下标展开为资源字符串。v5.8 统计为 `Pattern.Items.Count=2351`、`Pattern.IntArrays.Count=2553`，HLS 中 12183 个 `pattern_layer.resource_ref` 全部落在 `IntArrays` 范围内，其中 345 个大于等于 `Items.Count`。当 `scr decompile` 取得全局 `--params params.dat` / `--game-root` 自动探测的 Params 上下文，或找到同工作区 `analysis/params/params.json` / 显式 `--params-json` 时，HLS 会追加 `params_int_array=[...]` 和下一行 `// params_resources: ...`，例如 `resource_ref=2489 params_int_array=[1364,1365,0,116]`。
  v5.8 全量统计：`absolute_position` 全为 `0`；`filter` 取值为 `-1/0/1/8`；`filter_params` 常见三元组如 `[255,245,245]`、`[240,200,145]`、`[230,230,230]`。IDA 已追到 v5.8 `sub_4339A0`（v5.4 同源为 `sub_43D650`）的滤镜分派，HLS 会按分支和参数追加 `filter_op`。`filter=-1/0` 在当前路径不触发滤镜处理。

| filter | HLS `filter_op` | 已确认依据 |
| --- | --- | --- |
| `1` | `SurfaceFilter` | 3 个参数合成为颜色值后调用 DLL 导出。 |
| `2` | `SurfaceFlush` | 无参数路径调用 DLL 导出。 |
| `3` | `SurfaceBlur` / `SurfaceGaussBlur` | `filter_params[0]` 为 `0/1` 时分别调用对应 DLL 导出。 |
| `4` | `SurfaceAddColor` / `SurfaceSubColor` / `SurfaceAddColor+SurfaceSubColor` | 正参数分量走 AddColor，负参数分量走 SubColor，混合正负会连续触发两者。 |
| `5` | `SurfaceClear` / `SurfaceMulAlpha` / `SurfaceCopyDraw` | `filter_params[0] == 0` 清空，`1..254` 走 MulAlpha，`>=255` 走 CopyDraw。 |
| `6` | `SurfaceAddSubSurface` | 使用辅助 surface 路径调用 DLL 导出。 |
| `7` | `SurfaceMosaic` | 1 参数路径调用 DLL 导出。 |
| `8` | `SurfaceMulColor` | 3 个参数合成为颜色值后调用 DLL 导出。 |
| `9` | `SurfaceFilter2` | 3 个参数合成为颜色值后调用 DLL 导出。 |
| `11` | `SurfaceFlip` | 1 参数路径调用 DLL 导出。 |
- `opcode 21 / title_cmd`：`byte_count:u8 + CP932 text bytes`。

## 未确认/待逆点

以下项目的结构边界、读取路径或样本统计已经确认，但还没有足够证据提升为最终业务名。HLS 必须继续使用中性技术名，禁止把这些字段写成“看起来像”的业务语义。

| 项目 | 已确认 | 未确认/限制 |
| --- | --- | --- |
| `opcode 6 / update_cmd.aux_entry` | body 位置和类型已确认；v5.8 全量 6404 条中全部为 `-1`。 | 消费语义未确认，不能命名为窗口/资源/状态类业务字段。 |
| `opcode 6 / update_cmd.flags` | `0x01 immediate_value`、`0x04 reference_value`、`0x08 position_overrides` 已在 v5.8 样本中出现；`0x08` payload 为 `u8 count + count * {layer:u8, x:i16, y:i16}`。 | `0x02 variable_value` 与 `0x10 submode` 当前 v5.8 未覆盖，仅按 handler/长度规则保留；`immediate_value/reference_value` 的更高层业务含义仍未命名。 |
| `text/menu.pattern_entry` | 传入 `sub_4B2930`，作为 `pattern_entries` 表索引来选择/刷新 pattern 数据。 | 这是稳定技术名；仍不输出 `show_layer` 这类动作名。 |
| `text/menu.message_resource` | 传入 `sub_4BA000`，经 `sub_46CB30` 装载资源字符串并写入 message state。 | 这是稳定技术名；不进一步假定为窗口样式或文本框皮肤。 |
| `resource_slots.primary/secondary` | `GameSystem.V5Scalars[3] / [1]` 确认了 v5.8 拆分为 `3/3`；primary 进入 `sub_4BABD0`，secondary 进入 `sub_4BA850`，选择结果对由 `sub_4BBCE0/sub_4BBF60` 读取。 | primary/secondary 每个槽位的更细业务名仍未确认。 |
| `reserved/message_sequence` | 原 slots[6] 在 v5.8 全量 28922 条 text/menu 中均为 `-1`，因此输出为 `reserved`；原 slots[7] 与脚本内 `title/text/menu` 的出现序号完全一致，统计时把 `title` 纳入计数后 bad=0，因此输出为 `message_sequence`。 | `reserved` 是否在其他版本承载非 `-1` 值仍需样本覆盖；`message_sequence` 是稳定技术名，不进一步假定为台词 block index。 |
| `pattern_layer.resource_ref` | 运行时按 `resource_ref -> Pattern.IntArrays[resource_ref] -> Pattern.Items[...] -> resource strings` 展开，已由 IDA 路径和 v5.8 统计确认。 | 仍是资源引用字段，不等同于单个图片 id。 |
| `pattern_layer.absolute_position` | IDA 确认从 pattern layer item 读出并复制到 `CPatternLayer+125`；`sub_499F50` 等渲染矩形路径确认：1=自身坐标，0=叠加父/全局偏移；v5.8 全量为 `0`。 | 已完成；非 0 分支为代码路径确认、样本未覆盖。 |
| `pattern_layer.filter` | `filter=1/2/3/4/5/6/7/8/9/11` 已追到 v5.8 `sub_4339A0` 的 DLL 导出分派；HLS 会输出 `filter_op`。 | v5.8 当前 SCR 全量样本只实际出现 `-1/0/1/8`，其他分支是代码路径确认、样本未覆盖。 |
| `opcode 20 / program` 子 opcode | IDA `sub_4B5F40` 已枚举 `0..23` 的 handler 行为；HLS 名称按实际副作用命名，扫描器会报告未知子码。v5.8 只见 `0/1/14/16/17/19`。 | 已完成到 handler 行为层；未见子码是样本覆盖限制，不再把它当作 opcode 结构疑点。 |
| `opcode 5 wait` | IDA `sub_4B6EF0` confirms flags are routed by priority to runtime wait modes: `0x40 -> engine_mode_1`, `0x80 -> sound`, `0x20 -> surface_complete`, `0x10 -> surface_progress`, otherwise `countdown`; `0x02` makes `value` a VM value ref resolved through `sub_4AF500`. `sub_4AD410` confirms `countdown` decrements per tick, `sound` waits/stops through `SoundIsPlay/SoundStop`, `surface_complete/surface_progress` query a surface entry, and `surface_progress` uses `aux` as `progress_threshold`. | v5.8 samples only cover `flags=0` and `flags=0x80`; `engine_mode_1/surface_complete/surface_progress` are handler-confirmed but sample-uncovered, so HLS keeps conservative technical names. |
| `opcode 12/16/26 flags` | IDA 已确认三者共用 `file_jump_cmd` target 解码：`0x02` 表示 `target` 是 VM value ref，经 `sub_4AF500` 取目标 file id；`0x04` 表示 `target` 是立即 file id。`opcode 12 / file_jump` 额外确认：`0x10` 表示带入口 PC 操作数，`0x20` 表示入口 PC 立即数，`0x40` 表示入口 PC 来自 VM value ref，`0x80` 会调用 `sub_4BB420` 清 call stack。`opcode 16 / file_call` 会先把当前 file/next PC 压入 `CStack`；`opcode 26 / follow_jump` 会写 `follow_return/follow_point/game_end` 后加载目标 file。 | v5.8 样本覆盖 `flags=0x04` 与 `file_jump flags=0x52`；`0x80` 和 `0x20` 是 handler 路径确认但样本未触发。 |
| 控制流结构化 | 所有跳转/调用目标保持符号标签，offset 可重定位。 | 暂不自动生成 `if/else`、`while`、`switch` 等高级块，除非后续控制流分析能严格证明。 |
| message 联动 | `message.dat` 结构和现有 linker 不依赖 HLS 字段名；HLS 改名不会影响当前 split/merge/import。 | 如果后续要让 msg 输出引用 `menu_cmd` 的 `choices/resource_slots/pattern_entry/message_resource` 等结构化字段，需要单独适配 linker，不能在 SCR decompiler 中猜测文本范围。 |

## 控制流策略

所有本地跳转、条件跳转和本地调用继续使用符号标签，例如：

```text
@loc_00001200:
  if_false flags=1 value=0 goto @loc_00001340;
```

`opcode 25 / save` 的 body 参数不是 PC target；存档入口位置来自容器 `[SAVE]` 表。当前不会自动生成 `if/else`、`while`、`switch` 等结构化块，除非后续控制流分析能从跳转边界严格证明。这样做是为了避免把不可证明的控制流写成看似确定的高级脚本。

## 与 message.dat 的关系

SCR 的 `opcode 7 / text` 只提供文本入口索引；真正的台词文本在 `message.dat` 中。不同 message 版本映射方式不同：

- ver2/ver3：`.scr opcode 7` 的首个 `i32` 直接对应 message block。
- ver4.0：`.scr opcode 7` 的首个 `i32` 先索引 `Commands`，再由 command params 指向 messages。

因此 HLS 初版保持 SCR-only 输出。后续若要输出真正接近 VN 脚本的文本块，需要走现有 msg linker，而不是在 SCR decompiler 中猜测文本对应关系。
