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
namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class ApHandler : IFormatHandler
{
    public string Tag => "ap";

    public sealed class Metadata
    {
        public string Magic { get; set; } = "AP";
        public uint Width { get; set; }
        public uint Height { get; set; }
        public short Bpp { get; set; }
        public int HeaderSize { get; set; } = 12;
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public int PixelBytesPerPixel { get; set; }
    }

    public bool Identify(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 12) return false;
        reader.BaseStream.Position = 0;
        var magic = reader.ReadUInt16();
        return magic is 0x5041 or 0x4F41;
    }

    public object Convert(string sourceFile, string destPath)
    {
        using var stream = File.OpenRead(sourceFile);
        using var reader = new BinaryReader(stream);
        var magicValue = reader.ReadUInt16();
        var magic = magicValue switch
        {
            0x5041 => "AP",
            0x4F41 => "AO",
            _ => throw new InvalidDataException($"Unsupported AP magic: 0x{magicValue:X4}")
        };
        var metadata = new Metadata
        {
            Magic = magic,
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32(),
            Bpp = reader.ReadInt16()
        };
        if (magic == "AO")
        {
            metadata.OffsetX = reader.ReadInt32();
            metadata.OffsetY = reader.ReadInt32();
            metadata.HeaderSize = 20;
        }

        stream.Position = metadata.HeaderSize;
        var pixelCount = checked((long)metadata.Width * metadata.Height);
        var pixelPayloadSize = stream.Length - metadata.HeaderSize;
        if (pixelCount <= 0 || pixelPayloadSize <= 0 || pixelPayloadSize % pixelCount != 0)
        {
            throw new InvalidDataException($"Invalid AP pixel payload size: {pixelPayloadSize}");
        }

        metadata.PixelBytesPerPixel = checked((int)(pixelPayloadSize / pixelCount));
        if (metadata.PixelBytesPerPixel is not (1 or 3 or 4))
        {
            throw new NotSupportedException($"Unsupported AP payload bytes per pixel: {metadata.PixelBytesPerPixel}");
        }

        var pixelDataSize = checked((int)pixelPayloadSize);
        var pixelData = reader.ReadBytes(pixelDataSize);
        if (pixelData.Length != pixelDataSize)
        {
            throw new EndOfStreamException("Failed to read AP pixel payload.");
        }

        BitmapHelpers.SavePngFromBottomUpPixels(
            BitmapHelpers.ToBgra32(pixelData, (int)metadata.Width, (int)metadata.Height, metadata.PixelBytesPerPixel * 8),
            (int)metadata.Width,
            (int)metadata.Height,
            PicturePathHelper.ChangeExtensionPreservingName(destPath, ".png"));
        return metadata;
    }

    public void Repack(string sourcePath, string destFile)
    {
        var pngPath = sourcePath + ".png";
        var jsonPath = PicturePathHelper.GetMetadataPathForSource(pngPath);
        if (!File.Exists(pngPath)) throw new FileNotFoundException($"Missing PNG for repack: {pngPath}");
        if (!File.Exists(jsonPath)) throw new FileNotFoundException($"Missing JSON metadata for repack: {jsonPath}");

        var metadata = System.Text.Json.JsonSerializer.Deserialize<Metadata>(File.ReadAllText(jsonPath)) ?? throw new InvalidDataException("Failed to parse JSON metadata.");
        var bgra = BitmapHelpers.ReadBottomUpPixelsFromImage(pngPath, out var width, out var height);
        using var stream = File.Create(destFile);
        using var writer = new BinaryWriter(stream);
        if (string.Equals(metadata.Magic, "AO", StringComparison.OrdinalIgnoreCase))
        {
            writer.Write((ushort)0x4F41);
        }
        else
        {
            writer.Write((ushort)0x5041);
        }
        writer.Write((uint)width);
        writer.Write((uint)height);
        writer.Write(metadata.Bpp);
        if (string.Equals(metadata.Magic, "AO", StringComparison.OrdinalIgnoreCase))
        {
            writer.Write(metadata.OffsetX);
            writer.Write(metadata.OffsetY);
        }
        writer.Write(ConvertBgra32ToPayload(bgra, GetPixelBytesPerPixel(metadata, sourcePath)));
    }

    private static int GetPixelBytesPerPixel(Metadata metadata, string sourcePath)
    {
        if (metadata.PixelBytesPerPixel is 1 or 3 or 4)
        {
            return metadata.PixelBytesPerPixel;
        }

        if (TryFindOriginalAp(sourcePath, out var originalApPath))
        {
            using var stream = File.OpenRead(originalApPath);
            using var reader = new BinaryReader(stream);
            var magic = reader.ReadUInt16();
            var headerSize = magic == 0x4F41 ? 20 : 12;
            var width = reader.ReadUInt32();
            var height = reader.ReadUInt32();
            var pixelCount = checked((long)width * height);
            var payloadSize = stream.Length - headerSize;
            if (pixelCount > 0 && payloadSize > 0 && payloadSize % pixelCount == 0)
            {
                var bytesPerPixel = checked((int)(payloadSize / pixelCount));
                if (bytesPerPixel is 1 or 3 or 4)
                {
                    return bytesPerPixel;
                }
            }
        }

        throw new InvalidDataException("AP metadata does not contain PixelBytesPerPixel and original payload layout cannot be recovered.");
    }

    private static bool TryFindOriginalAp(string sourcePath, out string originalApPath)
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
            originalApPath = "";
            return false;
        }

        var relativePath = Path.GetRelativePath(current.FullName, pngPath);
        originalApPath = Path.Combine(current.Parent.FullName, "orig", PicturePathHelper.ChangeExtensionPreservingName(relativePath, ".alp"));
        return File.Exists(originalApPath);
    }

    private static byte[] ConvertBgra32ToPayload(byte[] bgra, int bytesPerPixel)
    {
        if (bytesPerPixel == 4)
        {
            return bgra;
        }

        if (bytesPerPixel == 3)
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

        if (bytesPerPixel == 1)
        {
            return BitmapHelpers.ToGrayscale(bgra);
        }

        throw new NotSupportedException($"Unsupported AP payload bytes per pixel: {bytesPerPixel}");
    }
}
