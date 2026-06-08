# Kaguya_YaneKit 架构文档

目标: 逐步完成 Kaguya/Yane 引擎资源格式、脚本、文本、图片和归档工作流的结构化解析与可回封编辑。

## 分层

```text
Kaguya_YaneKit
  /scr                源码根目录
    Program.cs        CLI 入口
    /App              CLI / GUI 入口层, 命令分发, 运行时上下文
    /Core             通用验证、二进制读写、JSON 工具
    /Script
      /Params         Params系 .scr 容器、bytecode、SCR-HLS 高级 IR、SCRASM v2 低级 IR、编译验证
      /Tblstr         TBLSTR系 .scr 容器、opcode schema、HLS/反汇编输出
    /Text
      /MessageDat     message.dat 编解码 / 占位符配置 / .scr 联动拆分合并
      /Tblstr         tblstr.arc / TBL support tables 文本资源解析
    /Formats
      /Archive        LINK 档案 (LINK3/4/5/6) 与 AF01 档案解包/打包
      /Params         params.dat 解析/序列化
      /Picture        图片格式 (AP/AP-0/AP-2/AP-3/BMP/ANM) 分拣/转换/重打包/还原
      /Character      CG/SP 立绘合成
      /Pe             exe/dll 分析 (TBD)
    /Gui              Avalonia UI 查看器 (SP Viewer)
  /docs               分析文档和格式说明
  /ini                message.dat处理默认配置
```

## 启动上下文

工具启动时先建立 `KaguyaRuntimeContext`:

```text
gameRoot       游戏根目录, 默认当前进程目录; 也可用 --game-root 指定
workDirectory  工作目录路径, 默认 <tool>/workplace; 也可用 --workdir 指定
params.dat     默认从 gameRoot/params.dat 或当前目录探测; 也可用 --params 指定
```

只要找到 `params.dat`, 启动上下文会立即解析它, 并把 `GameSystem.RawBlob.LinkXorKeyBase64` 解码后的 bytes 暴露为 LINK 加密 key.  
后续 `link extract` 不应再孤立处理归档; 遇到 `entryFlags & 4` 的条目时, 默认使用这个 key 解密.  
只有显式 `--no-decrypt` / `--raw` 时才导出原始 payload, 主要用于 byte-for-byte 回封验证.

`KaguyaRuntimeContext` 只解析并保存 workDirectory 路径, 不在普通 CLI 启动时创建目录。交互模式进入后会调用 `WorkspacePaths.EnsureDirectories()`，但它现在只确保工作根目录存在；`analysis/params`、`analysis/scr`、`analysis/scr_hls`、`archive_unpack`、`pic` 等功能目录都由对应功能在真正写入时按需创建。

交互模式的启动分析先识别引擎族：

- 检测到 `params.dat`：按 Params系处理，已有 `analysis/params/params.json` 时跳过 params 导出，已有 `analysis/scr/*.scr` 时跳过 scr.arc 解包，已有 `analysis/scr_hls/*.hls.txt` 时跳过 HLS 高级解析。
- 检测不到 `params.dat`：默认按 TBLSTR系处理，启动阶段只做 `SCR.ARC` 解包准备；在 TBLSTR opcode schema 未稳定前，不自动调用旧 `[SCR-Ver5.x]` HLS。

所有启动分析都是非覆盖式的。手动菜单命令仍可显式重新生成对应产物。

## GUI

当前使用 **Avalonia UI 11** (code-only, 无 XAML), 从控制台 app 在 STA 线程上启动窗口.

已实现:
- **SP Viewer** - 立绘查看器, 支持角色/差分/叠加层/背景选择, 预览合成, 单张/批量导出
- 可折叠面板: 角色 / 差分 / Overlay / 背景 / 自由背景
- 内置背景: 扫描 `pic/bgd` 和 `pic/BG` 下的 PNG, 兼容 `BG*` 背景命名
- 自由背景: 用户选择文件夹, 按宽高比过滤或裁剪超尺寸图, 路径持久化

SP Viewer 的数据源分成两个同级分支：

- Params 系：从 `params.dat` 的 `Pattern.IntArrays` 生成 CG/SP 合成计划。
- TBLSTR 系：从 `analysis/scr/*.scr` 扫描 `LOAD_SPRITE*`、`SET_SPRITE_POS`、清层/重置等 ADV SP 指令，生成脚本实际出现过的立绘快照。

封包角色不能混用：`SP_` 是立绘/表情资源，`BG_` 是背景资源，`CG_`/`CGW` 是事件 CG，`PARTS` 是 UI/零件资源。TBLSTR 的 SP Viewer 只用 `SP_` 解析立绘，背景列表单独扫描 `pic` 下的 `bg*` 目录。

