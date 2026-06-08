# params.dat 横向结构记录

本文记录 Kaguya/YaneSDK `params.dat` 的结构、版本差异和工具实现策略。本文按“横向字段族”组织，不再用“某版对某版”的流水账叙述。

当前已确定并由工具支持的版本：

- `[SCR-PARAMS]v02`
- `[SCR-PARAMS]v03`
- `[SCR-PARAMS]v04`
- `[SCR-PARAMS]v05`
- `[SCR-PARAMS]v05.1`
- `[SCR-PARAMS]v05.3` - `[SCR-PARAMS]v05.8`

以后继续分析更早版本时，新证据应直接填入“版本总览”“GameSystem 横向差异”“Pattern 横向差异”等矩阵。

旧版文档已备份为 `README_params_dat.md.bk_20260520_flat`。

当前“已支持”以命令闭环为准：主线样本 `v02`、`v03`、`v04`、`v05`、`v05.1`、`v05.3`、`v05.4`、`v05.5`、`v05.6`、`v05.7`、`v05.8`，以及旁支样本 `v02_2`、`v03_2`、`v05.1_2`、`v05.4_2`、`v05.4_3`、`v05.5_2`、`v05.6_2` 均已通过 `params verify-json`。BARE&BUNNY `[1]` - `[15]` 也已纳入批量回归，`params` 项全部 OK。

## 快速结论

| 主题 | 结论 |
| --- | --- |
| 顶层顺序 | v02、v03、v04、v05、v05.1、v05.3 - v05.8 均为 `header -> GameSystem -> Pattern -> SceneLabel -> EOF` |
| header | v02/v03/v04/v05 为 15 字节 ASCII token；v02 后面直接是 `width/height`，v03/v04 后面的 `01 00`、v05 后面的 `00 00` 属于 `GameSystem.versionMarker`；v05.1+ 为 17 字节 ASCII `[SCR-PARAMS]v05.x`，无 NUL |
| 文本编码 | v02/v03/v04 为 `u8 byteLen + ANSI bytes`，资源名/索引名固定按 CP932 处理，只有 `GameTitle` / `DisplayTitle` / `Brand` / `StaffName1` / `StaffName2` 接受 CLI 指定的 legacy 读写编码；部分表项 payload 会按对象内 XOR key 异或；v05+ 为 `u16 byteLen + UTF-16LE bytes` |
| 运行时编码 | exe 读入后会经 `WideCharToMultiByte(3 / CP_THREAD_ACP)` 降到窄字节；乱码根因在运行时 ANSI 链路，不在 `params.dat` 字段宽度 |
| LINK6 key | 来自 `GameSystem.RawBlob.LinkXorKeyBase64`，原始结构始终为 `u32 length + byte[length]`；这不是未解析数据块，而是 LINK6 加密条目使用的循环 XOR key bytes；算法不随 v04、v05、v05.1、v05.3 - v05.8 变化 |
| CG/SP 合成 | 主要消费 `Pattern.IntArrays` 和 `Pattern.GroupTable1/2`；合成算法可复用 AST，但必须使用同版本 `params.dat` 与同版本资源 |
| 回封 | 当前 C# codec 支持二进制闭环：`params verify` / `params verify-json` |

## 版本总览

下表是已确认样本的平面化差异。`RegCg` / `RegScene` 写作 `组数/项数`，`GT1` / `GT2` 写作 `组数/index 总数`。

