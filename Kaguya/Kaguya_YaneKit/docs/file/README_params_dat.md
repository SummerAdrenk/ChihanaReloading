# params.dat 格式逆向记录

## 运行时文本编码链路

`params.dat` 里的结构字符串本身是 `string16`，也就是：

```text
u16 byteLen
byte[byteLen] UTF-16LE data
```

但 exe 读入后不会一直以 Unicode 形式保存/绘制。v5.8 IDA 链路如下：

```text
sub_485F60
  -> 打开并读取 params.dat
  -> CSerialize 入口
  -> sub_42F160
       -> 识别 [SCR-PARAMS]v05.x
       -> sub_417190 读取 GameSystem
       -> sub_41A220 读取 Pattern
       -> sub_41B8E0 读取 SceneLabel

sub_41C6C0 / sub_41C7D0
  -> 读取 u16 byteLen
  -> 复制 UTF-16LE 临时缓冲
  -> sub_40CCF0
       -> WideCharToMultiByte(3 /* CP_THREAD_ACP */, ...)
       -> 存入引擎内部的 char string

Graphics.dll
  -> TextCreateFont
  -> CreateFontIndirectA
  -> TextOutA
```

关键结论：

- `params.dat` 的 `u16` / `u8` 只是二进制结构里的长度、计数或索引字段宽度，不代表运行时文字渲染支持 Unicode。
- `sub_40CCF0` 使用 `WideCharToMultiByte(3, ...)`，其中 `3` 是 `CP_THREAD_ACP`。在日文环境或日文引擎默认上下文里，这通常会落到 CP932/Shift-JIS。
- 写入中文 UTF-16LE 时，结构解析可以完全正确；但进入运行时对象时会被转成当前线程 ANSI 代码页的窄字节。CP932 不能表示的中文会被替换或变成错误字节序列。
- `Graphics.dll` 后续也走 `CreateFontIndirectA` / `TextOutA`，不是 `CreateFontIndirectW` / `TextOutW`，所以显示层同样按 ANSI 字节解释文本。

因此，中文乱码的根因不是 `params.dat` 的结构字段宽度错了，而是引擎运行时把 `params` 文本从 UTF-16LE 降级到 ANSI 代码页。要稳定显示中文，不能全局把 `WideCharToMultiByte` 改成 GBK/CP936；那会污染标题、品牌名、资源名等其他 params 字符串，甚至影响启动链路。

