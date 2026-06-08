# .scr 容器与版本差异

本文记录 Kaguya/YaneSDK `.scr` 文件的容器结构、指令边界、版本差异和与 `message.dat` 的联动关系。opcode 业务语义见 [README_op.md](README_op.md)，文本库结构见 [README_message_dat.md](README_message_dat.md)。

## 版本总览

| SCR 版本 | header | 容器结构 | 指令格式 | 当前状态 |
| --- | --- | --- | --- | --- |
| `[SCR-Ver5]` | ASCII 到 `]` | `bytecode + [SAVE] + [LAYER] + tail` | `u16 opcode + u16 instrLen + body` | 已实测 `func/v2/v3/v4`，parse/write 与 text roundtrip OK |
| `[SCR-Ver5.1]` | ASCII 到 `]` | 同构 | 同构 | 已实测 `workplacev54` 与旧式 message ver3 联动 |
| `[SCR-Ver5.2]` | ASCII 到 `]` | 预期同构 | 预期同构 | 已识别为同构容器，缺少独立大批量样本验证 |
| `[SCR-Ver5.3]` | ASCII 到 `]` | 同构 | 同构 | 已实测当前主样本池和 BARE&BUNNY `[1]` - `[15]` |

当前所有已见版本的核心二进制容器没有结构性分裂。版本差异主要体现在：

- 文件头版本字符串不同。
- 内嵌字符串和 opcode body 的业务组合随游戏变化。
- `message.dat` 版本不同，导致 `opcode 7` 的文本映射路线不同。

## 容器结构

```text
ascii header                  ; "[SCR-Ver5]" / "[SCR-Ver5.1]" / "[SCR-Ver5.2]" / "[SCR-Ver5.3]"
u32 codeSize
byte[codeSize] bytecode
ascii "[SAVE]"
u32 saveCount
u32[saveCount] saveOffsets
ascii "[LAYER]"
u32 layerCount
u32[layerCount] layerOffsets
byte[] tail
```

`saveOffsets` 指向 bytecode 内的存档入口；`layerOffsets` 指向容器尾部 layer tail 流中的记录起点，不是主 bytecode 偏移。反汇编再汇编时，工具会重算 save 标签；layer 表保持 tail-relative offset，以避免把 layer 记录误标成代码标签。

layer tail 已确认是变长记录流：

```text
record:
  u8 itemCount
  repeat itemCount:
    u16 itemLength
    byte[itemLength - 2] itemBody
```

v5.8 全量样本中 `itemLength` 只见 `14` 和 `26`。IDA `sub_4B82B0` 确认 item 尾部不是一个 `u16 tail16`，而是 `i8 filter + u8 filter_param_count + i32[filter_param_count] filter_params`；`itemLength=14` 对应 0 个参数，`itemLength=26` 对应 3 个参数。当前 HLS 把 `[LAYER]` tail 提升为 `pattern_entries` / `patterns` / `pattern_layer`，字段保持保守命名：`resource_ref, params_int_array, layer, position, absolute_position, filter, filter_op, filter_params`。其中 `layer/position` 与 `opcode 6 / update_cmd` 的 result item 读取路径一致；`absolute_position` 由 IDA 确认从 pattern layer item 读出并复制到 `CPatternLayer+125`；渲染矩形计算中该 flag 为 1 时直接使用 pattern layer 自身坐标，为 0 时叠加父/全局偏移；`resource_ref` 在 `sub_4B2930` 中会与 `CPatternLayer+20` 比较并写回，非负值传给 `sub_498870` 展开资源字符串列表，负值走清空图层路径。IDA `sub_498870` 与 v5.8 全量样本确认：`resource_ref` 不是 `Pattern.Items` 下标，而是 `Pattern.IntArrays` 下标，运行时按 `resource_ref -> Pattern.IntArrays[resource_ref] -> Pattern.Items[...] -> resource strings` 展开；v5.8 中 12183 个 `pattern_layer.resource_ref` 全部小于 `Pattern.IntArrays.Count=2553`，其中 345 个大于等于 `Pattern.Items.Count=2351`。`scr decompile` 取得全局 `--params params.dat` / `--game-root` 自动探测的 Params 上下文，或找到同工作区 `analysis/params/params.json` / 显式 `--params-json` 时，会追加 `params_int_array=[...]` 和 `// params_resources: ...`；没有 params 上下文时仅输出原始 `resource_ref`。`filter/filter_params` 对应 `Graphics::CFilterParams` 的读取路径。v5.8 全量统计中 `absolute_position` 全为 `0`，`filter` 取值为 `-1/0/1/8`；源字节 `0xFF` 在 HLS 中按 handler 的 signed 读取路径显示为 `-1`。IDA 已追到 v5.8 `sub_4339A0` 的滤镜分派：`1=SurfaceFilter`，`2=SurfaceFlush`，`3=SurfaceBlur/SurfaceGaussBlur`，`4=SurfaceAddColor/SurfaceSubColor`，`5=SurfaceClear/SurfaceMulAlpha/SurfaceCopyDraw`，`6=SurfaceAddSubSurface`，`7=SurfaceMosaic`，`8=SurfaceMulColor`，`9=SurfaceFilter2`，`11=SurfaceFlip`。`filter=3/4/5` 会按 `filter_params` 再细分具体 DLL 导出；`filter=-1/0` 在当前路径不触发滤镜处理。

