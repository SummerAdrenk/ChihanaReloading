# Kaguya_YaneKit 架构文档

目标: 逐步完成 Kaguya/Yane 引擎资源格式、脚本、文本、图片和归档工作流的结构化解析与可回封编辑。

## 分层

```text
Kaguya_YaneKit
  /scr                源码根目录
    Program.cs        CLI 入口
    /App              CLI / GUI 入口层, 命令分发, 运行时上下文
    /Core             通用验证、二进制读写、JSON 工具
    /Scene            .scr 容器、bytecode、SCRASM v2 文本 IR、编译验证
    /Message          message.dat 编解码 / 占位符配置 / .scr 联动拆分合并
    /Formats
      /Archive        LINK 档案 (LINK3/4/5/6) 解包/打包
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

只要找到 `params.dat`, 启动上下文会立即解析它, 并把 `GameSystem.RawBlob` 暴露为 LINK 加密 key.  
后续 `link extract` 不应再孤立处理归档; 遇到 `entryFlags & 4` 的条目时, 默认使用这个 key 解密.  
只有显式 `--no-decrypt` / `--raw` 时才导出原始 payload, 主要用于 byte-for-byte 回封验证.

`KaguyaRuntimeContext` 只解析并保存 workDirectory 路径, 不在普通 CLI 启动时创建目录。交互模式进入后会调用 `WorkspacePaths.EnsureDirectories()` 创建完整工作树; 具体命令也只在需要写输出时创建目标目录。

## GUI

当前使用 **Avalonia UI 11** (code-only, 无 XAML), 从控制台 app 在 STA 线程上启动窗口.

已实现:
- **SP Viewer** - 立绘查看器, 支持角色/差分/叠加层/背景选择, 预览合成, 单张/批量导出
- 可折叠面板: 角色 / 差分 / Overlay / 背景 / 自由背景
- 内置背景: 扫描 `pic/bgd` 和 `pic/BG` 下的 PNG, 兼容 `BG*` 背景命名
- 自由背景: 用户选择文件夹, 按宽高比过滤或裁剪超尺寸图, 路径持久化

格式解析逻辑全部在 `/Formats` 层, GUI 仅负责展示和交互, 不直接处理二进制格式.  
如果未来需要换前端框架, 格式层不受影响.

## CLI 命令

```text
Kaguya_YaneKit link list|extract|pack6|repack6|verify ...
Kaguya_YaneKit scr disasm|asm|verify|verify-text|dump ...
Kaguya_YaneKit msg export|import|verify|verify-text|dump|map|split|merge ...
Kaguya_YaneKit params dump|export-json|import-json|verify|verify-json|extract-raw|replace-raw ...
Kaguya_YaneKit pic sort|convert|repack|repack-fix|restore|restore-with-replenish|export-game ...
Kaguya_YaneKit character compose ...
```

无参数启动时进入交互式菜单模式 (`InteractiveSession`).

## 交互式菜单

```text
Main Menu
├── 1. Archive Unpack      解包 .arc 档案到 link6_unpack/
├── 2. Archive Pack        从 link6_pack/ 子目录打包为 .arc
├── 3. Message Processing  message.dat 导出/导入/按脚本拆分/合并
├── 4. Params Processing   params.dat JSON 导出/导入, RawBlob 提取/替换
├── 5. SCR Processing      .scr 反汇编/汇编 (支持自定义编码)
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

当前采用两层模型:

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

## SCRASM v2 文本 IR

当前反汇编输出使用 SCRASM v2 格式, 所有已知 opcode `1..28` 都有助记符形式:

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
