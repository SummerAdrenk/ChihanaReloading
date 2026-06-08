# TBLSTR系 TBL 表文件

## `.tbl` 文件

`.tbl` 是脚本辅助表，不是 opcode 流。

当前已实现解析/回封一致性校验的表：

| 文件 | 读取函数证据 | 二进制结构 | 工具状态 |
| --- | --- | --- | --- |
| `value.tbl` | `sub_52B0D0` 按 CRLF 切行，逐行调用 `sub_5526F0` | 每行文本字节逐字节 bitwise NOT；CRLF 本身不变 | 已解析、已回写校验 |
| `globalvalue.tbl` / `Globalvalue.tbl` | 同 `value.tbl` 路径 | 同上 | 已解析、已回写校验 |
| `label.tbl` | `sub_52ADB0` | `i32 scriptNameLen + scriptName + i32 labelLen + label + i32 targetOffset` 重复到 EOF | 已解析、已回写校验 |
| `eventfg.tbl` / `EventFg.tbl` | `sub_5261D0` | 首字节是 XOR key；后续 payload 按 key 解密后走 CSerialize：`i32 count + cstring + i32 vectorCount` 递归结构 | 已解析、已回写校验 |

当前样本解包目录中没有 `partitionInfo.tbl` 实物；只在 `sub_5068C0` 里看到加载路径。
因此它不计入本轮 `.tbl` 主表收尾，后续拿到样本后单独补。

### `value.tbl` / `globalvalue.tbl`

确认规则来自 `sub_52B0D0` 和 `sub_5526F0`：

```text
repeat line:
    byte[] encoded_name
    0D 0A

decoded_name[i] = ~encoded_name[i]
```

当前样本 `value.tbl` 解出的是脚本局部 value 名，例如 `kaisou_fg`、`result`、`PLAY_MODE`。
`globalvalue.tbl` 解出的是全局 value 名，例如 `%opening_fg`。

### `label.tbl`

确认规则来自 `sub_52ADB0`：

```text
repeat until EOF:
    i32 script_name_byte_len
    byte[script_name_byte_len] script_name_cp932
    i32 label_byte_len
    byte[label_byte_len] label_cp932
    i32 target_offset
```

`target_offset` 目前只按“脚本目标偏移/入口值”处理。它和 TBLSTR 系 `.scr` 指令流的精确关系要等 `.scr` opcode 边界确认后再细命名。

### `EventFg.tbl`

确认规则来自 `sub_5261D0`、`sub_5054B0`、`sub_5051E0`、`sub_5052F0`：

```text
u8 xor_key
byte[] encrypted_payload

payload[i] = encrypted_payload[i] ^ xor_key

i32 character_count
repeat character_count:
    cstring character_name_cp932
    i32 slot_count
    repeat slot_count:
        cstring slot_name_cp932
        i32 event_count
        repeat event_count:
            i32 field0
            i32 field1
            cstring event_name_cp932

i32 kaisou_count
repeat kaisou_count:
    cstring kaisou_name_cp932
    i32 slot_count
    repeat slot_count:
        i32 field0
        cstring slot_name_cp932
        cstring script_name_cp932
```

`field0` / `field1` 是已确认的结构字段，但业务名还没有证死，所以工具不会硬命名。
当前 CC 样本 `eventfg.tbl` 的 key 是 `0xF0`，按上述结构可完整消费到 EOF。

### CLI / 交互输出

CLI：

```text
tbl export <tbl-file|tbl-dir> <output-dir> [--json]
tbl verify <tbl-file|tbl-dir>
```

交互模式不再把 `.tbl` 放在 Text 菜单下单独处理；`.tbl` 跟随 TBLSTR 系 SCR 的 HLS 流程：

- `SCR Processing -> Decompile .scr -> HLS` 会把 `analysis/scr/*.tbl` 同步导出到 `analysis/scr_hls/`。
- `SCR Processing -> Assemble HLS -> .scr` 会把 `analysis/scr_hls/tbl_*.json` 同步重建到 `analysis/scr_asm/`。
- 手动入口保留在 `SCR Processing -> TBL support tables`，用于单独导出、重建、验证。

```text
analysis/scr_hls/tbl_value.txt
analysis/scr_hls/tbl_value.json
analysis/scr_hls/tbl_globalvalue.txt
analysis/scr_hls/tbl_globalvalue.json
analysis/scr_hls/tbl_label.txt
analysis/scr_hls/tbl_label.json
analysis/scr_hls/tbl_eventfg.txt
analysis/scr_hls/tbl_eventfg.json
```

JSON 是重建 `.tbl` 的输入；CLI 也可以显式使用：

```text
tbl export <tbl-file|tbl-dir> <output-dir> --json
tbl import <tbl-json-file|tbl-json-dir> <output-dir>
```

### 本轮收尾状态

当前 CC 样本中实际存在的 `.tbl` 文件已经闭合：

```text
eventfg.tbl      read -> write byte-for-byte OK
globalvalue.tbl  read -> write byte-for-byte OK
label.tbl        read -> write byte-for-byte OK
value.tbl        read -> write byte-for-byte OK
```

剩余项不属于“当前样本 `.tbl` 主表未完成”：

- `partitionInfo.tbl`：只看到加载路径，当前样本未提供文件。
- `readfg.idx`：不是 `.tbl`，只确认缺失时会创建零填充文件，业务含义留到 SCR/TBLSTR 链路阶段。
- `EventFg.tbl` 的 `field0` / `field1`：结构字段已闭合，业务名不硬定。
