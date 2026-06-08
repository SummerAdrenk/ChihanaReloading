# message.dat 结构与版本差异

本文只记录 `message.dat` 的二进制结构、版本差异、可编辑文本 IR，以及 `.scr` 联动拆分/合并策略。opcode 级脚本语义见 [README_op.md](README_op.md)，`.scr` 容器见 [README_scr.md](README_scr.md)。

当前工具已支持：

- 旧式 `[SCR-MESSAGE]ver` + `version=2`
- 旧式 `[SCR-MESSAGE]ver` + `version=3`
- 新式 `[SCR-MESSAGE]ver4.0`

## 版本总览

| message 版本 | 文件头 | 顶层结构 | 文本块结构 | `.scr` 引用方式 | 当前状态 |
| --- | --- | --- | --- | --- | --- |
| ver2 | `"[SCR-MESSAGE]ver" + u8 version=2 + u8 encryptedOrXorKey` | 连续 block，无 Names/Choices/Commands 表 | `cstring formatName; u8 itemCount; itemCount * (cstring voice + cstring msg)` | `opcode 7` 首个 `i32` 直接引用 block index | parse/write、文本导入导出、split/merge/import 已闭合 |
| ver3 | `"[SCR-MESSAGE]ver" + u8 version=3 + u8 encrypted + u8 xorKey` | 连续 block，无 Names/Choices/Commands 表 | `cstring formatName; u8 itemCount; itemCount * (u8 voiceCount + voices + cstring msg)` | `opcode 7` 首个 `i32` 直接引用 block index | parse/write、文本导入导出、split/merge/import 已闭合 |
| ver4.0 | `"[SCR-MESSAGE]ver4.0" + u8 encrypted + u8 xorKey` | `Names -> Choices -> Messages -> Commands -> RawTail` | `i32 textLen + text + u8 voiceCount + utf16 voice names` | `opcode 7` 引用 `Commands` 表，再由 command params 指到 message index | parse/write、文本导入导出、split/merge/import 已闭合 |

样本覆盖以当前回归测试为准：

| 样本范围 | 已验证项目 |
| --- | --- |
| `func/v2` - `func/v5.8_2` 全部版本目录 | `msg verify-text`、`.scr` 联动 split/merge/import |
| BARE&BUNNY `[1]` - `[15]` | `msg verify-text`、`.scr` 联动 split/merge/import |

注意：对 ver4.0 新样本，默认 `msg import` 会执行自动换行/长度修正 workflow，因此无编辑回封也可能出现 byte diff。若要验证结构和拆分链路的字节级保真，使用 `--no-workflow`。

## 顶层结构

### 旧式 ver2

```text
ascii "[SCR-MESSAGE]ver"       ; 16 bytes
u8 version = 2
u8 encryptedOrXorKey           ; 当前样本 0xFF，同时作为 encrypted 和 xorKey
repeat until EOF:
    u32 blockLength
    byte[blockLength] block    ; encrypted 时整个 block XOR
```

ver2 没有单独的 `xorKey` 字节。block 内 item 固定是一条 voice + 一条 message：

```text
cstring formatName
u8 itemCount
repeat itemCount:
    cstring voice
    cstring message
```

### 旧式 ver3

```text
ascii "[SCR-MESSAGE]ver"       ; 16 bytes
u8 version = 3
u8 encrypted
u8 xorKey
repeat until EOF:
    u32 blockLength
    byte[blockLength] block    ; encrypted 时整个 block XOR
```

ver3 block 内 item 支持多 voice：

```text
cstring formatName
u8 itemCount
repeat itemCount:
    u8 voiceCount
    repeat voiceCount:
        cstring voice
    cstring message
```

### ver4.0

