# BMP 与其他图片

这里放两类：

- 标准 `BMP`
- 不属于独立家族、但也应并入图片处理范围的其他格式

## BMP

魔数：

```text
42 4D
```

当前工具行为：

- 提取：直接转 PNG
- 回封：写成 24-bit Windows BMP，并尽量沿用原 BMP 的 DIB header 布局

回封侧的关键点：

- 使用 GDI+ / `System.Drawing` 读取 PNG 像素，但不再直接用 GDI+ BMP encoder 落盘。
- 强制输出 24bpp RGB 像素，按 BMP bottom-up 行序写入。
- 若 `orig/` 中存在同名 BMP，则保留原文件的 file header + DIB header 长度和扩展字段，只更新 size、width、height、bpp、compression、imageSize 等必要字段。
- v5.8 的 `bgd/bg_white.bmp` 是 108 字节 DIB header、pixel offset `0x7A` 的 BMP V4。旧逻辑会被 GDI+ 改成 40 字节 DIB header、pixel offset `0x36`，文件少 68 字节；当前逻辑已修正为保留原布局。
- v5.8 还有两类容易造成字节差异的 BMP 细节：部分文件的 `biSizeImage` 比标准 `rowStride * height` 多 2 字节尾随 payload；`vrm/L無地.bmp`、`vrm/無地.bmp` 的每行 padding 字节不是全 0。当前回封在 PNG 尺寸不变时会保留这些原始 padding/tail 字节。
- 逆向确认：`Graphics.dll!SurfaceLoadBMP -> sub_1000DFB0` 会用 `rowStride = ((biBitCount * width / 8) + 3) & ~3` 在每行之间移动源指针，然后只按 `width` 拷贝有效像素。也就是说行 padding 字节参与行定位但不参与显示像素；尾随 payload 不被该循环消费。保留它们是为了无损/字节级回封，不是因为显示效果依赖这些字节。

## 其他

当前仓库里没有再找到能单独归类成家族的新图片格式。

也就是说，现阶段“其他”部分先是空的，不额外硬拆新 md。
