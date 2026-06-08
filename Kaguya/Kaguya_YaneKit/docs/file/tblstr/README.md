# TBLSTR系文件文档索引

本文档组只记录“使用 `tblstr.arc` / `TBLSTR.ARC` 作为文本资源”的 Kaguya 引擎分支。不要把 Params系的 `params.dat` / `message.dat` 调用链混进本目录。

## 子文档

- [TBLSTR 文本资源容器](README_tblstr.md)
- [TBLSTR系 SCR 容器与分发链](README_scr.md)
- [TBLSTR系 TBL 表文件](README_tbl.md)
- [TBLSTR系 HLS/反汇编输出](README_hls.md)
- [TBLSTR系 opcode 逆向记录](README_op.md)

## TBLSTR系加载路线

当前样本观察到的 TBLSTR系路线：

```text
SCR.ARC -> .scr / .tbl
TBLSTR.ARC -> 文本资源容器
.scr + label/value/readfg 表 -> 引用 TBLSTR 文本
```

因此：

- 初始化识别规则：检测不到 `params.dat` 时按 TBLSTR系准备。
- TBLSTR系 `.scr` 使用本目录记录的容器、opcode schema 和 HLS/反汇编输出。
- `TBLSTR.ARC` 属于 TBLSTR系文本资源容器。
- TBLSTR 文本处理是 TBLSTR系自己的分支：`Text.Tblstr` 下的 `TblstrCodec` / `TblstrTextWriter` / `TblstrScriptLinker` 负责容器解析、name/msg/choice 分类和按 SCR 切分。
- message.dat 的行宽、编码、占位符等逻辑属于通用文本辅助能力，后续应抽到中立模块复用；不能把 TBLSTR 当成 `Text.MessageDat` 的子功能。
- 可以复用“opcode census -> dispatcher -> handler 读参数”的逆向方法。
- 未来应当接入同一个 HLS 框架接口，但仍使用 TBLSTR系自己的容器解析、opcode schema 和文本 linker。

## 当前样本

当前样本来自：

```text
G:\アトリエかぐや\CC\気になる彼女のママは現役魔法少女
```

伪代码导出：

```text
G:\アトリエかぐや\CC\気になる彼女のママは現役魔法少女\project\export-for-ai
```

已确认加载链：

| 函数 | 已确认资源 |
| --- | --- |
| `sub_50D8B0` | `arc/tblstr.arc`, `readfg.idx`, `scr/label.tbl` |
| `sub_509230` / `sub_5094C0` | `scr/value.tbl` |
| `sub_5068C0` | `scr/Globalvalue.tbl`, `g_value.tbl`, `e_value.tbl`, `scr/EventFg.tbl`, `scr/partitionInfo.tbl` |
| `sub_526E00` | 打开 `scr/<name>.scr` 并校验脚本头 |

当前样本封包：

| 文件 | 当前观察 |
| --- | --- |
| `SCR.ARC` | `AF01`，包含 `.scr` 和 `.tbl` |
| `TBLSTR.ARC` | `UF01`，文本资源包；不是 `message.dat` |

## TBLSTR 版本预留

已知 TBLSTR 大致存在三类版本。当前样本是目前见到的最新版本：

| 版本槽位 | magic / 特征 | 当前状态 |
| --- | --- | --- |
| 旧版 A | 待补样本 | 预留 |
| 旧版 B | 待补样本 | 预留 |
| 最新版 | `UF01` | 当前已开始支持导出 |

后续新增旧版时必须单独记录 magic、头部字段和记录表差异，不能把旧版字段混进 `UF01` 最新版说明。

## 当前边界

已经收口：

- `UF01` / `TBLSTR.ARC` 最新版样本可导出、切分、导入、合并和 `verify-text`；未修改记录会复用原始文本字节，以保证 UF01 原样回封。
- TBLSTR系 `.scr` opcode 表、取指边界、base length、内联字符串消费规则已闭合；HLS 已支持 `verify-hls` 和已覆盖可读字段的语义写回，新生成 HLS 不再输出行尾 `bytes=` / `raw=`。
- `.scr -> TBLSTR` 主文本引用链已接入：opcode `19` 区分 name/msg，opcode `10` 区分 choice；已确认的菜单分支控制流会输出真实 `choice-range`。
- TBLSTR 使用独立的 `ini/tblstr_config.ini`，只复用编码、GBK/行宽检查和字节占位符这类通用文本配置，不读取 message.dat 的分支调整和加密配置。行宽修复/检查只作用于 `msg` 行；带人名的对话 msg 自动换行时会补全角空格。

仍需样本或调用链补齐的边界：

- `UF01` / `TBLSTR.ARC` 旧版 A/B 差异；后续必须按版本单独记录，不能混进最新版说明。
- 其他菜单控制流形态的 `choice-range` 验证；当前只输出已经证实的真实范围。
- 字体大小、ruby/ルビ、行内富文本控制码；当前只看到字体渲染层符号，没有证到 TBLSTR SCR opcode 或文本控制码。
- `partitionInfo.tbl` 样本补齐后再解析。
- `readfg.idx` 的业务含义；它不是 `.tbl` 主表。

跨文档的总清单以 [../../reverse_todo.md](../../reverse_todo.md) 为准；本页只保留 TBLSTR 分支入口级边界，不再重复展开 opcode 明细。