| 版本 | 画布 | raw blob | install | demos 命令 | thumbnails | scene names | RegCg | RegScene | Pattern items | Pattern kind 分布 | IntArrays | GT1 | GT2 | SceneLabel |
| --- | --- | ---: | ---: | --- | ---: | ---: | --- | --- | ---: | --- | --- | --- | --- | ---: |
| v02 | `800x600` | `0x1D4C00` | 9 | 无 | 无 | 无 | 无 | 无 | 1121 | `0=1121` | `1159/3344` | `80/683` | `19/101` | 51 |
| v03 | `800x600` | `0x1D4C00` | 15 | 无 | 无 | 无 | 无 | 无 | 2210 | `0=2210` | `2341/7586` | `81/1231` | `34/75` | 49 |
| v04 | `1024x600` | `0x258000` | 20 | 无 | 无 | 无 | 无 | 无 | 2324 | `0=1884, 1=440` | `2542/9889` | `80/1625` | `27/113` | 56 |
| v05 | `1024x600` | `0x258000` | 16 | 无 | 无 | 无 | 无 | 无 | 2033 | `0=1701, 1=332` | `2299/8496` | `80/1547` | `28/70` | 60 |
| v05.1 | `1024x600` | `0x258000` | 15 | 无 | 无 | 无 | 无 | 无 | 1966 | `0=1625, 1=341` | `2216/8322` | `81/1313` | `36/79` | 55 |
| v05.3 | `1024x600` | `0x258000` | 16 | `9,100` | 80 | 65 | `5/80` | `5/65` | 2468 | `0=2052, 1=416` | `2835/11788` | `80/1807` | `28/85` | 65 |
| v05.4 | `1024x600` | `0x258000` | 13 | `15,65` | 87 | 59 | `5/86` | `5/59` | 2204 | `0=2180, 1=24` | `1945/5825` | `86/660` | `54/86` | 59 |
| v05.5 | `1024x600` | `0x258000` | 12 | `15,83` | 90 | 58 | `5/90` | `5/58` | 1960 | `0=1950, 1=10` | `2016/5279` | `90/637` | `50/83` | 58 |
| v05.6 | `1024x600` | `0x258000` | 14 | `15,116` | 77 | 57 | `5/77` | `5/57` | 1994 | `0=1990, 1=4` | `1865/5265` | `77/632` | `62/112` | 57 |
| v05.7 | `1280x720` | `0x384000` | 16 | `15,116` | 80 | 59 | `5/80` | `5/59` | 2206 | `0=2197, 1=8, 2=1` | `2424/9606` | `80/615` | `26/98` | 59 |
| v05.8 | `1280x720` | `0x384000` | 17 | `15,131` | 80 | 62 | `5/80` | `5/62` | 2351 | `0=2343, 1=8` | `2553/10084` | `80/609` | `26/108` | 62 |

这些数量来自实际 `params.dat -> params.json` AST，并已与 IDA 伪代码分支互相校验。它们不是格式常量，只是当前样本统计；工具不得把数量写死。

## 顶层布局

```text
versioned header            // v05: 15 bytes; v05.1+: 17 bytes
GameSystem gameSystem
Pattern pattern
SceneLabel[] sceneLabels
EOF
```

v02、v03、v04、v05、v05.1、v05.3 - v05.8 的顶层顺序没有变化。变化集中在 header 长度、字符串编码、`GameSystem` 的中后段子块、`Pattern` 的 item/group 子格式、以及 `_regist_scene` 的 item 子格式。

## 基础类型

### `string16`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `byteLen` | `u16le` | 后续 UTF-16LE 字节数，不含自身 |
| `data` | `byte[byteLen]` | UTF-16LE 文本；`byteLen == 0` 表示空字符串 |

### typed value

| wrapper | 二进制布局 | 说明 |
| --- | --- | --- |
| typed string | `u32 type=0; string16 value` | 回封时必须保留 type |
| typed int | `u32 type=1; u32 value` | 常用于计数、标志、索引 |
| typed point | `u32 type=2; u32 x; u32 y` | `_regist_cg` 坐标 |

### `SettingTag`

```text
string16 tagName
u32 pairCount
repeat pairCount:
    string16 key
    string16 value
u32 childCount
repeat childCount:
    SettingTag child
```

`SettingTag` 是 `GameSystem` 内部的递归子结构，不是整个 `params.dat` 的通用容器。

### v02/v03/v04 short string

v02/v03/v04 仍是旧 ANSI 结构，字符串不是 UTF-16LE：

```text
u8 byteLen
byte[byteLen] cp932Bytes
```

