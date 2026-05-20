# LINK 封包格式逆向记录

适用目标：Kaguya 引擎 LINK3 / LINK4 / LINK5 / LINK6 封包。

当前测试样本的所有 `.arc` 文件都属于 `LINK6`。  
`LINK3/4/5` 的头部布局来自伪代码，读取器按该布局保留了支持分支;  
但暂时没有真实样本参与回归，因此并不可靠。  
目前基于 `Hakoniwaハコニワ` 的各个封包进行测试逆向。

## 版本判定

由 `471DF0.c`、`4775E0.c`、`46F6A0.c` 确认：

| 版本 | 头部大小 | 头部格式 |
| --- | ---: | --- |
| `LINK3` | 8 | `magic[5] + name[3]` |
| `LINK4` | 10 | `magic[5] + name[3] + flags:u16le` |
| `LINK5` | 10 | `magic[5] + name[3] + flags:u16le` |
| `LINK6` | `8 + nameLen` | `magic[5] + flags:u16le + nameLen:u8 + name[nameLen]` |

`471DF0.c` 会把版本识别为 3、4、5、6；不是这四种时版本号为 `-1`。

`arc` 样本头部观察：

| 文件 | 头部前缀 |
| --- | --- |
| `bgd.arc` | `LINK6 + flags=0 + nameLen=3 + "bgd"` |
| `cg00.arc` | `LINK6 + flags=1 + nameLen=4 + "cg00"` |
| `voice00.arc` | `LINK6 + flags=0 + nameLen=7 + "voice00"` |

这里的 `flags` 是归档头级别的 `archiveFlags`，不是文件条目的
`entryFlags`。  
当前 LINK6 样本中已确认至少有 `0/1` 两种取值;  
工具在
`repack6` 时必须原样保留该字段。它不参与条目压缩/加密判断，条目级
压缩/加密由每个 chunk 内的 `entryFlags` 决定。

## LINK6 chunk

LINK6 chunk 结构为：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `chunkSize` | `u32le` | 从本字段开始到文件数据结束的总 chunk 大小 |
| `entryFlags` | `u16le` | 文件条目标志；`bit0-1 != 0` 表示压缩候选，`bit2` 表示加密 |
| `year` | `u16le` | 文件时间戳年份 |
| `month` | `u8` | 文件时间戳月份 |
| `day` | `u8` | 文件时间戳日期 |
| `hour` | `u8` | 文件时间戳小时 |
| `minute` | `u8` | 文件时间戳分钟 |
| `second` | `u8` | 文件时间戳秒 |
| `nameByteLen` | `u16le` | UTF-16LE 文件名字节长度 |
| `name` | `byte[nameByteLen]` | UTF-16LE 文件名 |
| `data` | `byte[]` | 文件内容 |

归档末尾写入 `u32le 0` 作为结束标记。

当前 `pack6` 默认只枚举目标目录第一层文件;  
显式传入 `--recursive` 时会递归子目录，并把相对路径以反斜杠写入 entry 名。  
`_link_manifest.json` 会被自动排除，不会打回归档。  

`4773A0.c` 的 LINK6 entry 读取顺序与样本校验一致：

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

旧工具把时间戳字段写成 0 也能被 loader 接受，但真实包里这些字段基本是有效日期，例如 `2025-06-16 18:19:54`。

## Kaguya_YaneKit 实现状态

非 GUI 命令：

```text
link list <archive.arc>
link extract <archive.arc> <output-dir> [--params params.dat] [--no-decrypt|--raw]
link verify <archive.arc>
link pack6 <input-dir> <output.arc> [--name archiveName] [--flags 0] [--recursive]
link repack6 <input-dir> <_link_manifest.json> <output.arc>
```

`extract` 会按 entry 逐项解包，不一次性把整个大包读进内存，并在输出目录写 `_link_manifest.json`，记录 header、chunk offset、entry flags、时间戳、data offset/size 等信息。  
普通条目以流式复制/解密为主；带 BMR 魔数的压缩条目需要按单条 payload 读入后再解压。

启动上下文会优先从游戏根目录解析 `params.dat`;  
如果当前进程目录或 `--game-root` 指向游戏目录，LINK 解包会自动取得 `params.dat` 的 GameSystem raw blob 作为加密 key。也可以对单次命令显式传入 `--params <params.dat>`。

`entryFlags & 4 != 0` 表示该 entry 加密。当前 `LinkArchiveCodec` 沿用旧 `ArcLINK.cs` 的 `LinkEncryption` 规则处理：

| 文件头 | 解密方式 |
| --- | --- |
| `BM` | 保留 `0x36` 字节 BMP header，从像素区开始循环 XOR params key |
| `AP-2` | 保留 `0x18` 字节 header，从图像数据区开始循环 XOR params key |
| `AP-3` | 保留 `0x18` 字节 header，从图像数据区开始循环 XOR params key |
| `AP` | 保留 `0x0C` 字节 header，从图像数据区开始循环 XOR params key |

`--no-decrypt` / `--raw` 会原样导出 payload，供字节级回封验证使用。默认解包会尽量输出可直接查看/编辑的明文图片；若包内存在加密 entry 但找不到 `params.dat`，命令会报错而不是静默吐出不可用图片。

当前压缩处理只对明文 BMR payload 做解压；`entryFlags` 同时标记压缩和加密的组合尚未在样本里回归验证，遇到这类 entry 应优先使用 `--raw` 保留原始数据再单独分析。

`pack6` 当前按目录文件重建普通 LINK6，文件名写 UTF-16LE，时间戳取源文件 LastWriteTime，archiveFlags 可由 `--flags` 指定，entryFlags 默认写 0。默认非递归；需要保留子目录 entry 时使用 `--recursive`。

`repack6` 使用 `_link_manifest.json` 回封，按原 entry 顺序写入，保留 archive flags、entry flags 和时间戳。若要 byte-for-byte 重建原 LINK6 包，应使用 `extract --raw` 得到原始 payload；默认解密输出面向编辑，不等价于原始封包数据。

## 当前工具范围

- 可靠：`LINK6` 普通归档封包。
- 可靠：`LINK6` 加密 BMP/AP/AP-2/AP-3 图片解包，key 来自 `params.dat` GameSystem raw blob。
- 已按伪代码实现读取但未用真实样本验证：`LINK3` / `LINK4` / `LINK5`。

## 未完成点

1. 还没有 `LINK3/4/5` 的真实样本参与验证；目前只确认它们的头部布局来自伪代码。
2. 旧 `ArcLINK.cs` 里还存在 `AN00/AN10/AN20/AN21/PL00/PL10` 的加密 entry 局部解密分支；这些需要结合 ANM/PL frame 结构继续接入当前工具层。
