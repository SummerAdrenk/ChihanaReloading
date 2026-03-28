## v555_tool 说明

- `#3_YSTBTool_v555.py`
  - `decompile`: ybn -> json + ins.tsv
  - `compile`: json/ins.tsv -> ybn
- `#4_texttwolines_v555.py`
  - `export-ystb`: ystb json -> 双行文本（◇原文 / ◆译文）
  - `apply-ystb`: 双行文本 -> ystb json
  - `repack-ystb`: `apply-ystb + compile`
- `#5_rubyclean_v555.py`
  - 清理 ◆ 译文行里的 ruby/注音标记，保留基文
  - 支持:
    - `@ruby基文@注音@`
    - `《基文｜注音》`
    - `≪基文／注音≫`

## USE

```powershell
# 1) 提取
python #3_YSTBTool_v555.py decompile "ysbin_dec" "v550_dump" --source-encoding cp932

# 2) 导出双行
python #4_texttwolines_v555.py export-ystb "v550_dump" "v550_txt"

# 3) 清理注音（只清理◆译文行）
python #5_rubyclean_v555.py "v550_txt" "v550_txt_clean"

# 4) 写回 JSON
python #4_texttwolines_v555.py apply-ystb "v550_dump" "v550_txt_clean" "v550_json_trans"

# 5) 回封
python #3_YSTBTool_v555.py compile "ysbin_dec" "v550_json_trans" "v550_ybn_new" --source-encoding cp932 --text-encoding gbk --encoding-errors strict
```