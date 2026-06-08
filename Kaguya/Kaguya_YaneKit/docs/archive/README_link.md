# LINK 封包格式记录

适用目标：Kaguya/Yane 系 LINK3 / LINK4 / LINK5 / LINK6 封包。

当前真实样本主要覆盖 LINK6；LINK3/4/5 的读取布局来自伪代码与结构推断，保留读支持，但回封仍以 LINK6 为主。

## 版本判定

| 版本 | 头部大小 | 头部格式 |
| --- | ---: | --- |
| `LINK3` | 8 | `magic[5] + name[3]` |
| `LINK4` | 10 | `magic[5] + name[3] + flags:u16le` |
| `LINK5` | 10 | `magic[5] + name[3] + flags:u16le` |
| `LINK6` | `8 + nameLen` | `magic[5] + flags:u16le + nameLen:u8 + name[nameLen]` |

头部 `flags` 是 archive 级字段，不是 entry 级压缩/加密标志。回封时需要原样保留。

## LINK6 Entry

```text
u32 chunkSize
u16 entryFlags
u16 year
u8  month
u8  day
u8  hour
u8  minute
u8  second
u16 nameByteLen
utf16le[nameByteLen] name
byte[chunkSize - 15 - nameByteLen] data
```

`entryFlags` 当前确认：

| 位 | 含义 |
| --- | --- |
| `entryFlags & 3 != 0` | payload 是 BMR 压缩候选 |
| `entryFlags & 4 != 0` | payload 按 LINK 规则 XOR 加密 |

## BMR 压缩

BMR payload 头部为 0x14 字节：

```text
0x00: "BMR"
0x03: step
0x04: finalSize:u32le
0x08: key:u32le
0x0C: unpackedSize:u32le
0x10: huffmanSize:u32le
0x14: Huffman bitstream, exactly huffmanSize bytes
```

解压流程：

1. Huffman 解码。
2. Move-To-Front 逆变换。
3. BWT 逆变换。
4. `step != 0` 时执行 RLE 逆变换。

当前打包器已经实现 BMR 编码器：RLE（可选）+ BWT + MTF + Huffman。编码端会先尝试 `step=4` 的 RLE；如果 RLE 后更小，就写入 `step=4`，否则写入 `step=0` 直接进入 BWT 阶段。现有 `BmrDecoder` 可直接解回原文件。

## LINK 加密

加密 key 来自 `params.dat` 的 `GameSystem.RawBlob.LinkXorKeyBase64`。

已确认的加密范围：

| 文件头 | XOR 范围 |
| --- | --- |
| `BM` | 保留 `0x36` 字节 BMP 头，从像素区开始 XOR |
| `AP-2` | 保留 `0x18` 字节头，从图像数据区开始 XOR |
| `AP-3` | 保留 `0x18` 字节头，从图像数据区开始 XOR |
| `AP` | 保留 `0x0C` 字节头，从图像数据区开始 XOR |
| `AN00` | 解析 AN00 帧表，只 XOR 每帧 raw BGRA 像素 payload |
| `AN10` | 解析 AN10 帧表，只 XOR 每帧 raw 像素 payload |
| `AN20` | 解析 AN20 控制表/branch 表后，只 XOR raw 帧像素 payload |
| `AN21` | 按 AN21 特殊布局，只 XOR首帧完整像素 payload |
| `PL00` | 解析 PL00 帧表，只 XOR 每帧 raw 像素 payload |
| `PL10` | 按 PL10 特殊布局，只 XOR 首帧像素 payload |

如果用户要求重新加密，但 payload 不是上述可识别图像头，工具会直接报错，不会静默保留错误 flag。

## 命令状态

统一入口：

```text
archive_unpack <archive.arc> <output-dir> [--params params.dat] [--no-decrypt|--raw]
archive_pack <input-dir> <manifest.json> <output.arc> [--compress|--no-compress] [--encrypt|--no-encrypt] [--params params.dat]
```

旧 LINK 调试入口仍可用：

```text
link list <archive.arc>
link extract <archive.arc> <output-dir> [--params params.dat] [--no-decrypt|--raw]
link verify <archive.arc>
link pack6 <input-dir> <output.arc> [--name archiveName] [--flags 0] [--recursive]
link repack6 <input-dir> <_link_manifest.json> <output.arc> [--compress|--no-compress] [--encrypt|--no-encrypt] [--params params.dat]
```

`--keep-encryption-flags` 作为旧参数兼容保留，等价于 `--encrypt`，现在含义是“重新加密并保留加密 flag”，不再只是保留 flag。

## 解包策略

`archive_unpack` / `link extract` 读取 manifest 后，会打开一次共享只读句柄，并用 `RandomAccess.Read` 按 entry 的 `DataOffset/DataSize` 直接随机读数据。这样 600 个以上 entry 的封包不会再为每个 entry 反复 `File.OpenRead(archivePath)`。

输出文件仍按 entry 级并行写出，当前并行度为 256：

- BMR 压缩 entry：按 entry payload 读入内存，识别 `BMR` 后解压。
- 普通 entry：从共享句柄按 offset 分块复制到输出文件。
- 加密 entry：从共享句柄按 offset 分块读取，按图像头部长度跳过明文头后 XOR 解密。

## 回封策略

从普通解包结果回封时：

1. 原 entry 带压缩标志，且选择 `--compress`：把输入文件重新编码为 BMR，并保留压缩 flag。
2. 原 entry 带压缩标志，且选择 `--no-compress`：写入明文文件，并清除压缩 flag。
3. 原 entry 带加密标志，且选择 `--encrypt`：用 params key 重新 XOR 加密，并保留加密 flag。
4. 原 entry 带加密标志，且选择 `--no-encrypt`：写入明文文件，并清除加密 flag。

交互式 `Archive Pack` 会分别询问是否重新压缩、是否重新加密。

## 已验证范围

- BMR 编码后可由当前 BMR 解码器还原。
- LINK 解包已改为共享句柄 + `RandomAccess.Read`，避免多 entry 场景反复打开同一封包。
- LINK6 manifest 回封时，压缩 flag 可保留，payload 会实际写成 BMR。
- LINK6 manifest 回封时，加密 flag 可保留，BM/AP/AP-2/AP-3/AN00/AN10/AN20/AN21/PL00/PL10 payload 会实际按 params key XOR。
- `archive_pack` 的 LINK 压缩回环已验证：打包后再 `archive_unpack` 可还原原文件。

未完成/待补：

- LINK3/4/5 真实样本回封验证。
- 尚未在现有 LINK 样本中观察到 `entryFlags` 同时标记压缩和加密的条目；如果出现这类样本，需要先确认引擎到底是先解密再解压，还是压缩 payload 本身不参与图像头 XOR。
- `AN20` 自身还可能包含 mode `3` BMR 或 mode `4` LZSS；`AN21/PL10` 也有自身的 RLE/diff 结构。这些是图片格式内部压缩，和 LINK entryFlags 的封包层 BMR 压缩不是同一层。
