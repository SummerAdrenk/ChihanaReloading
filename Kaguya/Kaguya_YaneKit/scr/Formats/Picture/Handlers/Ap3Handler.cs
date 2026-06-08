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
using System.Text;
using System.Text.Json;

namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class Ap3Handler : IFormatHandler
{
    public string Tag => "ap3";

    public sealed class Metadata
    {
        public string Format { get; set; } = "AP-3";
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        public int Bpp { get; set; }
        public int EmbeddedImageHeaderSize { get; set; }
        public int EmbeddedImageBytesPerPixel { get; set; }
        public List<ApsRecord> Records { get; set; } = [];
        public uint ImageBlockSize { get; set; }
        public short ImageMode { get; set; }
        public uint ImageStoredSize { get; set; }
        public uint ImageUnpackedSize { get; set; }
        public string EmbeddedImageMagic { get; set; } = "";
        public string TailBase64 { get; set; } = "";
    }

    public sealed class ApsRecord
    {
        public uint FrameIndex { get; set; }
        public string Name { get; set; } = "";
        public uint SourceX { get; set; }
        public uint SourceY { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int LayerOrGroup { get; set; }
        public int Field24 { get; set; }
        public int Field28 { get; set; }
    }

    public bool Identify(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 4) return false;
        reader.BaseStream.Position = 0;
        if (reader.ReadUInt32() == 0x332D5041)
        {
            return true;
        }

        reader.BaseStream.Position = 0;
        var length = reader.ReadByte();
        if (length is not (4))
        {
            return false;
        }

        var signature = Encoding.ASCII.GetString(reader.ReadBytes(length));
        return signature is "APS3" or "APS4";
    }

    public object Convert(string sourceFile, string destPath)
    {
        using var stream = File.OpenRead(sourceFile);
        using var reader = new BinaryReader(stream);
        if (IsApsContainer(reader))
        {
            return ConvertAps(sourceFile, destPath);
        }

        reader.BaseStream.Position = 4;
        var metadata = new Metadata
        {
            Format = "AP-3",
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32(),
            Bpp = reader.ReadInt32()
        };

        stream.Position = 0x18;
        var expectedSize = checked((int)(metadata.Width * metadata.Height * (metadata.Bpp / 8)));
        var pixelData = reader.ReadBytes(expectedSize);
        if (pixelData.Length != expectedSize)
        {
            throw new EndOfStreamException("Failed to read AP-3 pixel payload.");
        }

        BitmapHelpers.SavePngFromBottomUpPixels(BitmapHelpers.ToBgra32(pixelData, (int)metadata.Width, (int)metadata.Height, metadata.Bpp), (int)metadata.Width, (int)metadata.Height, PicturePathHelper.ChangeExtensionPreservingName(destPath, ".png"));
        return metadata;
    }

    public void Repack(string sourcePath, string destFile)
    {
        var pngPath = sourcePath + ".png";
        var jsonPath = PicturePathHelper.GetMetadataPathForSource(pngPath);
        if (!File.Exists(pngPath)) throw new FileNotFoundException($"Missing PNG for repack: {pngPath}");
        if (!File.Exists(jsonPath)) throw new FileNotFoundException($"Missing JSON metadata for repack: {jsonPath}");

        var metadata = System.Text.Json.JsonSerializer.Deserialize<Metadata>(File.ReadAllText(jsonPath)) ?? throw new InvalidDataException("Failed to parse JSON metadata.");
        if (metadata.Format is "APS3" or "APS4")
        {
            RepackAps(pngPath, destFile, metadata);
            return;
        }

        var bgra = BitmapHelpers.ReadBottomUpPixelsFromImage(pngPath, out var width, out var height);
        using var stream = File.Create(destFile);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x332D5041);
        writer.Write(metadata.OffsetX);
        writer.Write(metadata.OffsetY);
        writer.Write((uint)width);
        writer.Write((uint)height);
        writer.Write(metadata.Bpp);
        if (metadata.Bpp == 32)
        {
            writer.Write(bgra);
        }
        else if (metadata.Bpp == 24)
        {
            var outBytes = new byte[width * height * 3];
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

    private static bool IsApsContainer(BinaryReader reader)
    {
        reader.BaseStream.Position = 0;
        if (reader.BaseStream.Length < 9)
        {
            return false;
        }

        var length = reader.ReadByte();
        if (length != 4)
        {
            return false;
        }

        var signature = Encoding.ASCII.GetString(reader.ReadBytes(length));
        return signature is "APS3" or "APS4";
    }

    private static Metadata ConvertAps(string sourceFile, string destPath)
    {
        using var stream = File.OpenRead(sourceFile);
        using var reader = new BinaryReader(stream);
        var metadata = ReadApsMetadata(reader, out var imageBytes);
        FillEmbeddedImageMetadata(metadata, imageBytes);
        SaveEmbeddedImageAsPng(imageBytes, PicturePathHelper.ChangeExtensionPreservingName(destPath, ".png"));
        return metadata;
    }

    private static Metadata ReadApsMetadata(BinaryReader reader, out byte[] imageBytes)
    {
        var signatureLength = reader.ReadByte();
        var signature = Encoding.ASCII.GetString(reader.ReadBytes(signatureLength));
        if (signature is not ("APS3" or "APS4"))
        {
            throw new InvalidDataException($"Unsupported APS signature: {signature}");
        }

        var metadata = new Metadata { Format = signature };
        var recordCount = reader.ReadUInt32();
        for (var i = 0; i < recordCount; i++)
        {
            var record = new ApsRecord
            {
                FrameIndex = reader.ReadUInt32()
            };
            var nameLength = reader.ReadByte();
            record.Name = Encoding.ASCII.GetString(reader.ReadBytes(nameLength));
            record.SourceX = reader.ReadUInt32();
            record.SourceY = reader.ReadUInt32();
            record.Width = reader.ReadInt32();
            record.Height = reader.ReadInt32();
            record.LayerOrGroup = reader.ReadInt32();
            record.Field24 = reader.ReadInt32();
            record.Field28 = reader.ReadInt32();
            metadata.Records.Add(record);
        }

        metadata.ImageBlockSize = reader.ReadUInt32();
        metadata.ImageMode = reader.ReadInt16();
        metadata.ImageStoredSize = reader.ReadUInt32();
        if (metadata.ImageMode == 1)
        {
            metadata.ImageUnpackedSize = reader.ReadUInt32();
            var expectedStoredSize = checked((int)metadata.ImageStoredSize);
            var compressed = reader.ReadBytes(expectedStoredSize);
            if (compressed.Length != expectedStoredSize)
            {
                throw new EndOfStreamException("Failed to read APS compressed image payload.");
            }

            imageBytes = ApsLzUnpack(compressed, checked((int)metadata.ImageUnpackedSize));
        }
        else
        {
            metadata.ImageUnpackedSize = metadata.ImageStoredSize;
            var expectedStoredSize = checked((int)metadata.ImageStoredSize);
            imageBytes = reader.ReadBytes(expectedStoredSize);
            if (imageBytes.Length != expectedStoredSize)
            {
                throw new EndOfStreamException("Failed to read APS image payload.");
            }
        }

        var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (remaining > 0)
        {
            metadata.TailBase64 = System.Convert.ToBase64String(reader.ReadBytes(checked((int)remaining)));
        }

        return metadata;
    }

    private static void FillEmbeddedImageMetadata(Metadata metadata, byte[] imageBytes)
    {
        if (imageBytes.Length < 12)
        {
            throw new InvalidDataException("APS embedded image is too short.");
        }

        using var stream = new MemoryStream(imageBytes);
        using var reader = new BinaryReader(stream);
        var magic = reader.ReadUInt16();
        metadata.EmbeddedImageMagic = magic switch
        {
            0x5041 => "AP",
            0x4F41 => "AO",
            _ => throw new InvalidDataException($"Unsupported APS embedded image magic: 0x{magic:X4}")
        };
        metadata.Width = reader.ReadUInt32();
        metadata.Height = reader.ReadUInt32();
        metadata.Bpp = reader.ReadInt16();
        metadata.EmbeddedImageHeaderSize = metadata.EmbeddedImageMagic == "AO" ? 20 : 12;
        if (metadata.EmbeddedImageMagic == "AO")
        {
            metadata.OffsetX = reader.ReadInt32();
            metadata.OffsetY = reader.ReadInt32();
        }

        var pixelCount = checked((long)metadata.Width * metadata.Height);
        var payloadSize = imageBytes.Length - metadata.EmbeddedImageHeaderSize;
        if (pixelCount <= 0 || payloadSize <= 0 || payloadSize % pixelCount != 0)
        {
            throw new InvalidDataException($"Invalid APS embedded image payload size: {payloadSize}");
        }

        metadata.EmbeddedImageBytesPerPixel = checked((int)(payloadSize / pixelCount));
    }

    private static void SaveEmbeddedImageAsPng(byte[] imageBytes, string outputPath)
    {
        using var stream = new MemoryStream(imageBytes);
        using var reader = new BinaryReader(stream);
        var magic = reader.ReadUInt16();
        var width = reader.ReadUInt32();
        var height = reader.ReadUInt32();
        var bpp = reader.ReadInt16();
        var headerSize = magic == 0x4F41 ? 20 : 12;
        stream.Position = headerSize;
        var pixelCount = checked((long)width * height);
        var payloadSize = stream.Length - headerSize;
        if (pixelCount <= 0 || payloadSize <= 0 || payloadSize % pixelCount != 0)
        {
            throw new InvalidDataException($"Invalid APS embedded image payload size: {payloadSize}");
        }

        var bytesPerPixel = checked((int)(payloadSize / pixelCount));
        if (bytesPerPixel is not (1 or 3 or 4))
        {
            throw new NotSupportedException($"Unsupported APS embedded image bytes per pixel: {bytesPerPixel}");
        }

        var pixelData = reader.ReadBytes(checked((int)payloadSize));
        BitmapHelpers.SavePngFromBottomUpPixels(
            BitmapHelpers.ToBgra32(pixelData, (int)width, (int)height, bytesPerPixel * 8),
            (int)width,
            (int)height,
            outputPath);
    }

    private static void RepackAps(string pngPath, string destFile, Metadata metadata)
    {
        var bgra = BitmapHelpers.ReadBottomUpPixelsFromImage(pngPath, out var width, out var height);
        var imageBytes = BuildEmbeddedApBytes(metadata, width, height, bgra);
        using var output = File.Create(destFile);
        using var writer = new BinaryWriter(output);
        writer.Write((byte)metadata.Format.Length);
        writer.Write(Encoding.ASCII.GetBytes(metadata.Format));
        writer.Write((uint)metadata.Records.Count);
        foreach (var record in metadata.Records)
        {
            writer.Write(record.FrameIndex);
            var nameBytes = Encoding.ASCII.GetBytes(record.Name ?? "");
            if (nameBytes.Length > byte.MaxValue)
            {
                throw new InvalidDataException($"APS record name is too long: {record.Name}");
            }

            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(record.SourceX);
            writer.Write(record.SourceY);
            writer.Write(record.Width);
            writer.Write(record.Height);
            writer.Write(record.LayerOrGroup);
            writer.Write(record.Field24);
            writer.Write(record.Field28);
        }

        writer.Write((uint)(2 + 4 + imageBytes.Length));
        writer.Write((short)0);
        writer.Write((uint)imageBytes.Length);
        writer.Write(imageBytes);
        if (!string.IsNullOrEmpty(metadata.TailBase64))
        {
            writer.Write(System.Convert.FromBase64String(metadata.TailBase64));
        }
    }

    private static byte[] BuildEmbeddedApBytes(Metadata metadata, int width, int height, byte[] bgra)
    {
        var bytesPerPixel = metadata.EmbeddedImageBytesPerPixel != 0
            ? metadata.EmbeddedImageBytesPerPixel
            : metadata.Bpp / 8;
        var payload = bytesPerPixel switch
        {
            4 => bgra,
            3 => DropAlpha(bgra),
            1 => BitmapHelpers.ToGrayscale(bgra),
            _ => throw new NotSupportedException($"Unsupported APS embedded bytes per pixel: {bytesPerPixel}")
        };

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(string.Equals(metadata.EmbeddedImageMagic, "AO", StringComparison.OrdinalIgnoreCase)
            ? (ushort)0x4F41
            : (ushort)0x5041);
        writer.Write((uint)width);
        writer.Write((uint)height);
        writer.Write((short)metadata.Bpp);
        if (string.Equals(metadata.EmbeddedImageMagic, "AO", StringComparison.OrdinalIgnoreCase))
        {
            writer.Write(metadata.OffsetX);
            writer.Write(metadata.OffsetY);
        }

        writer.Write(payload);
        return stream.ToArray();
    }

    private static byte[] DropAlpha(byte[] bgra)
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

    private static byte[] ApsLzUnpack(byte[] input, int unpackedSize)
    {
        var output = new byte[unpackedSize];
        var frame = new byte[0x1000];
        var framePos = 1;
        var dst = 0;
        var pos = 0;
        var mask = 0x80;
        var control = 0;
        while (dst < output.Length)
        {
            if (mask == 0x80)
            {
                if (pos >= input.Length)
                {
                    throw new EndOfStreamException("APS LZ stream ended before terminator.");
                }

                control = input[pos++];
            }

            var currentMask = mask;
            mask >>= 1;
            if (mask == 0)
            {
                mask = 0x80;
            }

            if ((control & currentMask) != 0)
            {
                var b = (byte)ReadApsBits(input, ref pos, ref mask, ref control, 8);
                output[dst++] = b;
                frame[framePos++ & 0xFFF] = b;
                continue;
            }

            var offset = ReadApsBits(input, ref pos, ref mask, ref control, 12);
            if (offset == 0)
            {
                break;
            }

            var count = ReadApsBits(input, ref pos, ref mask, ref control, 4) + 2;
            for (var i = 0; i < count && dst < output.Length; i++)
            {
                var b = frame[(offset + i) & 0xFFF];
                output[dst++] = b;
                frame[framePos++ & 0xFFF] = b;
            }
        }

        if (dst != output.Length)
        {
            throw new InvalidDataException($"APS LZ decoded size mismatch: decoded={dst}, expected={output.Length}.");
        }

        return output;
    }

    private static int ReadApsBits(byte[] input, ref int pos, ref int mask, ref int control, int count)
    {
        var value = 0;
        for (var bit = count - 1; bit >= 0; bit--)
        {
            if (mask == 0x80)
            {
                if (pos >= input.Length)
                {
                    throw new EndOfStreamException("APS LZ bitstream ended unexpectedly.");
                }

                control = input[pos++];
                mask = 0x80;
            }

            if ((control & mask) != 0)
            {
                value |= 1 << bit;
            }

            mask >>= 1;
            if (mask == 0)
            {
                mask = 0x80;
            }
        }

        return value;
    }
}