`GameSystem` 的标题、安装表等前段字符串不异或；`GameSystem` 后段 `_voice/_multi/_sound` 的部分字符串、`Pattern`、`SceneLabel` 会先用对应对象内的 `u8 xorKey` 对 payload 字节异或。工具读取时会解 XOR 后按 CP932 转为 JSON 文本，写回时再按 CP932 编码并恢复 XOR。

### `DemoData`

```text
byte[9] "[Demo3.0]"
u16 commandCount
repeat commandCount:
    u8 type
    u8 length          // 包含 type/length 两字节
    byte[length - 2] payload
```

已实现 type：`0 End`, `1 Next`, `2 Wait`, `3 Sound`, `4 Load`, `5 Transit`, `6 Disp`, `7 Update`, `8 Move`, `9 Pos`。未知 type 保留 raw payload。

短字符串使用 `u8 byteLen + UTF-16LE bytes`，不是 `string16`。

## GameSystem 横向差异

### 前段公共布局

v05、v05.1、v05.3 - v05.8 的 `GameSystem` 前段基本一致，但 v05/v05.1 在 install 表后只有 3 个 scalar，v05.3+ 为 4 个 scalar：

```text
u16 versionMarker
u32 width
u32 height
u8 configByteCount
byte[configByteCount] configBytes
string16 gameTitle
string16 displayTitle
string16 brand
u8 staffFlag
string16 staffName1
string16 staffName2
u8 installCount
repeat installCount:
    string16 file
    string16 media
u32 scalar0
u32 scalar1
u32 scalar2
u32 scalar3             // v05.3+
versioned tail
```

`staffFlag` 已确认控制默认自定义人名能力：

| value | 含义 |
| ---: | --- |
| `0` | 默认人名不可自定义 |
| `1` | 可自定义名 |
| `2` | 可自定义姓 |
| `3` | 可自定义姓名 |

### `V5TailByte`

| 版本 | `4 * u32 scalar` 后面 |
| --- | --- |
| v05 / v05.1 | 只有 `3 * u32 scalar`，随后直接进入旧式 `_voice/_multi/_sound` 子块 |
| v05.3 - v05.4 | 直接进入 3 个 `SettingTag` present flag |
| v05.5 - v05.8 | 先读 `u8 V5TailByte`，再进入 3 个 present flag |

这是 v05.3/v05.4 最容易错位的字段。若把第一个 present flag 误读成 `V5TailByte`，后续 raw blob、thumbnail、`_regist_scene` 都会错 1 字节。

### v04 中后段顺序

v02/v03/v04 与 v05/v05.1 同样没有 `SettingTag`、`DemoData`、thumbnail、scene name、`_regist_cg`、`_regist_scene` 子块，但字符串是短 CP932。v02 在 install table 后直接进入对象内 XOR key；v03/v04 在 3 个 scalar 后进入对象内 XOR key：

```text
u16 versionMarker
u32 width
u32 height
u8 configByteCount
byte[configByteCount] configBytes
short_cp932 gameTitle
short_cp932 displayTitle
short_cp932 brand
u8 staffFlag
short_cp932 staffName1
short_cp932 staffName2
u8 installCount
repeat installCount:
    short_cp932 file
    short_cp932 media
u32 scalar0
u32 scalar1
u32 scalar2
u8 xorKey
u8 voiceCount
repeat voiceCount:
    u8 flag
    xor short_cp932 name
    u8 primaryCount
    primaryCount * xor short_cp932
    u8 secondaryCount
    secondaryCount * short_cp932
u8 byteGroupCount
repeat byteGroupCount:
    xor short_cp932 name
    u8 valueCount
    valueCount * u8
u8 soundGroupCount
repeat soundGroupCount:
    xor short_cp932 name
    u8 primaryCount
    primaryCount * xor short_cp932
    u8 secondaryCount
    secondaryCount * short_cp932
u32 rawBlobLength
byte[rawBlobLength] rawBlob
```

