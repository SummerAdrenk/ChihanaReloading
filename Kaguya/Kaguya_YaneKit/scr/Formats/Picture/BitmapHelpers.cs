// ============================================================================
// BitmapHelpers.cs
// 底层位图操作工具: 处理 BGRA 像素数据与 System.Drawing.Bitmap 之间的转换
//
// 职责:
//   ReadBottomUpPixelsFromImage  -- 读取图片文件为自底向上的 BGRA 像素数组
//   SavePngFromBottomUpPixels    -- 将自底向上的 BGRA 像素保存为 PNG
//
// 注意: Yane 引擎的图片格式使用自底向上 (bottom-up) 的像素排列,
//       与 System.Drawing 的自顶向下不同, 需要垂直翻转
//
// 依赖: System.Drawing.Common (Bitmap, BitmapData, Graphics)
// ============================================================================
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Kaguya_YaneKit.Formats.Picture;

internal static class BitmapHelpers
{
    public static byte[] ReadBottomUpPixelsFromImage(string filePath)
    {
        using var bmp = new Bitmap(filePath);
        if (bmp.PixelFormat == PixelFormat.Format32bppArgb)
        {
            return GetBottomUpPixels(bmp);
        }

        using var converted = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(converted);
        g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
        return GetBottomUpPixels(converted);
    }

    public static void SavePngFromBottomUpPixels(byte[] bgraPixels, int width, int height, string filePath)
    {
        var flippedPixels = FlipPixelsVertical(bgraPixels, width, height);
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        BitmapData? data = null;
        try
        {
            data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            Marshal.Copy(flippedPixels, 0, data.Scan0, flippedPixels.Length);
        }
        finally
        {
            if (data is not null)
            {
                bmp.UnlockBits(data);
            }
        }

        bmp.Save(filePath, ImageFormat.Png);
    }

    public static byte[] FlipPixelsVertical(byte[] pixels, int width, int height)
    {
        var flipped = new byte[pixels.Length];
        var stride = width * 4;
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
        }

        return flipped;
    }

    public static byte[] GetBottomUpPixels(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData? data = null;
        try
        {
            data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bytes = new byte[Math.Abs(data.Stride) * bmp.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return FlipPixelsVertical(bytes, bmp.Width, bmp.Height);
        }
        finally
        {
            if (data is not null)
            {
                bmp.UnlockBits(data);
            }
        }
    }

    public static byte[] ToBgra32(byte[] pixels, int width, int height, int bpp)
    {
        if (bpp == 32)
        {
            return pixels;
        }

        var output = new byte[width * height * 4];
        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            if (bpp == 24)
            {
                output[dst++] = pixels[src++];
                output[dst++] = pixels[src++];
                output[dst++] = pixels[src++];
                output[dst++] = 255;
            }
            else if (bpp == 8)
            {
                var gray = pixels[src++];
                output[dst++] = gray;
                output[dst++] = gray;
                output[dst++] = gray;
                output[dst++] = 255;
            }
            else
            {
                throw new NotSupportedException($"Unsupported bpp: {bpp}");
            }
        }

        return output;
    }

    public static byte[] ToGrayscale(byte[] bgraPixels)
    {
        var output = new byte[bgraPixels.Length / 4];
        int i = 0;
        int j = 0;
        for (; i < bgraPixels.Length; i += 4, j++)
        {
            var b = bgraPixels[i + 0];
            var g = bgraPixels[i + 1];
            var r = bgraPixels[i + 2];
            output[j] = (byte)((r * 30 + g * 59 + b * 11) / 100);
        }

        return output;
    }
}
