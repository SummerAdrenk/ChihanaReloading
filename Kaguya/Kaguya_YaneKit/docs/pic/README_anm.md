# ANM 家族

当前已确认版本：

- `AN00`
- `AN01`
- `AN20`
- `AN21`

来源：

- `Start.exe` IDAPro 分析
- `Graphics.dll` 中的 `SurfaceLoadANM`
- 早期 `BARE_BUNNY_Picture_Tool/ArcANM.cs` 与当前 `Kaguya_YaneKit` ANM codec 实现

## IDA 确认点

`Start.exe` 不是只拿 `SurfaceLoadANM` 函数指针这么简单。加载流程里：

- `sub_441840` 识别文件头 `AN00/AN01/AN20/AN21` 等外层版本。
- `AN00/AN01` 进入 `sub_43F620`，读取旧式控制表后读取 raw image records。
- `AN20` 进入 `sub_43FAF0`。因为外层版本为 `2.0`，不会读取 `[PIC]xx` 子版本，随后调用 `sub_441790(..., 0.0)`，即 `sub_440560` raw frame 分支。
- `AN21` 进入 `sub_43FAF0`，会要求控制表后存在 `[PIC]xx` 子版本，再由 `sub_441790` 分派到具体像素 handler。
- `[PIC]00/10/20/30` 等子版本才决定 `sub_440560 / sub_4408C0 / sub_440DB0 / sub_441270` 这类像素 payload handler。

`Graphics.dll!SurfaceLoadANM -> sub_1000E2B0` 只负责把已经解码好的像素缓冲拷贝到 surface，并按通道数做像素格式转换；它不是 ANM 文件结构 parser。

## 控制表与播放信息

ANM 文件不只有 PNG 像素帧。`Start.exe` 先读取控制表，再读取图像段：

- `sub_43F620` 旧式路径会读取画布信息、控制项数量、控制项，再读取 image records。
- `sub_43FAF0` 新式路径会读取控制项数量、控制 opcode `0..5`，然后读取 `CAnimationBranch` 表。每个 branch item 是两个 `i32le`。
- 图像 handler，例如 `sub_440560 / sub_440DB0 / sub_441270`，只负责 surface 创建、像素解码和 `SurfaceLoadANM`。

因此，若 ANM 文件自身携带播放顺序、分支或速度/帧间隔信息，它在前置控制表/branch 表里，而不是在 PNG 像素 payload 里。当前工具会把已确认结构展开到 `AnimationControl`：

- `Format=NewControlCommands`：`sub_43FAF0` 路径，含 `Commands` 与 `Branches`。
- `Format=LegacyControlPairs`：`sub_43F620` 路径，含 `LegacyPairs`。
- `Tail` 是控制表之后、图像 payload 之前的剩余头部结构，例如 image count、canvas、`[PIC]xx`、mode、channels 等字段。

`AnimationControl` 可编辑，回封时用它重建前置结构。工具不再输出兼容用的 prefix/tail base64 黑盒字段；如果前置结构不能完全解析，转换会直接失败。注意：opcode 与 branch pair 的业务名仍未全部证死，所以这里保留技术字段名，不把某个值硬命名为播放速度。

## 当前工具范围

提取：

- 支持 `AN00 / AN01 / AN20 / AN21` 的 PNG 序列导出。
- `AN20` 支持外层 `2.0` 的 raw frame 分支；这类文件没有全局 `mode` 字段。
- 带 `[PIC]20` 的高版本容器按 `Start.exe!sub_440DB0` 的结构读取全局 `mode`、每帧 `payloadSize`，并支持 mode `3`/`4` 解码。
- `AN21` 支持首帧完整像素、后续帧 diff + RLE 的解码。

回封：

