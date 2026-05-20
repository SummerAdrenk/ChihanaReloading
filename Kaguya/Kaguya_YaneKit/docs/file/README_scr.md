# scr / message.dat 概览

这份文档只保留 `.scr` 容器、指令边界、`message.dat` 主结构，以及它们之间的映射关系。

更细的 opcode 级分析已拆到 [README_op.md](README_op.md)。

## 已确认范围

- `.scr` 容器：`[SCR-Ver5]` / `[SCR-Ver5.1]` / `[SCR-Ver5.2]` / `[SCR-Ver5.3]`
- `message.dat`：`[SCR-MESSAGE]ver4.0`，详细结构与工具逻辑见 [README_message_dat.md](README_message_dat.md)
- 映射关系：`.scr -> opcode 7 -> message.dat Commands`
- 工具链：
  - `Kaguya_YaneKit msg export/import/verify/verify-text/dump` 可导出、重建、校验和检查 `message.dat`
  - `Kaguya_YaneKit msg map/split/merge` 可按 `.scr` 做映射、分割和合并
  - `Kaguya_YaneKit scr disasm/asm/verify/verify-text/dump` 可解析、反汇编、汇编和回归校验 `.scr`

## 容器结构

```text
ascii header = "[SCR-Ver5.3]"   ; 12 bytes，样本均为 5.3
u32 codeSize
byte[codeSize] bytecode
ascii "[SAVE]"
u32 saveCount
u32[saveCount] saveOffsets
ascii "[LAYER]"
u32 layerCount
u32[layerCount] layerOffsets
tail bytes
```

## 指令统一格式

每条指令都是：

```text
u16 opcode
u16 instrLen
byte[instrLen-4] body
```

有效 opcode 范围已确认是 `1..28`。

## message.dat 结构

```text
[SCR-MESSAGE]ver4.0
flag / xorKey
Names
Choices
Messages
Commands
raw tail
```

其中 `Commands` 是 `.scr` 主文本入口 `opcode 7` 唯一稳定引用的索引表。

## 当前工具状态

已完成：

- `.scr` 容器结构确认
- `message.dat` 主结构确认
- `.scr -> message.dat` 的主映射确认
- `Kaguya_YaneKit msg map/split/merge` 的 `.scr` 联动映射、分割与合并流程
- `Kaguya_YaneKit` 的 `.scr` 解析 / 回封 / 反汇编 / 汇编 / 校验流程
- `disasm` 可读可写文本 IR：支持 opcode `1..28` 的 mnemonic 形式、标签、跳转目标重算、`.save` / `.layer` 表重算
- 字符串编码可指定：`.disasm.txt` 文件为 UTF-8；`.scr` 内嵌字符串默认 CP932，可通过 `--read-encoding` / `--write-encoding` 覆盖
- 当前 156 个 `[SCR-Ver5.3]` 样本已通过：
  - `scr verify`：二进制 parse/write roundtrip
  - `scr verify-text`：`.scr -> .disasm.txt -> .scr` roundtrip

当前结论：

- 非 GUI 的 `.scr` 结构、编辑、删减、增添指令、重编译验证流程已经闭合。
- OP 级结构细节见 [README_op.md](README_op.md)。那份文档里的剩余项主要是少数未在 156 个样本中出现的 opcode 的业务命名精度，不是 `.scr` VM 框架或回封能力缺口。
- `message.dat` raw tail 的业务含义仍属于 `message.dat` 自身的尾部语义问题，不影响 `.scr` 与 `message.dat Commands` 的主映射和 `.scr` 回编。