当前 v02/v03/v04 `GameSystem` 已按字段结构化写回，标题、品牌、安装表、voice/multi/sound、RawBlob 等字段都不再依赖原始 payload 拼接。v02 在 `Brand` 后额外有 `V02Copyright` 字段，写回时按原位置保留。`--read-encoding` / `--write-encoding` 只作用于 `GameTitle` / `DisplayTitle` / `Brand` / `StaffName1` / `StaffName2`，其余 legacy 字符串固定 CP932，以避免资源名、安装表、Pattern 名和 SceneLabel 被误写成其它代码页。字符串回封时会重算 `u8 byteLen`；若字符无法被目标编码表示，工具会报错，避免静默写成 `?`。

### v05 / v05.1 中后段顺序

v05 与 v05.1 仍有 `raw blob`、`Pattern`、`SceneLabel`，但没有 v05.3+ 的 `SettingTag`、`DemoData`、thumbnail、scene name、`_regist_cg`、`_regist_scene` 子块。IDA `GameScript::GameSystem` reader 确认为：

```text
u8 voiceCount
repeat voiceCount:
    u8 flag
    string16 name
    u8 primaryCount
    primaryCount * string16
    u8 secondaryCount
    secondaryCount * string16
u8 byteGroupCount
repeat byteGroupCount:
    string16 name
    u8 valueCount
    valueCount * u8
u8 soundGroupCount
repeat soundGroupCount:
    string16 name
    u8 primaryCount
    primaryCount * string16
    u8 secondaryCount
    secondaryCount * string16
u32 rawBlobLength
byte[rawBlobLength] rawBlob
if v05.1:
    u32 v51StringCount
    v51StringCount * string16
    u32 v51PlaceCount
    repeat v51PlaceCount:
        string16 placeName
        u32 value
```

v05 在 `rawBlob` 后直接进入 `Pattern`。当前 v05 样本统计：`voiceCount=5`、`byteGroupCount=0`、`soundGroupCount=7`。

当前 v05.1 样本统计：`voiceCount=6`、`byteGroupCount=0`、`soundGroupCount=7`、`v51StringCount=34`、`v51PlaceCount=37`。

### v05.3+ 中后段公共顺序

```text
repeat 3:
    u8 present
    SettingTag? root
u32 v53TripleRawCount
if v05.3:
    repeat v53TripleRawCount:
        u32 value1
        u32 value2
        u32 value3
u32 rawBlobLength
byte[rawBlobLength] rawBlob
u8 demoCount
repeat demoCount:
    string16 name
    DemoData demo
u32 v51StringCount
repeat v51StringCount:
    string16 value
u32 v51PlaceCount
repeat v51PlaceCount:
    string16 placeName
    u32 value
if v05.4+:
    string16 v54NestedListName
    u32 v54NestedOuterCount
u32 thumbnailUnitCount
thumbnailUnitCount / 11 * Thumbnail
u32 sceneNameCount
sceneNameCount * typed string
RegistCg
RegistScene
```

三元组表由 `u32 count + count * (3 * u32)` 组成。v05.3 主样本中 `v53TripleRawCount == 4`；部分 v05.4 - v05.6 旁支样本中也会出现非零 count，因此工具按 count 判断是否存在，而不按版本号硬编码。当前业务名未确认，但二进制回封和 JSON 往返已经闭合。

v05.3 样本中 `v51PlaceCount == 25`，其布局为 `string16 placeName + u32 value`，与旧地图/地点名 UI 相关。v05.4 - v05.8 当前样本该表为 0。

`V54NestedListName + V54NestedOuterCount` 从 v05.4 开始出现；v05.3 没有这两个字段，当前位置直接进入 `thumbnailUnitCount`。当前 v05.4 - v05.8 样本中 `v54NestedOuterCount` 均为 0，非零嵌套体仍需新样本确认。

### raw blob 与 LINK6 key

raw blob 是 `array<unsigned char>`，IDA 逻辑只按长度分配并 `memmove`。在工具里它暴露为 JSON base64，也可用 `params extract-raw` / `params replace-raw` 单独操作。

| 版本 | raw blob 长度 | 对 LINK6 的影响 |
| --- | ---: | --- |
| v02 - v03 | `0x1D4C00` | 对应 `800 * 600 * 4` |
| v04 / v05 / v05.1 / v05.3 - v05.6 | `0x258000` | 对应 `1024 * 600 * 4` |
| v05.7 - v05.8 | `0x384000` | 对应 `1280 * 720 * 4` |