- 新提取出来的 metadata 会记录原始结构前缀、展开后的 `AnimationControl`、每帧 offset/size/channel 等信息。
- `AN00 / AN01 / AN21` 在帧数和尺寸不变时，会保留原始控制结构，只替换像素 payload。
- raw `AN20` 在帧数和尺寸不变时，会保留原始控制表/branch 表/画布与帧 offset/channel，并回封 raw payload。
- compressed `[PIC]20` 路径在帧数和尺寸不变时，会保留原始控制表/branch 表/画布、全局压缩 mode 与帧 offset/channel；原文件为 mode `3` 时回封 BMR payload，原文件为 mode `4` 时回封 LZSS payload。
- 老 metadata 没有这些结构字段时，仍走旧的简化回封路径。

仍有限制：

- 不支持通过 PNG 目录增删 ANM 帧；帧数变化会报错。
- 不支持改变单帧尺寸；尺寸变化会报错。
- compressed `[PIC]20` 回封保持原始压缩 mode，但不追求原 payload 字节一致；编辑 PNG 后会重新编码为同 mode 的合法 payload。

## AN00 / AN01

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

`AN01` 图像记录比 `AN00` 多一个 `channels:i32le`：

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
- 外层 `AN20` 的图像段结构为 `imageCount:u16 + canvas(4*i32/u32) + frames...`，没有 `mode:u16`。
- `imageCount == 0` 是合法的 no-image AN20：控制表/branch 表之后只保留 `u16 0`，不再跟随 canvas 或 frame record。
- raw 每帧记录为 `offsetX:i32 + offsetY:i32 + width:u32 + height:u32 + channels:i32 + pixels`。
- 带 `[PIC]20` 的 compressed 图像段结构为 `imageCount:u16 + canvas(4*i32/u32) + mode:u16 + frames...`。
- compressed 每帧记录为 `offsetX:i32 + offsetY:i32 + width:u32 + height:u32 + channels:i32 + blockSize:i32 + block`。
- `mode == 3`：payload 是 `BMR`，读取 Huffman bitstream，经过 MTF、BWT 逆变换；若 `BMR[3] != 0`，再按该 step 做 RLE 展开。
- `mode == 4`：block 前 4 字节是 `unpackedSize:u32`，后面才是 LZSS bitstream；控制位 MSB-first，literal 为 `1 + 8bit`，copy 为 `0 + 12bit offset + 4bit length`，offset `0` 表示结束。

当前状态：

- 提取支持 raw `AN20`，也支持 compressed mode `3` 与 mode `4`。
- raw `AN20` 回封保持 raw payload，不插入 mode。
- compressed 路径回封保持 metadata 中记录的原始 mode；mode `3` 重新生成 AN20 BMR，mode `4` 重新生成 `unpackedSize + LZSS bitstream`。
- metadata 会记录 `GlobalCompressionMode`；raw `AN20` 记为 `0`，compressed 文件记录原始 mode。

## AN21

IDA `Start.exe!sub_431730` 已确认 `[PIC]10` 图像段结构：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `imageCount` | `u16le` | 帧数 |
| `canvasOffsetX` | `i32le` | 画布 X |
| `canvasOffsetY` | `i32le` | 画布 Y |
| `canvasWidth` | `u32le` | 画布宽 |
| `canvasHeight` | `u32le` | 画布高 |
| `frameOffsetX` | `i32le` | 首帧/差分帧 surface X |
| `frameOffsetY` | `i32le` | 首帧/差分帧 surface Y |
| `frameWidth` | `u32le` | 帧宽 |
| `frameHeight` | `u32le` | 帧高 |
| `channels` | `i32le` | 像素通道数 |
| `firstFrame` | `frameWidth * frameHeight * channels` | 第一帧完整像素 |
| `nextFrames` | records | 后续差分帧 |

注意：`[PIC]10` 的 `channels` 后面没有 `u16 compressionMode`；后续帧记录从第一帧 raw payload 后立刻开始。

后续差分帧记录：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `rleStep` | `u8` | RLE 通道步长 |
| `packedSize` | `u32le` | RLE diff payload 长度 |
| `packed` | bytes | 相对上一帧的差分 RLE |

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
