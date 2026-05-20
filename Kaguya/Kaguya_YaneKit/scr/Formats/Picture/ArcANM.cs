// ============================================================================
// ArcANM.cs
// ANM 动画精灵格式的底层解包/打包实现
//
// 支持的版本:
//   AN00 -- 基础格式: 帧表 + 未压缩 BGRA 像素数据
//   AN10 -- 扩展格式: 同 AN00 结构但帧像素支持可变通道数 (channels)
//   AN20 -- 变体格式: 不同的帧表布局, 画布信息在帧数据前 (非文件头)
//   AN21 -- 差分帧格式: 首帧为完整像素, 后续帧使用 RLE 压缩的差分数据
//
// 二进制结构 (AN00/AN10):
//   [0x00] 4B  魔数 ("AN00"/"AN10")
//   [0x04] 16B 画布信息 (OffsetX, OffsetY, Width, Height)
//   [0x14] 2B  帧表数量
//   [0x18] N*4 帧表
//   [+00]  2B  图像帧数量
//   每帧: OffsetX(4) + OffsetY(4) + Width(4) + Height(4) [+ Channels(4)] + PixelData
//
// AN21 RLE 压缩: 按通道交错 (rleStep=4), 连续相同值用计数编码,
//   count < 128 用 1 字节, >= 128 用 2 字节 (高位标记)
//
// 依赖: BitmapHelpers (像素 I/O), System.Drawing (尺寸查询)
// ============================================================================
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Kaguya_YaneKit.Formats.Picture;

public sealed class AnmInfo
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public string? StructurePrefixBase64 { get; set; }
    public int? GlobalChannels { get; set; }
    public int? GlobalCompressionMode { get; set; }
    public List<AnmFrameInfo> Frames { get; set; } = [];
}

public sealed class AnmFrameInfo
{
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public int Channels { get; set; } = 4;
    public int RleStep { get; set; } = 4;
    public int? PayloadSize { get; set; }
}

public static class ArcANM
{
    public static (string version, AnmInfo canvasInfo) Extract(string anmPath, string outputDir)
    {
        using var fs = File.OpenRead(anmPath);
        var magicBytes = new byte[4];
        fs.ReadExactly(magicBytes);
        var version = Encoding.ASCII.GetString(magicBytes);
        AnmInfo canvasInfo = version switch
        {
            "AN00" => ExtractAN00(anmPath, outputDir),
            "AN10" => ExtractAN10(anmPath, outputDir),
            "AN20" => ExtractAN20(anmPath, outputDir),
            "AN21" => ExtractAN21(anmPath, outputDir),
            _ => throw new NotSupportedException($"Unsupported ANM version: {version}")
        };
        return (version, canvasInfo);
    }

    public static void Create(string inputDir, string anmPath, string version, AnmInfo canvasInfo)
    {
        var imagePaths = Directory.GetFiles(inputDir, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (imagePaths.Count == 0)
        {
            throw new FileNotFoundException("No .png files were found in the input directory.");
        }

        switch (version.ToUpperInvariant())
        {
            case "AN00": CreateAN00(imagePaths, anmPath, canvasInfo); break;
            case "AN10": CreateANXX(imagePaths, anmPath, "AN10", 4, canvasInfo); break;
            case "AN20": CreateAN20(imagePaths, anmPath, 4, canvasInfo); break;
            case "AN21": CreateAN21(imagePaths, anmPath, canvasInfo); break;
            default: throw new NotSupportedException($"Unsupported ANM pack version: {version}");
        }
    }

    private static AnmInfo ExtractAN00(string path, string outDir)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var baseInfo = ReadBaseInfo(reader);
        reader.BaseStream.Position = 0x14;
        var frameCount = reader.ReadInt16();
        reader.BaseStream.Position = 0x18 + frameCount * 4;
        var imageCount = reader.ReadInt16();
        var prefixEnd = reader.BaseStream.Position;
        baseInfo.StructurePrefixBase64 = Convert.ToBase64String(File.ReadAllBytes(path).AsSpan(0, (int)prefixEnd));
        Directory.CreateDirectory(outDir);
        var baseName = Path.GetFileNameWithoutExtension(path);
        for (var i = 0; i < imageCount; i++)
        {
            var offsetX = reader.ReadInt32();
            var offsetY = reader.ReadInt32();
            var width = reader.ReadUInt32();
            var height = reader.ReadUInt32();
            baseInfo.Frames.Add(new AnmFrameInfo { OffsetX = offsetX, OffsetY = offsetY, Width = width, Height = height, Channels = 4 });
            var pixels = reader.ReadBytes((int)(width * height * 4));
            SavePixelsAsPng(pixels, (int)width, (int)height, Path.Combine(outDir, $"{baseName}_{i:D4}.png"));
        }
        return baseInfo;
    }

