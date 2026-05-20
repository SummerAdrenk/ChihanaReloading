# message.dat 逻辑

`message.dat` 是 `[SCR-MESSAGE]ver4.0` 文本库文件。  
`.scr` 的 `opcode 7` 持有 `Commands` 表索引，再由 `Commands` 表映射到消息 ID

## 二进制结构

```text
ascii "[SCR-MESSAGE]ver4.0"    ; 19 bytes
u8 encrypted
u8 xorKey

Names:
  i32 count
  repeat count:
    u16 byteLength
    byte[byteLength] encodedName      ; encrypted 时逐字节 xor

Choices:
  i32 count
  repeat count:
    u16 byteLength
    byte[byteLength] encodedChoice    ; encrypted 时逐字节 xor

Messages:
  i32 count
  repeat count:
    i32 blockLength
    byte[blockLength] messageBlock    ; encrypted 时整个 block xor

messageBlock:
  i32 textByteLength
  byte[textByteLength] encodedText
  u8 voiceCount
  repeat voiceCount:
    utf16le nul-terminated voiceName

Commands:
  i32 count
  repeat count:
    i32 id
    u8 paramCount
    i32[paramCount] params

byte[] rawTail
```

`rawTail` 当前不参与文本读取路径，但回封必须原样保留

## 可编辑文本格式

沿用 `MsgTool` 的 `◇/◆` 格式：

```text
◇A00000000◇原人名
◆A00000000◆译人名

◇B00000000◇原选项
◆B00000000◆译选项

◇C00000000◇name◇说话人
◆C00000000◆name◆说话人

◇C00000000◇msg◇branch01◇原文本\n
◆C00000000◆msg◆branch01◆译文本\n
```

导入时只读取 `◆` 行：

- `A` 更新 `Names`
- `B` 更新 `Choices`
- `C...msg` 更新 `Messages`
- `C...name` 是辅助显示，不写回
- `branchXX` 标签表示使用自定义人名的分支，只用于编辑上下文，导入时会剥离

## Kaguya_YaneKit CLI

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

当前实现：

- 结构 parse/write roundtrip
- `message.dat -> message.txt -> message.dat` 文本 IR roundtrip
- XOR 加密读写
- Names / Choices / Messages / Commands 全量保留
- voice name 读写保留
- raw tail 原样保留
- 未修改的 Names / Choices / message text 会优先使用读取时保留的原始明文字节回写，避免缺少占位符配置时由编码 fallback 破坏 byte-for-byte roundtrip
- `MsgTool` 风格文本导出 / 导入
- `config.ini` 中当前激活 profile 的占位符字节码替换
- `config.ini` 中当前激活 profile 的 `ReadingEncoding` / `WritingEncoding` / `EncryptEnabled` / `EncryptKey`
- `.scr` 联动：
  - `map` 读取每个 `.scr` 的 opcode 7 `cmd`，映射到 `message.dat Commands`
  - `split` 按 `.scr` 使用情况拆分文本，额外输出 `_map.json`、`_base_message.txt`、`_names_choices.txt`、`_commands.txt`、`_shared.txt`、`_orphan.txt`
  - `merge` 收集拆分目录里的 `◆` 行，合并回总 `message.txt`

## 复用文本处理

如果同一条 `Messages[Cxxxx]` 被多个 `.scr` 引用，`split` 不会把它重复写入每个脚本文件，而是集中写入：

```text
_shared.txt
```

每条共享文本前会标注引用它的 `.scr` 文件列表。各脚本自己的 txt 只包含独占文本，并在头部提示共享文本在 `_shared.txt`。

`merge` 会扫描拆分目录下所有 txt，包括 `_shared.txt`。因此共享文本只需要翻译一次，再合并回总 `message.txt`。

默认配置文件已迁入：

```text
Kaguya_YaneKit/ini/message_config.ini
```

构建时会复制到输出目录的 `ini/message_config.ini`。未显式传 `--ini` 时，CLI 会优先尝试读取这个默认配置。

如果显式传入 `--ini` 但文件不存在，命令会直接报错，避免因为占位符配置缺失而把 `F040` 这类控制码解成不可读私用区字符。纯结构 roundtrip 测试可使用 `--no-workflow` 关闭自动换行、GBKCheck、分支交换等翻译辅助策略，但仍保留占位符配置。

原 `MsgTool` 说明文档已保留在：

```text
Kaguya_YaneKit/docs/legacy/MsgTool_README.md
```

### 对话换行空格规则

`MsgLengthFix` 自动添加换行时，会额外处理“对话文本”：

- 该 message 由 `Commands` 映射到有效 name，也就是导出文本中上一组会出现 `Cxxxx name`
- 文本去掉原有换行后，以 `（）`、`『』` 或 `「」` 成对包围

命中后，每个由修正器生成的 `\n` 前会确保存在一个全角空格 `　`。为了避免补空格后超出 `MsgLengthSet`，换行计算会为这一个全角空格预留 1 字显示长度。
