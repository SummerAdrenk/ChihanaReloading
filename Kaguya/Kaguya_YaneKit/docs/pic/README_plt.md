# PLT 家族

## 范围

`PL00` / `PL01` / `PL10` / `PL11` / `PL20` / `PL30` 是 Kaguya/Yane 系的图片动画资源，扩展名通常为 `.plt`。当前工具把它接入 `pic sort / convert / repack / restore` 图片编辑链：

- `pic sort`：识别 `PL00` / `PL01` / `PL10` / `PL11` / `PL20` / `PL30`，落到 `plt/orig`，metadata 落到 `plt/metadata`。
- `pic convert`：导出为逐帧 PNG 目录，路径为 `plt/png/<原相对路径>/0000.png`、`0001.png` 等。
- `pic repack`：从 PNG 目录和 metadata 回封到 `plt/new`。
- 帧数、单帧尺寸和通道数不允许在 PNG 编辑阶段改变；改变会报错。

## PL00

结构：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| magic | `char[4]` | `PL00` |
| frameCount | `u16le` | 帧数 |
| canvasOffsetX | `i32le` | 画布 X 偏移 |
| canvasOffsetY | `i32le` | 画布 Y 偏移 |
| canvasWidth | `u32le` | 画布宽 |
| canvasHeight | `u32le` | 画布高 |
| frames | records | 逐帧记录 |

逐帧记录：

| 字段 | 类型 |
| --- | --- |
| offsetX | `i32le` |
| offsetY | `i32le` |
| width | `u32le` |
| height | `u32le` |
| channels | `i32le` |
| pixels | `width * height * channels` |

回封会保留 metadata 中的画布信息与每帧 offset/尺寸/channel，只替换像素 payload。

## PL10

结构：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| magic | `char[4]` | `PL10` |
| frameCount | `u16le` | 帧数 |
| reserved | `16 bytes` | 保留头，metadata 以 Base64 保存 |
| canvasOffsetX | `i32le` | 画布 X 偏移 |
| canvasOffsetY | `i32le` | 画布 Y 偏移 |
| canvasWidth | `u32le` | 画布宽 |
| canvasHeight | `u32le` | 画布高 |
| channels | `i32le` | 像素通道数 |
| firstFrame | raw pixels | 第一帧完整像素 |
| nextFrames | RLE diff records | 后续帧差分压缩 |

后续帧记录：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| rleStep | `u8` | RLE interleave step |
| packedSize | `u32le` | 压缩数据长度 |
| packed | bytes | 与上一帧的差分 RLE |

回封会保留 `reserved`、画布信息、通道数和每帧 RLE step；第一帧写 raw pixels，后续帧重新计算相对上一帧的 diff 并写 RLE。这里不追求 RLE 字节与原文件一致，只保证解码后的像素一致。

## 其他版本

- `PL01`：第一帧为完整 raw pixels，后续帧为相对上一帧的 raw diff；回封会重新计算 diff。
- `PL11`：带 16 字节 reserved header；第一帧为完整 raw pixels，随后有 2 字节 extra header；后续帧为 Huffman-only diff；回封保留 reserved/extra header 并重新生成 Huffman-only diff。
- `PL20`：每帧独立记录，带全局压缩 mode；当前支持 mode `3` BMR 与 mode `4` LZSS，回封保持 metadata 中记录的 mode。
- `PL30`：第一帧为完整 raw pixels，后续帧支持 raw diff 或 block-convert payload；提取时会解 block-convert，回封时写引擎支持的 raw diff 子路径。

## 当前限制

- 当前只支持 `channels` 为 `1`、`3`、`4` 的 PLT。
- 不支持通过 PNG 目录增删帧。
- 不支持改变帧尺寸或通道数。
