# OP 总览

这份文档只记录“可被解释为命令 / opcode / 命令表”的部分。

已确认的只有三类：
- `.scr` bytecode VM opcode
- `message.dat` 的 `Commands` 索引表
- `params.dat` 里的 `DemoData` 命令序列

## 1. `.scr` VM opcode

来源：
- `Start_Cracker.exe`
- `4B8A70.c`
- `4B0DB0.c`
- 各 `4Bxxxx.c` opcode handler

统一指令格式：
```text
u16 opcode
u16 instrLen
byte[instrLen-4] body
```

有效 opcode 范围：
```text
1..28
```

已确认 handler：
| opcode | handler | 说明 |
| ---: | --- | --- |
| 1 | `4B6D10.c` | value / assign |
| 2 | `4B5200.c` | flag set |
| 3 | `4B5130.c` | flag clear |
| 4 | `4B4AF0.c` | end / return |
| 5 | `4B6EF0.c` | wait |
| 6 | `4B7C00.c` | update / layer |
| 7 | `4B7780.c` | text command |
| 8 | `4B59B0.c` | menu command |
| 9 | `4B71E0.c` | sound / SE / voice slot |
| 10 | `4B5E60.c` | BGM |
| 11 | `4B5540.c` | label jump |
| 12 | `4B4ED0.c` | file jump |
| 13 | `4B4910.c` | compare |
| 14 | `4B5760.c` | if true jump |
| 15 | `4B5610.c` | if false jump |
| 16 | `4B4730.c` | file jump variant |
| 17 | `4B6820.c` | return / restore variant |
| 18 | `4B58B0.c` | gosub-like jump |
| 19 | `4B69A0.c` | pop return |
| 20 | `4B5F40.c` | program / system command |
| 21 | `4B7A20.c` | embedded title text |
| 22 | `4B6B10.c` | scene command |
| 23 | `4B7640.c` | date window update |
| 24 | `4B7560.c` | date / place reset |
| 25 | `4B69D0.c` | save command |
| 26 | `4B52D0.c` | follow / file jump |
| 27 | `4B7FC0.c` | voice command |
| 28 | `4B5120.c` | nop |

### 重点结论

- `opcode 7` 是 `.scr` 主文本入口。
- `opcode 7` 的参数索引唯一稳定对应 `message.dat` 的 `Commands`。
- `opcode 12 / 26` 是文件 / 跟随跳转，不应误并入文本命令。
- `opcode 21` 是内嵌标题文本，不是 `message.dat` 索引。

### 命名规则

为了避免把推断写成事实，本文档把命名分三层：

1. `final`：有字符串、符号或调用链直接锚定。
2. `high-confidence`：行为已经单义，但仍是基于 handler 反推。
3. `tentative`：只靠上下文和使用场景暂定，后续可替换。

像 `prog_param00`、`prog_result` 这种，保留原始参数名最稳;  
像 `restart_point`、`follow_point` 这种，已经可以按最终语义写;  
像 `opcode 20` 的部分分支，保留“行为式命名”，不硬装成最终语义。

### 样本覆盖状态

当前 156 个 `[SCR-Ver5.3]` 样本覆盖到的 opcode：
```text
1,4,5,6,7,8,9,10,11,12,13,14,15,20,21,22,24,25,26
```

handler 存在但样本中没有出现的 opcode：
```text
2,3,16,17,18,19,23,27,28
```

这几类不能只靠样本字节反推，必须以 IDA handler 为准。

### 1.1 跨文件调用栈与本文件返回栈

来源：
- `4B4730.c`
- `4B6820.c`
- `4B58B0.c`
- `4B69A0.c`
- `4B8A70.c`
- `4BB350.c`
- `4B9270.c`
- `4B91D0.c`
- `strings.txt`

这组 opcode 有两套栈，不能混在一起：

| opcode | 栈 | 行为 |
| ---: | --- | --- |
| 16 | `CStack` 跨文件调用栈 | 压入 `{scriptId/fileId, returnPc}`，切换到目标脚本 |
| 17 | `CStack` 跨文件调用栈 | 弹出 `{scriptId/fileId, returnPc}`，切回原脚本并恢复 PC |
| 18 | 本文件 return vector | 压入 `nextPc`，跳到当前脚本内 `targetPc` |
| 19 | 本文件 return vector | 弹出 PC 并返回 |

`opcode 16` 使用 `file_jump_cmd` operand，解码规则与 `opcode 12 / 26` 同族：

```text
u8 flags
if flags & 0x02:
    u32 fileIdOrVarRef        ; 通过变量表取目标 file id
elif flags & 0x04:
    u32 targetFileId          ; 立即数目标
else:
    throw
```

自然指令长度为：

```text
u16 opcode = 16
u16 instrLen = 9
u8 flags
u32 fileIdOrVarRef
```

执行时 handler 在跳转前调用 `4B9270`：

```text
CStack.push({
    scriptIdOrFileId = current script descriptor +4,
    returnPc = nextPc
})
load_script(targetFileId)
pc = loaded_script.currentPc
```