LINK6 解密算法本身不变：BM/AP/AP-2/AP-3 的 XOR key 都取 `GameSystem.RawBlob.LinkXorKeyBase64` 解码后的 key bytes。工具必须按 header 走正确结构分支找到 `u32 length + byte[length]`，不能写死偏移或长度。

## thumbnail / scene name / RegistCg

这些子块 v05.3 - v05.8 的布局稳定，差异主要是数量和内容。

### Thumbnail

`thumbnailUnitCount` 是 typed unit 数，不是 entry 数。每个 entry 固定 11 个 unit：

```text
8 * typed string
3 * typed int
```

entry 数 = `thumbnailUnitCount / 11`。

### scene name list

```text
u32 count
count * typed string
```

与回想/场景 UI 和 `SceneLabel` 名称集合相关。

### `_regist_cg`

```text
u32 unitCount
while usedUnits < unitCount:
    typed string groupName
    typed int itemCount
    repeat itemCount:
        typed string itemName
        typed point position
        typed int value
```

每组固定消耗 `2 + itemCount * 3` 个 unit。回封时必须重算 `unitCount`。

## RegistScene 横向差异

`_regist_scene` 是 v05.8 相对旧版的主要变化点之一。

| 版本 | item 布局 | 说明 |
| --- | --- | --- |
| v05.3 - v05.7 | `typed string sceneName; typed string cgName` | legacy 双字符串；v05.1 没有 `_regist_scene` 子块 |
| v05.8 | `typed string itemName; typed int nestedCount; nestedCount * typed string sceneName` | 嵌套 scene list |

legacy 布局：

```text
u32 unitCount
while usedUnits < unitCount:
    typed string groupName
    typed int itemCount
    repeat itemCount:
        typed string sceneName
        typed string cgName
```

v05.8 布局：

```text
u32 unitCount
while usedUnits < unitCount:
    typed string groupName
    typed int itemCount
    repeat itemCount:
        typed string itemName
        typed int nestedCount
        repeat nestedCount:
            typed string sceneName
```

这个差异主要影响完整解析/回封和回想元数据，不直接参与当前 `CharacterComposer` 的 CG/SP 图层合成。

## Pattern 横向差异

`Pattern` 是 CG/SP 合成最关键的块。工具内部统一导出为 `ParamsPatternItem` AST，但写回时必须按 header 写原版本布局。

### item 布局

| 版本 | 原始布局 | AST 映射 |
| --- | --- | --- |
| v02 - v03 | legacy 单字符串 item，名称为 XOR short CP932 | 每项映射为 `Kind=0 + Name`；没有 v04 的 fileNameCount/name-list |
| v04 | legacy `FileNames`，名称和文件名为 XOR short CP932 | `fileNameCount == 0 -> Kind=0`；`fileNameCount > 0 -> Kind=1 + Strings` |
| v05 / v05.1 / v05.3 - v05.6 | legacy `FileNames` | `fileNameCount == 0 -> Kind=0`；`fileNameCount > 0 -> Kind=1 + Strings` |
| v05.7 - v05.8 | explicit `PatternItem` | 原样保留 `Kind` 与对应 payload |

legacy `FileNames`：

```text
u32 itemCount
repeat itemCount:
    string16 name
    u8 fileNameCount
    repeat fileNameCount:
        string16 fileName
```

explicit `PatternItem`：

```text
u32 itemCount
repeat itemCount:
    string16 name
    u8 kind
    payload by kind
```

| kind | payload | 说明 |
| ---: | --- | --- |
| `0` | none | 单名资源项 |
| `1` | `u32 count; count * string16` | 文件名/资源名列表 |
| `2` | `string16 subName; u32 x; u32 y` | `ExcPosition` 分支，v05.7 样本出现 1 项 |
| `3` | `string16 subName; u32 value` | `FileConvert` 分支，v05.8 引擎支持，当前样本未出现 |

### IntArrays

所有已确认版本结构相同：

