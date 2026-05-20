# params.dat 横向结构记录

本文记录 Kaguya/YaneSDK `params.dat` 的结构、版本差异和工具实现策略。当前工具覆盖
`[SCR-PARAMS]v05.4` - `[SCR-PARAMS]v05.8`。以后继续分析 v05.0 或更早版本时，不再按
“某版对某版”追加流水账，而是把新证据填进本文的横向矩阵。

旧版文档已备份为 `README_params_dat.md.bk_20260520_flat`。

## 快速结论

| 主题 | 结论 |
| --- | --- |
| 顶层顺序 | v05.4 - v05.8 均为 `header -> GameSystem -> Pattern -> SceneLabel -> EOF` |
| header | ASCII `[SCR-PARAMS]v05.x`，长度固定 17 字节，无 NUL |
| 文本编码 | 文件内结构字符串是 `u16 byteLen + UTF-16LE bytes` |
| 运行时编码 | exe 读入后会经 `WideCharToMultiByte(3 / CP_THREAD_ACP)` 降到窄字节；乱码根因在运行时 ANSI 链路，不在 `params.dat` 字段宽度 |
| LINK6 key | 来自 `GameSystem.RawBlob`，结构始终为 `u32 length + byte[length]`；算法不随 v05.4 - v05.8 变化 |
| CG/SP 合成 | 主要消费 `Pattern.IntArrays` 和 `Pattern.GroupTable1/2`；合成算法可复用 AST，但必须使用同版本 `params.dat` 与同版本资源 |
| 回封 | 当前 C# codec 支持二进制闭环：`params verify` / `params verify-json` |

## 版本总览

下表是已确认样本的平面化差异。`RegCg` / `RegScene` 写作 `组数/项数`，`GT1` / `GT2` 写作 `组数/index 总数`。

| 版本 | 画布 | raw blob | install | demos 命令 | thumbnails | scene names | RegCg | RegScene | Pattern items | Pattern kind 分布 | IntArrays | GT1 | GT2 | SceneLabel |
| --- | --- | ---: | ---: | --- | ---: | ---: | --- | --- | ---: | --- | --- | --- | --- | ---: |
| v05.4 | `1024x600` | `0x258000` | 13 | `15,65` | 87 | 59 | `5/86` | `5/59` | 2204 | `0=2180, 1=24` | `1945/5825` | `86/660` | `54/86` | 59 |
| v05.5 | `1024x600` | `0x258000` | 12 | `15,83` | 90 | 58 | `5/90` | `5/58` | 1960 | `0=1950, 1=10` | `2016/5279` | `90/637` | `50/83` | 58 |
| v05.6 | `1024x600` | `0x258000` | 14 | `15,116` | 77 | 57 | `5/77` | `5/57` | 1994 | `0=1990, 1=4` | `1865/5265` | `77/632` | `62/112` | 57 |
| v05.7 | `1280x720` | `0x384000` | 16 | `15,116` | 80 | 59 | `5/80` | `5/59` | 2206 | `0=2197, 1=8, 2=1` | `2424/9606` | `80/615` | `26/98` | 59 |
| v05.8 | `1280x720` | `0x384000` | 17 | `15,131` | 80 | 62 | `5/80` | `5/62` | 2351 | `0=2343, 1=8` | `2553/10084` | `80/609` | `26/108` | 62 |

这些数量来自实际 `params.dat -> params.json` AST，并已与 IDA 伪代码分支互相校验。它们不是格式常量，只是当前样本统计；工具不得把数量写死。

## 顶层布局

```text
byte[17] header             // "[SCR-PARAMS]v05.x"
GameSystem gameSystem
Pattern pattern
SceneLabel[] sceneLabels
EOF
```

v05.4 - v05.8 的顶层顺序没有变化。变化集中在 `GameSystem` 的几个尾部子块、`Pattern` 的 item/group 子格式、以及 `_regist_scene` 的 item 子格式。

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

v05.4 - v05.8 的 `GameSystem` 前段一致：

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
u32 scalar3
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
| v05.4 | 直接进入 3 个 `SettingTag` present flag |
| v05.5 - v05.8 | 先读 `u8 V5TailByte`，再进入 3 个 present flag |

这是 v05.4 最容易错位的字段。若把 v05.4 第一个 present flag 误读成 `V5TailByte`，后续 raw blob、thumbnail、`_regist_scene` 都会错 1 字节。

### 中后段公共顺序

```text
repeat 3:
    u8 present
    SettingTag? root
u32 v53TripleRawCount
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
string16 v54NestedListName
u32 v54NestedOuterCount
u32 thumbnailUnitCount
thumbnailUnitCount / 11 * Thumbnail
u32 sceneNameCount
sceneNameCount * typed string
RegistCg
RegistScene
```

当前样本中 `v51PlaceCount` 与 `v54NestedOuterCount` 均为 0。codec 保留字段并在非零时 fail fast，因为样本还不足以确认其内部布局。

### raw blob 与 LINK6 key

raw blob 是 `array<unsigned char>`，IDA 逻辑只按长度分配并 `memmove`。在工具里它暴露为 JSON base64，也可用 `params extract-raw` / `params replace-raw` 单独操作。

| 版本 | raw blob 长度 | 对 LINK6 的影响 |
| --- | ---: | --- |
| v05.4 - v05.6 | `0x258000` | 对应 `1024 * 600 * 4` |
| v05.7 - v05.8 | `0x384000` | 对应 `1280 * 720 * 4` |