`opcode 17` 不读取 operand。它调用 `4B91D0` 弹出 `CStack`，再：

```text
load_script(stack.scriptIdOrFileId)
pc = stack.returnPc
```

`CmdCall` 对应 `.scr` VM 的 `opcode 18`，不是 `message.dat Commands` 里的文本命令。

`4B8A70.c` 的执行顺序是：
```text
currentPc = enginePc
nextPc = currentPc + instrLen
handler(opcode)
enginePc = scriptPc
```

因此进入 handler 时，`this+44` 已经是 call 后面的返回地址。

`opcode 18 / CmdCall` handler 的行为：
```text
push_return(nextPc)
scriptPc = u32 body[0..3]
```

也就是说，call 的立即数是 `body+0` 的 `u32le targetPc`，含义是当前 `.scr` bytecode 内的绝对 PC。返回地址不是文件里另一个立即数字段，而是执行器按 `currentPc + instrLen` 自动得到的。

按 handler 推断，`CmdCall` 的自然编码应与 `opcode 11 / CmdExec` 同型：
```text
u16 opcode = 18
u16 instrLen = 8
u32 targetPc
```

`opcode 19 / CmdRtn` 对应 `CmdRtn`，handler 从返回栈弹出地址并写回 `this+44`：
```text
scriptPc = pop_return()
```

注意：当前 156 个 `[SCR-Ver5.3]` 样本里没有出现 `opcode 18` 和 `opcode 19`，所以这部分是由 IDA 伪代码闭合出来的结构，不是样本字节覆盖出来的结论。

同样，当前样本里也没有出现 `opcode 16` 和 `opcode 17`;  
上面的跨文件栈语义来自 IDA handler。

### 1.2 `opcode 20 / program_cmd`

来源：
- `4B5F40.c`
- `4B0390.c`

operand 格式：

```text
u8 flags
if flags & 0x02:
    u16 varNameIdOrVarIndex   ; 通过变量表取 programId
elif flags & 0x01:
    u16 programId             ; 立即数
else:
    throw
```

自然指令长度为：

```text
u16 opcode = 20
u16 instrLen = 7
u8 flags
u16 programIdOrVarRef
```

当前样本 17 条全部是 `flags=0x01`、`instrLen=7`，只覆盖 programId：

```text
0, 1, 14, 16, 17, 19
```

handler 支持 `0..23`，其中 `6` 和 `22` 共用空成功分支，`default` 抛异常。  
下表的“业务名”只按 handler 行为暂命名; 凡是涉及子系统编号的，还需要结合 DLL/运行时对象继续命名。

| programId | 暂名 | handler 行为 |
| ---: | --- | --- |
| 0 | playGameParamMedia | 非 replay 时读取 `prog_param00`，再经 `ゲームパラメータ` 驱动的资源/图形/影片路径打开播放，返回执行状态 `8` |
| 1 | setSystemWordFlag | 写子系统 7 的 word 字段 `+12 = 1` |
| 2 | setReplayOrSkipFlag | 按 replay 状态或脚本状态写 `engine+36`，并把当前模式记到 `this+36`，返回 `9` |
| 3 | return10 | 直接返回执行状态 `10` |
| 4 | enableSubsystem3And4 | 调子系统 3、4 的虚函数 `+24(1)` |
| 5 | disableSubsystem3And4 | 调子系统 3、4 的虚函数 `+24(0)` |
| 6 | noop | 成功返回 `0` |
| 7 | randomProgResult | `prog_result = rand() % max(prog_result, 1)` |
| 8 | waitSubsystem1Ready | 若子系统 1 字节 `+132` 为假，返回 `11`；否则成功 |
| 9 | setSubsystem2Flag | 写子系统 2 的 dword `+20 = 1` |
| 10 | readSubsystem1Flag111 | `prog_result = subsystem1.byte[111] != 0` |
| 11 | return12 | 直接返回执行状态 `12` |
| 12 | readSystemWordArray | `prog_result = subsystem7.wordArray[prog_param00]`，索引必须 `< 32` |
| 13 | writeSystemWordArray | `subsystem7.wordArray[prog_param00] = prog_result`，索引必须 `< 32` |
| 14 | return13 | 直接返回执行状态 `13` |
| 15 | subsystem4NullCall | 取得子系统 4 后调用 `nullsub_2()`，实际等价 no-op |
| 16 | setSubsystem1Bool | `subsystem1.boolArray[prog_param00] = (prog_param01 != 0)` |
| 17 | readSubsystem1Bool | `prog_result = subsystem1.boolArray[prog_param00]` |
| 18 | readMageGaugeFlag | 读取变量 `gague_disp`；内存字符串已确认对应魔素/ゲージ显示标志 |
| 19 | restartPointDispatch | 若脚本状态 bit `0x02` 置位，调用 `4B9630` 重建 `restart_point` / `follow_point` / `game_end` 流程，返回 `14` |
| 20 | return15 | 直接返回执行状态 `15` |
| 21 | readGaugeDisplayFlag | 读取 `ゲームパラメータ` 族中的另一条全局变量名；当前导出只解析到地址 `byte_573F68`，但语义已可收束到 gauge/display 这一支 |
| 22 | noop | 成功返回 `0` |
| 23 | return16 | 直接返回执行状态 `16` |