```text
ascii "[SCR-MESSAGE]ver4.0"
u8 encrypted
u8 xorKey

i32 nameCount
repeat nameCount:
    u16 byteLength
    byte[byteLength] encodedName

i32 choiceCount
repeat choiceCount:
    u16 byteLength
    byte[byteLength] encodedChoice

i32 messageCount
repeat messageCount:
    i32 blockLength
    byte[blockLength] messageBlock

i32 commandCount
repeat commandCount:
    i32 id
    u8 paramCount
    i32[paramCount] params

byte[] rawTail
```

`messageBlock`：

```text
i32 textByteLength
byte[textByteLength] encodedText
u8 voiceCount
repeat voiceCount:
    utf16le nul-terminated voiceName
```

`RawTail` 的业务含义仍未最终命名，但读写会原样保留，不影响文本和 `.scr` 联动编辑。

## 业务分类

### ver2/ver3 block

旧式 message 没有独立人名表和命令表，`formatName` 是 block 自身的业务字段。工具按平面规则导出：

| 条件 | 导出类型 | 含义 |
| --- | --- | --- |
| `itemCount == 0` | `msg` | 选项、标签、章节名等只有 `formatName` 的文本 |
| `itemCount > 0 && formatName == ""` | 不导出 name | 旁白 |
| `itemCount > 0 && formatName != ""` | `name` | 说话人、动态名占位符，如 `[FirstName]` |
| item 带 voice | `voiceXX` | 语音文件名，可编辑回封 |

示例：

```text
◇V3B0007◇name◇[FirstName]
◆V3B0007◆name◆[FirstName]

◇V3B0007I0000◇msg◇「すみません。」\n
◆V3B0007I0000◆msg◆「すみません。」\n
```

没有正文的选项/标签 block 不会再导出 `format` 行：

```text
◇V3B002C◇msg◇選択肢テキスト
◆V3B002C◆msg◆選択肢テキスト
```

旧导出中的 `format` 行仍可被导入器兼容，但新导出不再使用它。

### ver4.0 tables

ver4.0 通过 `Commands` 建立人名、选项和正文之间的关系：

| 表 | 用途 |
| --- | --- |
| `Names` | 角色名/显示名列表 |
| `Choices` | 选项文本列表 |
| `Messages` | 正文文本和 voice 列表 |
| `Commands` | `.scr opcode 7` 引用入口；`id` 通常指向 `Names`，`params` 指向 `Messages` |

导出文本里的 `Cxxxx name` 是上下文提示，不直接写回 `Commands.id`；真正可编辑正文是 `Cxxxx msg`。

## 可编辑文本 IR

所有版本都使用 `◇/◆` 双行格式：

```text
◇A00000000◇原人名
◆A00000000◆译人名

◇B00000000◇原选项
◆B00000000◆译选项

◇C00000000◇name◇原说话人
◆C00000000◆name◆译说话人

◇C00000000◇msg◇branch01◇原正文\n
◆C00000000◆msg◆branch01◆译正文\n
```

导入规则：

| 行类型 | ver4.0 | ver2/ver3 |
| --- | --- | --- |
| `A` | 写回 `Names` | 不使用 |
| `B` | 写回 `Choices` | 不使用 |
| `C...name` | 仅上下文提示，不写回 | 写回 block `formatName` |
| `C...msg` | 写回 `Messages[index].Text` | 写回 item message 或无 item block 的 `formatName` |
| `voiceXX` | ver4.0 voice name 暂不作为主翻译对象 | 写回对应 item voice |

`\n` 在文本 IR 中表示 message 内部换行。可以加长、缩短和增减 `\n`，导入时会重算长度字段。若要避免自动换行器改变原文，传 `--no-workflow`。

## `.scr` 联动拆分

### ver4.0 路径

```text
.scr instruction opcode 7
  -> body[0..4] commandIndex
  -> message.dat Commands[commandIndex]
  -> command.Params[] message indices
  -> Messages[index]
```

