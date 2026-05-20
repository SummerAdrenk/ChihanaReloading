# ANM 家族

当前已确认版本：

- `AN00`
- `AN10`
- `AN20`
- `AN21`

来源：

- `Start.exe` IDAPro 分析
- `Graphics.dll` 中的 `SurfaceLoadANM`
- 早期 `BARE_BUNNY_Picture_Tool/ArcANM.cs` 与当前 `Kaguya_YaneKit` ANM codec 实现

## IDA 确认点

`Start.exe` 不是只拿 `SurfaceLoadANM` 函数指针这么简单。加载流程里：

- `sub_441840` 识别文件头 `AN00/AN10/AN20/AN21`
- `AN00/AN10` 进入 `sub_43F620` 等 raw frame 分支
- `AN20` 进入 `sub_43FAF0 -> sub_441790 -> sub_440DB0`
- `AN21` 进入 `sub_43FAF0`，先识别 `[PIC]xx`，再进入 `sub_441790 -> sub_441270`

`Graphics.dll!SurfaceLoadANM -> sub_1000E2B0` 只负责把已经解码好的像素缓冲拷贝到 surface，并按通道数做像素格式转换；它不是 ANM 文件结构 parser。

## 当前工具范围

提取：

- 支持 `AN00 / AN10 / AN20 / AN21` 的 PNG 序列导出。
- `AN20` 按 `Start.exe!sub_440DB0` 的结构读取全局 `mode`、每帧 `payloadSize`，并支持 mode `3`/`4` 解码。
- `AN21` 支持首帧完整像素、后续帧 diff + RLE 的解码。

回封：

- 新提取出来的 metadata 会记录原始结构前缀、frame table/control table、每帧 offset/size/channel 等信息。
- `AN00 / AN10 / AN21` 在帧数和尺寸不变时，会保留原始控制结构，只替换像素 payload。
- `AN20` 在帧数和尺寸不变时，会保留原始控制表/branch 表/画布与帧 offset/channel，并统一回封为引擎支持的 mode `4` LZSS payload。
- 老 metadata 没有这些结构字段时，仍走旧的简化回封路径。

仍有限制：

- 不支持通过 PNG 目录增删 ANM 帧；帧数变化会报错。
- 不支持改变单帧尺寸；尺寸变化会报错。
- `AN20` 回封不保持原始压缩算法：即使原文件是 mode `3` BMR/Huffman/MTF/BWT/RLE，当前也会转写为 mode `4` LZSS。游戏读取链路支持该 mode，但若要做字节级还原，需要保留原 payload 而不是编辑 PNG。

## AN00 / AN10

头部：

| 偏移 | 类型 | 含义 |
| --- | --- | --- |
| `0x00` | `char[4]` | magic |
| `0x04` | `i32le` | canvas offsetX |
| `0x08` | `i32le` | canvas offsetY |
| `0x0C` | `u32le` | canvas width |
| `0x10` | `u32le` | canvas height |
| `0x14` | `i16le` | frameCount |
| `0x16` | `2 bytes` | flags/保留区 |
| `0x18` | `frameCount * 4` | frame table |
| 后续 | `i16le` | imageCount |
| 后续 | records | 图像帧记录 |

`AN00` 图像记录：

| 字段 | 类型 |
| --- | --- |
| `frameOffsetX` | `i32le` |
| `frameOffsetY` | `i32le` |
| `width` | `u32le` |
| `height` | `u32le` |
| `pixels` | `width * height * 4` |

`AN10` 图像记录比 `AN00` 多一个 `channels:i32le`：

| 字段 | 类型 |
| --- | --- |
| `frameOffsetX` | `i32le` |
| `frameOffsetY` | `i32le` |
| `width` | `u32le` |
| `height` | `u32le` |
| `channels` | `i32le` |
| `pixels` | `width * height * channels` |

当前状态：新 metadata 下回封会保留原始 `frame table` 和每帧 offset/channel。

## AN20

核心特征：

- 前面有可变长控制表。
- 需要先跳过控制表与 branch 表，才能进入图像段。
- 图像段结构为 `imageCount:u16 + canvas(4*i32/u32) + mode:u16 + frames...`。
- 每帧记录为 `offsetX:i32 + offsetY:i32 + width:u32 + height:u32 + channels:i32 + blockSize:i32 + block`。
- `mode == 3`：payload 是 `BMR`，读取 Huffman bitstream，经过 MTF、BWT 逆变换；若 `BMR[3] != 0`，再按该 step 做 RLE 展开。
- `mode == 4`：block 前 4 字节是 `unpackedSize:u32`，后面才是 LZSS bitstream；控制位 MSB-first，literal 为 `1 + 8bit`，copy 为 `0 + 12bit offset + 4bit length`，offset `0` 表示结束。

当前状态：

- 提取支持 mode `3` 与 mode `4`。
- 回封统一写 mode `4`，并会把 metadata 前缀中的 mode 改为 `4`；每帧 block 会按 `unpackedSize + LZSS bitstream` 生成。
- metadata 会记录 `GlobalCompressionMode` 和每帧原始 `PayloadSize`/`blockSize`，用于追踪原文件状态。

## AN21

核心特征：

- 前面有可变长控制表。
- 控制表后有 `[PIC]xx` 子版本标记。
- 支持差分帧。
- 第 1 帧是完整像素，后续帧是相对前一帧的 diff。
- diff 数据按 `rleStep` 分组压缩。

当前状态：

- 提取时记录原始前缀、全局通道数、帧尺寸与每帧 `rleStep`。
- 回封时保留原始前缀，重新生成首帧 payload 与后续 diff + RLE payload。
- 要求帧数与尺寸保持不变。
