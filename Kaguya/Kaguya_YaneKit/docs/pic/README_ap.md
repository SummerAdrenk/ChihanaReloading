# AP 家族

当前已确认的 AP 族包含：

- `AP`
- `AP-0`
- `AP-2`
- `AP-3`

来源：

- `Start.exe` IDAPro 分析
- 早期 `BARE_BUNNY_Picture_Tool` 与当前 `Kaguya_YaneKit` Picture codec 实现

## 共同结论

这组格式是游戏自定义图片格式。当前工具已经能做提取和回封;  
但不承诺字节级复刻原文件。

当前回封策略是：

- 图像尺寸以 PNG 实际尺寸为准写回。
- `AP-2` 的 `0x14` 字段已确认是固定 `headerSize = 0x18`，不再作为 metadata 暴露；`AP-3` 的 `offsetX/offsetY/bpp`、普通 `AP` 的 `bpp` 会从 metadata 保留。
- 普通 `AP` 的像素 payload 宽度不能直接由 `bpp` 推导。v5.8 全量普通 AP 样本头字段 `bpp=24`，但 payload 实际都是 `width * height * 4` 字节。工具现在用 `PixelBytesPerPixel` 记录真实 payload 通道宽度。
- 像素区按当前 codec 重新从 PNG 生成，因此只要图片内容发生变化，输出文件不会和原文件字节级一致。
- 当前工作目录样本：普通 `AP` 实际存在大量头字段 `bpp=24` 文件，但实际 payload 为 4 bytes/pixel；旧 codec 按 `bpp/8` 截断 payload 是错误的。

## AP

魔数：

```text
41 50
```

头部：

| 偏移 | 类型 | 含义 |
| --- | --- | --- |
| `0x00` | `u16le` | magic = `"AP"` |
| `0x02` | `u32le` | width |
| `0x06` | `u32le` | height |
| `0x0A` | `i16le` | bpp 头字段 |
| `0x0C` | `byte[]` | 像素数据 |

已确认点：

- 像素按自下而上顺序存储
- `Start.exe sub_463090` 对普通 `AP` 固定从 `0x0C` 取 payload，并把 `fileSize - 0x0C` 整段交给加载链；没有按 `0x0A` 的 `bpp` 字段计算 payload 长度。
- 当前实现按实际 payload 长度计算 `PixelBytesPerPixel`，支持 1/3/4 bytes per pixel。
- v5.8 工作目录普通 `AP` 样本全量为 `bpp=24` 且 `PixelBytesPerPixel=4`。

支持的 `PixelBytesPerPixel`：

| bytes/pixel | 解释 |
| ---: | --- |
| 1 | 灰度 |
| 3 | Bgr24 |
| 4 | Bgra32 |

## AP-0

魔数：

```text
41 50 2D 30
```

头部：

| 偏移 | 类型 | 含义 |
| --- | --- | --- |
| `0x00` | `u32le` | magic = `"AP-0"` |
| `0x04` | `u32le` | width |
| `0x08` | `u32le` | height |
| `0x0C` | `byte[]` | 灰度像素 |

已确认点：

- 单通道灰度图
- 像素同样按自下而上存储
- 回封时也按垂直翻转写回

## AP-2

魔数：

```text
41 50 2D 32
```

头部：

| 偏移 | 类型 | 含义 |
| --- | --- | --- |
| `0x00` | `u32le` | magic = `"AP-2"` |
| `0x04` | `i32le` | offsetX |
| `0x08` | `i32le` | offsetY |
| `0x0C` | `i32le` | width |
| `0x10` | `i32le` | height |
| `0x14` | `i32le` | headerSize = `0x18` |
| `0x18` | `byte[]` | BGRA32 像素 |

已确认点：

- `0x14` 的 4 字节是固定头长 `0x18`。Start.exe `sub_463090` 对 `AP-2`/`AP-3` 固定从 `0x18` 取 payload；v5.8 workplace 的 AP-2 样本该字段全为 `24`。
- 像素按自下而上顺序
- 当前工具校验 `headerSize == 0x18`，回封时写入固定 `0x18`，不再把它作为 metadata 字段保存。

## AP-3

魔数：

```text
41 50 2D 33
```

头部：

| 偏移 | 类型 | 含义 |
| --- | --- | --- |
| `0x00` | `u32le` | magic = `"AP-3"` |
| `0x04` | `i32le` | offsetX |
| `0x08` | `i32le` | offsetY |
| `0x0C` | `u32le` | width |
| `0x10` | `u32le` | height |
| `0x14` | `i32le` | bpp |
| `0x18` | `byte[]` | 像素数据 |

支持的 `bpp`：

| bpp | 解释 |
| ---: | --- |
| 8 | 灰度 |
| 24 | Bgr24 |
| 32 | Bgra32 |

已确认点：

- 像素按自下而上顺序
- `offsetX / offsetY` 会保留
- 回封会按元数据里的 `bpp` 选择写入方式

## 当前工具范围

- 已支持裸图：`AP / AO / AP-0 / AP-2 / AP-3`
- 已支持容器：`APS3 / APS4`，详见 [README_aps.md](README_aps.md)
- 未发现：其他独立 AP 子族
## AO

`気になる彼女のママは現役魔法少女` 样本里新增了 `AO` 图像。它属于 AP 家族的相近变体：头部在普通 `AP` 的基础上多了 `offsetX / offsetY`，像素区仍按实际 payload 宽度解析。

magic：

```text
41 4F
```

已确认结构：

| 偏移 | 类型 | 含义 |
| --- | --- | --- |
| `0x00` | `u16le` | magic = `"AO"` |
| `0x02` | `u32le` | width |
| `0x06` | `u32le` | height |
| `0x0A` | `i16le` | bpp 头字段 |
| `0x0C` | `i32le` | offsetX |
| `0x10` | `i32le` | offsetY |
| `0x14` | `byte[]` | bottom-up 像素数据 |

说明：

- `AO` 按 AP 家族处理，但保留额外的 `offsetX / offsetY`。
- 像素 bytes/pixel 由 `payloadSize / (width * height)` 推导；`bpp` 字段不能单独决定 payload 宽度。
- 未观察到额外图片层加密。

## APS3 / APS4

`APS3` / `APS4` 是带 sprite 记录表的容器，不是裸 AP 图像。因为封包扩展名仍是 `.ap3`，工具会放到 `ap3` 工作目录，但真实格式记录在 metadata 的 `Format` 字段。详细结构见 [README_aps.md](README_aps.md)。

magic 是长度前缀 ASCII：

```text
04 41 50 53 33  # "APS3"
04 41 50 53 34  # "APS4"
```

逆向证据：

- `sub_562E00` 把该族分派到 `sub_563C90` (`APS3`) 和 `sub_564720` (`APS4`)。
- `sub_565250` 读取内嵌图像块。
- `sub_556F00` / `sub_557020` 是内层图像块 `mode == 1` 的 CLZSS 解码。

工具行为：

- PNG 转换导出内嵌 AP/AO 图集，并在 metadata 保留 sprite 记录表。
- 回封时内嵌图像块写成 raw `mode == 0`；游戏读取器支持该分支。
- 外层 AF01 封包仍可能压缩整个 `.ap3` entry；这是封包层压缩，不是图片层加密。