这里的 split/linker 直接读取二进制 `.scr` 指令体，不解析 `.hls.txt` 文本。因此 SCR-HLS 中 `arg08/arg0c/slots` 改名为 `pattern_entry/message_resource/resource_slots/reserved/message_sequence` 不影响现有 message split/merge/import。若后续要在 choice 注释中额外展示这些 HLS 级字段，需要单独扩展 linker 的输出摘要。

拆分输出：

| 文件 | 内容 |
| --- | --- |
| `_base_message.txt` | 完整 message 文本 |
| `_map.json` | `.scr -> command -> message` 映射 |
| `_names.txt` | Names |
| `_commands.txt` | Commands 只读摘要 |
| `_shared.txt` | 被多个 `.scr` 引用的正文 |
| `_orphan.txt` | 未被 `.scr` 引用的正文 |
| `<script>.txt` | 对应 `.scr` 独占正文；opcode 8 menu 引用的选项会按引用位置写成 `B... choice` 组 |

### ver2/ver3 路径

```text
.scr instruction opcode 7
  -> body[0..4] blockIndex
  -> message block
```

拆分输出：

| 文件 | 内容 |
| --- | --- |
| `_base_message_ver3.txt` | 完整旧式 message 文本 |
| `_map.json` | `.scr -> opcode 7 site -> block index` 映射 |
| `_shared.txt` | 多脚本共享 block |
| `_orphan.txt` | 未被 `.scr` 引用的 block |
| `<script>.txt` | 对应 `.scr` 独占 block；opcode 7 引用到的 `itemCount == 0` 选项/标签 block 会按引用位置写入这里 |

## CLI

```text
Kaguya_YaneKit msg export <message.dat> <message.txt> [--read-encoding cp932] [--ini config.ini] [--no-workflow]
Kaguya_YaneKit msg import <message.dat> <message.txt> <output.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini] [--encrypt true|false] [--xor-key FF] [--no-workflow]
Kaguya_YaneKit msg verify <message.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini]
Kaguya_YaneKit msg verify-text <message.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini]
Kaguya_YaneKit msg dump <message.dat> [--read-encoding cp932] [--ini config.ini]
Kaguya_YaneKit msg map <message.dat> <scr-dir> <output.json> [--read-encoding cp932] [--ini config.ini] [--no-workflow]
Kaguya_YaneKit msg split <message.dat> <scr-dir> <output-dir> [--read-encoding cp932] [--ini config.ini] [--no-workflow]
Kaguya_YaneKit msg merge <base-message.txt> <split-dir> <output-message.txt>
```

默认配置文件：

```text
Kaguya_YaneKit/ini/message_config.ini
```

配置用于占位符字节码、读写编码、加密参数和导入 workflow。显式传入不存在的 `--ini` 会报错。

## 回归结论

当前大批量回归结果：

| 项 | 结果 |
| --- | --- |
| `msg verify-text` | 34/34 OK |
| `.scr` 联动 split/merge/import 默认流程 | v2 - v5.3 字节级 OK；v5.4+ 因 workflow 自动修正产生 byte diff |
| `.scr` 联动 split/merge/import `--no-workflow` | v5.4 - v5.8_2 与 `[1]` - `[15]` 全部字节级 OK |

因此当前结构层和自由编辑链路已经闭合。需要区分两个目标：

- 保真验证/无编辑回封：使用 `--no-workflow`。
- 汉化导入并希望自动换行/长度修正：使用默认 workflow，但不要期待 byte-for-byte 不变。

## 待回填点

| 项 | 状态 | 影响 |
| --- | --- | --- |
| ver4.0 `RawTail` 业务含义 | 原样保留，未最终命名 | 不影响文本编辑、`.scr` 联动和回封 |
| ver4.0 voice name 主动编辑 | 结构可读写，当前不是主翻译入口 | 不影响正文、选项和人名翻译 |
| 部分 command `id/params` 的业务名 | 引用结构已清楚，业务命名仍可继续细化 | 不影响按 `.scr` 拆分与合并 |
