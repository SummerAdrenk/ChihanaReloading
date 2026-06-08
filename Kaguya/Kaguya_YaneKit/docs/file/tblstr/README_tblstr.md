# TBLSTR 文本资源容器

## TBLSTR 版本预留

已知 TBLSTR 大致存在三类版本。当前样本是目前见到的最新版本：

| 版本槽位 | magic / 特征 | 当前状态 |
| --- | --- | --- |
| 旧版 A | 待补样本 | 预留 |
| 旧版 B | 待补样本 | 预留 |
| 最新版 | `UF01` | 当前已开始支持导出 |

后续新增旧版时必须单独记录 magic、头部字段和记录表差异，不能把旧版字段混进 `UF01` 最新版说明。

## `UF01` 最新版结构

当前样本确认：

```text
char[4] magic = "UF01"
u32 record_table_offset
u32 header_reserved_08
u32 header_field_0C
byte[] record_data
u32[] record_offsets
```

`record_offsets` 位于 `record_table_offset`，每个表项是相对 `file + 0x08` 的记录偏移：

```text
absolute_record_offset = 0x08 + record_offsets[i]
```

记录边界由当前 offset 和下一个 offset 决定；最后一条记录结束于 `record_table_offset`。

当前记录格式：

```text
byte[] encoded_text
u32 meta0
u32 meta1
```

`encoded_text` 每字节 xor `0xFF` 后按 CP932 解码。`meta0/meta1` 当前只作为保留元数据输出，不硬命名。

当前工具输出：

```text
msg/tblstr.txt
```

对应 CLI：

```text
tblstr export <tblstr.arc> <output-dir> [--json] [--scr scr-dir] [--ini tblstr_config.ini]
tblstr import <tblstr.arc> <tblstr.txt> <output.arc> [--ini tblstr_config.ini]
tblstr split <tblstr.arc> <scr-dir> <output-dir> [--json] [--ini tblstr_config.ini]
tblstr merge <base-tblstr.txt> <split-dir> <output-tblstr.txt>
tblstr verify-text <tblstr.arc> [--ini tblstr_config.ini]
```

`import` 会保留 `UF01` 的头字段、记录顺序和每条记录的 `meta0/meta1`。未修改的文本记录会复用原始编码字节，因此即使原始文本里存在不完整的 CP932 字节序列，也能无损回封；只有译文发生变化的记录才重新按 CP932 编码。

TBLSTR 使用独立配置文件 `ini/tblstr_config.ini`，不和 message.dat 的 `message_config.ini` 混用。当前会消费通用文本字段：

```text
ReadingEncoding
WritingEncoding
GbkCheck
MsgLengthCheck
MsgLengthFix
MsgLengthSet
占位符 key=hexBytes
占位符显示宽度 key_len=n
```

TBLSTR 不消费 message.dat 专用字段：`AdjustBranchMessages`、`AdjustMsgId`、`AdjustMsgDetails`、`EncryptEnabled`、`EncryptKey`，也不使用 A/B/C 分区或 branch 交换逻辑。

`MsgLengthFix` 只处理 `msg` 行。导入文本中如果 `◆T...◆name◆...` 后紧跟 `◆T...◆msg◆...`，且 msg 内容是 `「...」`、`『...』` 或 `（...）` 这种对话文本，自动换行时会和 message.dat 一样在每行行尾补全角空格。

## name / msg / choice 分类

`UF01` 记录本身只保存文本、长度和 `meta0/meta1`。当前样本里 `meta0/meta1` 不能单独稳定区分 name、msg、choice，因此工具不按 metadata 猜业务类型。

可靠分类来自 TBLSTR系 `.scr` 引用链：

| 文本类型 | SCR 证据 | 工具输出 |
| --- | --- | --- |
| name | opcode `19` 的 `i32+4` speaker index | `◇T00000000◇name◇...` / `◆T...◆name◆...` |
| msg | opcode `19` 的 `i32+8` message index；`i32+12 != -1` 时也是 alternate message index | `◇T00000000◇msg◇...` / `◆T...◆msg◆...` |
| choice | opcode `10` 的 `i32+4` choice text index | `◇T00000000◇choice◇...` / `◆T...◆choice◆...` |

`tblstr export ... --scr scr-dir` 会先扫描 SCR 生成引用图，再按 TBLSTR 记录原始顺序平铺输出，不强行套用 message.dat 的 name/choice/msg 分区。当前 CC 样本统计：

```text
name=12896
msg=21512
choice=42
unreferenced=0
```

`tblstr split <tblstr.arc> <scr-dir> <output-dir> [--json]` 会按脚本文件拆分 TBLSTR 文本，并写出 `_map.json`。正文中不输出 `message-site` 这类调试注释。

`tblstr merge <base-tblstr.txt> <split-dir> <output-tblstr.txt>` 会从拆分文件收集 `◆T...◆` 译文行，合并回平铺的 `tblstr.txt`。合并 key 是 TBLSTR 记录 ID，不按 name/msg/choice tag 区分；同一个 T 记录在多个脚本里出现时，如果译文不一致会计入 conflict。

菜单选项文本来自 opcode `10`。`choice-range` 只在工具能从 SCR 控制流中确认选项分支文本范围时输出，当前已支持 CC 样本里确认过的形态：

已确认的菜单文本序列：

```text
opcode 9      menu begin
opcode 10...  choice entries
opcode 11     menu commit
```

已确认的分支形态：

```text
MENU_COMMIT result=<slot>
IF_NE <result> != <choice_id_1> -> next_branch
  ; choice 1 text branch
  JUMP join
next_branch:
  ; choice 2 text branch
join:
```

工具会在每个实际分支块内收集 opcode `19` 的 message index，写成：

```text
// choice-range: T00000F7F -> T00000F93
◇T00000F7C◇choice◇念入りに中出し魔力補給
◆T00000F7C◆choice◆念入りに中出し魔力補給
```

没有识别到这种控制流时不输出 `choice-range`。不能用“菜单之后到下一个菜单之前”的文本硬当成选项分支范围。

分割文本示例：

```text
◇T00000001◇choice◇スキップする
◆T00000001◆choice◆スキップする

◇T00000004◇name◇男子学園生Ａ
◆T00000004◆name◆男子学園生Ａ

◇T00000005◇msg◇「やっぱメイド喫茶だろ、メイド喫茶！」\n
◆T00000005◆msg◆「やっぱメイド喫茶だろ、メイド喫茶！」\n
```

这套 codec 位于 `Text.Tblstr` 命名空间，和 `Text.MessageDat` 下的 `MessageDatCodec` 隔离。
