// ============================================================================
// ApHandler.cs
// AP Alpha Plane 格式处理器 (IFormatHandler 实现)
//
// 格式识别: 魔数 0x5041 ("AP", 小端序, 文件头 2 字节)
//
// 二进制结构:
//   [0x00] 2B  魔数 "AP"
//   [0x02] 4B  Width (uint32)
//   [0x06] 4B  Height (uint32)
//   [0x0A] 2B  Bpp (int16, 位深)
//   [0x0C] ...  BGRA 像素数据 (每像素 4 字节, 自底向上排列)
//
// 转换 (Convert):
//   读取 BGRA 像素数据, 直接保存为 PNG
//   返回 Metadata: Width, Height, Bpp
//
// 重打包 (Repack):
//   从 PNG 读取 BGRA 像素, 写入 AP 头 + 像素数据
//
// 依赖: BitmapHelpers, PicturePathHelper, System.Drawing
// ============================================================================
using System.Drawing;

namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class ApHandler : IFormatHandler
{
    public string Tag => "ap";

    public sealed class Metadata
    {
        public uint Width { get; set; }
        public uint Height { get; set; }
        public short Bpp { get; set; }
    }

    public bool Identify(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 12) return false;
        reader.BaseStream.Position = 0;
        return reader.ReadUInt16() == 0x5041;
    }

    public object Convert(string sourceFile, string destPath)
    {
        using var stream = File.OpenRead(sourceFile);
        using var reader = new BinaryReader(stream);
        reader.BaseStream.Position = 2;
        var metadata = new Metadata
        {
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32(),
            Bpp = reader.ReadInt16()
        };
        if (metadata.Bpp is not (8 or 24 or 32))
        {
            throw new NotSupportedException($"Unsupported AP BPP: {metadata.Bpp}");
        }

        stream.Position = 12;
        var pixelDataSize = checked((int)(metadata.Width * metadata.Height * (metadata.Bpp / 8)));
        var pixelData = reader.ReadBytes(pixelDataSize);
        if (pixelData.Length != pixelDataSize)
        {
            throw new EndOfStreamException("Failed to read AP pixel payload.");
        }

        BitmapHelpers.SavePngFromBottomUpPixels(
            BitmapHelpers.ToBgra32(pixelData, (int)metadata.Width, (int)metadata.Height, metadata.Bpp),
            (int)metadata.Width,
            (int)metadata.Height,
            Path.ChangeExtension(destPath, ".png"));
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
        using var stream = File.Create(destFile);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x5041);
        writer.Write((uint)image.Width);
        writer.Write((uint)image.Height);
        writer.Write(metadata.Bpp);
        writer.Write(ConvertBgra32ToBpp(BitmapHelpers.ReadBottomUpPixelsFromImage(pngPath), metadata.Bpp));
    }

    private static byte[] ConvertBgra32ToBpp(byte[] bgra, int bpp)
    {
        if (bpp == 32)
        {
            return bgra;
        }

        if (bpp == 24)
        {
            var output = new byte[bgra.Length / 4 * 3];
            var dst = 0;
            for (var src = 0; src < bgra.Length; src += 4)
            {
                output[dst++] = bgra[src];
                output[dst++] = bgra[src + 1];
                output[dst++] = bgra[src + 2];
            }

            return output;
        }

        if (bpp == 8)
        {
            return BitmapHelpers.ToGrayscale(bgra);
        }

        throw new NotSupportedException($"Unsupported AP BPP: {bpp}");
    }
}
