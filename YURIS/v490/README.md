## YSTB 工具说明

### 功能

- `YSTB_GuessXorKey.py`
  - 用于猜密钥
- `YSTB_XOR.py`
  - `dec`: 用于解密ae解包的ybn文件(需要key.txt，默认输入文件夹为ysbin，默认输出文件夹为ysbin_dec)
  - `enc`: 用于加密解密的ybn文件(默认输入文件夹为ysbin_dec，默认输出文件夹为ysbin_enc)
- `ystb_tool.py`
  - `decompile`: 把 `YSTB .ybn` 提取成 `json + ins.tsv`
  - `compile`: 把编辑后的 `json/ins.tsv` 回封成 `.ybn`
- `texttwolines.py`
  - `export-ystb`: 把 `json` 导出为双行文本（`◇原文 / ◆译文`）
  - `apply-ystb`: 把双行文本写回 `json`
  - `repack-ystb`: `apply-ystb + compile` 一条龙
- `rubyclean.py`
  - 清理双行文本里 `◆` 译文行的 ruby 标记，只保留 basic

### 常用命令

```powershell
# 猜密钥
python YSTB_GuessXorKey.py xxx.ybn

# 解密ybn
python YSTB_XOR.py dec

# 加密ybn
python YSTB_XOR.py enc
```

```powershell
# 单文件提取
python ystb_tool.py decompile "ysbin_dec\yst00139.ybn" "ysbin_dec_dump"

# 目录批量提取
python ystb_tool.py decompile "ysbin_dec" "ysbin_dec_dump"
```

```powershell
# 双行文本导出
python texttwolines.py export-ystb "ysbin_dec_dump" "ysbin_dec_dump_txt"

# 清理 ruby : 《basic｜ruby》
python rubyclean.py "ysbin_dec_dump_txt" "ysbin_dec_dump_txt_clean"
```

```powershell
# 双行写回 json
python texttwolines.py apply-ystb "ysbin_dec_dump" "ysbin_dec_dump_txt_trans" "ysbin_dec_dump_trans"

# 回封ybn
python ystb_tool.py compile "ysbin_dec" "ysbin_dec_dump_trans" "ysbin_dec_new" --source-encoding cp932 --text-encoding gbk --encoding-errors strict

# 或者你也可以直接回封
python texttwolines.py repack-ystb "ysbin_dec" "ysbin_dec_dump" "ysbin_dec_dump_txt_trans" "ysbin_dec_new" --temp-json-dir "ysbin_dec_dump_trans" --source-encoding cp932 --text-encoding gbk --encoding-errors strict
```

### 说明

- `repack-ystb` 只回封翻译目录里存在同名 `.txt` 的文件
- 如果遇到编码失败（`strict`），会直接报错并停止当前文件回封
- 对于部分重叠文本块，工具使用固定长度安全写回(就截断，不过你正常文本别怕，遇不到这个问题)，避免破坏结构
