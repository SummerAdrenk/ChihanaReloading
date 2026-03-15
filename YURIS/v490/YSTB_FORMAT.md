## YSTB 文件结构说明

### 1) 头部（固定 0x20 字节）

小端序: 

1. `magic`（4字节）=`YSTB`
2. `version`（u32）
3. `instruction_count`（u32）
4. `section1_size`（u32）
5. `section2_size`（u32）
6. `section3_size`（u32）
7. `section4_size`（u32）
8. `reserved`（u32）

总大小校验: 

`0x20 + section1_size + section2_size + section3_size + section4_size`

### 2) section1

- 指令索引区（原样保留）
- 工具不会改写该区语义，只在需要时整体沿用

### 3) section2（记录表）

- 每条记录固定 12 字节: 
  - `type`（u16）
  - `flag`（u16）
  - `length`（u32）
  - `offset`（u32，指向 section3 的相对偏移）

常见可编辑文本记录特征（本作中高频）: 

- `type = 0`
- `flag = 0`

### 4) section3（文本与负载区）

- section2 里的 `offset + length` 都落在这里
- 文本提取、文本替换、长度变化处理都发生在该区

### 5) section4

- 附加表区（常见与 `instruction_count` 相关）
- 回封时按原始结构保留

## 提取与回封规则

### 提取

- 主路径: 直接提取文本 chunk
- 回退路径: 从指令记录中提取引号字符串（用于某些脚本）
- 会保留 `record_index/offset/length`，便于精准写回

### 回封

1. 把译文映射到原文本条目
2. 按 `target_encoding` 编码文本字节
3. 写回 section3，并根据长度变化更新 section2 偏移与长度

## GBK 全量回封规则

- 当 `source_encoding != target_encoding`（例如 `cp932 -> gbk`）时: 
  - 工具会对全部可编辑文本条目执行目标编码重编码

## 安全策略

- 若编辑区间之间互相重叠，会报错并停止该文件
- 对“部分重叠引用”的文本块，回封采用固定长度安全写入，避免结构损坏
- `--encoding-errors strict` 下，目标编码无法表示的字符会直接报错