`program_cmd` 与变量 `prog_param00..prog_param18` / `prog_result` 配合使用。参数不是写在 opcode 20 自己的 payload 中，而是通过前面的变量赋值 opcode 准备好。

### 1.3 `opcode 27 / voice_cmd`

来源：
- `4B7FC0.c`
- `4B05C0.c`

operand 格式：

```text
u8 count
repeat count:
    i32 messageStringIndex
```

自然指令长度：

```text
u16 opcode = 27
u16 instrLen = 5 + 4 * count
u8 count
i32[count] messageStringIndex
```

执行语义：

```text
if count == 0:
    stop_or_clear_voice()
else:
    strings = [messageStringTable[index] for index in indices]
    play_voice(strings)
```

handler 通过 `IMessage`/message string table 把每个 `messageStringIndex` 转成字符串，再交给声音/voice manager。  
当前样本没有 opcode 27，所以索引具体对应 `message.dat` 哪个字符串区仍按 handler 命名为 `messageStringIndex`，后续可用包含 voice opcode 的变体样本校验。

### 1.4 仍需继续细化的 `.scr` opcode

以下不是结构入口未明，而是业务参数还没完全命名：

| opcode | 状态 | 说明 |
| ---: | --- | --- |
| 2 / 3 | handler 已逆，样本未覆盖 | `flag_cmd`，读 `u16 varIndex` 后写 1/0 |
| 16 | handler 已逆，样本未覆盖 | 跨文件 call，压入 `CStack`，见上节 |
| 17 | handler 已逆，样本未覆盖 | 跨文件 return，从 `CStack` 恢复，见上节 |
| 18 / 19 | handler 已逆，样本未覆盖 | 本文件 `CmdCall / CmdRtn`，见上节 |
| 23 | handler 已逆，样本未覆盖 | `date_win` 更新，不读普通 operand |
| 27 | handler 已逆，样本未覆盖 | voice command，payload 是可变数量 message/string 索引，见上节 |
| 28 | handler 已逆，样本未覆盖 | nop，无 operand |

因此现在的“遗漏”主要不是 VM 框架，而是未覆盖 opcode 的业务字段命名。  
结构层面，`16/17/18/19/20/27` 已经能反汇编、编辑并回写;  
但 `program_cmd` 的业务名和 voice 索引区仍建议在后续 DLL/更多样本中继续校名。

## 2. `message.dat` 的 `Commands`

来源：
- `4537F0.c`
- `4B7780.c`
- `44B8B0.c`

结构概览：
```text
[SCR-MESSAGE]ver4.0
flag / xorKey
Names
Choices
Messages
Commands
raw tail
```

这里的 `Commands` 是 `.scr` `opcode 7` 的索引表，不是独立 bytecode。

## 3. `params.dat` 里的 `DemoData`

来源：
- `417190.c`
- `416420.c`

`params.dat` 不是 VM。
它是结构化配置树，主线可按：
```text
GameSystem -> Pattern -> SceneLabel
```
顺序完整解析。

但其中有一块 `DemoData`，本质上是一段“演示命令序列”：
```text
magic = "[Demo3.0]"
u16 commandCount
repeat commandCount:
    u8 type
    u8 length
    byte[length-2] payload
```

已见命令类型：
| type | 类别 |
| ---: | --- |
| 0 | End |
| 1 | Next |
| 2 | Wait |
| 3 | Sound |
| 4 | Load |
| 5 | Transit |
| 6 | Disp |
| 7 | Update |
| 8 | Move |
| 9 | Pos |

payload 可编辑结构见 [README_params_dat.md](README_params_dat.md) 的 `DemoData` 小节。这里不重复展开，以避免 `params.dat` 文档和 op 总览产生两份不同步的表。

这套东西是 `DemoData` 自己的命令格式，不等于 `.scr` VM opcode，也不等于 `message.dat` 的 `Commands`。

## 4. 已排除的误判项

- `params.dat` 的 `SettingTag` 不是 VM。
- `params.dat` 的 `typed string / typed int / typed point` 只是基础包装类型。
- 图片、link、anm、bmp 文件里暂未确认另一套独立 opcode VM。
- `export-for-ai_RenderDX.dll` 里的 `label / loop / call / callnz / texld / dcl` 等字符串是 Direct3D shader mnemonic / 反汇编相关表，不是本游戏脚本 VM。

## 5. 当前结论

到目前为止，真正“全 op”能落地的只有：
- `.scr` opcode 1..28
- `message.dat` 的 command index table
- `params.dat` 的 `DemoData` 命令类型 0..9

如果后续再发现别的 VM / opcode 系统，就追加到这份总文档里，不再单独混进别的格式说明。
