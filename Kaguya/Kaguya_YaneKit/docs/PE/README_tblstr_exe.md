# TBLSTR 系 EXE 字符串与 PE 编辑

本文记录 TBLSTR 系主程序 `GAME_SYS_Crack.exe` 的 PE 逆向结论。它和此前 `README_dll.md` 记录的 Params 系运行时 DLL/hook 不是同一个问题；这里关注的是主程序自身内置的可编辑字符串与后续回写方案。

## 当前样本

- 样本：`G:\アトリエかぐや\CC\気になる彼女のママは現役魔法少女\GAME_SYS_Crack.exe`
- 格式：PE32，`ImageBase = 0x00400000`
- `SectionAlignment = 0x1000`
- `FileAlignment = 0x200`
- `SizeOfImage = 0x003F0000`

Section 表：

| section | RVA | VirtualSize | Raw |
|---|---:|---:|---:|
| `.text` | `0x00001000` | `0x002BA912` | `0x00000400 + 0x002BAA00` |
| `.rdata` | `0x002BC000` | `0x0008D0E8` | `0x002BAE00 + 0x0008D200` |
| `.data` | `0x0034A000` | `0x00026570` | `0x00348000 + 0x00021200` |
| `_RDATA` | `0x00371000` | `0x000011E0` | `0x00369200 + 0x00001200` |
| `.fptable` | `0x00373000` | `0x00000080` | `0x0036A400 + 0x00000200` |
| `.rsrc` | `0x00374000` | `0x0005BFA0` | `0x0036A600 + 0x0005C000` |
| `.reloc` | `0x003D0000` | `0x0001F658` | `0x003C6600 + 0x0001F800` |

Section header 末尾在 `0x350`，第一个 section raw offset 是 `0x400`，中间还剩 `0xB0` 字节，足够追加新的 section header。因此当前样本可以采用“新增 section”方案，不必强行覆盖现有 section。

## 已确认的内置字符串

TBLSTR 系 exe 内部确实有大量可编辑内容，主要集中在 `.rdata`，编码按 CP932/Shift-JIS 读取。粗扫结果中 `.rdata` 有数百条日文字符串，其中包括：

- TBLSTR 资源路径：`arc/tblstr.arc`、`scr/value.tbl`、`scr/label.tbl`、`scr/Globalvalue.tbl`、`scr/EventFg.tbl`、`scr/partitionInfo.tbl`
- TBLSTR/脚本错误文本：`文字列アーカイブファイルが見つかりません`、`文字列インデックスの準備ができていません`、`文字列テーブルアクセスの為のアドレスが不正です`
- 消息读取错误：`メッセージIDの読み込みに失敗`、`メッセージのレングス読み込みに失敗`、`メッセージの読み込みに失敗`
- 角色/默认名：`ルビー`、`アクア`、`カガミ`、`ダイヤ`、`太郎`
- 选项标题：`＜選択肢＞`
- 启动/配置 UI：`起動設定`、`ウィンドウサイズを最大化して起動することはできません`、`ゲームの起動に失敗しました`、`解像度の変更に失敗しました`
- 内置资源名：`wav/常駐/カーソル音１.ogg`、`wav/常駐/キャンセル音.ogg`、若干 `.prs` 过渡资源名

这类文本不属于外部 `tblstr.arc`，需要 PE 层单独处理。

## 引用形态

样本里大量 `.rdata` 字符串由 `.text` 中的 32-bit 绝对 VA 立即数引用。例如：

| 字符串 VA | 文本 | 引用位置 |
|---:|---|---|
| `0x006BEA4C` | `文字列アーカイブファイルが見つかりません` | `.text:0x00126B74` |
| `0x006BEA78` | `文字列インデックスの準備ができていません` | `.text:0x0010DF4A` |
| `0x006BEAA8` | `%で囲まれているデフォルトの名前と一致しない名前が使用されています` | `.text:0x00122D97`, `.text:0x00127A6E`, `.text:0x0012D687` |
| `0x006BEB1C` | `メッセージIDの読み込みに失敗` | `.text:0x00123C42` |
| `0x006BEB3C` | `メッセージのレングス読み込みに失敗` | `.text:0x00123B0B` |
| `0x006C045C` | `ルビー` | `.text:0x000EC3A5`, `.text:0x00131AE1`, `.text:0x00131B5F` |
| `0x006C046C` | `アクア` | `.text:0x000EC3B6`, `.text:0x00131B9C`, `.text:0x00131C1A` |
| `0x006C047C` | `カガミ` | `.text:0x000EC3E9`, `.text:0x00131C57`, `.text:0x00131CD5` |
| `0x006C0488` | `ダイヤ` | `.text:0x000EC419`, `.text:0x00131D12`, `.text:0x00131D90` |
| `0x006C1418` | `太郎` | `.text:0x00142286` |

从反编译看，角色名初始化是直接把 `.rdata` 字符串拷贝进全局 `std::string`：

```cpp
sub_4F4E70(&unk_74C1C0, &unk_6C045C, 6u); // ルビー
sub_4F4E70(&byte_74C1D8, &unk_6C046C, 6u); // アクア
sub_4F4E70(&byte_74C1F0, &unk_6C047C, 6u); // カガミ
sub_4F4E70(&byte_74C208, &unk_6C0488, 6u); // ダイヤ
```

