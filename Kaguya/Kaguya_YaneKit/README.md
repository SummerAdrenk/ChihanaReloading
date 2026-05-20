# Kaguya_YaneKit

>- `Kaguya_YaneKit` 是面向 `アトリエかぐや` 系列引擎资源的综合性分析、解包、编辑与回封工具。
>- 当前工程定位不是单一游戏补丁工具，而是可扩展的格式工具箱：以 `params.dat`、LINK archive、脚本、文本、图片和 CG/SP 合成为核心，逐步覆盖不同 `[SCR-*]` 版本与不同作品样本。

## 1. 工具信息

| 项目 | 内容 |
| --- | --- |
| 工具名 | `Kaguya_YaneKit` |
| 目标引擎 | `アトリエかぐや` 使用的 Yane/Kaguya 系资源格式 |
| 当前状态 | Ver1.0.0 |
| 当前重点覆盖 | BARE＆BUNNY 分社样本，已分析到 `params.dat` `[SCR-PARAMS]v05.4` - `[SCR-PARAMS]v05.8` |
| 运行环境 | `.NET 8` / `net8.0-windows` |
| 主要能力 | LINK 解包/回封、`params.dat` 解析/回封、`message.dat` 导入导出、`.scr` 反汇编/汇编、图片格式转换/回封、CG/SP 合成 |

详细格式说明见：

- [docs/README_formats.md](docs/README_formats.md)
- [docs/file/README_params_dat.md](docs/file/README_params_dat.md)
- [docs/archive/README_link.md](docs/archive/README_link.md)
- [docs/file/README_scr.md](docs/file/README_scr.md)
- [docs/file/README_message_dat.md](docs/file/README_message_dat.md)
- [docs/pic/README_ap.md](docs/pic/README_ap.md)
- [docs/pic/README_anm.md](docs/pic/README_anm.md)
- [docs/PE/README_dll.md](docs/PE/README_dll.md)

## 2. Usage

无参数启动时进入交互菜单：

```powershell
Kaguya_YaneKit.exe
```

也可以使用命令行子命令：

```powershell
Kaguya_YaneKit.exe [global options] <command> [args]
```

全局参数：

| 参数 | 说明 |
| --- | --- |
| `--game-root <dir>` | 指定游戏根目录；工具会尝试从其中自动定位 `params.dat` |
| `--workdir <dir>` | 指定工作目录；未指定时运行期默认使用 `<tool>/workplace` |
| `--params <params.dat>` | 显式指定 `params.dat`，优先级高于自动搜索 |
| `--help` | 显示帮助 |
| `--self-test` | 运行轻量自检入口 |

### 常用命令

脚本：

```powershell
Kaguya_YaneKit.exe scr disasm <input.scr> <output.disasm.txt> [--read-encoding cp932]
Kaguya_YaneKit.exe scr asm <input.disasm.txt> <output.scr> [--write-encoding cp932]
Kaguya_YaneKit.exe scr verify <input.scr>
Kaguya_YaneKit.exe scr verify-text <input.scr> [--read-encoding cp932] [--write-encoding cp932]
Kaguya_YaneKit.exe scr dump <input.scr>
```

文本：

```powershell
Kaguya_YaneKit.exe msg export <message.dat> <message.txt> [--read-encoding cp932] [--ini config.ini] [--no-workflow]
Kaguya_YaneKit.exe msg import <message.dat> <message.txt> <output.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini] [--encrypt true|false] [--xor-key FF] [--no-workflow]
Kaguya_YaneKit.exe msg verify <message.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini]
Kaguya_YaneKit.exe msg verify-text <message.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini]
Kaguya_YaneKit.exe msg dump <message.dat> [--read-encoding cp932] [--ini config.ini]
Kaguya_YaneKit.exe msg map <message.dat> <scr-dir> <output.json> [--read-encoding cp932] [--ini config.ini] [--no-workflow]
Kaguya_YaneKit.exe msg split <message.dat> <scr-dir> <output-dir> [--read-encoding cp932] [--ini config.ini] [--no-workflow]
Kaguya_YaneKit.exe msg merge <base-message.txt> <split-dir> <output-message.txt>
```

`params.dat`：

```powershell
Kaguya_YaneKit.exe params dump <params.dat>
Kaguya_YaneKit.exe params export-json <params.dat> <output.json>
Kaguya_YaneKit.exe params import-json <input.json> <output.dat>
Kaguya_YaneKit.exe params verify <params.dat>
Kaguya_YaneKit.exe params verify-json <params.dat>
Kaguya_YaneKit.exe params extract-raw <params.dat> <raw.bin>
Kaguya_YaneKit.exe params replace-raw <params.dat> <raw.bin> <output.dat>
```

图片：

```powershell
Kaguya_YaneKit.exe pic sort <source-dir> <work-dir>
Kaguya_YaneKit.exe pic convert <work-dir>
Kaguya_YaneKit.exe pic repack <work-dir>
Kaguya_YaneKit.exe pic repack-fix <fix-dir>
Kaguya_YaneKit.exe pic restore <work-dir> <output-dir>
Kaguya_YaneKit.exe pic restore-with-replenish <work-dir> <output-dir> [-exclude bmp,ap2]
Kaguya_YaneKit.exe pic export-game <game-root> <work-dir>
```