    private static void CreateAN00(List<string> imagePaths, string anmPath, AnmInfo canvasInfo)
    {
        if (!string.IsNullOrEmpty(canvasInfo.StructurePrefixBase64) && canvasInfo.Frames.Count > 0)
        {
            CreateWithPreservedRecords(imagePaths, anmPath, canvasInfo, hasChannels: false);
            return;
        }

        using var writer = new BinaryWriter(File.Create(anmPath));
        writer.Write(Encoding.ASCII.GetBytes("AN00"));
        WriteBaseInfo(writer, canvasInfo.Width, canvasInfo.Height, canvasInfo.OffsetX, canvasInfo.OffsetY);
        writer.Write(0);
        writer.Write((short)imagePaths.Count);
        writer.Write((short)0);
        for (var i = 0; i < imagePaths.Count; i++) writer.Write(0);
        writer.Write((short)imagePaths.Count);
        foreach (var imagePath in imagePaths)
        {
            var (w, h) = GetImageDimensions(imagePath);
            var pixels = ReadPixelsFromPng(imagePath);
            writer.Write(0);
            writer.Write(0);
            writer.Write(w);
            writer.Write(h);
            writer.Write(pixels);
        }
    }

    private static AnmInfo ExtractAN10(string path, string outDir) => ExtractANXX(path, outDir);

