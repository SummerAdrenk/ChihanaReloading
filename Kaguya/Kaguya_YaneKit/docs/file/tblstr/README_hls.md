# TBLSTR系 HLS/反汇编输出

## HLS 接入原则

项目里的脚本反汇编按引擎系分后端。TBLSTR系使用自己的 `.scr` 容器、`.tbl` 表和 `tblstr.arc` 文本资源，不和 Params系脚本共享调用链说明：

```text
IScriptFamily
  ParamsScrFamily   -> [SCR-Ver5.x] / params.dat / message.dat
  TblstrScrFamily   -> magic0/magic1 .scr / .tbl / tblstr.arc
```

建议输出目录也分开，避免覆盖：

```text
analysis/scr/             原始 .scr
analysis/scr_hls/         SCR 默认高级 HLS
analysis/scr_disasm/      SCR 低级反汇编
analysis/scr_asm/         SCR 低级回编输出
msg/tblstr.txt            TBLSTR 双行文本
```

SCR 工作区只保留 `scr`、`scr_hls`、`scr_disasm`、`scr_asm` 四类目录。TBLSTR 属于 Text Processing，交互模式默认只输出 `msg/tblstr.txt`，不属于 SCR HLS；所有这些目录都只在对应功能实际写入时创建，不在启动时提前铺空目录。结构化 JSON 只作为调试/逆向辅助，CLI 需要显式 `--json` 才会生成。

TBLSTR系 `.scr` 已接入独立后端。启动分析和 `scr decompile` 会按 magic 自动分流：

- Params系 `[SCR-Ver5.x]` 走 `ScrContainerCodec` / `ScrHighLevelDecompiler`。
- TBLSTR系 `0x0A0D0C01 0x05033B0C` 走 `TblstrScrCodec`。

当前 TBLSTR HLS 已接入可读语句回编，输出到同一个 `analysis/scr_hls/`。HLS 默认不再输出行尾 `bytes=` / `raw=` 保真层；回编器会从可读语义语句直接重建 opcode、base operands 和内联字符串。旧版带 `; bytes=` 的 HLS 仍可作为兼容输入读取，但不会作为新输出格式。

```text
.file kind=tblstr_scr_hls
.source "01_op_01h修正.scr"
.magic 0x0A0D0C01, 0x05033B0C
.payload_size 56450

.code

    IF_EQ flags=0x01 0x00000000 == 0x00000001 -> 0x00008133
    TITLE "現役魔法少女"
    MESSAGE speaker=-1 text=0
    LOAD_LAYER adv_back "教室"
    PLAY_BGM "10　ワクワク"
    PLAY_WAVE group=0 slot=1 "テニス　ストローク"
    LOAD_SPRITE adv_sp3 object="M_aquE" pattern="M1_aqu014"
```

CLI：

```text
scr decompile <input.scr> <output.hls.txt>
scr disasm <input.scr> <output.disasm.txt>
scr hls-asm <input.hls.txt> <output.scr>
scr scan-opcodes <input.scr|directory> [output.txt]
scr verify <input.scr>
scr verify-hls <input.scr>
```

`scr verify` 对 TBLSTR系执行 `read -> WriteRaw -> compare`，用于确认解析不会破坏二进制。
`scr verify-hls` 对 TBLSTR系执行 `binary -> HLS -> binary -> compare`。当前实现支持两类行为：

- 不修改 HLS 时，依靠可读语句本身保证字节级一致。
- 修改已覆盖的可读字段时，`scr hls-asm` 会把新值写回 `.scr`；如果某行被改成当前语义写回器不能识别的形式，会直接报错，不会静默沿用旧指令。

当前已覆盖常用数值字段、跳转/比较、消息索引、菜单项、等待、图层/精灵/对象控制、音频/视频/资源名等 TBLSTR SCR 可见字段。为保证无 `bytes=` 回环，HLS 会显式保留少数会影响编码的语义字段，例如 `SET_VALUE ... flags=...`、`PLAY_WAVE group=... slot=...`。

兼容说明：旧 HLS 里的 `PLAY_SOUND`、`STOP_OR_PAUSE`、`PENDING_TEXT_A`、`APPEND_PENDING_TEXT_PAIR` 仍可作为输入回编；新输出统一使用 `PLAY_WAVE`、`STOP_SCRIPT`、`VOICE_GROUP_PREFIX`、`VOICE_GROUP_ENTRY`。

当前 CC 样本验证结果：

```text
scr scan-opcodes workplace/analysis/scr
files: 50
instructions: 35204
issues: 0

全量 scr verify:
ok=50
fail=0

全量 scr verify-hls:
ok=50
fail=0
```

