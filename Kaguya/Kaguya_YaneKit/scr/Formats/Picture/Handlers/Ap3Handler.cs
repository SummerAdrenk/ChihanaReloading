// ============================================================================
// Ap3Handler.cs
// AP-3 Alpha Plane 格式处理器 (IFormatHandler 实现)
//
// 格式识别: 魔数 0x332D5041 ("AP-3", 小端序, 文件头 4 字节)
//
// 二进制结构:
//   [0x00] 4B  魔数 "AP-3"
//   [0x04] 4B  OffsetX (int32)
//   [0x08] 4B  OffsetY (int32)
//   [0x0C] 4B  Width (uint32)
//   [0x10] 4B  Height (uint32)
//   [0x14] 4B  Bpp (int32, 位深: 8/24/32)
//   [0x18] ...  像素数据 (自底向上排列, 字节数 = W * H * Bpp/8)
//
// 转换 (Convert):
//   读取像素数据, 通过 BitmapHelpers.ToBgra32 统一转为 BGRA, 保存为 PNG
//
// 重打包 (Repack):
//   读取 PNG 的 BGRA 像素, 根据原始 Bpp 转换:
//     32bpp -> 直接写入 BGRA
//     24bpp -> 丢弃 Alpha 通道, 写入 BGR
//      8bpp -> 转为灰度 (BitmapHelpers.ToGrayscale)
//
// 依赖: BitmapHelpers, PicturePathHelper, System.Drawing
// ============================================================================
using System.Drawing;

namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class Ap3Handler : IFormatHandler
{
    public string Tag => "ap3";

    public sealed class Metadata
    {
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        public int Bpp { get; set; }
    }

    public bool Identify(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 4) return false;
        reader.BaseStream.Position = 0;
        return reader.ReadUInt32() == 0x332D5041;
    }

    public object Convert(string sourceFile, string destPath)
    {
        using var stream = File.OpenRead(sourceFile);
        using var reader = new BinaryReader(stream);
        reader.BaseStream.Position = 4;
        var metadata = new Metadata
        {
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32(),
            Bpp = reader.ReadInt32()
        };

        stream.Position = 0x18;
        var pixelData = reader.ReadBytes(checked((int)(metadata.Width * metadata.Height * (metadata.Bpp / 8))));
        BitmapHelpers.SavePngFromBottomUpPixels(BitmapHelpers.ToBgra32(pixelData, (int)metadata.Width, (int)metadata.Height, metadata.Bpp), (int)metadata.Width, (int)metadata.Height, Path.ChangeExtension(destPath, ".png"));
        return metadata;
    }

    public void Repack(string sourcePath, string destFile)
    {
        var pngPath = sourcePath + ".png";
        var jsonPath = PicturePathHelper.GetMetadataPathForSource(sourcePath);
        if (!File.Exists(pngPath)) throw new FileNotFoundException($"Missing PNG for repack: {pngPath}");
        if (!File.Exists(jsonPath)) throw new FileNotFoundException($"Missing JSON metadata for repack: {jsonPath}");

        var metadata = System.Text.Json.JsonSerializer.Deserialize<Metadata>(File.ReadAllText(jsonPath)) ?? throw new InvalidDataException("Failed to parse JSON metadata.");
        using var image = Image.FromFile(pngPath);
        var bgra = BitmapHelpers.ReadBottomUpPixelsFromImage(pngPath);
        using var stream = File.Create(destFile);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x332D5041);
        writer.Write(metadata.OffsetX);
        writer.Write(metadata.OffsetY);
        writer.Write((uint)image.Width);
        writer.Write((uint)image.Height);
        writer.Write(metadata.Bpp);
        if (metadata.Bpp == 32)
        {
            writer.Write(bgra);
        }
        else if (metadata.Bpp == 24)
        {
            var outBytes = new byte[image.Width * image.Height * 3];
            int i = 0;
            int j = 0;
            for (; i < bgra.Length; i += 4, j += 3)
            {
                outBytes[j + 0] = bgra[i + 0];
                outBytes[j + 1] = bgra[i + 1];
                outBytes[j + 2] = bgra[i + 2];
            }
            writer.Write(outBytes);
        }
        else if (metadata.Bpp == 8)
        {
            writer.Write(BitmapHelpers.ToGrayscale(bgra));
        }
        else
        {
            throw new NotSupportedException($"Unsupported repack BPP: {metadata.Bpp}");
        }
    }
}