这里除了指针，还有长度立即数 `6u`。如果翻译后字节数变化，仅改字符串指针不够，还要同步修改同一调用点的长度参数，或者把调用点改成运行时 `strlen` 路径。默认名、系统提示、资源名也需要逐类确认是否存在固定长度立即数。

## 方案判断

你的方案可行，但需要做成 RVA/VA 感知的 PE 工具，而不是只靠“静态基址硬算”：

1. dump 阶段解析 PE header 和 section，按 CP932/指定编码扫描 `.rdata`、`.data`、`_RDATA`、`.rsrc` 中的零结尾字符串。
2. JSON 记录 `fileOffset`、`rva`、`va`、`section`、`encoding`、`rawHex`、`original`、`translated`、`refs`。
3. refs 通过扫描 32-bit little-endian `ImageBase + RVA` 建立。引用所在 section 要标明，尤其区分 `.text` 代码立即数与 `.rdata/.data` 指针表。
4. import 阶段把翻译后的字符串按指定编码写入新 section，例如 `.yktxt`。
5. 对已确认安全的引用，把原 VA 改成新字符串 VA。
6. 对带固定长度参数的调用点，必须同时 patch 长度，或者先标记为 `needsLengthPatch`，不允许静默只改指针。

## 风险边界

第一版不应该盲目全自动替换所有命中：

- `.rdata/.data` 里的指针表引用通常可以直接改。
- `.text` 中的 VA 立即数也能改，但要确认它真是 `push offset xxx` / `lea` / 构造字符串参数，而不是碰巧相同的整数。
- 固定长度参数必须处理，否则长文本会被截断，短文本可能残留旧尾巴。
- 资源路径类字符串可以改，但改变路径长度和目录结构可能牵动资源加载逻辑，应单独分类。
- `.text` 里扫出来的“字符串”大多是代码字节误判，不能作为可编辑文本来源；可编辑源应优先来自 `.rdata/.data/_RDATA/.rsrc`。

## 后续实现入口

PE 工具已经拆成独立入口，不复用 Params 系 DLL 文档语义：

- `pe string-dump <exe> <json> --encoding cp932`
- `pe string-import <exe> <json> <out.exe> --section .yktxt --encoding cp932`

导出示例：

```powershell
dotnet run -- pe string-dump `
  "G:\アトリエかぐや\CC\気になる彼女のママは現役魔法少女\GAME_SYS_Crack.exe" `
  "tmp\pe_tblstr_strings.json" `
  --encoding cp932
```

导入示例：

```powershell
dotnet run -- pe string-import `
  "G:\アトリエかぐや\CC\気になる彼女のママは現役魔法少女\GAME_SYS_Crack.exe" `
  "tmp\pe_tblstr_strings.json" `
  "tmp\GAME_SYS_Crack.edited.exe" `
  --encoding cp932 `
  --section .yktxt
```

当前实现会：

- 扫描默认非 `.text` 的可读 section，包含 `.rdata`、`.data`、`_RDATA`、`.rsrc`。
- 默认只导出“有引用的日文/全角可编辑字符串”，避免把 CRT/Boost/ATL 的英文调试文本、二进制误判乱码、脚本报错/警告文本混进翻译文件。
- 输出零结尾字符串的 `fileOffset`、`rva`、`va`、原始字节、文本和引用点。
- 扫描 32-bit little-endian 绝对 VA 引用。
- 识别 `push imm8; push offset string` 形态，并在导入时同步改 `imm8` 长度。
- 导入时优先原地修改：译文字节数不超过原字节数时，直接覆盖原位置并用 `00` 清掉尾部。
- 只有译文字节数超过原字节数时，才新增 `.yktxt` section 写入译文，并把 JSON 记录的引用改到新字符串 VA。
- 输入 EXE 永远只读；导入必须写到另一个输出 EXE，避免破坏原文件。

可选参数：

- `--include-ascii`：同时导出 ASCII-only 字符串。
- `--include-unreferenced`：同时保留没有扫描到 VA 引用的字符串。
- `--include-diagnostics`：同时导出报错/警告/脚本诊断文本。
- `--include-text`：也扫描 `.text`。这主要用于逆向调查，不建议作为日常翻译文件。
- `--sections .rdata,.data`：指定扫描 section。

当前不会静默兜底：

- `translated` 为空的条目默认不导入。
- 修改了没有引用的条目会报错，因为它不会影响 EXE。
- `push imm8` 长度超过 255 会报错，不会截断。
- 其他固定长度形态还没有证实前不会伪装成已支持。

JSON 字段建议：

```json
{
  "imageBase": "0x00400000",
  "encoding": "cp932",
  "entries": [
    {
      "id": "S002C045C",
      "section": ".rdata",
      "rva": "0x002C045C",
      "va": "0x006C045C",
      "fileOffset": "0x002BF25C",
      "original": "ルビー",
      "translated": "",
      "refs": [
        { "section": ".text", "rva": "0x000EC3A5", "kind": "absolute_va" }
      ],
      "needsLengthPatch": true,
      "status": "confirmed"
    }
  ]
}
```

`needsLengthPatch` 是必须保留的状态，不应兜底忽略。