CG/SP 合成：

```powershell
Kaguya_YaneKit.exe character compose <pic-dir> [output-dir]
```

LINK archive：

```powershell
Kaguya_YaneKit.exe link list <archive.arc>
Kaguya_YaneKit.exe link extract <archive.arc> <output-dir> [--params params.dat] [--no-decrypt|--raw]
Kaguya_YaneKit.exe link verify <archive.arc>
Kaguya_YaneKit.exe link pack6 <input-dir> <output.arc> [--name archiveName] [--flags 0] [--recursive]
Kaguya_YaneKit.exe link repack6 <input-dir> <_link_manifest.json> <output.arc>
```

### workplace 工作目录

`--workdir` 指向工具运行时的工作目录。交互流程和部分批处理流程会在其中生成中间产物、分析产物和回封输入输出。当前约定结构如下：

```text
{workdir}/
  analysis/
    params/          params.dat 导出的 JSON / 分析文件
    scr/             从 scr.arc 解包出的 .scr
    scr_disasm/      .scr 反汇编文本
    scr_asm/         重新汇编后的 .scr
  link6_unpack/      LINK6 解包输出
  link6_pack/        LINK6 打包输入/临时区
  pic/               图片分类、转换、修图、回封工作区
  character/         CG/SP 合成输出
  msg/               message.dat 导出、拆分、合并工作区
    _split_out/      按脚本拆分后的文本
```

注意：`workplace` 是运行期工作目录，不是编译时必须存在的目录。需要隔离不同游戏或不同版本样本时，应显式指定不同的 `--workdir`。

## 3. 支持的文件版本

| 文件/格式 | 已确认版本或魔数 | 当前工具状态 |
| --- | --- | --- |
| `params.dat` | `[SCR-PARAMS]v05.4` / `v05.5` / `v05.6` / `v05.7` / `v05.8` | 支持解析、JSON 导出/导入、二进制回封、raw blob 替换；横向差异见 [README_params_dat.md](docs/file/README_params_dat.md) |
| `.scr` | `[SCR-Ver5]` / `[SCR-Ver5.1]` / `[SCR-Ver5.2]` / `[SCR-Ver5.3]` | 支持反汇编、汇编、verify、dump；当前主样本以 `[SCR-Ver5.3]` 为主 |
| `message.dat` | `[SCR-MESSAGE]ver4.0` | 支持导出、导入、校验、脚本映射、按脚本拆分/合并 |
| LINK archive | `LINK6` | 已作为主路径支持 list/extract/verify/pack6/repack6；`LINK3/4/5` 有读取分支但仍按样本验证程度保守处理 |
| AP 图片 | `AP` / `AP-0` / `AP-2` / `AP-3` | 支持提取、PNG 转换、回封；细节见 [README_ap.md](docs/pic/README_ap.md) |
| BMP 图片 | BMP | 支持常规转换/恢复流程 |
| ANM 动画 | `AN00` / `AN10` / `AN20` / `AN21` | 支持提取和回封；`AN20` 可读 mode 3/4，编辑回封统一写入引擎支持的 mode 4 LZSS，不追求字节级还原 |
| APS | APS | 已记录格式线索，但缺少独立样本时不作为强保证格式宣称 |
| DLL/EXE 逆向记录 | `Graphics.dll` / `RenderDX.dll` / `Sound.dll` / `Start.exe` | 作为引擎链路、hook 点和运行时行为说明文档维护，不属于本工具直接回封的资源格式 |

版本支持原则：

- README 只列已经有实现或样本证据支撑的范围。
- 新增更早版本时，优先补充横向差异表，而不是继续写“某版本对某版本”的线性对比。
- 对于有读取分支但缺少真实样本闭环的格式，文档必须标明验证程度。

## 4. TEST

>#  `#アトリエかぐや BARE＆BUNNY`
>
>- `ばくあね ～弟しぼっちゃうぞ！`
>- `なまイキ ～生粋荘へようこそ！～`
>- `CHU×ペット`
>- `ハラミタマ`
>- `えろゼミ ～エッチにヤルきにABC～`
>- `ばくあね2 弟いっぱいしぼっちゃうぞ！`
>- `しごカレ ～エッチな女子大生とドキ×2ラブレッスン!!`
>- `Love×Holic ～魅惑の乙女と白濁カンケイ～`
>- `姉ちゃんのススメ ～お姉ちゃんのイタズラ性生活～`
>- `ままごと ～ままとないしょのえっちしましょ～`
>- `Mama×Holic ～魅惑のママと甘々カンケイ～`
>- `ねぇねぇ姉`
>- `アネトモ`
>- `Bunny’s ママ代行サービス`
>- `Pure×Holic～純潔乙女と婚姻カンケイ！？～`
>- `おっぱいでかいナースとイチャコラエロ×２入院生活！？`
>- `Hakoniwaハコニワ`
>- `ママトモ`[未发布]

测试含义: 这些作品是当前格式分析和工具流程的主要样本池。单个命令是否对某一作完整闭环，仍以对应格式文档和实际 verify/回封测试记录为准。
