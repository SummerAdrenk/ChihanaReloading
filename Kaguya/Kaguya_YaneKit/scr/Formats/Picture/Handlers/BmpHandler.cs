// ============================================================================
// BmpHandler.cs
// BMP 位图格式处理器 (IFormatHandler 实现)
//
// 格式识别: 魔数 0x4D42 ("BM", 小端序, 文件头 2 字节)
//   附加校验: reserved1/reserved2 均为 0, declaredSize <= 文件长度
//
// 转换 (Convert):
//   使用 GDI+ Image.FromFile 加载 BMP, 直接 Save 为 PNG
//   注意: 不使用 BitmapHelpers 的 bottom-up 路径, 因为 GDI+ 已处理行序
//
// 重打包 (Repack):
//   加载 PNG, 转为 24bpp RGB Bitmap, 通过 GDI+ Save 为 BMP
//   注意: BMP 行序由 GDI+ 编码器自动处理, 不需手动翻转
//
// 依赖: System.Drawing.Common (Image, Bitmap, Graphics)
// ============================================================================
using System.Drawing;
using System.Drawing.Imaging;
// using System.Runtime.InteropServices;

namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class BmpHandler : IFormatHandler
{
    public string Tag => "bmp";

    public sealed class Metadata { }

    public bool Identify(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 14) return false;
        reader.BaseStream.Position = 0;
        if (reader.ReadUInt16() != 0x4D42) return false;
        var declaredSize = reader.ReadUInt32();
        var reserved1 = reader.ReadUInt16();
        var reserved2 = reader.ReadUInt16();
        if (reserved1 != 0 || reserved2 != 0) return false;
        if (declaredSize > reader.BaseStream.Length) return false;
        return true;
    }

    public object Convert(string sourceFile, string destPath)
    {
        using var image = Image.FromFile(sourceFile);
        image.Save(Path.ChangeExtension(destPath, ".png"), ImageFormat.Png);

        // Old wrong implementation: BMP loaded by GDI+ is already a normal image.
        // Passing those top-down pixels to SavePngFromBottomUpPixels flipped the PNG.
        // using var bmp32 = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        // using (var g = Graphics.FromImage(bmp32))
        // {
        //     g.DrawImage(image, 0, 0, image.Width, image.Height);
        // }
        //
        // var rect = new Rectangle(0, 0, bmp32.Width, bmp32.Height);
        // BitmapData? data = null;
        // try
        // {
        //     data = bmp32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        //     var bytes = new byte[Math.Abs(data.Stride) * bmp32.Height];
        //     Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        //     BitmapHelpers.SavePngFromBottomUpPixels(bytes, bmp32.Width, bmp32.Height, Path.ChangeExtension(destPath, ".png"));
        // }
        // finally
        // {
        //     if (data is not null)
        //     {
        //         bmp32.UnlockBits(data);
        //     }
        // }

        return new Metadata();
    }

    public void Repack(string sourcePath, string destFile)
    {
        var pngPath = sourcePath + ".png";
        if (!File.Exists(pngPath))
        {
            throw new FileNotFoundException($"Missing PNG for repack: {pngPath}");
        }

        using var image = Image.FromFile(pngPath);
        using var bmp24 = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp24))
        {
            g.DrawImage(image, 0, 0, image.Width, image.Height);
        }
        bmp24.Save(destFile, ImageFormat.Bmp);

        // Keep repack on GDI+ image semantics. The BMP encoder owns BMP row order;
        // manual bottom-up conversion here would create the same class of inversion bug.
        // using var image = Image.FromFile(pngPath);
        // using var bmp24 = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        // using var g = Graphics.FromImage(bmp24);
        // g.DrawImage(image, 0, 0, image.Width, image.Height);
        // bmp24.Save(destFile, ImageFormat.Bmp);
    }
}
