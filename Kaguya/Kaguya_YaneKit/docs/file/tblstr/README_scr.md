# TBLSTR系 SCR 容器与分发链

## `.scr` 容器头

当前样本 `.scr` 前 8 字节：

```text
01 0C 0D 0A 0C 3B 03 05
```

`sub_526E00` 按两个 little-endian dword 校验：

```text
u32 magic0 = 0x0A0D0C01
u32 magic1 = 0x05033B0C
```

当前观察到的外层结构：

```text
u32 magic0
u32 magic1
u32 payload_size
byte[payload_size] script_payload
```

运行时代码会保存：

```text
script_payload = file + 0x0C
payload_size   = *(u32 *)(file + 0x08)
```

这是 TBLSTR系 `.scr` 的二进制容器头；Params系 `[SCR-Ver5.x]` ASCII 容器记录在 `../params/README_scr.md`。

## `.scr` 指令分发链

当前已确认的加载、取指和分发链如下：

| 阶段 | 函数 | 已确认作用 |
| --- | --- | --- |
| 加载脚本 | `sub_526E00` | 打开 `scr/<name>.scr`，校验 magic，保存 payload 指针、payload 大小和入口偏移 |
| 初始化 handler 表 | `sub_52E860` | 向 `this + 39` 的 map 写入 opcode -> handler 函数 |
| 执行循环 / 取指 | `sub_534830` | 从当前 payload + pc 取 opcode 和 length，查表调用 handler |

`sub_526E00` 加载后运行时字段对应关系：

```text
this[36] / *(this + 144) = script_payload = file + 0x0C
this[37] / *(this + 148) = pc / entry_offset
this[38] / *(this + 152) = payload_size
```

`sub_534830` 里的取指逻辑确认了基础指令头布局：

```text
u8 opcode              ; payload[pc + 0]
u8 base_length         ; payload[pc + 1]
byte[base_length - 2] base_operands
```

执行时先记录旧 pc，再把 pc 加上 `base_length`，然后把当前指令首地址传给 handler：

```text
old_pc = pc
inst   = payload + pc
pc    += inst[1]        ; 只推进基础长度
opcode = inst[0]
handler(this, inst)
```

`base_length` 不是所有 opcode 的完整静态长度。handler 可以继续推进 `pc`，当前最明确的是 `sub_523100` 内联字符串读取 helper：它会从当前 `payload + pc` 读 `a3` 字节并执行 `pc += a3`。例如 opcode `137` / `sub_51E4B0` 的基础长度是 4，`inst[2]` 和 `inst[3]` 分别是两个内联字符串长度；第二个长度为 0 时不追加读取。只按第二字节线性扫完整 payload 会在字符串正文中错位。

handler 返回值不是字节长度。`sub_534830` 在返回值为 `2` 时继续执行下一条；返回 `0` 时进入结束/暂停路径；其他值会交给 `sub_524170` 做后续状态处理。具体返回码业务含义还要继续核。

`sub_523100` 已确认是指令内联字符串读取 helper：调用方传入字符串字节数 `a3`，它从 `payload + pc` 读取 `a3` 字节，每字节按 bitwise NOT 解码，遇到原始字节 `0xFF` 结束，并把 `pc += a3`。所以带字符串立即数的 opcode 不能只按固定字段解释。

按 `base_length + sub_523100` 追加消费模拟后，当前样本可以完整线性闭合：

```text
sample .scr files: 50
decoded instructions: 35204
unknown opcode: 0
bad/cross length: 0
```

这只证明“取指边界”已经闭合，不等于所有 opcode 业务名已经完成。

## 当前工具状态

- `scr scan-opcodes` 能扫描 TBLSTR系 `.scr` 并检查取指边界。
- `scr decompile` 会按 magic 自动分流到 TBLSTR 后端。
- `scr verify` 对 TBLSTR系执行 `read -> WriteRaw -> compare`，用于确认解析不会破坏二进制。
- `scr hls-asm` / `scr verify-hls` 已对 TBLSTR系开放 HLS 回编：已覆盖的可读字段会语义写回，新生成的 HLS 不再输出行尾 `bytes=` / `raw=` 保真层。若可读语句被改成当前写回器不能识别的形式，会报错而不是静默沿用旧指令；旧版带 `; bytes=` 的 HLS 仅作为兼容输入读取。
