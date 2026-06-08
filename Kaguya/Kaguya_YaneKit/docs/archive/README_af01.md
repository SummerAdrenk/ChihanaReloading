# AF01 封包格式记录

当前样本来自 `気になる彼女のママは現役魔法少女` 的 `ARC/*.ARC`。GARbro 等工具只作为参考，最终以本地伪代码、原始字节和工具回归测试为准。

## 加载链

该引擎固定加载 `ARC` 目录下的资源包：

- `SCR.ARC`
- `BG_.ARC`
- `CG_.ARC`
- `CGW.ARC`
- `SP_.ARC`
- `PARTS.ARC`
- `BGM.ARC`
- `WAV.ARC`
- `VO1.ARC`
- `VO2.ARC`

额外资源：

- `TBLSTR.ARC` 是 UF01/TBLSTR 文本资源包，不是 AF01。
- `scr/label.tbl`、`scr/value.tbl`、`scr/partitionInfo.tbl` 属于脚本辅助表。

## 头部

```text
0x00 char[4]  magic = "AF01"
0x04 u32le    version
0x08 u32le    indexBaseOffset
```

真实 index offset：

```text
indexOffset = indexBaseOffset + 8
```

数据区从 `0x0C` 开始，index 位于所有 entry payload 之后。

## 数据条目

数据条目保存在 index 前：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `nameLen` | `i32le` | 加密文件名的字节长度 |
| `name` | `byte[nameLen]` | 每字节 XOR `0xFF`，按 CP932 解码 |
| `flags` | `u16le` | bit0 表示 AF01-LZ 压缩 |
| `storedSize` | `u32le` | 压缩时为 packed size，否则为 raw size |
| `unpackedSize` | `u32le` | 仅压缩条目存在 |
| `payload` | `byte[]` | raw payload 或 AF01-LZ payload |

## Index 条目

Index 重复记录 entry 元数据：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `nameLen` | `i32le` | 加密文件名的字节长度 |
| `name` | `byte[nameLen]` | 每字节 XOR `0xFF`，按 CP932 解码 |
| `flags` | `u16le` | bit0 表示压缩 |
| `packedSize` | `u32le` | 压缩大小；raw 条目为 `0` |
| `unpackedSize` | `u32le` | 解包后的原始大小 |

读取器会校验计算出的数据区边界必须等于 `indexOffset`。

## AF01-LZ

压缩标志：

```text
(flags & 0x0001) != 0
```

payload 是 MSB-first LZ bitstream：

- 4 KiB 滑动窗口。
- 初始窗口位置为 `1`。
- 控制 bit `1`：后接 literal byte。
- 控制 bit `0`：后接 copy token。
- copy token 为 `12-bit offset + 4-bit count`。
- 解码长度为 `count + 2`。

当前压缩器输出合法 AF01-LZ 流，目标是内容回环一致，不追求与原包压缩字节完全一致。

## 工具实现

统一命令：

```text
archive_unpack <archive.arc> <output-dir>
archive_pack <input-dir> <manifest.json> <output.arc> [--compress|--no-compress]
```

解包：

- 显示 magic、version、index offset。
- entry 级并行解包，默认并行度为 256。

打包：

- 使用 `_archive_manifest.json` 保留 entry 顺序、文件名、flags、index 信息。
- `--compress`：manifest 中原本 `IsPacked = true` 的条目重新 AF01-LZ 压缩，并保留 packed bit。
- `--no-compress`：写 raw payload，并清除 packed bit。
- 交互式会询问是否压缩；重定向/批处理默认压缩原 packed 条目。

## 打包性能

AF01 pack 现在采用 entry 级并行预压缩：

1. 先扫描 manifest，准备所有 entry 的文件名、flags、大小信息。
2. 对需要压缩的 entry 使用 `Parallel.ForEach` 并行执行 `LzPack`，当前并行度为 256。
3. 所有压缩任务完成后，再按 manifest 原顺序串行写出数据区和 index。

这样可以加速 `CGW.ARC` / `PARTS.ARC` 这类 packed entry 较多的包，同时不破坏 entry 顺序和 index offset。

## 回归验证

已确认：

```text
SCR.ARC:
  解包条目: 54
  pack --compress -> unpack -> SHA256 compare: OK
```

历史确认：

```text
CGW.ARC:
  解包条目: 73
  pack -> unpack -> SHA256 compare: OK

PARTS.ARC:
  图片 sort/convert/repack-png 路径:
    AP3 182/182 success
    AP  88/88 success
```

## 当前覆盖

| 封包 | 格式 | 内容 | 压缩条目 |
| --- | --- | --- | ---: |
| `SCR.ARC` | AF01 | `.scr`, `.tbl` | 0 |
| `CGW.ARC` | AF01 | `.prs` | 73 |
| `PARTS.ARC` | AF01 | `.ap3`, `.prs` | 已观察到 packed `.prs` |
| `BGM.ARC` | AF01 | `.ogg` | 0 |
| `VO1.ARC` | AF01 | `.ogg` | 0 |
| `VO2.ARC` | AF01 | `.ogg` | 0 |
| `WAV.ARC` | AF01 | `.ogg` | 0 |

非 AF01：

- `TBLSTR.ARC` 以 `UF01` 开头，是 TBLSTR 文本资源包，应交给 Text/TBLSTR 功能处理。