    private static AnmInfo ExtractAN20(string path, string outDir)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        _ = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var imageCountOffset = SkipAN20FrameTable(reader);
        reader.BaseStream.Position = imageCountOffset;
        var imageCount = reader.ReadInt16();
        var baseInfo = new AnmInfo
        {
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32()
        };
        var compressionMode = reader.ReadUInt16();
        baseInfo.GlobalCompressionMode = compressionMode;
        baseInfo.StructurePrefixBase64 = Convert.ToBase64String(File.ReadAllBytes(path).AsSpan(0, (int)reader.BaseStream.Position));
        Directory.CreateDirectory(outDir);
        var baseName = Path.GetFileNameWithoutExtension(path);
        for (var i = 0; i < imageCount; i++)
        {
            var offsetX = reader.ReadInt32();
            var offsetY = reader.ReadInt32();
            var width = reader.ReadUInt32();
            var height = reader.ReadUInt32();
            var channels = reader.ReadInt32();
            var payloadSize = reader.ReadInt32();
            var payload = reader.ReadBytes(payloadSize);
            var unpackedSize = checked((int)(width * height * (uint)channels));
            var pixels = compressionMode switch
            {
                3 => DecompressBmr(payload, unpackedSize),
                4 => DecompressAn20LzssBlock(payload, unpackedSize),
                _ => throw new NotSupportedException($"Unsupported AN20 compression mode: {compressionMode}")
            };
            baseInfo.Frames.Add(new AnmFrameInfo
            {
                OffsetX = offsetX,
                OffsetY = offsetY,
                Width = width,
                Height = height,
                Channels = channels,
                PayloadSize = payloadSize
            });
            var pixels32 = BitmapHelpers.ToBgra32(pixels, (int)width, (int)height, channels * 8);
            SavePixelsAsPng(pixels32, (int)width, (int)height, Path.Combine(outDir, $"{baseName}_{i:D4}.png"));
        }
        return baseInfo;
    }

    private static AnmInfo ExtractANXX(string path, string outDir)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        _ = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var baseInfo = ReadBaseInfo(reader);
        reader.BaseStream.Position = 0x14;
        var frameCount = reader.ReadInt16();
        reader.BaseStream.Position = 0x18 + frameCount * 4;
        var imageCount = reader.ReadInt16();
        var prefixEnd = reader.BaseStream.Position;
        baseInfo.StructurePrefixBase64 = Convert.ToBase64String(File.ReadAllBytes(path).AsSpan(0, (int)prefixEnd));
        Directory.CreateDirectory(outDir);
        var baseName = Path.GetFileNameWithoutExtension(path);
        for (var i = 0; i < imageCount; i++)
        {
            var offsetX = reader.ReadInt32();
            var offsetY = reader.ReadInt32();
            var width = reader.ReadUInt32();
            var height = reader.ReadUInt32();
            var channels = reader.ReadInt32();
            baseInfo.Frames.Add(new AnmFrameInfo { OffsetX = offsetX, OffsetY = offsetY, Width = width, Height = height, Channels = channels });
            var pixels = reader.ReadBytes((int)(width * height * channels));
            var pixels32 = BitmapHelpers.ToBgra32(pixels, (int)width, (int)height, channels * 8);
            SavePixelsAsPng(pixels32, (int)width, (int)height, Path.Combine(outDir, $"{baseName}_{i:D4}.png"));
        }
        return baseInfo;
    }

    private static void CreateANXX(List<string> imagePaths, string anmPath, string version, int channels, AnmInfo canvasInfo)
    {
        if (!string.IsNullOrEmpty(canvasInfo.StructurePrefixBase64) && canvasInfo.Frames.Count > 0)
        {
            CreateWithPreservedRecords(imagePaths, anmPath, canvasInfo, hasChannels: version != "AN00");
            return;
        }

        using var writer = new BinaryWriter(File.Create(anmPath));
        writer.Write(Encoding.ASCII.GetBytes(version));
        WriteBaseInfo(writer, canvasInfo.Width, canvasInfo.Height, canvasInfo.OffsetX, canvasInfo.OffsetY);
        writer.Write(0);
        writer.Write((short)imagePaths.Count);
        writer.Write((short)0);
        for (var i = 0; i < imagePaths.Count; i++) writer.Write(0);
        writer.Write((short)imagePaths.Count);
        foreach (var imagePath in imagePaths)
        {
            var (w, h) = GetImageDimensions(imagePath);
            var pixels = ReadPixelsFromPng(imagePath);
            writer.Write(0);
            writer.Write(0);
            writer.Write(w);
            writer.Write(h);
            writer.Write(channels);
            writer.Write(pixels);
        }
    }

    private static void CreateAN20(List<string> imagePaths, string anmPath, int channels, AnmInfo canvasInfo)
    {
        if (!string.IsNullOrEmpty(canvasInfo.StructurePrefixBase64) && canvasInfo.Frames.Count > 0)
        {
            CreateAN20Preserved(imagePaths, anmPath, canvasInfo);
            return;
        }

        using var writer = new BinaryWriter(File.Create(anmPath));
        writer.Write(Encoding.ASCII.GetBytes("AN20"));
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)imagePaths.Count);
        WriteBaseInfo(writer, canvasInfo.Width, canvasInfo.Height, canvasInfo.OffsetX, canvasInfo.OffsetY);
        writer.Write((ushort)4);
        foreach (var imagePath in imagePaths)
        {
            var (w, h) = GetImageDimensions(imagePath);
            var pixels = ReadPixelsFromPng(imagePath);
            writer.Write(0);
            writer.Write(0);
            writer.Write(w);
            writer.Write(h);
            writer.Write(channels);
            var lzssStream = CompressLzss(pixels);
            writer.Write(checked(lzssStream.Length + 4));
            writer.Write(pixels.Length);
            writer.Write(lzssStream);
        }
    }

    private static AnmInfo ExtractAN21(string path, string outDir)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var baseInfo = ReadBaseInfo(reader);
        reader.BaseStream.Position = 4;
        var tableCount = reader.ReadUInt16();
        reader.BaseStream.Position = 8;
        for (var i = 0; i < tableCount; i++)
        {
            switch (reader.ReadByte())
            {
                case 1: reader.BaseStream.Position += 8; break;
                case 2:
                case 3:
                case 4:
                case 5: reader.BaseStream.Position += 4; break;
            }
        }
        var count = reader.ReadUInt16();
        reader.BaseStream.Position += count * 8;
        reader.BaseStream.Position += 7;
        var frameCount = reader.ReadInt16();
        reader.BaseStream.Position += 0x12;
        var width = reader.ReadUInt32();
        var height = reader.ReadUInt32();
        var channels = reader.ReadInt32();
        var payloadStart = reader.BaseStream.Position;
        baseInfo.StructurePrefixBase64 = Convert.ToBase64String(File.ReadAllBytes(path).AsSpan(0, (int)payloadStart));
        baseInfo.GlobalChannels = channels;
        for (var i = 0; i < frameCount; i++)
        {
            baseInfo.Frames.Add(new AnmFrameInfo
            {
                OffsetX = baseInfo.OffsetX,
                OffsetY = baseInfo.OffsetY,
                Width = width,
                Height = height,
                Channels = channels
            });
        }
        Directory.CreateDirectory(outDir);
        var baseName = Path.GetFileNameWithoutExtension(path);
        byte[]? prevFramePixels = null;
        for (var i = 0; i < frameCount; i++)
        {
            byte[] currentFramePixels;
            if (i == 0)
            {
                currentFramePixels = reader.ReadBytes((int)(width * height * channels));
            }
            else
            {
                var rleStep = reader.ReadByte();
                baseInfo.Frames[i].RleStep = rleStep;
                var packedSize = reader.ReadUInt32();
                var packedData = reader.ReadBytes((int)packedSize);
                var diffPixels = DecompressRLE(packedData, (uint)(width * height * channels), rleStep);
                currentFramePixels = new byte[prevFramePixels!.Length];
                for (var p = 0; p < currentFramePixels.Length; p++)
                {
                    currentFramePixels[p] = (byte)(prevFramePixels[p] + diffPixels[p]);
                }
            }
            var pixels32 = BitmapHelpers.ToBgra32(currentFramePixels, (int)width, (int)height, channels * 8);
            SavePixelsAsPng(pixels32, (int)width, (int)height, Path.Combine(outDir, $"{baseName}_{i:D4}.png"));
            prevFramePixels = currentFramePixels;
        }
        return baseInfo;
    }

    private static void CreateAN21(List<string> imagePaths, string anmPath, AnmInfo canvasInfo)
    {
        if (!string.IsNullOrEmpty(canvasInfo.StructurePrefixBase64) && canvasInfo.Frames.Count > 0)
        {
            CreateAN21Preserved(imagePaths, anmPath, canvasInfo);
            return;
        }

        using var writer = new BinaryWriter(File.Create(anmPath));
        writer.Write(Encoding.ASCII.GetBytes("AN21"));
        writer.Write(new byte[] { 0x01, 0x00, 0x00, 0x00 });
        writer.Write(new byte[] { 0x00, 0x00, 0x00 });
        writer.Write(Encoding.ASCII.GetBytes("[PIC]10"));
        writer.Write((short)imagePaths.Count);
        writer.Write(new byte[0x12]);
        writer.Write(canvasInfo.OffsetX);
        writer.Write(canvasInfo.OffsetY);
        writer.Write(canvasInfo.Width);
        writer.Write(canvasInfo.Height);
        writer.Write(4);
        byte[]? prevPixels = null;
        for (var i = 0; i < imagePaths.Count; i++)
        {
            var currentPixels = ReadPixelsFromPng(imagePaths[i]);
            if (i == 0)
            {
                writer.Write(currentPixels);
            }
            else
            {
                var diffPixels = new byte[currentPixels.Length];
                for (var p = 0; p < diffPixels.Length; p++) diffPixels[p] = (byte)(currentPixels[p] - prevPixels![p]);
                const int rleStep = 4;
                var packedData = CompressRLE(diffPixels, rleStep);
                writer.Write((byte)rleStep);
                writer.Write((uint)packedData.Length);
                writer.Write(packedData);
            }
            prevPixels = currentPixels;
        }
    }

    private static byte[] DecompressRLE(byte[] input, uint unpackedSize, int rleStep)
    {
        var output = new byte[unpackedSize];
        using var ms = new MemoryStream(input);
        for (var i = 0; i < rleStep; ++i)
        {
            if (ms.Position >= ms.Length) break;
            var v1 = (byte)ms.ReadByte();
            output[i] = v1;
            var dst = i + rleStep;
            while (dst < output.Length)
            {
                if (ms.Position >= ms.Length) break;
                var v2 = (byte)ms.ReadByte();
                output[dst] = v2;
                dst += rleStep;
                if (v2 == v1)
                {
                    if (ms.Position >= ms.Length) break;
                    var count = ms.ReadByte();
                    if ((count & 0x80) != 0)
                    {
                        if (ms.Position >= ms.Length) break;
                        count = ms.ReadByte() + ((count & 0x7F) << 8) + 128;
                    }
                    for (var c = 0; c < count; c++)
                    {
                        if (dst >= output.Length) break;
                        output[dst] = v2;
                        dst += rleStep;
                    }
                    if (dst < output.Length)
                    {
                        if (ms.Position >= ms.Length) break;
                        v2 = (byte)ms.ReadByte();
                        output[dst] = v2;
                        dst += rleStep;
                    }
                }
                v1 = v2;
            }
        }
        return output;
    }

    private static byte[] CompressRLE(byte[] input, int rleStep)
    {
        using var outStream = new MemoryStream();
        for (var i = 0; i < rleStep; i++)
        {
            if (i >= input.Length) break;
            var v1 = input[i];
            outStream.WriteByte(v1);
            var src = i + rleStep;
            while (src < input.Length)
            {
                var v2 = input[src];
                outStream.WriteByte(v2);
                src += rleStep;
                if (v2 == v1)
                {
                    var count = 0;
                    while (src < input.Length && input[src] == v2 && count < (128 + 0x7FFF))
                    {
                        count++;
                        src += rleStep;
                    }
                    if (count > 0)
                    {
                        if (count < 128)
                        {
                            outStream.WriteByte((byte)count);
                        }
                        else
                        {
                            count -= 128;
                            outStream.WriteByte((byte)((count >> 8) | 0x80));
                            outStream.WriteByte((byte)(count & 0xFF));
                        }
                    }
                    if (src < input.Length)
                    {
                        v2 = input[src];
                        outStream.WriteByte(v2);
                        src += rleStep;
                    }
                }
                v1 = v2;
            }
        }
        return outStream.ToArray();
    }

    private static byte[] DecompressBmr(byte[] payload, int expectedSize)
    {
        if (payload.Length < 20)
        {
            throw new InvalidDataException("AN20/BMR payload is too short.");
        }

        if (payload[0] != (byte)'B' || payload[1] != (byte)'M' || payload[2] != (byte)'R')
        {
            throw new InvalidDataException("AN20 mode 3 payload does not start with BMR.");
        }

        var rleStep = payload[3];
        var finalSize = ReadInt32LE(payload, 4);
        var bwtIndex = ReadInt32LE(payload, 8);
        var bwtSize = ReadInt32LE(payload, 12);
        var huffmanSize = ReadInt32LE(payload, 16);
        if (finalSize != expectedSize)
        {
            throw new InvalidDataException($"AN20/BMR final size mismatch: header is {finalSize}, expected {expectedSize}.");
        }
        if (huffmanSize < 0 || 20 + huffmanSize > payload.Length)
        {
            throw new InvalidDataException("AN20/BMR Huffman stream size is invalid.");
        }

        var bitReader = new MsbBitReader(payload.AsMemory(20, huffmanSize));
        var root = ReadHuffmanNode(bitReader);
        var decoded = DecodeHuffman(bitReader, root, bwtSize);
        MoveToFrontDecode(decoded);
        InverseBwt(decoded, bwtIndex);
        return rleStep == 0 ? decoded : DecompressRLE(decoded, (uint)finalSize, rleStep);
    }

    private static byte[] DecompressLzss(byte[] input, int expectedSize)
    {
        var output = new byte[expectedSize];
        var window = new byte[0x1000];
        var reader = new MsbBitReader(input);
        var dst = 0;
        var windowPos = 1;

        while (dst < output.Length)
        {
            if (reader.ReadBit() != 0)
            {
                var value = (byte)reader.ReadBits(8);
                output[dst++] = value;
                window[windowPos] = value;
                windowPos = (windowPos + 1) & 0xFFF;
                continue;
            }

            var offset = reader.ReadBits(12);
            if (offset == 0)
            {
                break;
            }

            var length = reader.ReadBits(4) + 2;
            for (var i = 0; i < length && dst < output.Length; i++)
            {
                var value = window[(offset + i) & 0xFFF];
                output[dst++] = value;
                window[windowPos] = value;
                windowPos = (windowPos + 1) & 0xFFF;
            }
        }

        if (dst != output.Length)
        {
            throw new InvalidDataException($"AN20/LZSS unpacked size mismatch: decoded {dst}, expected {output.Length}.");
        }

        return output;
    }

    private static byte[] DecompressAn20LzssBlock(byte[] payload, int expectedSize)
    {
        if (payload.Length < 4)
        {
            throw new InvalidDataException("AN20/LZSS payload is too short.");
        }

        var unpackedSize = ReadInt32LE(payload, 0);
        if (unpackedSize != expectedSize)
        {
            throw new InvalidDataException($"AN20/LZSS unpacked size mismatch: header is {unpackedSize}, expected {expectedSize}.");
        }

        return DecompressLzss(payload[4..], expectedSize);
    }

    private static byte[] CompressLzss(byte[] input)
    {
        var writer = new MsbBitWriter();
        var positionsByHash = new Dictionary<int, List<int>>();
        var src = 0;
        while (src < input.Length)
        {
            var bestPos = -1;
            var bestLength = 0;
            if (src + 2 < input.Length)
            {
                var hash = Hash3(input, src);
                if (positionsByHash.TryGetValue(hash, out var candidates))
                {
                    for (var i = candidates.Count - 1; i >= 0; i--)
                    {
                        var candidate = candidates[i];
                        var distance = src - candidate;
                        if (distance > 0x1000)
                        {
                            candidates.RemoveRange(0, i + 1);
                            break;
                        }

                        var offset = (candidate + 1) & 0xFFF;
                        if (offset == 0)
                        {
                            continue;
                        }

                        var length = CountMatch(input, candidate, src, Math.Min(17, input.Length - src));
                        if (length > bestLength)
                        {
                            bestLength = length;
                            bestPos = candidate;
                            if (length == 17)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            if (bestLength >= 3)
            {
                writer.WriteBit(0);
                writer.WriteBits((bestPos + 1) & 0xFFF, 12);
                writer.WriteBits(bestLength - 2, 4);
                for (var i = 0; i < bestLength; i++)
                {
                    AddLzssPosition(positionsByHash, input, src + i);
                }

                src += bestLength;
                continue;
            }

            writer.WriteBit(1);
            writer.WriteBits(input[src], 8);
            AddLzssPosition(positionsByHash, input, src);
            src++;
        }

        writer.WriteBit(0);
        writer.WriteBits(0, 12);
        return writer.ToArray();
    }

    private static int Hash3(byte[] input, int offset)
    {
        return input[offset] | (input[offset + 1] << 8) | (input[offset + 2] << 16);
    }

    private static int CountMatch(byte[] input, int candidate, int current, int maxLength)
    {
        var length = 0;
        while (length < maxLength && input[candidate + length] == input[current + length])
        {
            length++;
        }

        return length;
    }

    private static void AddLzssPosition(Dictionary<int, List<int>> positionsByHash, byte[] input, int offset)
    {
        if (offset + 2 >= input.Length)
        {
            return;
        }

        var hash = Hash3(input, offset);
        if (!positionsByHash.TryGetValue(hash, out var positions))
        {
            positions = [];
            positionsByHash[hash] = positions;
        }

        positions.Add(offset);
    }

    private static HuffmanNode ReadHuffmanNode(MsbBitReader reader)
    {
        if (reader.ReadBit() == 0)
        {
            return new HuffmanNode(reader.ReadBits(8));
        }

        return new HuffmanNode(ReadHuffmanNode(reader), ReadHuffmanNode(reader));
    }

    private static byte[] DecodeHuffman(MsbBitReader reader, HuffmanNode root, int outputSize)
    {
        if (outputSize < 0)
        {
            throw new InvalidDataException("AN20/BMR decoded size is negative.");
        }

        var output = new byte[outputSize];
        for (var i = 0; i < output.Length; i++)
        {
            var node = root;
            while (!node.IsLeaf)
            {
                node = reader.ReadBit() == 0 ? node.Left! : node.Right!;
            }

            output[i] = (byte)node.Value;
        }

        return output;
    }

    private static void MoveToFrontDecode(byte[] data)
    {
        var table = new byte[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (byte)i;
        }

        for (var i = 0; i < data.Length; i++)
        {
            var index = data[i];
            var value = table[index];
            data[i] = value;
            while (index > 0)
            {
                table[index] = table[index - 1];
                index--;
            }

            table[0] = value;
        }
    }

    private static void InverseBwt(byte[] data, int primaryIndex)
    {
        if (data.Length == 0)
        {
            return;
        }
        if (primaryIndex < 0 || primaryIndex >= data.Length)
        {
            throw new InvalidDataException($"AN20/BMR BWT primary index is invalid: {primaryIndex}.");
        }

        var counts = new int[256];
        foreach (var value in data)
        {
            counts[value]++;
        }
        for (var i = 1; i < counts.Length; i++)
        {
            counts[i] += counts[i - 1];
        }

        var next = new int[data.Length];
        for (var i = data.Length - 1; i >= 0; i--)
        {
            var value = data[i];
            next[--counts[value]] = i;
        }

        var output = new byte[data.Length];
        var pos = next[primaryIndex];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = data[pos];
            pos = next[pos];
        }

        Buffer.BlockCopy(output, 0, data, 0, output.Length);
    }

    private static int ReadInt32LE(byte[] data, int offset)
    {
        return data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24);
    }

    private static void CreateWithPreservedRecords(List<string> imagePaths, string anmPath, AnmInfo canvasInfo, bool hasChannels)
    {
        if (imagePaths.Count != canvasInfo.Frames.Count)
        {
            throw new InvalidDataException($"ANM frame count changed: metadata has {canvasInfo.Frames.Count}, PNG directory has {imagePaths.Count}.");
        }

        var prefix = Convert.FromBase64String(canvasInfo.StructurePrefixBase64!);
        using var writer = new BinaryWriter(File.Create(anmPath));
        writer.Write(prefix);
        for (var i = 0; i < imagePaths.Count; i++)
        {
            var frame = canvasInfo.Frames[i];
            var (w, h) = GetImageDimensions(imagePaths[i]);
            if (w != frame.Width || h != frame.Height)
            {
                throw new InvalidDataException($"ANM frame {i:D4} size changed: metadata is {frame.Width}x{frame.Height}, PNG is {w}x{h}.");
            }

            writer.Write(frame.OffsetX);
            writer.Write(frame.OffsetY);
            writer.Write(frame.Width);
            writer.Write(frame.Height);
            if (hasChannels)
            {
                writer.Write(frame.Channels);
            }

            writer.Write(ConvertBgra32ToChannels(ReadPixelsFromPng(imagePaths[i]), frame.Channels));
        }
    }

    private static void CreateAN20Preserved(List<string> imagePaths, string anmPath, AnmInfo canvasInfo)
    {
        if (imagePaths.Count != canvasInfo.Frames.Count)
        {
            throw new InvalidDataException($"AN20 frame count changed: metadata has {canvasInfo.Frames.Count}, PNG directory has {imagePaths.Count}.");
        }

        var prefix = Convert.FromBase64String(canvasInfo.StructurePrefixBase64!);
        if (prefix.Length < 2)
        {
            throw new InvalidDataException("AN20 metadata prefix is too short.");
        }

        prefix[^2] = 4;
        prefix[^1] = 0;
        using var writer = new BinaryWriter(File.Create(anmPath));
        writer.Write(prefix);

        for (var i = 0; i < imagePaths.Count; i++)
        {
            var frame = canvasInfo.Frames[i];
            var (w, h) = GetImageDimensions(imagePaths[i]);
            if (w != frame.Width || h != frame.Height)
            {
                throw new InvalidDataException($"AN20 frame {i:D4} size changed: metadata is {frame.Width}x{frame.Height}, PNG is {w}x{h}.");
            }

            var pixels = ConvertBgra32ToChannels(ReadPixelsFromPng(imagePaths[i]), frame.Channels);
            var lzssStream = CompressLzss(pixels);
            writer.Write(frame.OffsetX);
            writer.Write(frame.OffsetY);
            writer.Write(frame.Width);
            writer.Write(frame.Height);
            writer.Write(frame.Channels);
            writer.Write(checked(lzssStream.Length + 4));
            writer.Write(pixels.Length);
            writer.Write(lzssStream);
        }
    }

    private static void CreateAN21Preserved(List<string> imagePaths, string anmPath, AnmInfo canvasInfo)
    {
        if (imagePaths.Count != canvasInfo.Frames.Count)
        {
            throw new InvalidDataException($"AN21 frame count changed: metadata has {canvasInfo.Frames.Count}, PNG directory has {imagePaths.Count}.");
        }

        var firstFrame = canvasInfo.Frames[0];
        var channels = canvasInfo.GlobalChannels ?? firstFrame.Channels;
        using var writer = new BinaryWriter(File.Create(anmPath));
        writer.Write(Convert.FromBase64String(canvasInfo.StructurePrefixBase64!));

        byte[]? prevPixels = null;
        for (var i = 0; i < imagePaths.Count; i++)
        {
            var frame = canvasInfo.Frames[i];
            var (w, h) = GetImageDimensions(imagePaths[i]);
            if (w != frame.Width || h != frame.Height)
            {
                throw new InvalidDataException($"AN21 frame {i:D4} size changed: metadata is {frame.Width}x{frame.Height}, PNG is {w}x{h}.");
            }

            var currentPixels = ConvertBgra32ToChannels(ReadPixelsFromPng(imagePaths[i]), channels);
            if (i == 0)
            {
                writer.Write(currentPixels);
            }
            else
            {
                var diffPixels = new byte[currentPixels.Length];
                for (var p = 0; p < diffPixels.Length; p++) diffPixels[p] = (byte)(currentPixels[p] - prevPixels![p]);
                var rleStep = frame.RleStep;
                var packedData = CompressRLE(diffPixels, rleStep);
                writer.Write((byte)rleStep);
                writer.Write((uint)packedData.Length);
                writer.Write(packedData);
            }

            prevPixels = currentPixels;
        }
    }

    private static byte[] ConvertBgra32ToChannels(byte[] bgraPixels, int channels)
    {
        return channels switch
        {
            4 => bgraPixels,
            3 => StripAlpha(bgraPixels),
            1 => BitmapHelpers.ToGrayscale(bgraPixels),
            _ => throw new NotSupportedException($"Unsupported ANM channel count: {channels}")
        };
    }

    private static byte[] StripAlpha(byte[] bgraPixels)
    {
        var output = new byte[bgraPixels.Length / 4 * 3];
        var dst = 0;
        for (var src = 0; src < bgraPixels.Length; src += 4)
        {
            output[dst++] = bgraPixels[src];
            output[dst++] = bgraPixels[src + 1];
            output[dst++] = bgraPixels[src + 2];
        }

        return output;
    }

    private static AnmInfo ReadBaseInfo(BinaryReader reader)
    {
        reader.BaseStream.Position = 4;
        return new AnmInfo
        {
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32()
        };
    }

    private static void WriteBaseInfo(BinaryWriter writer, uint width, uint height, int offsetX, int offsetY)
    {
        writer.Write(offsetX);
        writer.Write(offsetY);
        writer.Write(width);
        writer.Write(height);
    }

    private static long SkipAN20FrameTable(BinaryReader reader)
    {
        reader.BaseStream.Position = 4;
        var tableCount = reader.ReadInt16();
        reader.BaseStream.Position = 8;
        for (var i = 0; i < tableCount; ++i)
        {
            switch (reader.ReadByte())
            {
                case 1: reader.BaseStream.Seek(8, SeekOrigin.Current); break;
                case 2:
                case 3:
                case 4:
                case 5: reader.BaseStream.Seek(4, SeekOrigin.Current); break;
            }
        }
        var count = reader.ReadUInt16();
        reader.BaseStream.Seek(count * 8, SeekOrigin.Current);
        return reader.BaseStream.Position;
    }

    private static byte[] ReadPixelsFromPng(string filePath) => BitmapHelpers.ReadBottomUpPixelsFromImage(filePath);
    private static void SavePixelsAsPng(byte[] bgraPixels, int width, int height, string filePath) => BitmapHelpers.SavePngFromBottomUpPixels(bgraPixels, width, height, filePath);

    private static (uint, uint) GetImageDimensions(string filePath)
    {
        using var bmp = new Bitmap(filePath);
        return ((uint)bmp.Width, (uint)bmp.Height);
    }

    private sealed class HuffmanNode
    {
        public HuffmanNode(int value)
        {
            Value = value;
        }

        public HuffmanNode(HuffmanNode left, HuffmanNode right)
        {
            Left = left;
            Right = right;
            Value = -1;
        }

        public int Value { get; }
        public HuffmanNode? Left { get; }
        public HuffmanNode? Right { get; }
        public bool IsLeaf => Left is null && Right is null;
    }

    private sealed class MsbBitReader
    {
        private readonly ReadOnlyMemory<byte> data;
        private int byteOffset;
        private int mask = 0x80;

        public MsbBitReader(ReadOnlyMemory<byte> data)
        {
            this.data = data;
        }

        public int ReadBit()
        {
            if (byteOffset >= data.Length)
            {
                throw new EndOfStreamException("Unexpected end of ANM bitstream.");
            }

            var bit = (data.Span[byteOffset] & mask) != 0 ? 1 : 0;
            mask >>= 1;
            if (mask == 0)
            {
                mask = 0x80;
                byteOffset++;
            }

            return bit;
        }

        public int ReadBits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | ReadBit();
            }

            return value;
        }
    }

    private sealed class MsbBitWriter
    {
        private readonly MemoryStream stream = new();
        private int current;
        private int mask = 0x80;

        public void WriteBit(int bit)
        {
            if (bit != 0)
            {
                current |= mask;
            }

            mask >>= 1;
            if (mask == 0)
            {
                stream.WriteByte((byte)current);
                current = 0;
                mask = 0x80;
            }
        }

        public void WriteBits(int value, int count)
        {
            for (var bit = count - 1; bit >= 0; bit--)
            {
                WriteBit((value >> bit) & 1);
            }
        }

        public byte[] ToArray()
        {
            if (mask != 0x80)
            {
                stream.WriteByte((byte)current);
                current = 0;
                mask = 0x80;
            }

            return stream.ToArray();
        }
    }
}