格式解析逻辑全部在 `/Formats` 层, GUI 仅负责展示和交互, 不直接处理二进制格式.  
如果未来需要换前端框架, 格式层不受影响.

## CLI 命令

```text
Kaguya_YaneKit link list|extract|pack6|repack6|verify ...
Kaguya_YaneKit scr decompile|hls-asm|verify-hls|disasm|asm|verify|verify-text|dump ...
Kaguya_YaneKit msg export|import|verify|verify-text|dump|map|split|merge ...
Kaguya_YaneKit params dump|export-json|import-json|verify|verify-json|extract-raw|replace-raw ...
Kaguya_YaneKit pic sort|convert|repack|repack-png|repack-fix|restore|restore-with-replenish|export-game ...
Kaguya_YaneKit character compose ...
```

无参数启动时进入交互式菜单模式 (`InteractiveSession`).

## 交互式菜单

```text
Main Menu
├── 1. Archive Unpack      解包 .arc 档案到 archive_unpack/
├── 2. Archive Pack        从 archive_pack/ 子目录打包为 .arc
├── 3. Params Processing   params.dat JSON 导出/导入, RawBlob 提取/替换
├── 4. SCR Processing      .scr HLS 高级解析/回编，低级 SCRASM 调试；TBLSTR系走独立后端
├── 5. Text Processing     文本资源处理: message.dat / TBLSTR（TBLSTR 输出到 msg/）
├── 6. Picture Processing  图片分拣/转换/重打包/还原
├── 7. Character Compose   CG/SP 立绘合成, SP Viewer GUI
├── 8. PE Processing       (TBD)
└── 0. Exit
```

## .scr 编辑原则

`.scr` 必须具有最大自由度:

- 可以任意删除/插入/修改指令
- 可以修改 opcode 和 payload
- 可以保留未知尾部 bytes
- 指令长度根据操作数自动重新计算
- `[SAVE]` / `[LAYER]` offset table 自动重新计算
- 不能落到指令边界的 offset table 项会作为 raw uint 保留

当前采用两层模型，并默认在交互工作流中输出 HLS 高级 IR：

```text
ScrFileDocument        外层容器: [SCR-Ver] / codeSize / bytecode / SAVE / LAYER / tail
ScriptDocument         可编辑代码: label / comment / instruction / tail
```

底层 instruction 保持:

```text
u16 opcode
u16 instrLen
byte[instrLen - 4] body
```

编辑器可以在这个层面做无约束编辑;  
语义层 opcode 解释会作为附加视图, 而不是硬限制.

## SCR-HLS 默认高级 IR

交互模式启动分析和 SCR 菜单的默认入口会输出/读取 `analysis/scr_hls/*.hls.txt`。HLS 是保守高级 IR：它提升已经由样本和 handler 证据确认的字段，例如 `text/menu/update/pattern_layer`，但继续保留显式标签和跳转，不猜测 `if/else/while/switch`。

对应 CLI：

```text
Kaguya_YaneKit [--params params.dat] scr decompile <input.scr> <output.hls.txt> [--read-encoding cp932] [--params-json params.json]
Kaguya_YaneKit scr hls-asm <input.hls.txt> <output.scr> [--write-encoding cp932]
Kaguya_YaneKit [--params params.dat] scr verify-hls <input.scr> [--read-encoding cp932] [--write-encoding cp932] [--params-json params.json]
```

## SCRASM v2 文本 IR

SCRASM v2 是低级调试/回退格式, 所有已知 opcode `1..28` 都有助记符形式:

```text
; Kaguya_YaneKit SCRASM v2
.header [SCR-Ver5.3]
.code
@loc_00000000:
bgm track=81
wait flags=0 value=30 extra=[]
if_true flags=1 value=12 @loc_00000120
program flags=1 id=19 name=restart_point_dispatch
title "Scene Title"
voice 120 121
update_layer layer=0 ref=4294967295 extra=[1,0,128,0,0]
text cmd=0 arg08=0 arg0c=81 payload=[255,255,255] tail=0
.save
@loc_00000029
.layer
0x0000000F
```

`name=` 字段仅作为可读性注释, 汇编器以数值操作数为准.

`.save` / `.layer` 支持两种写法:

- `@label`: 编译时按 label 重新计算 offset
- `0x00000000`: 保留原始 offset 值

跳转类 opcode (`jump` / `if_true` / `if_false` / `call` / `save`) 的目标地址:

- 能定位到指令边界时显示为 `@label`
- 不能可靠定位时保留为原始数据, 避免错误重写

这保证了最大自由度: 能安全语义化的地方语义化, 不能安全语义化的地方保持 raw, 不牺牲 round-trip.