## 指令格式

所有已确认版本统一：

```text
u16 opcode
u16 instrLen                  ; 包含 opcode 和 instrLen 自身
byte[instrLen - 4] body
```

有效 opcode 范围当前为 `1..28`。未知或业务名未完全确认的 body 仍按 raw/typed 方式保留，确保回封不丢字节。

当前已确认的关键文本/菜单 opcode：

| opcode | HLS 名称 | body 布局 | 说明 |
| --- | --- | --- | --- |
| `6` | `update` | v5.8 为 `i32 pattern_entry, i32 aux_entry, u8 flags, variant payload`；低版本可出现短 body | `pattern_entry` 是 `pattern_entries` 表索引；`aux_entry` 在 v5.8 全量中全为 `-1`，仅保留中性名。`flags` 会在 HLS 中展开为 `flag_ops`，已确认 `0x01=immediate_value`、`0x04=reference_value`、`0x08=position_overrides`；低版本短体与异常尾部现在优先输出 `payload=[...]` 保真，而不是整条 `raw`。 |
| `7` | `text` | `i32 command, i32 pattern_entry, i32 message_resource, i32 resource_slots[6], i32 reserved, i32 message_sequence` | `command` 在 message ver4.0 中索引 `Commands` 表；`pattern_entry` 传给 `sub_4B2930`，作为 `pattern_entries` 表索引；`message_resource` 传给 `sub_4BA000` 并经 `sub_46CB30` 解析资源字符串/写 message state；`resource_slots` 由文本窗口/效果刷新链消费，有 params 上下文时按 `GameSystem.V5Scalars[3]` / `[1]` 拆成 `primary/secondary`；`reserved` 在 v5.8 全量为 `-1`，`message_sequence` 是脚本内 `title/text/menu` 序号。 |
| `8` | `menu` | `i8 mode, u8 choice_count, i32 choices[choice_count], i32 command, i32 pattern_entry, i32 message_resource, i32 resource_slots[6], i32 reserved, i32 message_sequence` | `choices` 是选项 id；后半段复用 text/menu handler 的状态刷新链。v5.8 全量 52 个 menu 样本已验证该长度规则。 |

## message 联动差异

`.scr` 与文本的映射不是由 `.scr` 版本单独决定，而是由 `message.dat` 版本决定。

| message 版本 | opcode 7 body 的主引用 | 映射路线 |
| --- | --- | --- |
| ver2/ver3 | 首个 `i32` 是 block index | `.scr -> block index -> message block` |
| ver4.0 | 首个 `i32` 是 command index | `.scr -> Commands[commandIndex] -> Messages[params]` |

因此工具在 `msg map/split` 时先识别 `message.dat` 版本，再选择 linker：

- `MessageVer3ScriptLinker`：用于旧式 ver2/ver3。
- `MessageScriptLinker`：用于 ver4.0。

## 可编辑文本 IR

`.scr` 反汇编文本是 UTF-8，内嵌字符串默认按 CP932 解释，可通过 `--read-encoding` / `--write-encoding` 覆盖。

默认 HLS 显示格式采用类似 `sample/scd反汇编` 的可读反汇编风格：

