# APS 家族

## 当前结论

`APS3` / `APS4` 已在 `気になる彼女のママは現役魔法少女` 的 `PARTS.ARC` 中确认。它们不是普通裸 `AP-3`，而是“长度前缀 magic + sprite 记录表 + 内嵌 AP/AO 图集”的容器。

因为封包里的扩展名仍是 `.ap3`，工具暂归到 `ap3` 工作目录；但 metadata 会保留真实 `Format`，不会把 APS3/APS4 当成普通 AP-3 混掉。

## magic

```text
04 41 50 53 33  # 长度前缀 "APS3"
04 41 50 53 34  # 长度前缀 "APS4"
```

## 结构定位

- 外层：APS3/APS4 容器，保存 sprite 记录表。
- 内层：一张 AP/AO 图集。
- 内层图像块 `mode == 1` 使用 CLZSS 解压，来自 `sub_565250`、`sub_556F00`、`sub_557020` 的逆向确认。
- 回封时当前工具把内层图像块写成 raw `mode == 0`；游戏读取器有明确 raw 分支，可以读取。

## 工具行为

- 识别：`Ap3Handler` 会识别 `APS3` / `APS4`。
- 路径：仍放在 `ap3` 工作目录，因为原始封包扩展名是 `.ap3`。
- metadata：使用 `Format: "APS3"` 或 `Format: "APS4"` 区分真实容器。
- PNG 导出：导出内嵌 AP/AO 图集，同时保留 sprite 记录表。

## 验证

`PARTS.ARC` 当前验证：

```text
pic sort: 270 success, 0 unrecognized
pic convert: AP3 182/182 success, AP 88/88 success
pic repack-png: AP3 182/182 success, AP 88/88 success
re-sort/re-convert repacked new files: success
```