默认自定义人名应按字段精确处理：v5.8 的 `雅章/支部` 先由 `sub_417190` 读入 `GameSystem +0x30/+0x34`，之后才复制到运行时 `GameSystem +0x58/+0x5C` 并传给 `Graphics.dll!SetTextFirstName/SetTextSecondName`。精确 hook 点和地址见 [DLL 分析记录](../PE/README_dll.md#86-paramsdat-默认人名的精确-hook-点)。

适用目标： `[SCR-PARAMS]v05.4` / `[SCR-PARAMS]v05.5` / `[SCR-PARAMS]v05.6` / `[SCR-PARAMS]v05.7` / `[SCR-PARAMS]v05.8`。
本文以 v5.8 为基准结构记录；版本差异统一追加到“版本差异记录”章节，后续分析更早版本时继续按同一格式补表。

本文档记录从样本 `params.dat` 与 对 `Start.exe` 的逆向分析确认的结构。  
结论：本样本已经可以按 `GameSystem -> Pattern -> SceneLabel` 从文件头后一路顺序解析到 EOF，结构级边界已闭环。仍未全部命名的是若干字段的业务语义，而不是二进制布局。

## 顶层布局

| 段 | 偏移范围 | 大小 | 说明 |
| --- | ---: | ---: | --- |
| header | `0x000000..0x000011` | 17 | ASCII `[SCR-PARAMS]v05.x`，无 NUL；当前工具支持 v05.4 - v05.8 |
| `GameSystem` | `0x000011..0x38A08F` | `0x389F7E` | `417190.c` |
| `Pattern` | `0x38A08F..0x3AB8F9` | `0x2186A` | `41A220.c` |
| `SceneLabel` | `0x3AB8F9..0x3ABEF7` | `0x05FE` | `41B8E0.c` |
| EOF | `0x3ABEF7` | `3,849,975` bytes | 解析正好结束 |

重要修正：`0x11` 的 `01` 不是分隔符，而是 `GameSystem` 第一个 `u16le` 值 `1` 的低字节。

## 伪代码入口

| 文件 | 作用 |
| --- | --- |
| `42F160.c` | 识别 `[SCR-PARAMS]` 版本，并按顺序调用三大解析器 |
| `417190.c` | `GameSystem` |
| `41A220.c` | `Pattern` |
| `41B8E0.c` | `SceneLabel` |
| `41BDF0.c` | `SettingTag` 递归节点 |
| `416420.c` | `DemoData` |
| `41C6C0.c` / `41C7D0.c` | `string16` |
| `4150C0.c` | `typed string` |
| `414730.c` | `typed int` |

`42F160.c` 的顺序是固定的：版本头之后从 `0x11` 调 `417190`；返回 offset 后调 `41A220`；再用返回 offset 调 `41B8E0`。因此回封工具不能靠全文件字符串扫描重建，必须按顺序 AST 序列化。

## 基础类型

### `string16`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `byteLen` | `u16le` | 后续 UTF-16LE 字节数，不含长度字段 |
| `data` | `byte[byteLen]` | UTF-16LE 文本 |

`byteLen == 0` 表示空字符串，只消耗 2 字节。

### typed wrappers

`typed string`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `type` | `u32le` | 必须为 `0` |
| `value` | `string16` | 文本 |

`typed int`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `type` | `u32le` | 必须为 `1` |
| `value` | `u32le` | 整数 |

`typed point`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `type` | `u32le` | 必须为 `2` |
| `x` | `u32le` | X |
| `y` | `u32le` | Y |

这些 wrapper 的 type 不匹配时，引擎会抛异常。回封时不能省略。

### `SettingTag`

由 `41BDF0.c` 确认：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `tagName` | `string16` | 节点名 |
| `pairCount` | `u32le` | 键值对数量 |
| `pairs` | `pairCount * (string16 key + string16 value)` | 文本键值对 |
| `childCount` | `u32le` | 子节点数量 |
| `children` | `childCount * SettingTag` | 递归节点 |

`SettingTag` 只是一种子结构，不是整个 `params.dat` 的通用格式。

### `DemoData`

由 `416420.c` 确认：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `magic` | `byte[9]` | ASCII `[Demo3.0]` |
| `commandCount` | `u16le` | 命令数量 |
| `commands` | `commandCount * DemoCommand` | 逐项命令 |

`DemoCommand` 的通用边界：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `type` | `u8` | 命令类型 |
| `length` | `u8` | 本命令总长度，包含 `type/length` 两字节 |
| `payload` | `byte[length - 2]` | 按 type 解释，未知时可原样保留 |

已确认命令 type：`0 End`, `1 Next`, `2 Wait`, `3 Sound`, `4 Load`, `5 Transit`, `6 Disp`, `7 Update`, `8 Move`, `9 Pos`。

`DemoData` 的 payload 已经能按 type 编辑并重建。下面 offset 均为 payload 内偏移，不包含 `type/length` 两字节。

| type | 名称 | payload | `length` | 说明 |
| ---: | --- | --- | ---: | --- |
| 0 | `CmdEnd` | 空 | `2` | 结束命令 |
| 1 | `CmdNext` | 空 | `2` | 下一步命令；当前样本未出现，但 handler 存在 |
| 2 | `CmdWait` | `u8 modeOrFlag; u32 value` | `7` | 等待/延迟；handler 原样保存 1 字节标志和 4 字节值 |
| 3 | `CmdSound` | `u8 fieldA; u8 fieldB; u8 byteLen; utf16le[byteLen] name` | `5 + byteLen` | 声音命令；字符串长度是 1 字节，不是 `string16` |
| 4 | `CmdLoad` | `u8 slotOrLayer; u8 byteLen; utf16le[byteLen] name` | `4 + byteLen` | 载入资源；第 1 字节进入对象字段 `+4` |
| 5 | `CmdTransit` | `u8 byteLen1; utf16le[byteLen1] effect; u32 value; u8 byteLen2; utf16le[byteLen2] arg` | `8 + byteLen1 + byteLen2` | 转场命令；样本出现 `BLEND_BLT` 和空第二字符串 |
| 6 | `CmdDisp` | `u8 rawLayer; u8 visible` | `4` | 显示/隐藏；`visible != 0` 为真 |
| 7 | `CmdUpdate` | 空 | `2` | 更新/刷新命令 |
| 8 | `CmdMove` | `u8 idOrLayer; u32 durationOrValue; u32 value2; u32 value3; u32 value4; u32 value5` | `23` | 移动/插值类命令；字段布局已确认，坐标/时长业务名仍按运行效果细化 |
| 9 | `CmdPos` | `u8 idOrLayer; u32 value1; u32 value2` | `11` | 位置类命令；字段布局已确认 |

`CmdDisp.rawLayer` 的符号转换由 handler 明确给出：

```text
if rawLayer <= 0x80:
    layer = rawLayer
else:
    layer = (~rawLayer & 0xff) - 1
```

`DemoData` 内部的短字符串统一由 `41C500.c` 处理：`u8 byteLen + UTF-16LE bytes`，允许 `byteLen == 0` 表示空字符串。编辑时必须重算每条命令的 `length`，并同步重算 `commandCount`。

## GameSystem v5.8

样本的 `GameSystem` 从 `0x11` 到 `0x38A08F`，已完整走通。

| 顺序 | 偏移范围 | 结构 | 样本值/说明 |
| ---: | ---: | --- | --- |
| 1 | `0x000011..0x000069` | 基础信息 | 见下表 |
| 2 | `0x000069..0x0002AE` | install table | `u8 count=17`，每项 `string16 file + string16 media` |
| 3 | `0x0002AE..0x0002BF` | v5 scalar block | `u32[4]={3,3,1,3}` + `u8=2` |
| 4 | `0x0002BF..0x001A56` | 3 个 optional `SettingTag` | 每个前置 `u8 present` |
| 5 | `0x001A56..0x001A5A` | v5.3 triple table header | `u32 rawCount=0`；伪代码使用低 8 位作为 count |
| 6 | `0x001A5A..0x385A5E` | raw blob | `u32 length=0x00384000` + `byte[length]` |
| 7 | `0x385A5E..0x3867BD` | demos | `u8 count=2`，每项 `string16 name + DemoData` |
| 8 | `0x3867BD..0x3867C5` | v5.1 lists | `u32 stringCount=0` + `u32 placeCount=0` |
| 9 | `0x3867C5..0x3867CB` | v5.4 nested lists | `string16 name=""` + `u32 outerCount=0` |
| 10 | `0x3867CB..0x3885CF` | thumbnails | `u32 unitCount=880`，80 项 |
| 11 | `0x3885CF..0x388AD5` | scene name list | `u32 count=62` + `62 * typed string` |
| 12 | `0x388AD5..0x3895D7` | `_regist_cg` | `u32 unitCount=250`，5 组 |
| 13 | `0x3895D7..0x38A08F` | `_regist_scene` | `u32 unitCount=196`，5 组 |

基础信息段：

| 偏移 | 类型 | 样本值 |
| ---: | --- | --- |
| `0x0011` | `u16le` | `1` |
| `0x0013` | `u32le` | `1280` |
| `0x0017` | `u32le` | `720` |
| `0x001B` | `u8 n + n*u8` | `GameSystem` 的 `array<unsigned char>`；样本 `n=6`, values `[2, 9, 9, 8, 8, 8]` |
| `0x0022` | `string16` | `Hakoniwa` |
| `0x0034` | `string16` | `― ハ コ ニ ワ ―` |
| `0x004C` | `string16` | `アトリエかぐや` |
| `0x005B` | `u8` | `1` |
| `0x005C` | `string16` | `雅章` |
| `0x0062` | `string16` | `支部` |

`0x001B` 的 6 字节数组来自 `417190.c` 开头：

```text
sub_40F010(this + 4, count)
repeat count:
    array[i] = read_u8()
```

它在 RTTI/对象布局上是 `GameSystem` 内的 `array<unsigned char>` 字段，不是字符串，也不是 bitset。当前样本值为 `[2, 9, 9, 8, 8, 8]`。由于伪代码没有给出每个槽位的显式名称，工具导出为 `ConfigBytes` 数组，允许逐项编辑并回封；不要再用不可读的 hex 字符串表达。

这里很有意思的是 0x005B 对应的值，也就是 `StaffFlag` 对应的数值:
| value | func |
| --- | --- |
| `0` | `自定义姓名无效` |
| `1` | `可自定义名` |
| `2` | `可自定义姓` |
| `3` | `可自定义姓名` |


install table 样本前几项：

| file | media |
| --- | --- |
| `scr.arc` | `DVD-ROM` |
| `bgd.arc` | `DVD-ROM` |
| `cg00.arc` | `DVD-ROM` |
| `cg01.arc` | `DVD-ROM` |
| `cg02.arc` | `DVD-ROM` |

3 个 `SettingTag`：

| gate 偏移 | present | tag 起止 | root | 节点数 |
| ---: | ---: | ---: | --- | ---: |
| `0x0002BF` | `1` | `0x0002C0..0x001780` | `サウンド設定` | 109 |
| `0x001780` | `1` | `0x001781..0x0019C3` | `カラー設定` | 10 |
| `0x0019C3` | `1` | `0x0019C4..0x001A56` | `ウィンドウ設定` | 4 |

raw blob 从 `0x001A5E` 开始，长度 `0x384000`。伪代码只按长度整块复制到对象中，目前业务含义未命名；工具必须把它作为可替换 raw payload 暴露，未修改时原样保留，修改时重算长度。

demos 样本：

| name | DemoData 偏移 | commandCount | 前几类命令 |
| --- | ---: | ---: | --- |
| `ロゴ` | `0x385A65` | 15 | `4,5,2,4,5,2,4,5,2,4,5,2,4,5,0` |
| `エンディング` | `0x385BEF` | 131 | `3,4,7,2,4,7,2,4,7,2,...` |

### thumbnails

`thumbnails` 的计数不是 entry 数，而是 unit 数。样本 `unitCount=880`，每个 entry 消耗 11 个 typed unit，所以 entry 数为 `80`。

每个 thumbnail entry：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `strings` | `8 * typed string` | 样本首项为 `cg01`, `cg01.vrm`, 后续多为空 |
| `ints` | `3 * typed int` | 样本常见 `0, 0, 0xFFFFFFFF` |

注意：`417190.c` 这一段反编译出来的局部变量类型较乱，容易误读成 28 个字符串；以实际样本和对象大小 `0x38` 校验，v5.8 样本为 8 个 typed string + 3 个 typed int。

### scene name list

结构为：

```text
u32 count
count * typed string
```

样本 `count=62`，首项为 `小羽玖　シーン①`，与 `SceneLabel` 的 name 集合对应。

### `_regist_cg`

`unitCount` 同样不是 entry 数。样本 `unitCount=250`，实际 5 组。

```text
u32 unitCount
while usedUnits < unitCount:
    typed string groupName
    typed int itemCount
    usedUnits += 2
    repeat itemCount:
        typed string itemName
        typed point position
        typed int value
        usedUnits += 3
```

样本组：`小羽玖`, `永久`, `碧桜`, `朱夏`, `その他`。

### `_regist_scene` v5.8

样本 `unitCount=196`，实际 5 组。

```text
u32 unitCount
while usedUnits < unitCount:
    typed string groupName
    typed int itemCount
    usedUnits += 2
    repeat itemCount:
        typed string itemName
        typed int nestedCount
        usedUnits += 2
        repeat nestedCount:
            typed string sceneName
            usedUnits += 1
```

样本组同样为：`小羽玖`, `永久`, `碧桜`, `朱夏`, `その他`。

## Pattern v5.8

样本 `Pattern` 从 `0x38A08F` 到 `0x3AB8F9`。

| 字段 | 类型 | 样本值 |
| --- | --- | --- |
| `itemCount` | `u32le` | `2351` |
| `items` | `itemCount * PatternItem` | 文件名/资源名/转换项 |
| `intArrayCount` | `u32le` | `2553` |
| `intArrays` | `intArrayCount * (u8 n + n*u32)` | 索引数组 |
| `groupTable1` | `GroupTable` | `80` groups |
| `groupTable2` | `GroupTable` | `26` groups |

`PatternItem`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `name` | `string16` | 文件名或键名 |
| `kind` | `u8` | `0..3` |

按 `kind` 分支：

| kind | 后续字段 |
| ---: | --- |
| `0` | 无 |
| `1` | `u32 count + count * string16` |
| `2` | `string16 subName + u32 x + u32 y` |
| `3` | `string16 subName + u32 value` |

引擎 v5.8 的 `41A220.c` 明确保留 `kind=2` (`ExcPosition`) 与 `kind=3`
(`FileConvert`) 分支；不过本次 `v5.8/params.dat` 样本实际计数为
`kind 0 = 2343`, `kind 1 = 8`, `kind 2 = 0`, `kind 3 = 0`。

`GroupTable`：

```text
u32 groupCount
repeat groupCount:
    string16 name
    u16 indexCount
    indexCount * u32 index
```

v5.8 使用 `u16 indexCount`；旧版本分支有 `u8`，不要混用。

样本首批 item：空字符串、`数値設定.scr`、`プロローグ.scr`、`初期設定.scr`、`スタート.scr`、`スタート（ロード）.scr`、`opening_demo.mpg`、`bgd:\bg_black.bmp`。

## SceneLabel v5.8

样本 `SceneLabel` 从 `0x3AB8F9` 到 EOF。

```text
u32 sceneCount
repeat sceneCount:
    string16 name
    u32 value1
    u32 value2
```

样本 `sceneCount=62`。前几项：

| name | value1 | value2 |
| --- | ---: | ---: |
| `小羽玖　シーン①` | 10 | 4121 |
| `碧桜　シーン①` | 11 | 2666 |
| `永久　シーン①` | 13 | 3248 |
| `小羽玖　シーン②` | 18 | 6868 |
| `碧桜　シーン②` | 22 | 8197 |

`value1/value2` 的业务含义倾向于脚本索引与脚本内位置，但仍需结合 `.scr` op 解析进一步命名。结构上已确认。

## 版本差异记录

版本差异按“基准版本 -> 对照版本”的方式记录：先给摘要表，再为发生布局变化的字段单独建小节，最后写对 LINK6 / 图片合成 / 回封的影响。这样后续更早版本可以继续追加，不需要改写已有结论。

### v5.4 -> v5.5

本节来自 `v5.4` 与 `v5.5` 的伪代码/RTTI 对照，并用两份实际 `params.dat` 做结构解析与回封校验。  
结论：v5.4 仍是旧式 `Pattern::FileNames` / 双字符串 `_regist_scene`，`Pattern` group table 也仍是 `u8 indexCount`;  
真正会影响解析偏移的是 `GameSystem` 前段，v5.4 没有 v5.5+ 的 `V5TailByte`。

| 项 | v5.4 | v5.5 | 影响 |
| --- | --- | --- | --- |
| header | `[SCR-PARAMS]v05.4` | `[SCR-PARAMS]v05.5` | `ParamsDatCodec` 按 header 分支读写 |
| 顶层顺序 | `GameSystem -> Pattern -> SceneLabel` | 同左 | 顶层顺序不变 |
| 文件大小 | `0x276F48` | `0x2729CA` | 体量接近，但表内容不同 |
| 画布尺寸 | `1024x600` | `1024x600` | raw blob 长度同为 `0x258000` |
| LINK6 raw key blob | length `0x258000` | length `0x258000` | 结构仍是 `u32 length + byte[length]` |
| `GameSystem` scalar tail | `4 * u32` 后直接进入 3 个 setting present flag | `4 * u32 + u8 V5TailByte` 后进入 setting present flag | **v5.4 少 1 字节；误读会把第一个 setting flag 当成 tail** |
| install table | 13 项 | 12 项 | 归档枚举必须跟随对应版本 params |
| thumbnails | 87 项 | 90 项 | 结构相同，数量不同 |
| scene name list | 59 项 | 58 项 | 结构相同，数量不同 |
| `_regist_cg` | 5 组，86 项 | 5 组，90 项 | 结构相同，内容数量不同 |
| `_regist_scene` | 5 组，59 项，双字符串 item | 5 组，58 项，双字符串 item | 两版结构兼容 |
| Pattern item | 2204 项；旧版 `FileNames`；映射后 `kind 0=2180`, `kind 1=24` | 1960 项；旧版 `FileNames` | item 布局兼容，资源表数量不同 |
| Pattern int arrays | 1945 组，5825 个引用 | 2016 组 | 数量不同；CG/SP 合成计划必须用同版本 params |
| Pattern groupTable1 | 86 组，660 个 index，`u8 indexCount` | 90 组，`u8 indexCount` | 计数字段宽度相同，内容不同 |
| Pattern groupTable2 | 54 组，86 个 index，`u8 indexCount` | 50 组，`u8 indexCount` | 计数字段宽度相同，内容不同 |
| SceneLabel | 59 项，起点 `0x27697A` | 58 项 | 结构相同，数量不同 |

### `GameSystem` v5.4 / v5.5

v5.4 与 v5.5 的 `GameSystem` 大体一致，差异点在设置树之前：

```text
v5.4: u32 scalar0, scalar1, scalar2, scalar3
      repeat 3: u8 present + SettingTag?

v5.5: u32 scalar0, scalar1, scalar2, scalar3
      u8 V5TailByte
      repeat 3: u8 present + SettingTag?
```

v5.4 样本中 4 个 scalar 后的第一个字节是第一个 setting root 的 present flag，而不是 tail。这个字段错 1 字节后，后面的 raw blob、thumbnail、`_regist_scene` 都会整体错位，典型失败表现是在 `GameSystem` 尾部读出超大 string16 长度并越过 EOF。

v5.4 样本实际边界：

| 段 | 偏移 |
| --- | ---: |
| `GameSystem` end / `Pattern` start | `0x25DCC7` |
| `Pattern` end / `SceneLabel` start | `0x27697A` |
| EOF | `0x276F48` |

### `Pattern` v5.4 / v5.5

v5.4 的 RTTI 同时能看到 `GameScript::Pattern::Group` 与 `GameScript::Pattern::FileNames`，但实际序列化布局仍与 v5.5 的旧式 `FileNames` 分支一致：

```text
u32 itemCount
repeat itemCount:
    string16 name
    u8 fileNameCount
    fileNameCount * string16 fileName
u32 intArrayCount
intArrayCount * (u8 count + count * u32)
groupTable1
groupTable2
```

group table 在 v5.4 / v5.5 都是：

```text
u32 groupCount
repeat groupCount:
    string16 name
    u8 indexCount
    indexCount * u32 index
```

因此 v5.4 不需要新的 CG/SP 合成算法；工具只需要在 params 读写层把 v5.4 归入旧式 `FileNames` 和 `u8 indexCount` 分支。

### v5.4 对 LINK6 / CG-SP 的影响

LINK6 解包 XOR 逻辑不需要因 v5.4 改动。v5.4 的 raw key blob 仍是 `u32 length + byte[length]`，长度为 `0x258000`，也就是 `1024 * 600 * 4`。受影响的是找到 raw blob 前的 `GameSystem` 偏移：必须按 v5.4 省略 `V5TailByte`，否则 key blob 起点会错。

CG/SP 合成方面，v5.4 的 `Pattern` 仍可映射到当前 AST，`CharacterComposer` 可以复用旧式资源名解析分支。需要注意的是 v5.4 的资源引用数量、group table 数量和 index 内容都与 v5.5 不同，合成时必须使用 v5.4 的 `params.dat` 配 v5.4 的 `pic/` 目录，不能跨版本套用。

### v5.5 -> v5.6

本节来自 `v5.5` 与 `v5.6` 的伪代码对照，并用两份实际 `params.dat` 做结构解析和回封校验。结论先写在前面：v5.5 仍然是旧式 `Pattern::FileNames` / 双字符串 `_regist_scene`，与 v5.6 同代；真正的布局差异在 `Pattern` 的 group table，v5.5 的 `indexCount` 仍是 `u8`，v5.6 起改为 `u16`。

| 项 | v5.5 | v5.6 | 影响 |
| --- | --- | --- | --- |
| header | `[SCR-PARAMS]v05.5` | `[SCR-PARAMS]v05.6` | 解析器必须接受 5.5 头，否则启动时直接失败 |
| 顶层入口 | 与 v5.6 同类，均是 `GameSystem -> Pattern -> SceneLabel` | 同左 | 顶层顺序不变 |
| 文件大小 | `0x2729CA` | `0x27280C` | 体量接近，但不是同一份表 |
| 画布尺寸 | `1024x600` | `1024x600` | raw blob 长度同为 `0x258000` |
| LINK6 raw key blob | length `0x258000` | length `0x258000` | key 载荷格式不变，仍是 `u32 length + byte[length]` |
| install table | 12 项 | 14 项 | 归档枚举必须跟随对应版本 params |
| thumbnails | 90 项 | 77 项 | 结构相同：每项 `8 * typed string + 3 * typed int`，数量不同 |
| scene name list | 58 项 | 57 项 | 结构相同，数量不同 |
| `_regist_cg` | 5 组，90 项 | 5 组，77 项 | 结构相同，内容数量不同 |
| `_regist_scene` | 5 组，58 项，双字符串 item | 5 组，57 项，双字符串 item | 5.5 与 5.6 结构兼容 |
| Pattern item | 1960 项；旧版 `FileNames` | 1994 项；旧版 `FileNames` | 5.5 与 5.6 共享旧式 item 布局 |
| Pattern int arrays | 2016 组 | 1865 组 | 数量不同；CG/SP 合成计划必须用同版本 params |
| Pattern groupTable1 | 90 组，`u8 indexCount` | 77 组，`u16 indexCount` | **计数字段宽度不同** |
| Pattern groupTable2 | 50 组，`u8 indexCount` | 62 组，`u16 indexCount` | **计数字段宽度不同** |
| SceneLabel | 58 项 | 57 项 | 结构相同，数量不同 |

### `Pattern` v5.5 / v5.6

v5.5 和 v5.6 使用同一套旧式 `Pattern::FileNames` 布局，不是 v5.7/v5.8 的 `PatternItem` 分支结构。实际 item 布局为：

```text
u32 itemCount
repeat itemCount:
    string16 name
    u8 fileNameCount
    fileNameCount * string16 fileName
u32 intArrayCount
intArrayCount * (u8 count + count * u32)
groupTable1
groupTable2
```

`Pattern` item 本身可以共用旧式文件名映射分支，但 group table 的索引计数字段不同：

```text
v5.5: u32 groupCount; repeat: string16 name + u8  indexCount + indexCount * u32 index
v5.6: u32 groupCount; repeat: string16 name + u16 indexCount + indexCount * u32 index
```

这意味着 5.5 与 5.6 的 CG/SP 合成逻辑可以共用当前 `CharacterComposer` 的旧式资源名解析，不需要再拆一套算法；但 params 读写必须按 header 区分 group table 的计数字段宽度。合成时仍必须使用同版本 `params.dat`，不能拿 5.6 的表去套 5.5 的资源目录。

### `_regist_scene` v5.5 / v5.6

v5.5 与 v5.6 一样使用固定的“两字符串 item”：

```text
u32 unitCount
while usedUnits < unitCount:
    typed string groupName
    typed int itemCount
    usedUnits += 2
    repeat itemCount:
        typed string sceneName
        typed string cgName
        usedUnits += 2
```

因此 5.5 与 5.6 的 `_regist_scene` 回封规则一致，当前工具的 legacy 分支可以直接覆盖两者。这个块不会影响 `CharacterComposer` 的合成算法，但会影响完整 `params.dat` 的读写闭环。

### 对 LINK6 / CG-SP 的影响

LINK6 解包 XOR 逻辑不需要因 v5.5 改动；受影响的是从 `params.dat` 找到并导出 raw key blob 的解析分支。5.5 与 5.6 的 blob 长度相同，都是 `0x258000`，所以工具只需要信任文件中的 length 字段即可。

CG/SP 合成受影响较小：5.5 与 5.6 的 `Pattern` item 主体布局一致，且 `_regist_scene` 也是同一旧式双字符串结构。当前 `CharacterComposer` 的旧式资源名兼容逻辑可同时覆盖 5.5 / 5.6；差异集中在 params 解析/回封阶段的 group table `indexCount` 宽度，以及资源表数量。

### v5.6 -> v5.7

本节来自 `v5.6` 与 `v5.7` 的伪代码对照，并用两份实际 `params.dat` 做结构解析和回封校验。v5.6 的顶层顺序仍是 `GameSystem -> Pattern -> SceneLabel`，但 `Pattern` item 布局比 v5.7 更旧。

| 项 | v5.6 | v5.7 | 影响 |
| --- | --- | --- | --- |
| header | `[SCR-PARAMS]v05.6` | `[SCR-PARAMS]v05.7` | `ParamsDatCodec` 已按 header 分支读写 |
| 顶层入口 | `42C600.c` -> `42A6B0` / `413B80` / `414620` | `42E780.c` -> `42C810` / `413B00` / `414B00` | 顶层顺序不变 |
| 文件大小 | `0x27280C` | `0x3AA340` | v5.6 内容量明显更小，主要来自分辨率/raw blob 和资源表数量 |
| 画布尺寸 | `1024x600` | `1280x720` | raw blob 长度随像素数变化 |
| LINK6 raw key blob | length `0x258000` | length `0x384000` | 结构仍是 `u32 length + byte[length]`，但不能再假设固定 `0x384000` |
| install table | 14 项 | 16 项 | 归档枚举必须跟随对应版本 params |
| thumbnails | 77 项 | 80 项 | CG 缩略图/鉴赏入口数量变化 |
| scene name list | 57 项 | 59 项 | 回想/场景数量变化 |
| `_regist_cg` | 5 组，77 项 | 5 组，80 项 | 结构相同，内容数量不同 |
| `_regist_scene` | 5 组，57 项，双字符串 item | 5 组，59 项，双字符串 item | v5.6 与 v5.7 结构兼容，均不同于 v5.8 |
| Pattern item | 1994 项；旧版 `FileNames`；映射后 `kind 0=1990`, `kind 1=4` | 2206 项；新版 `PatternItem`；`kind 0=2197`, `kind 1=8`, `kind 2=1` | **结构不同**，详见下节 |
| Pattern int arrays | 1865 组，5265 个引用 | 2424 组，9606 个引用 | CG/SP 合成计划必须使用同版本 params |
| Pattern groupTable1 | 77 组，632 个 index | 80 组，615 个 index | CG 分组数量和引用内容都不同 |
| Pattern groupTable2 | 62 组，112 个 index | 26 组，98 个 index | SP/资源分组差异较大，不能跨版本套用 |
| SceneLabel | 57 项 | 59 项 | 结构相同，数量不同 |

### `Pattern` v5.6

v5.6 的 `413B80.c` 使用 RTTI `GameScript::Pattern::FileNames`，不是 v5.7/v5.8 的 `FileNameItem / FileGroup / ExcPosition / FileConvert` 分支结构。实际 item 布局为：

```text
u32 itemCount
repeat itemCount:
    string16 name
    u8 fileNameCount
    fileNameCount * string16 fileName
u32 intArrayCount
intArrayCount * (u8 count + count * u32)
groupTable1
groupTable2
```

其中 `groupTable1/2` 在 v5.6 已经使用 `u16 indexCount`：

```text
u32 groupCount
repeat groupCount:
    string16 name
    u16 indexCount
    indexCount * u32 index
```

伪代码里 `a5 >= 5.6` 分支明确把 group index 数读取为 2 字节；v5.4 / v5.5 已确认仍为 `u8 indexCount`，v5.6 起改为 `u16 indexCount`。

工具内部为了复用现有 JSON AST，会把 v5.6 `FileNames` 映射到 `ParamsPatternItem`：`fileNameCount == 0` 时导出为 `Kind=0`，`fileNameCount > 0` 时导出为 `Kind=1` 且文件名放入 `Strings`。回封时仍按 header 写回 v5.6 原始布局，也就是不会写 v5.7+ 的 `kind` 字节和 `u32 stringCount`。

v5.6 样本中大多数 item 没有附加文件名；少数形如：

```text
name = 律香裸甲大
fileNameCount = 1
fileName[0] = 律香モザイク大.spd
```

### `_regist_scene` v5.6

v5.6 与 v5.7 一样使用双字符串 item：

```text
u32 unitCount
while usedUnits < unitCount:
    typed string groupName
    typed int itemCount
    usedUnits += 2
    repeat itemCount:
        typed string sceneName
        typed string cgName
        usedUnits += 2
```

v5.6 样本首项形如：

| group | sceneName | cgName |
| --- | --- | --- |
| `律香` | `律香　シーン①` | `cg01` |

这说明 v5.8 的嵌套 scene list 是后续新增，不应向下套到 v5.6/v5.7。

### v5.6 对 LINK6 和 CG/SP 的影响

LINK6 解包 XOR 逻辑不需要因 v5.6 改动；受影响的是从 `params.dat` 找到并导出 raw key blob 的解析分支。v5.6 的 blob 长度是 `0x258000`，刚好等于 `1024 * 600 * 4`，而 v5.7/v5.8 是 `1280 * 720 * 4 = 0x384000`。因此工具必须信任文件中的 length 字段，不能把 v5.8 的 raw blob 长度写死。

CG/SP 合成受影响更明显：v5.6 的 `Pattern` item 布局不同，`GroupTable1/2` 数量也与 v5.7 不同，尤其 `groupTable2` 从 62 组变为 v5.7 的 26 组。当前 `CharacterComposer` 消费的是解析后的 AST，因此不需要另写一套合成算法；但必须用 v5.6 的 `params.dat` 配 v5.6 的 `pic/` 资源，不能混用 v5.7/v5.8 的索引表。

### v5.7 -> v5.8

本节来自 `v5.7` 与 `v5.8` 的伪代码对照，并用两份实际 `params.dat` 做结构解析与回封校验。

| 项 | v5.7 | v5.8 | 影响 |
| --- | --- | --- | --- |
| header | `[SCR-PARAMS]v05.7` | `[SCR-PARAMS]v05.8` | `ParamsDatCodec` 已按 header 分支读写 |
| 顶层入口 | `42E780.c` -> `42C810` / `413B00` / `414B00` | `42F160.c` -> `417190` / `41A220` / `41B8E0` | 顶层顺序仍是 `GameSystem -> Pattern -> SceneLabel` |
| 文件大小 | `0x3AA340` | `0x3ABEF7` | 内容量变化，不是格式整体换代 |
| `GameSystem` 结束 / `Pattern` 起点 | `0x3899DC` | `0x38A08F` | v5.7 的 `GameSystem` 可顺序走通，但尾部 `_regist_scene` 结构不同 |
| LINK6 raw key blob | offset `0x177A`, length `0x384000` | offset `0x1A5E`, length `0x384000` | key 载荷格式不变；必须使用对应版本的 `params.dat`，不要混用 raw blob |
| install table | 16 项，`cg07.arc` 不存在 | 17 项，多 `cg07.arc` | `pic export-game` 应按对应 `params.dat` 的 install table 枚举归档 |
| demos | 2 项，命令数 `15,116` | 2 项，命令数 `15,131` | 只影响启动/ED demo 数据 |
| thumbnails | `unitCount=880`, 80 项 | `unitCount=880`, 80 项 | 结构相同：每项 `8 * typed string + 3 * typed int` |
| scene name list | 59 项 | 62 项 | 回想/场景数量变化 |
| `_regist_cg` | `unitCount=250`, 5 组, 80 项 | `unitCount=250`, 5 组, 80 项 | 结构相同，只是角色/CG 名称不同 |
| `_regist_scene` | `unitCount=128`, 5 组, 59 项 | `unitCount=196`, 5 组, 62 项 | **结构不同**，详见下方 |
| Pattern item | 2206 项；`kind 0=2197`, `kind 1=8`, `kind 2=1` | 2351 项；实际 `kind 0=2343`, `kind 1=8` | v5.7 是 v5.8 支持分支的子集；Pattern 主体布局兼容 |
| Pattern int arrays | 2424 组，9606 个引用 | 2553 组，10084 个引用 | 数量变化；CG/SP 合成计划需用对应版本 params |
| Pattern groupTable1 | 80 组，615 个 index | 80 组，609 个 index | CG 组数相同，引用内容不同 |
| Pattern groupTable2 | 26 组，98 个 index | 26 组，108 个 index | SP/资源组数相同，引用内容不同 |
| SceneLabel | 59 项，起点 `0x3A9DAE` | 62 项，起点 `0x3AB8F9` | 结构相同，数量不同 |

### `_regist_scene` v5.7

v5.7 的 `_regist_scene` 不是 v5.8 的嵌套列表。它使用固定的“两字符串 item”：

```text
u32 unitCount
while usedUnits < unitCount:
    typed string groupName
    typed int itemCount
    usedUnits += 2
    repeat itemCount:
        typed string sceneName
        typed string cgName
        usedUnits += 2
```

样本为 `unitCount=128`，5 组：`凛子`, `伽奈`, `清子`, `紬貴`, `その他`。前几项形如：

| group | sceneName | cgName |
| --- | --- | --- |
| `凛子` | `凛子　シーン①` | `cg01` |
| `凛子` | `凛子　シーン②` | `cg02` |
| `伽奈` | `伽奈　シーン①` | `cg20` |

v5.8 则为：

```text
u32 unitCount
while usedUnits < unitCount:
    typed string groupName
    typed int itemCount
    usedUnits += 2
    repeat itemCount:
        typed string itemName
        typed int nestedCount
        usedUnits += 2
        repeat nestedCount:
            typed string sceneName
            usedUnits += 1
```

因此，用 v5.8 的 `ReadRegistScene` 直接读 v5.7 会在第一组第一项处把 `typed string`
误读成 `typed int`，典型失败点是 v5.7 offset `0x3892CA` 附近。

### 对 LINK6 和 CG/SP 的影响

LINK6 解密算法不受 v5.7 / v5.8 结构差异影响：两版都在 `GameSystem` 中保存
`u32 length + byte[length]` raw blob，长度均为 `0x384000`。受影响的是“如何从
params.dat 找到并解析出 raw blob”：工具必须先按 header 进入正确版本分支。
当前 `ParamsDatCodec` 已支持 v5.4 / v5.5 / v5.6 / v5.7 / v5.8 的 header 识别、v5.4/v5.5/v5.6 旧版 `Pattern::FileNames`、v5.4/v5.5 的 `u8 group index count`、v5.6+ 的 `u16 group index count`、v5.4 省略 `V5TailByte`、以及版本化 `_regist_scene`
读取/写回；解包 XOR 逻辑本身不用改。

CG/SP 合成主要依赖 `Pattern.IntArrays` 与 `Pattern.GroupTable1/2`。v5.7 的 Pattern
主体布局与 v5.8 兼容，且 `kind` 是 v5.8 支持集合的子集，所以合成器的资源解析逻辑不需要
为 v5.7 另写一套规则。需要注意的是两版资源索引和 install table 数量不同：v5.8 多
`cg07.arc`，Pattern 计数也不同；合成时必须使用同版本 `params.dat` 与同版本 `pic/`
资源目录。

`_regist_scene` 的差异主要影响回想/场景登记元数据，不直接参与当前
`CharacterComposer` 的 CG/SP 图层合成；但它会阻塞“完整 params 解析/回封”。当前工具
已经补齐 v5.7 分支，`params verify` 和 `params verify-json` 均可 byte-for-byte 闭环。

## 跨文件待回填点

以下项目不是 `params.dat` 的结构阻塞点；它们需要等后续 `.scr`、`message.dat`、LINK 归档、图片/动画资源和 DLL 运行时逻辑继续分析后回填业务语义。

| 待回填项 | 当前已知 | 依赖的后续分析 |
| --- | --- | --- |
| `SceneLabel.value1/value2` 的准确含义 | 每个 scene 为 `string16 name + u32 value1 + u32 value2`；疑似脚本索引和脚本内位置 | `.scr` 文件索引、op 跳转/标签、场景回想调用逻辑 |
| `Pattern` item index 的业务引用关系 | `GroupTable.indices` 指向 `PatternItem` 下标；可结构解析 | `.scr` op 对资源 id/index 的调用方式，资源加载函数 |
| `Pattern.kind=1/2/3` 的业务命名 | 结构分别为 string list、坐标项、转换项 | `.scr` 资源调用、图片定位、文件名转换逻辑 |
| `GameSystem` raw blob | `0x001A5A` 处 `u32 length=0x384000`，后接整块数据；`417190.c` 按 byte-array 复制 | 若后续要给每个字节业务命名，再从运行时使用点继续追踪；结构和回封已不阻塞 |
| `GameSystem` 前段未命名标量 | 已知值如 `u32[4]={3,3,1,3}`、`u8=2` | 设置界面、初始化逻辑、运行时对象字段引用 |
| `SettingTag` 中各 key/value 的效果 | 三棵树已完整解析：声音、颜色、窗口 | 游戏设置保存/读取逻辑，UI 控件与实际变量映射 |
| `DemoData` command 的运行时效果命名 | `type 0..9` payload 布局已确认到可编辑级；`Move/Pos/Wait` 的部分字段仍是暂名 | 启动 logo/ending 播放流程、渲染/声音子系统调用 |
| `thumbnail` 8 个 typed string 的具体槽位 | 每项为 `8 * typed string + 3 * typed int`；首两项常为 cg id / `.vrm` | CG 鉴赏、缩略图显示、`.vrm`/图片资源载入逻辑 |
| `_regist_cg` point/int 字段语义 | 每 item 为 cg 名、坐标、整数值 | CG 鉴赏坐标、差分显示、资源展开后的图片尺寸/位置 |
| `_regist_scene` 与 scene name list 的关系 | 5 组角色/分类映射到 62 个 scene name | 回想模式 UI、`.scr` 入口和 `SceneLabel` 数值对应 |
| install table 的 media 字段实际用途 | 每项 `file + media`，样本 media 均为 `DVD-ROM` | 归档加载、安装检查、介质切换相关代码 |

回填原则：

1. 如果只影响字段命名，不改变二进制布局，直接补充说明即可。
2. 如果发现字段之间存在强约束，例如 index 必须同步更新，要补进“回封规则”。
3. 如果发现 raw blob 可继续拆分，先新增子结构文档；在工具实现中仍保留未知尾部 raw。

## 回封规则

1. 按顶层顺序序列化：header、`GameSystem`、`Pattern`、`SceneLabel`。
2. 所有 `string16` 的 `byteLen` 必须按 UTF-16LE 字节数重算。
3. 所有 typed wrapper 的 type 必须保留：string=`0`，int=`1`，point=`2`。
4. `GameSystem` 的 raw blob 是 `u32 length + byte[length]`，回封时必须重算 length；未主动修改时应 byte-for-byte 保留，主动替换时允许任意 raw bytes。
5. `thumbnail/_regist_cg/_regist_scene` 的 count 是 unit count，不是 entry count；编辑时要按单位重新计算。
6. `DemoCommand.length` 是命令总长度，改 payload 必须同步更新。
7. `Pattern` 的 group indices 指向 `PatternItem` 下标；增删 item 会影响引用。
8. `SceneLabel` 的 name 应与 scene name list / `_regist_scene` 保持一致；`value1/value2` 修改前要先确认与 `.scr` 的关系。

## 当前工具状态

早期 Python params 扫描/报告脚本只作为归档参考；当前可用实现已经迁移到 `Kaguya_YaneKit params`。现有 C# codec 会严格顺序生成 AST，再由 AST 序列化回二进制，并通过 `params verify` / `params verify-json` 做 byte-for-byte 闭环校验。

按本文档，`params.dat` 已具备“全量反汇编为结构化 AST，再无改动汇编回原文件”的工具实现；字段中文/业务名称可继续随 `.scr` 和资源引用关系补完。

## Kaguya_YaneKit 实现状态

`Kaguya_YaneKit params` 已按本结构实现非 GUI 工作流：

- `params dump <params.dat>`：输出结构摘要。
- `params export-json <params.dat> <output.json>`：导出可编辑 JSON AST。
- `params import-json <input.json> <output.dat>`：由 JSON AST 回封。
- `params verify <params.dat>`：二进制解析后直接回封，做 byte-for-byte 校验。
- `params verify-json <params.dat>`：`binary -> JSON -> binary` 校验。
- `params extract-raw <params.dat> <raw.bin>`：导出 `GameSystem` 中的 byte-array payload。
- `params replace-raw <params.dat> <raw.bin> <output.dat>`：替换该 payload，并自动重算长度。

`GameSystem` 中 `u32 length + byte[length]` 的大块数据不是扫描漏项：`417190.c` 中对应逻辑是按长度分配 `array<unsigned char>`，再 `memmove` 复制整块。也就是说在 `params.dat` 结构层它就是显式 raw payload。为了保持和 `.scr` 一样的自由度，工具不会把它锁死，而是以 JSON base64 字段和独立 raw 文件两种形式暴露，允许替换任意字节并回封。