LINK6 解密算法本身不变：BM/AP/AP-2/AP-3 的 XOR key 都取这个 raw blob。工具必须按 header 走正确结构分支找到 blob，不能写死偏移或长度。

## thumbnail / scene name / RegistCg

这些子块 v05.4 - v05.8 的布局稳定，差异主要是数量和内容。

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
| v05.4 - v05.7 | `typed string sceneName; typed string cgName` | legacy 双字符串 |
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
| v05.4 - v05.6 | legacy `FileNames` | `fileNameCount == 0 -> Kind=0`；`fileNameCount > 0 -> Kind=1 + Strings` |
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

### GroupTable1 / GroupTable2

| 版本 | indexCount 字段 |
| --- | --- |
| v05.4 - v05.5 | `u8 indexCount` |
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

v05.4 - v05.8 布局稳定：

```text
u32 count
repeat count:
    string16 name
    u32 value1
    u32 value2
```

`value1/value2` 倾向于脚本索引与脚本内位置，但业务命名仍依赖 `.scr` op 与回想调用链继续确认。结构层已经可解析和回封。

## 运行时文本链路

`params.dat` 文件内文本是 UTF-16LE，但 Start.exe 读入后通常会转成引擎内部 `char string`：

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
4. 默认人名应按字段精确 hook。v05.6 - v05.8 走 params `string16` reader；v05.4 - v05.5 走 legacy resource/string wrapper。具体运行时地址和 hook 矩阵见 [DLL 分析记录](../PE/README_dll.md#86-paramsdat-默认人名的精确-hook-点)。

## 对工具功能的影响

### LINK6 解包

| 项 | 影响 |
| --- | --- |
| key 来源 | `GameSystem.RawBlob` |
| v05.4 - v05.8 算法 | 不变 |
| 版本差异风险 | 找 raw blob 的偏移会因 `GameSystem` 分支变化而变，尤其 v05.4 缺 `V5TailByte` |
| 工具策略 | 先完整解析 params AST，再取 raw blob；不按固定 offset 搜索 |

### CG/SP 合成

| 项 | 影响 |
| --- | --- |
| 依赖字段 | `Pattern.Items`, `Pattern.IntArrays`, `Pattern.GroupTable1/2` |
| 算法层 | 当前 `CharacterComposer` 消费统一 AST，可兼容 legacy/new Pattern |
| 数据层 | 不能跨版本混用 params 与 pic 资源；数量和索引内容都不同 |
| 版本差异风险 | v05.4/v05.5 group index 是 `u8`，v05.6+ 是 `u16`；v05.4 - v05.6 item 是 legacy `FileNames` |

### 回封

回封必须遵守：

1. 按顶层顺序写回：header、`GameSystem`、`Pattern`、`SceneLabel`。
2. 所有 `string16` 重算 UTF-16LE 字节长度。
3. typed wrapper 的 type 不可省略。
4. raw blob 写 `u32 length + bytes`，未修改时 byte-for-byte 保留。
5. thumbnail / `_regist_cg` / `_regist_scene` 的 unit count 不是 entry count。
6. `DemoCommand.length` 包含 type/length 两字节，改 payload 必须同步更新。
7. Pattern item 增删会影响 group indices，应整体维护。

## 实现状态

当前实现位于 `scr/Formats/Params/ParamsDatCodec.cs` 和 `ParamsModels.cs`。

| 功能 | 状态 |
| --- | --- |
| v05.4 - v05.8 header | 已支持 |
| v05.4 省略 `V5TailByte` | 已支持 |
| v05.4 - v05.6 legacy `Pattern::FileNames` | 已支持，AST 映射到 `ParamsPatternItem` |
| v05.4 - v05.5 `u8 group indexCount` | 已支持 |
| v05.6 - v05.8 `u16 group indexCount` | 已支持 |
| v05.4 - v05.7 legacy `_regist_scene` | 已支持 |
| v05.8 nested `_regist_scene` | 已支持 |
| `DemoData` type 0 - 9 | 已支持 |
| raw blob extract/replace | 已支持 |
| byte-for-byte verify | 已支持 |

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

启动上下文解析到 `params.dat` 时会同时报告版本号，并把 `GameSystem.RawBlob` 暴露为 LINK6 解密 key。

## 继续扩展到更早版本

分析 v05.0 或更早版本时，按下面顺序补本文，不再追加 pairwise 对比章节：

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

这些不是当前结构阻塞点：

| 项 | 当前状态 | 后续依赖 |
| --- | --- | --- |
| `SceneLabel.value1/value2` | 结构已确认，业务名未最终定 | `.scr` 标签/op 与回想调用链 |
| `Pattern.kind=2/3` 业务名 | IDA 分支和布局已确认，样本覆盖有限 | 图片定位、资源转换调用 |
| `SettingTag` key/value 效果 | 三棵树可完整解析 | 设置 UI 与运行时变量 |
| `DemoData` 部分字段名 | 可编辑回封；`Move/Pos/Wait` 仍有暂名 | demo 播放流程 |
| thumbnail 槽位语义 | 固定 `8 string + 3 int` | CG 鉴赏 UI |
| `_regist_cg` point/int 语义 | 结构已确认 | CG 鉴赏坐标/差分显示 |
| install media 字段 | 样本多为 `DVD-ROM` | 安装检查/介质切换逻辑 |