```text
u32 intArrayCount
repeat intArrayCount:
    u8 count
    count * u32 index
```

`count` 仍是 `u8`，不要和 group table 的 index count 混淆。

SCR/HLS `pattern_layer.resource_ref` 的运行时消费链也落在这里。IDA `sub_498870` 确认该字段不是 `Pattern.Items` 下标，而是 `Pattern.IntArrays` 下标；引擎先取 `Pattern.IntArrays[resource_ref]`，再把数组内的值作为 `Pattern.Items` 下标展开成资源字符串列表。v05.8 全量样本中 `Pattern.Items.Count=2351`、`Pattern.IntArrays.Count=2553`，所有 12183 个 `pattern_layer.resource_ref` 都小于 `IntArrays.Count`，其中 345 个大于等于 `Items.Count`，因此不能再把 `resource_ref` 直接解释为 item/resource id。

### GroupTable1 / GroupTable2

| 版本 | indexCount 字段 |
| --- | --- |
| v02 / v03 / v04 / v05 / v05.1 / v05.3 - v05.5 | `u8 indexCount` |
| v05.6 - v05.8 | `u16 indexCount` |

结构：

```text
u32 groupCount
repeat groupCount:
    string16 name
    versioned indexCount
    indexCount * u32 patternItemIndex
```

`GroupTable.indices` 指向 `Pattern.Items` 下标。增删 item 时必须同步修正所有引用，否则 CG/SP 合成会错。

## SceneLabel

v05、v05.1、v05.3 - v05.8 布局稳定：

```text
u32 count
repeat count:
    string16 name
    u32 value1
    u32 value2
```

`value1/value2` 倾向于脚本索引与脚本内位置，但业务命名仍依赖 `.scr` op 与回想调用链继续确认。结构层已经可解析和回封。

## 运行时文本链路

v05+ 的 `params.dat` 文件内文本是 UTF-16LE，但 Start.exe 读入后通常会转成引擎内部 `char string`；v04 本身已经是短 CP932 ANSI 字符串，部分表项还带对象 XOR key：

```text
string16 reader
  -> read u16 byteLen
  -> copy UTF-16LE temporary buffer
  -> WideCharToMultiByte(3 /* CP_THREAD_ACP */, ...)
  -> engine char string

Graphics.dll
  -> CreateFontIndirectA
  -> TextOutA / ExtTextOutA
```

所以：

