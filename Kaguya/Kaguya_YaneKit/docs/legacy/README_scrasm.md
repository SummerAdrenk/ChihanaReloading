# SCRASM v2

`scrasm` 是 `.scr` 文件的低级可编辑文本中间表示 (IR)。

当前 Params系默认脚本工作流是 HLS：`scr decompile` / `scr hls-asm`，见 [../file/params/README_hls.md](../file/params/README_hls.md)。SCRASM 保留为 opcode/body 级调试、未知结构排查和最宽松 roundtrip 的回退格式。

本文已移入 `docs/legacy/`。它不是当前推荐脚本编辑入口，只作为低级调试和历史兼容说明保留。

## 设计目标:

- 所有已知 `.scr` opcode `1..28` 都有助记符形式
- 标签可自由编辑, 在汇编时计算实际地址
- 指令长度根据操作数自动重新计算
- 未知或非标准的 payload 保留为十进制字节列表, 可编辑
- `.disasm.txt` 文本文件使用 UTF-8 编码
- 内嵌的 `.scr` 字符串默认使用 CP932 编码, 可通过参数覆盖

## 命令

```text
Kaguya_YaneKit scr disasm <input.scr> <output.disasm.txt> [--read-encoding cp932]
Kaguya_YaneKit scr asm <input.disasm.txt> <output.scr> [--write-encoding cp932]
Kaguya_YaneKit scr verify <input.scr>
Kaguya_YaneKit scr verify-text <input.scr> [--read-encoding cp932] [--write-encoding cp932]
Kaguya_YaneKit scr dump <input.scr>
```

`verify` 仅验证二进制的解析/写入往返一致性  
`verify-text` 验证 二进制 -> 反汇编文本 -> 二进制 的往返一致性

## 指令格式

示例:

```text
@loc_00000000:
bgm track=81
wait flags=0 value=30 extra=[]
if_true flags=1 value=12 @loc_00000120
program flags=1 id=19 name=restart_point_dispatch
title "Scene Title"
voice 120 121
```

`name=` 字段仅作为可读性注释, 汇编时不使用该值.
汇编器以数值操作数为准.

## 字节列表

对于结构已知但尚未完全命名的 payload, 使用十进制字节列表表示:

```text
update_layer layer=0 ref=4294967295 extra=[1,0,128,0,0]
text cmd=0 arg08=0 arg0c=81 payload=[255,255,255] tail=0
```

这样既避免了纯十六进制输出, 又保持了逐字节的可编辑性.

## 标签

当 PC 目标地址恰好落在指令边界时, 会以标签形式输出  
编辑后, 汇编器会重新计算目标偏移量

支持 PC 目标地址的 opcode:

```text
jump
if_true
if_false
call
save
```

`.save` 和 `.layer` 表也支持标签引用.
