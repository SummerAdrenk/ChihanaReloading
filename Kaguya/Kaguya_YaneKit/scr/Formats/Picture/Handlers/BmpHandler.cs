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
using System.Buffers.Binary;
// using System.Runtime.InteropServices;

namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class BmpHandler : IFormatHandler
{
    public string Tag => "bmp";

    public sealed class Metadata
    {
        public uint DibHeaderByteCount { get; set; }
        public uint PixelDataOffset { get; set; }
        public ushort BitsPerPixel { get; set; }
        public uint Compression { get; set; }
    }

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
        image.Save(PicturePathHelper.ChangeExtensionPreservingName(destPath, ".png"), ImageFormat.Png);
        var header = BmpHeaderTemplate.Read(sourceFile);

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
        //     BitmapHelpers.SavePngFromBottomUpPixels(bytes, bmp32.Width, bmp32.Height, PicturePathHelper.ChangeExtensionPreservingName(destPath, ".png"));
        // }
        // finally
        // {
        //     if (data is not null)
        //     {
        //         bmp32.UnlockBits(data);
        //     }
        // }

        return new Metadata
        {
            DibHeaderByteCount = header.DibHeaderByteCount,
            PixelDataOffset = header.PixelDataOffset,
            BitsPerPixel = header.BitsPerPixel,
            Compression = header.Compression
        };
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

        var template = TryFindOriginalBmp(sourcePath, out var originalBmpPath)
            ? BmpHeaderTemplate.Read(originalBmpPath)
            : BmpHeaderTemplate.CreateDefault();
        WriteBmpWithTemplate(bmp24, template, destFile);

        // Keep repack on GDI+ image semantics. The BMP encoder owns BMP row order;
        // manual bottom-up conversion here would create the same class of inversion bug.
        // using var image = Image.FromFile(pngPath);
        // using var bmp24 = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        // using var g = Graphics.FromImage(bmp24);
        // g.DrawImage(image, 0, 0, image.Width, image.Height);
        // bmp24.Save(destFile, ImageFormat.Bmp);
    }

    private static void WriteBmpWithTemplate(Bitmap bmp, BmpHeaderTemplate template, string destFile)
    {
        var bottomUpBgra = BitmapHelpers.GetBottomUpPixels(bmp);
        var rowStride = checked(((bmp.Width * 3) + 3) & ~3);
        var pixelTail = template.Width == bmp.Width && template.Height == bmp.Height
            ? template.PixelTail
            : [];
        var imageSize = checked(rowStride * bmp.Height + pixelTail.Length);
        var fileSize = checked((int)template.PixelDataOffset + imageSize);
        var header = template.Header.ToArray();

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(10, 4), template.PixelDataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18, 4), bmp.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22, 4), template.BottomUp ? bmp.Height : -bmp.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28, 2), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(30, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(34, 4), (uint)imageSize);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destFile))!);
        using var output = File.Create(destFile);
        output.Write(header);
        if (header.Length < template.PixelDataOffset)
        {
            output.Write(new byte[template.PixelDataOffset - header.Length]);
        }

        Span<byte> padding = stackalloc byte[3];
        var paddingSize = rowStride - bmp.Width * 3;
        for (var y = 0; y < bmp.Height; y++)
        {
            var srcRow = template.BottomUp ? y : bmp.Height - 1 - y;
            var src = srcRow * bmp.Width * 4;
            for (var x = 0; x < bmp.Width; x++)
            {
                output.WriteByte(bottomUpBgra[src++]);
                output.WriteByte(bottomUpBgra[src++]);
                output.WriteByte(bottomUpBgra[src++]);
                src++;
            }

            if (paddingSize > 0)
            {
                var paddingOffset = y * paddingSize;
                if (template.RowPadding.Length >= paddingOffset + paddingSize)
                {
                    output.Write(template.RowPadding.AsSpan(paddingOffset, paddingSize));
                }
                else
                {
                    output.Write(padding[..paddingSize]);
                }
            }
        }

        if (pixelTail.Length > 0)
        {
            output.Write(pixelTail);
        }
    }

    private static bool TryFindOriginalBmp(string sourcePath, out string originalBmpPath)
    {
        var pngPath = sourcePath + ".png";
        var current = Directory.GetParent(Path.GetFullPath(pngPath));
        while (current is not null &&
               !current.Name.Equals("png", StringComparison.OrdinalIgnoreCase) &&
               !current.Name.Equals("fix", StringComparison.OrdinalIgnoreCase))
        {
            current = current.Parent;
        }

        if (current?.Parent is null)
        {
            originalBmpPath = "";
            return false;
        }

        var relativePath = Path.GetRelativePath(current.FullName, pngPath);
        originalBmpPath = Path.Combine(current.Parent.FullName, "orig", PicturePathHelper.ChangeExtensionPreservingName(relativePath, ".bmp"));
        return File.Exists(originalBmpPath);
    }

    private sealed class BmpHeaderTemplate
    {
        public required byte[] Header { get; init; }
        public required uint DibHeaderByteCount { get; init; }
        public required uint PixelDataOffset { get; init; }
        public required ushort BitsPerPixel { get; init; }
        public required uint Compression { get; init; }
        public required bool BottomUp { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] RowPadding { get; init; }
        public required byte[] PixelTail { get; init; }

        public static BmpHeaderTemplate Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 54 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
            {
                throw new InvalidDataException($"Invalid BMP header: {path}");
            }

            var pixelDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10, 4));
            var dibHeaderByteCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14, 4));
            if (dibHeaderByteCount < 40 || pixelDataOffset < 14 + dibHeaderByteCount || pixelDataOffset > bytes.Length)
            {
                throw new InvalidDataException($"Unsupported BMP header layout: {path}");
            }

            var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2));
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(30, 4));
            var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
            var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4));
            var height = Math.Abs(rawHeight);
            var expectedPixelBytes = bitsPerPixel == 24 && compression == 0
                ? checked(((width * 3 + 3) & ~3) * height)
                : 0;
            var payloadBytes = checked(bytes.Length - (int)pixelDataOffset);
            var rowPadding = ExtractRowPadding(bytes, (int)pixelDataOffset, width, height, bitsPerPixel, compression);
            var pixelTail = expectedPixelBytes > 0 && payloadBytes >= expectedPixelBytes
                ? bytes[((int)pixelDataOffset + expectedPixelBytes)..]
                : [];
            return new BmpHeaderTemplate
            {
                Header = bytes[..(int)pixelDataOffset],
                DibHeaderByteCount = dibHeaderByteCount,
                PixelDataOffset = pixelDataOffset,
                BitsPerPixel = bitsPerPixel,
                Compression = compression,
                BottomUp = rawHeight >= 0,
                Width = width,
                Height = height,
                RowPadding = rowPadding,
                PixelTail = pixelTail
            };
        }

        public static BmpHeaderTemplate CreateDefault()
        {
            var header = new byte[54];
            header[0] = (byte)'B';
            header[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(10, 4), 54);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), 40);
            return new BmpHeaderTemplate
            {
                Header = header,
                DibHeaderByteCount = 40,
                PixelDataOffset = 54,
                BitsPerPixel = 24,
                Compression = 0,
                BottomUp = true,
                Width = 0,
                Height = 0,
                RowPadding = [],
                PixelTail = []
            };
        }

        private static byte[] ExtractRowPadding(byte[] bytes, int pixelDataOffset, int width, int height, ushort bitsPerPixel, uint compression)
        {
            if (bitsPerPixel != 24 || compression != 0 || width <= 0 || height <= 0)
            {
                return [];
            }

            var pixelBytesPerRow = checked(width * 3);
            var rowStride = checked((pixelBytesPerRow + 3) & ~3);
            var paddingSize = rowStride - pixelBytesPerRow;
            if (paddingSize == 0)
            {
                return [];
            }

            var requiredBytes = checked(pixelDataOffset + rowStride * height);
            if (bytes.Length < requiredBytes)
            {
                return [];
            }

            var rowPadding = new byte[checked(paddingSize * height)];
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(
                    bytes,
                    pixelDataOffset + y * rowStride + pixelBytesPerRow,
                    rowPadding,
                    y * paddingSize,
                    paddingSize);
            }

            return rowPadding;
        }
    }
}