1. `u16` / `u8` 字段宽度只说明二进制长度或计数，不代表运行时 Unicode 显示能力。
2. 中文默认人名乱码不是 `params.dat` 结构错，而是运行时按 CP932/SJIS 解释或降级。
3. 不应全局把 `WideCharToMultiByte` 改成 CP936；那会污染标题、品牌名、资源名等其它 params 字符串。
4. 默认人名应按字段精确 hook。v05.6 - v05.8 走 params `string16` reader；v04、v05、v05.1、v05.3 - v05.5 属于旧版 legacy resource/string wrapper 路线，具体 hook 点仍应按对应 exe 反推。已确认矩阵见 [DLL 分析记录](../../PE/README_dll.md#86-paramsdat-默认人名的精确-hook-点)。

## 对工具功能的影响

### LINK6 解包

| 项 | 影响 |
| --- | --- |
| key 来源 | `GameSystem.RawBlob.LinkXorKeyBase64` |
| v04 / v05 / v05.1 / v05.3 - v05.8 算法 | 不变 |
| 版本差异风险 | 找 raw blob 的偏移会因 `GameSystem` 分支变化而变，尤其 v04 是 short CP932 + XOR key，v05/v05.1 有旧式 `_voice/_multi/_sound`，v05 在 raw blob 后直接进入 Pattern，v05.3 以及部分 v05.4 - v05.6 旁支样本有三元组表，v05.3 缺 `V54NestedListName/V54NestedOuterCount`，v05.3/v05.4 均缺 `V5TailByte` |
| 工具策略 | 先完整解析 params AST，再取 raw blob；不按固定 offset 搜索 |

### CG/SP 合成

| 项 | 影响 |
| --- | --- |
| 依赖字段 | `Pattern.Items`, `Pattern.IntArrays`, `Pattern.GroupTable1/2` |
| 算法层 | 当前 `CharacterComposer` 消费统一 AST，可兼容 legacy/new Pattern |
| 数据层 | 不能跨版本混用 params 与 pic 资源；数量和索引内容都不同 |
| 版本差异风险 | v04、v05、v05.1、v05.3 - v05.5 group index 是 `u8`，v05.6+ 是 `u16`；v04、v05、v05.1、v05.3 - v05.6 item 是 legacy `FileNames`；v04 的 Pattern 字符串还需要按对象 XOR key 处理 |

### 回封

回封必须遵守：

1. 按顶层顺序写回：header、`GameSystem`、`Pattern`、`SceneLabel`。
2. v05+ 的所有 `string16` 重算 UTF-16LE 字节长度；v04 的 short CP932 字符串重算 `u8` 字节长度并恢复 XOR。
3. typed wrapper 的 type 不可省略。
4. raw blob 写 `u32 length + bytes`，未修改时 byte-for-byte 保留。
5. thumbnail / `_regist_cg` / `_regist_scene` 的 unit count 不是 entry count。
6. `DemoCommand.length` 包含 type/length 两字节，改 payload 必须同步更新。
7. Pattern item 增删会影响 group indices，应整体维护。

## 实现状态

当前实现位于 `scr/Formats/Params/ParamsDatCodec.cs` 和 `ParamsModels.cs`。

| 功能 | 状态 |
| --- | --- |
| v04 15 字节 header token | 已支持；`01 00` 按 `GameSystem.versionMarker` 处理 |
| v02 15 字节 header token | 已支持；header 后直接读取 `width/height`，没有 `GameSystem.versionMarker`；install table 后直接进入对象内 XOR key，没有 `V5Scalars`；`Pattern` 使用单字符串 item 分支 |
| v03 15 字节 header token | 已支持；`01 00` 按 `GameSystem.versionMarker` 处理，`GameSystem` / `SceneLabel` 走 v04-family，`Pattern` 使用单字符串 item 分支 |
| v02/v03/v04 short ANSI 字符串与对象 XOR key | 已支持，`GameSystem` / `Pattern` / `SceneLabel` 均按 IDA reader 分支处理；CLI legacy 编码只波及标题、会社名和 staff 人名字段，其余 legacy 字符串固定 CP932 |
| v04 `GameSystem` legacy payload | 已支持结构化写回：标题、品牌、安装表、voice/multi/sound、RawBlob 均可按字段重建 |
| v04 legacy `Pattern::FileNames` / group table / SceneLabel | 已支持，二进制与 JSON 往返已闭合 |
| v05 15 字节 header token | 已支持；`00 00` 按 `GameSystem.versionMarker` 处理 |
| v05.1 / v05.3 - v05.8 17 字节 header | 已支持 |
| v05 raw blob 后直接进入 Pattern | 已支持 |
| v05 / v05.1 旧式 `_voice/_multi/_sound` 中段 | 已支持，导出为 `V51VoiceEntries` / `V51ByteGroups` / `V51SoundGroups` |
| v05 / v05.1 省略 `SettingTag` / `DemoData` / thumbnail / scene name / `_regist_cg` / `_regist_scene` | 已支持 |
| v05.3 - v05.4 省略 `V5TailByte` | 已支持 |
| `V53Triples` 三元组表 | 已支持，按 `u32 count + count * 3*u32` 结构化保留；不是 v05.3 专属，v05.4 - v05.6 的部分旁支样本也会出现 |
| v05.3 `V51Places` 地点表 | 已支持，按 `u32 count + count * (string16 + u32)` 原样保留 |
| v05.3 省略 `V54NestedListName/V54NestedOuterCount` | 已支持 |
| v05 / v05.1 / v05.3 - v05.6 legacy `Pattern::FileNames` | 已支持，AST 映射到 `ParamsPatternItem` |
| v05 / v05.1 / v05.3 - v05.5 `u8 group indexCount` | 已支持 |
| v05.6 - v05.8 `u16 group indexCount` | 已支持 |
| v05.3 - v05.7 legacy `_regist_scene` | 已支持 |
| v05.8 nested `_regist_scene` | 已支持 |
| `DemoData` type 0 - 9 | 已支持 |
| raw blob extract/replace | 已支持 |
| byte-for-byte verify | 已支持 |

已验证样本矩阵：

| 样本 | `verify-json` |
| --- | --- |
| `v04` / `v05` / `v05.1` / `v05.1_2` / `v05.3` | OK |
| `v05.4` / `v05.4_2` / `v05.4_3` / `v05.5` / `v05.5_2` / `v05.6` / `v05.6_2` | OK |
| `v05.7` / `v05.8` | OK |
| `v02` / `v02_2` / `v03` / `v03_2` | OK |

CLI：

```text
params dump <params.dat>
params export-json <params.dat> <output.json>
params import-json <input.json> <output.dat>
params verify <params.dat>
params verify-json <params.dat>
params extract-raw <params.dat> <raw.bin>
params replace-raw <params.dat> <raw.bin> <output.dat>
```

legacy ANSI 版本可追加编码参数：

```text
--read-encoding cp932
--write-encoding cp932
```

当前别名包括 `cp932` / `sjis` / `shift_jis`、`gbk` / `cp936`、`utf-8`，也可直接传数字代码页。该参数只影响 v02/v03/v04 的 `GameTitle` / `DisplayTitle` / `Brand` / `StaffName1` / `StaffName2`；legacy 资源名、安装介质名、Pattern 名和 SceneLabel 固定 CP932，v05+ 的 `string16` 始终按 UTF-16LE 处理。

启动上下文解析到 `params.dat` 时会同时报告版本号，并把 `GameSystem.RawBlob.LinkXorKeyBase64` 解码后的 bytes 暴露为 LINK6 解密 key。

## 继续扩展到更早版本

分析更早版本时，按下面顺序补本文，不再追加 pairwise 对比章节：

1. 在“版本总览”新增一行：尺寸、raw blob、install、Pattern/SceneLabel 统计。
2. 在对应横向表新增版本范围：例如 `V5TailByte`、Pattern item、group index、`_regist_scene`。
3. 若发现新子结构，新增一个以字段族命名的小节，而不是“vX -> vY”小节。
4. 对 LINK6 / CG-SP / 回封的影响只写到“对工具功能的影响”。
5. 如果只是字段业务语义补完，不改变二进制布局，写到对应字段说明或“待回填点”。

最小逆向证据应包括：

| 证据 | 用途 |
| --- | --- |
| header 识别入口 | 确认版本字符串与顶层调度 |
| `GameSystem` reader | 确认 raw blob、setting、RegistCg/RegistScene 分支 |
| `Pattern` reader | 确认 item 布局、int arrays、group index count 宽度 |
| `SceneLabel` reader | 确认尾部结构和 EOF |
| 实际 params.dat 闭环 | 用 `params verify` / `verify-json` 证明结构无错位 |

## 待回填点

这些不是当前结构阻塞点，也不影响 `params.dat -> json -> params.dat` 的自由编辑闭环。它们主要是业务命名精度问题：

| 项 | 当前状态 | 后续依赖 |
| --- | --- | --- |
| `SceneLabel.value1/value2` | 结构已确认，业务名未最终定 | `.scr` 标签/op 与回想调用链 |
| `Pattern.kind=2/3` 业务名 | IDA 分支和布局已确认，样本覆盖有限 | 图片定位、资源转换调用 |
| `SettingTag` key/value 效果 | 三棵树可完整解析 | 设置 UI 与运行时变量 |
| `DemoData` 部分字段名 | 可编辑回封；`Move/Pos/Wait` 仍有暂名 | demo 播放流程 |
| thumbnail 槽位语义 | 固定 `8 string + 3 int` | CG 鉴赏 UI |
| `_regist_cg` point/int 语义 | 结构已确认 | CG 鉴赏坐标/差分显示 |
| install media 字段 | 样本多为 `DVD-ROM` | 安装检查/介质切换逻辑 |