```text
.file kind=params_scr_hls
.source "A000　プロローグ①.scr"
.header "[SCR-Ver5.1]"

.code

    ASSIGN dst=24 flags=24 op=0 src08=0 src0c=7
    SOUND id=34566 extra=[0]
    BGM track=136
loc_00000056:
    TEXT command=0 pattern_entry=0 message_resource=136 resource_slots=[-1,-1,-1,-1,-1,-1,-1,0]
```

解析器同时兼容旧的 `script "..." { header "..."; ... }` 写法和新的 directive 写法；新写法只是默认显示更接近反汇编文本，不改变二进制结构。

工具支持：

- opcode mnemonic 形式输出。
- 标签化跳转目标。
- `.save` / `.layer` 表输出。
- 汇编时重算 instruction length、跳转 offset、save/layer offset。

## CLI

默认编辑入口是 HLS 高级 IR；低级 SCRASM 只作为调试/回退格式保留：

```text
Kaguya_YaneKit scr dump <input.scr>
Kaguya_YaneKit [--params params.dat] scr decompile <input.scr> <output.hls.txt> [--read-encoding cp932] [--params-json params.json]
Kaguya_YaneKit scr hls-asm <input.hls.txt> <output.scr> [--write-encoding cp932]
Kaguya_YaneKit [--params params.dat] scr verify-hls <input.scr> [--read-encoding cp932] [--write-encoding cp932] [--params-json params.json]
Kaguya_YaneKit scr disasm <input.scr> <output.disasm.txt> [--read-encoding cp932]      ; low-level SCRASM
Kaguya_YaneKit scr asm <input.disasm.txt> <output.scr> [--write-encoding cp932]         ; low-level SCRASM
Kaguya_YaneKit scr verify <input.scr>
Kaguya_YaneKit scr verify-text <input.scr> [--read-encoding cp932] [--write-encoding cp932]
```

交互模式的启动分析也默认生成 `analysis/scr_hls/*.hls.txt`；`analysis/scr_disasm` 只在 SCR 菜单中选择 low-level disasm 时生成。

为保护编辑内容，启动分析会跳过已有产物：已有 `analysis/params/params.json` 不再导出 params，已有 `analysis/scr/*.scr` 不再解包 scr.arc，已有 `analysis/scr_hls/*.hls.txt` 不再重新 decompile。

## 当前回归结果

| 范围 | `.scr` 解析 | `.scr` 文本回环 | message split/merge 联动 |
| --- | --- | --- | --- |
| `func/v2` - `func/v5.8_2` | OK | OK | OK |
| BARE&BUNNY `[1]` - `[15]` | OK | OK | OK |

批量回归中，所有样本的 `scr verify-text` 均通过。HLS 回编路径也已接入：`tmp_scr_hls_20260527/hls_all_v58` 全量 156 个 HLS 均可 `hls-asm`，并通过 `HLS -> SCR -> HLS -> SCR` 字节闭环（`ok=156 fail=0`）。当前 `func/params` 目录 766 个 Params系 `.scr` 也已通过 `scr verify-hls`（`ok=766 fail=0`）。对 `message.dat` 的 split/merge 联动，v5.4 之后默认 workflow 会导致 message byte diff；这是 message 导入自动修正行为，不是 `.scr` 容器回封失败。使用 `--no-workflow` 可得到字节级闭环。

## 对自由编辑的影响

| 编辑类型 | 当前结论 |
| --- | --- |
| 修改字符串 | 支持，按指定编码重写 |
| 增删普通指令 | 支持，汇编器会重算长度和偏移 |
| 修改跳转目标 | 支持，标签会回写为 offset |
| 修改 save/layer 入口 | 支持，表会重算 |
| 修改 opcode body 业务字段 | 已知 opcode 可按结构编辑；未知业务字段可 raw 保留 |
| 跨版本混用 `.scr` 和 `message.dat` | 不建议；文本索引和 command/block 关系必须同版本匹配 |

## 待回填点

