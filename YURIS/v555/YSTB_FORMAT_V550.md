## YSTB v550 文件结构说明

## 1) 头部（固定 0x20 字节）

小端序，字段顺序：

1. `magic`（4 字节）=`YSTB`
2. `version`（u32）
3. `instruction_count`（u32）
4. `section1_size`（u32）
5. `section2_size`（u32）
6. `section3_size`（u32）
7. `section4_size`（u32）
8. `reserved`（u32）

总长校验：

`0x20 + section1_size + section2_size + section3_size + section4_size`

v550 样本中常见 `version = 555`。

## 2) section1

- 指令索引/辅助区，回封时按原结构保留。
- 工具不对该区做语义级改写。

## 3) section2（记录表）

每条记录固定 12 字节：

- `type`（u16）
- `flag`（u16）
- `length`（u32）
- `offset`（u32，指向 section3 的相对偏移）

常见可编辑文本记录（主路径）特征：

- `type = 0`
- `flag = 0`

## 4) section3（文本与负载区）

- 记录表中的 `offset + length` 引用都落在 section3。
- 文本提取、译文替换、长度变化后的重排都在 section3 发生。

绝对文件偏移换算：

`sec3_base = 0x20 + section1_size + section2_size`

`abs_off = sec3_base + record.offset`

## 5) section4

- 附加表区，通常与 `instruction_count` 关联。
- 回封时保留原始结构。

## 6) v550 运行时 XOR 说明

逆向 `sub_450A1D` 相关路径可见：运行时会对 section1~4 做 4 字节循环 XOR。

- key 来源：`byte_834054..byte_834057`
- key 由初始化阶段哈希结果拆字节写入

## 7) 提取与回封规则

### 提取

- 主路径：直接提取可解码文本 chunk（常见 `type=0, flag=0`）。
- 回退路径：在记录负载中提取引号包裹文本（quoted spans）。
- 导出保留 `record_index/offset/length/type/flag`，用于精准写回。

### 回封

1. 以 `record_index` 或 `id` 匹配编辑条目。
2. 按目标编码生成新字节。
3. 重建 section3，并同步修正 section2 的 `offset/length`。