| 项 | 状态 | 影响 |
| --- | --- | --- |
| `[SCR-Ver5.2]` 独立样本池 | 容器同构已识别，但缺少和 v5/v5.1/v5.3 同规模回归 | 不影响已测版本 |
| `opcode 6 / update_cmd.aux_entry` | 结构已确认；v5.8 全量 6404 条均为 `-1`，消费语义未确认 | 使用中性名，不影响回封 |
| `opcode 6 / update_cmd.flags` | `0x01 immediate_value`、`0x04 reference_value`、`0x08 position_overrides` 已由样本覆盖；`0x02 variable_value`、`0x10 submode` v5.8 未覆盖 | 已按 bitmask 安全解析；未覆盖分支不能写业务名 |
| `text/menu.pattern_entry` | 已确认传入 `sub_4B2930` 并索引 `pattern_entries` | 稳定技术名；不能硬叫 `show_layer` 或窗口样式 |
| `text/menu.message_resource` | 已确认传入 `sub_4BA000`，经 `sub_46CB30` 解析资源字符串并写 message state | 稳定技术名；不进一步假定为文本框皮肤 |
| `resource_slots.primary/secondary` | 按 `GameSystem.V5Scalars[3] / [1]` 拆组；primary/secondary 消费路径已确认 | 每个槽位的更细业务名仍未确认 |
| `reserved/message_sequence` | 原 slots[6] 在 v5.8 全量 text/menu 中均为 `-1`；原 slots[7] 与脚本内 `title/text/menu` 序号一致 | `reserved` 的跨版本非 `-1` 语义仍待样本覆盖；`message_sequence` 不等同于 message block index |
| `pattern_layer.resource_ref` | 已确认是 `Pattern.IntArrays` 下标，并展开到 `Pattern.Items[...]` 资源字符串 | 仍是资源引用，不是单个图片 id |
| `pattern_layer.absolute_position` | 已确认复制到 `CPatternLayer+125`；`sub_499F50` 等渲染矩形路径确认：1=自身坐标，0=叠加父/全局偏移；v5.8 全量为 `0` | 已完成；非 0 分支为代码路径确认、样本未覆盖 |
| `pattern_layer.filter` | `filter=1/2/3/4/5/6/7/8/9/11` 已确认到 v5.8 `sub_4339A0` 的 DLL 导出分派；`filter=3/4/5` 还会按参数细分 | v5.8 当前 SCR 全量样本只实际出现 `-1/0/1/8`，其他分支是代码路径确认、样本未覆盖 |
| `opcode 20 / program` 子 opcode | IDA `sub_4B5F40` 已枚举 `0..23` 的 handler 行为；HLS 名称按实际副作用命名，扫描器会报告未知子码；v5.8 只见 `0/1/14/16/17/19` | 已完成到 handler 行为层；未见子码是样本覆盖限制 |
| `opcode 5 wait` | IDA `sub_4B6EF0` confirms flags are routed by priority to runtime wait modes: `0x40 -> engine_mode_1`, `0x80 -> sound`, `0x20 -> surface_complete`, `0x10 -> surface_progress`, otherwise `countdown`; `0x02` makes `value` a VM value ref resolved through `sub_4AF500`. `sub_4AD410` confirms `countdown` decrements per tick, `sound` waits/stops through `SoundIsPlay/SoundStop`, `surface_complete/surface_progress` query a surface entry, and `surface_progress` uses `aux` as `progress_threshold`. | v5.8 samples only cover `flags=0` and `flags=0x80`; `engine_mode_1/surface_complete/surface_progress` are handler-confirmed but sample-uncovered, so HLS keeps conservative technical names. |
| `opcode 12/16/26 flags` | IDA 已确认三者共用 `file_jump_cmd` target 解码：`0x02` 表示 `target` 是 VM value ref，经 `sub_4AF500` 取目标 file id；`0x04` 表示 `target` 是立即 file id。`opcode 12 / file_jump` 额外确认：`0x10` 表示带入口 PC 操作数，`0x20` 表示入口 PC 立即数，`0x40` 表示入口 PC 来自 VM value ref，`0x80` 会调用 `sub_4BB420` 清 call stack。`opcode 16 / file_call` 会先把当前 file/next PC 压入 `CStack`；`opcode 26 / follow_jump` 会写 `follow_return/follow_point/game_end` 后加载目标 file。 | v5.8 样本覆盖 `flags=0x04` 与 `file_jump flags=0x52`；`0x80` 和 `0x20` 是 handler 路径确认但样本未触发。 |
| 控制流结构化 | 跳转/调用目标已标签化并可重定位 | 暂不生成 `if/else`、`while`、`switch`，直到能严格证明 |
| message 联动到 HLS 结构字段 | 当前 `message.dat` linker 不依赖 HLS 字段名 | 若要让 msg 输出引用新 `menu/text` 结构字段，需要另行适配 |
