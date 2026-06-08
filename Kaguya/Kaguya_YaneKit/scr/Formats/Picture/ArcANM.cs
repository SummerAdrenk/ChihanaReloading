// ============================================================================
// ArcANM.cs
// ANM 鍔ㄧ敾绮剧伒鏍煎紡鐨勫簳灞傝В鍖?鎵撳寘瀹炵幇
//
// 鏀寔鐨勭増鏈?
//   AN00 -- 鍩虹鏍煎紡: 甯ц〃 + 鏈帇缂?BGRA 鍍忕礌鏁版嵁
//   AN01 -- 鎵╁睍鏍煎紡: 鍚?AN00 缁撴瀯浣嗗抚鍍忕礌鏀寔鍙彉閫氶亾鏁?(channels)
//   AN20 -- 鍙樹綋鏍煎紡: 涓嶅悓鐨勫抚琛ㄥ竷灞€, 鐢诲竷淇℃伅鍦ㄥ抚鏁版嵁鍓?(闈炴枃浠跺ご)
//   AN21 -- 宸垎甯ф牸寮? 棣栧抚涓哄畬鏁村儚绱? 鍚庣画甯т娇鐢?RLE 鍘嬬缉鐨勫樊鍒嗘暟鎹?//
// 浜岃繘鍒剁粨鏋?(AN00/AN01):
//   [0x00] 4B  榄旀暟 ("AN00"/"AN01")
//   [0x04] 16B 鐢诲竷淇℃伅 (OffsetX, OffsetY, Width, Height)
//   [0x14] 2B  甯ц〃鏁伴噺
//   [0x18] N*4 甯ц〃
//   [+00]  2B  鍥惧儚甯ф暟閲?//   姣忓抚: OffsetX(4) + OffsetY(4) + Width(4) + Height(4) [+ Channels(4)] + PixelData
//
// AN21 RLE 鍘嬬缉: 鎸夐€氶亾浜ら敊 (rleStep=4), 杩炵画鐩稿悓鍊肩敤璁℃暟缂栫爜,
//   count < 128 鐢?1 瀛楄妭, >= 128 鐢?2 瀛楄妭 (楂樹綅鏍囪)
//
// 渚濊禆: BitmapHelpers (鍍忕礌 I/O), System.Drawing (灏哄鏌ヨ)
// ============================================================================
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Kaguya_YaneKit.Formats.Archive;

namespace Kaguya_YaneKit.Formats.Picture;

public sealed class AnmInfo
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public AnmAnimationControlInfo? AnimationControl { get; set; }
    public int? GlobalPixelChannels { get; set; }
    public int? GlobalCompressionMode { get; set; }
    public List<AnmFrameInfo> Frames { get; set; } = [];
}

public sealed class AnmAnimationControlInfo
{
    public string Format { get; set; } = "";
    public string Magic { get; set; } = "";
    public int ControlHeaderWord { get; set; }
    public AnmControlCanvasInfo? LegacyCanvas { get; set; }
    public List<AnmAnimationCommandInfo> Commands { get; set; } = [];
    public List<AnmAnimationBranchInfo> Branches { get; set; } = [];
    public List<AnmLegacyControlPairInfo> LegacyPairs { get; set; } = [];
    public AnmAnimationTailInfo? Tail { get; set; }
}

public sealed class AnmControlCanvasInfo
{
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
}

public sealed class AnmAnimationTailInfo
{
    public string Format { get; set; } = "";
    public int ImageCount { get; set; }
    public string? PicSubVersion { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public int? CompressionMode { get; set; }
    public int? PixelChannels { get; set; }
    public int? FrameOffsetX { get; set; }
    public int? FrameOffsetY { get; set; }
    public uint? FrameWidth { get; set; }
    public uint? FrameHeight { get; set; }
    public int? FramePixelChannels { get; set; }
}

public sealed class AnmAnimationCommandInfo
{
    public string Command { get; set; } = "";
    public int? FrameSlot { get; set; }
    public int? FrameIndex { get; set; }
    public int? WaitTicks { get; set; }
    public int? BranchIndex { get; set; }
    public int? ResultCode { get; set; }
}

public sealed class AnmAnimationBranchInfo
{
    public int JumpCommandIndex { get; set; }
    public int RepeatCount { get; set; }
}

public sealed class AnmLegacyControlPairInfo
{
    public int DelayTicks { get; set; }
    public int FrameIndex { get; set; }
}

public sealed class AnmFrameInfo
{
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public int PixelChannels { get; set; } = 4;
    public int RleInterleaveStep { get; set; } = 4;
    public int? FrameImageDataByteCount { get; set; }
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
            "AN01" => ExtractAN01(anmPath, outputDir),
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
            if (IsNoImageAnimation(canvasInfo))
            {
                using var writer = new BinaryWriter(File.Create(anmPath));
                writer.Write(GetStructurePrefixBytes(canvasInfo));
                return;
            }

            throw new FileNotFoundException("No .png files were found in the input directory.");
        }

        switch (version.ToUpperInvariant())
        {
            case "AN00": CreateAN00(imagePaths, anmPath, canvasInfo); break;
            case "AN01": CreateANXX(imagePaths, anmPath, "AN01", 4, canvasInfo); break;
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
        SetStructurePrefix(baseInfo, File.ReadAllBytes(path), (int)prefixEnd);
        Directory.CreateDirectory(outDir);
        var baseName = Path.GetFileNameWithoutExtension(path);
        for (var i = 0; i < imageCount; i++)
        {
            var offsetX = reader.ReadInt32();
            var offsetY = reader.ReadInt32();
            var width = reader.ReadUInt32();
            var height = reader.ReadUInt32();
            baseInfo.Frames.Add(new AnmFrameInfo { OffsetX = offsetX, OffsetY = offsetY, Width = width, Height = height, PixelChannels = 4 });
            var pixels = reader.ReadBytes((int)(width * height * 4));
            SavePixelsAsPng(pixels, (int)width, (int)height, Path.Combine(outDir, $"{baseName}_{i:D4}.png"));
        }
        return baseInfo;
    }

    private static void CreateAN00(List<string> imagePaths, string anmPath, AnmInfo canvasInfo)
    {
        if (canvasInfo.AnimationControl is not null && canvasInfo.Frames.Count > 0)
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

    private static AnmInfo ExtractAN01(string path, string outDir) => ExtractANXX(path, outDir);

    private static AnmInfo ExtractAN20(string path, string outDir)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        _ = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var imageCountOffset = SkipAN20FrameTable(reader);
        var data = File.ReadAllBytes(path);
        if (imageCountOffset >= data.Length)
        {
            var noImageInfo = new AnmInfo();
            SetStructurePrefix(noImageInfo, data, data.Length);
            Directory.CreateDirectory(outDir);
            return noImageInfo;
        }

        reader.BaseStream.Position = imageCountOffset;
        var imageCount = reader.ReadInt16();
        if (imageCount == 0)
        {
            var noImageInfo = new AnmInfo();
            SetStructurePrefix(noImageInfo, data, (int)reader.BaseStream.Position);
            Directory.CreateDirectory(outDir);
            return noImageInfo;
        }

        var baseInfo = new AnmInfo
        {
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32()
        };
        var frameDataStart = reader.BaseStream.Position;
        if (TryExtractAN20Uncompressed(path, outDir, imageCount, baseInfo, frameDataStart, out var uncompressedInfo))
        {
            return uncompressedInfo;
        }

        var compressionMode = reader.ReadUInt16();
        baseInfo.GlobalCompressionMode = compressionMode;
        SetStructurePrefix(baseInfo, File.ReadAllBytes(path), (int)reader.BaseStream.Position);
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
            var unpackedSize = CheckedPixelByteCount(width, height, channels, $"AN20 frame {i:D4}");
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
                PixelChannels = channels,
                FrameImageDataByteCount = payloadSize
            });
            var pixels32 = BitmapHelpers.ToBgra32(pixels, (int)width, (int)height, channels * 8);
            SavePixelsAsPng(pixels32, (int)width, (int)height, Path.Combine(outDir, $"{baseName}_{i:D4}.png"));
        }
        return baseInfo;
    }

    private static bool TryExtractAN20Uncompressed(string path, string outDir, int imageCount, AnmInfo baseInfo, long frameDataStart, out AnmInfo result)
    {
        result = baseInfo;
        if (imageCount <= 0)
        {
            return false;
        }

        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = frameDataStart;
        var frames = new List<(AnmFrameInfo Frame, long PayloadOffset, int ByteCount)>();
        try
        {
            for (var i = 0; i < imageCount; i++)
            {
                var offsetX = reader.ReadInt32();
                var offsetY = reader.ReadInt32();
                var width = reader.ReadUInt32();
                var height = reader.ReadUInt32();
                var channels = reader.ReadInt32();
                var byteCount = CheckedPixelByteCount(width, height, channels, $"AN20 uncompressed frame {i:D4}");
                if (reader.BaseStream.Position + byteCount > reader.BaseStream.Length)
                {
                    throw new InvalidDataException($"AN20 uncompressed frame {i:D4} exceeds file size.");
                }

                frames.Add((new AnmFrameInfo
                {
                    OffsetX = offsetX,
                    OffsetY = offsetY,
                    Width = width,
                    Height = height,
                    PixelChannels = channels,
                    FrameImageDataByteCount = byteCount
                }, reader.BaseStream.Position, byteCount));
                reader.BaseStream.Position += byteCount;
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return false;
        }

        var data = File.ReadAllBytes(path);
        result = new AnmInfo
        {
            OffsetX = baseInfo.OffsetX,
            OffsetY = baseInfo.OffsetY,
            Width = baseInfo.Width,
            Height = baseInfo.Height,
            GlobalCompressionMode = 0,
            Frames = frames.Select(f => f.Frame).ToList()
        };
        SetStructurePrefix(result, data, (int)frameDataStart);

        Directory.CreateDirectory(outDir);
        var baseName = Path.GetFileNameWithoutExtension(path);
        for (var i = 0; i < frames.Count; i++)
        {
            var (frame, payloadOffset, byteCount) = frames[i];
            var pixels = data.AsSpan((int)payloadOffset, byteCount).ToArray();
            var pixels32 = BitmapHelpers.ToBgra32(pixels, (int)frame.Width, (int)frame.Height, frame.PixelChannels * 8);
            SavePixelsAsPng(pixels32, (int)frame.Width, (int)frame.Height, Path.Combine(outDir, $"{baseName}_{i:D4}.png"));
        }

        return true;
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
        SetStructurePrefix(baseInfo, File.ReadAllBytes(path), (int)prefixEnd);
        Directory.CreateDirectory(outDir);
        var baseName = Path.GetFileNameWithoutExtension(path);
        for (var i = 0; i < imageCount; i++)
        {
            var offsetX = reader.ReadInt32();
            var offsetY = reader.ReadInt32();
            var width = reader.ReadUInt32();
            var height = reader.ReadUInt32();
            var channels = reader.ReadInt32();
            baseInfo.Frames.Add(new AnmFrameInfo { OffsetX = offsetX, OffsetY = offsetY, Width = width, Height = height, PixelChannels = channels });
            var pixels = reader.ReadBytes((int)(width * height * channels));
            var pixels32 = BitmapHelpers.ToBgra32(pixels, (int)width, (int)height, channels * 8);
            SavePixelsAsPng(pixels32, (int)width, (int)height, Path.Combine(outDir, $"{baseName}_{i:D4}.png"));
        }
        return baseInfo;
    }

    private static void CreateANXX(List<string> imagePaths, string anmPath, string version, int channels, AnmInfo canvasInfo)
    {
        if (canvasInfo.AnimationControl is not null && canvasInfo.Frames.Count > 0)
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
        if (canvasInfo.AnimationControl is not null && canvasInfo.Frames.Count > 0)
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
        var data = File.ReadAllBytes(path);
        var baseInfo = ReadBaseInfo(reader);

        var tailStart = SkipAN20FrameTable(reader);
        if (!TryParseNewAnimationTailAt(data, checked((int)tailStart), out var tail, out var payloadStart))
        {
            throw new InvalidDataException("AN21 animation image header could not be fully parsed.");
        }

        baseInfo.OffsetX = tail.OffsetX;
        baseInfo.OffsetY = tail.OffsetY;
        baseInfo.Width = tail.Width;
        baseInfo.Height = tail.Height;
        SetStructurePrefix(baseInfo, data, payloadStart);

        var frameCount = tail.ImageCount;
        var frameOffsetX = tail.FrameOffsetX ?? tail.OffsetX;
        var frameOffsetY = tail.FrameOffsetY ?? tail.OffsetY;
        var width = tail.FrameWidth ?? tail.Width;
        var height = tail.FrameHeight ?? tail.Height;
        var channels = tail.FramePixelChannels ?? tail.PixelChannels ?? 4;
        reader.BaseStream.Position = payloadStart;
        baseInfo.GlobalPixelChannels = channels;
        for (var i = 0; i < frameCount; i++)
        {
            baseInfo.Frames.Add(new AnmFrameInfo
            {
                OffsetX = frameOffsetX,
                OffsetY = frameOffsetY,
                Width = width,
                Height = height,
                PixelChannels = channels
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
                baseInfo.Frames[i].RleInterleaveStep = rleStep;
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
        if (canvasInfo.AnimationControl is not null && canvasInfo.Frames.Count > 0)
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

    private static uint ReadUInt32LE(byte[] data, int offset)
    {
        return data[offset]
            | ((uint)data[offset + 1] << 8)
            | ((uint)data[offset + 2] << 16)
            | ((uint)data[offset + 3] << 24);
    }

    private static void WriteInt32LE(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static int ToUInt16(byte[] data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8);
    }

    private static void SetStructurePrefix(AnmInfo info, byte[] data, int length)
    {
        var prefix = data.AsSpan(0, length).ToArray();
        info.AnimationControl = TryParseAnimationControl(prefix);
        if (info.AnimationControl is null)
        {
            throw new InvalidDataException("ANM animation prefix could not be fully parsed.");
        }
    }

    private static byte[] GetStructurePrefixBytes(AnmInfo info)
    {
        if (info.AnimationControl is null)
        {
            throw new InvalidDataException("ANM metadata is missing structured AnimationControl.");
        }

        return BuildAnimationControlPrefix(info.AnimationControl);
    }

    private static bool IsNoImageAnimation(AnmInfo info)
    {
        return info.AnimationControl?.Tail?.Format == "NewNoImageTail"
            && info.AnimationControl.Tail.ImageCount == 0
            && info.Frames.Count == 0;
    }

    private static AnmAnimationControlInfo? TryParseAnimationControl(byte[] prefix)
    {
        if (prefix.Length < 4)
        {
            return null;
        }

        var magic = Encoding.ASCII.GetString(prefix, 0, 4);
        return magic switch
        {
            "AN00" or "AN01" => TryParseLegacyAnimationControl(prefix),
            "AN20" or "AN21" => TryParseNewAnimationControl(prefix),
            _ => null
        };
    }

    private static AnmAnimationControlInfo? TryParseNewAnimationControl(byte[] prefix)
    {
        var pos = 4;
        if (!TryReadUInt16(prefix, ref pos, out var commandCount) || pos + 2 > prefix.Length)
        {
            return null;
        }

        var headerOffset = pos;
        pos += 2;
        var commands = new List<AnmAnimationCommandInfo>();
        for (var i = 0; i < commandCount; i++)
        {
            if (pos >= prefix.Length)
            {
                return null;
            }

            var opcode = prefix[pos++];
            AnmAnimationCommandInfo command;
            switch (opcode)
            {
                case 0:
                    command = new AnmAnimationCommandInfo { Command = "NoOp" };
                    break;
                case 1:
                    if (!TryReadInt32(prefix, ref pos, out var op1a) || !TryReadInt32(prefix, ref pos, out var op1b))
                    {
                        return null;
                    }
                    command = new AnmAnimationCommandInfo
                    {
                        Command = "SetFrameMapping",
                        FrameSlot = op1a,
                        FrameIndex = op1b
                    };
                    break;
                case 2:
                    if (!TryReadInt32(prefix, ref pos, out var waitTicks))
                    {
                        return null;
                    }
                    command = new AnmAnimationCommandInfo
                    {
                        Command = "WaitTicks",
                        WaitTicks = waitTicks
                    };
                    break;
                case 3:
                    if (!TryReadInt32(prefix, ref pos, out var loadBranchIndex))
                    {
                        return null;
                    }
                    command = new AnmAnimationCommandInfo
                    {
                        Command = "LoadBranchRepeatCounter",
                        BranchIndex = loadBranchIndex
                    };
                    break;
                case 4:
                    if (!TryReadInt32(prefix, ref pos, out var loopBranchIndex))
                    {
                        return null;
                    }
                    command = new AnmAnimationCommandInfo
                    {
                        Command = "DecrementBranchAndJumpIfPositive",
                        BranchIndex = loopBranchIndex
                    };
                    break;
                case 5:
                    if (!TryReadInt32(prefix, ref pos, out var resultCode))
                    {
                        return null;
                    }
                    command = new AnmAnimationCommandInfo
                    {
                        Command = "SetAnimationResultCode",
                        ResultCode = resultCode
                    };
                    break;
                default:
                    return null;
            }

            commands.Add(command);
        }

        if (!TryReadUInt16(prefix, ref pos, out var branchCount))
        {
            return null;
        }

        var branches = new List<AnmAnimationBranchInfo>();
        for (var i = 0; i < branchCount; i++)
        {
            if (!TryReadInt32(prefix, ref pos, out var value0) || !TryReadInt32(prefix, ref pos, out var value1))
            {
                return null;
            }

            branches.Add(new AnmAnimationBranchInfo { JumpCommandIndex = value0, RepeatCount = value1 });
        }

        var tailBytes = prefix.AsSpan(pos).ToArray();
        var tail = TryParseNewAnimationTail(tailBytes)
            ?? throw new InvalidDataException("ANM new animation tail could not be fully parsed.");
        return new AnmAnimationControlInfo
        {
            Format = "NewControlCommands",
            Magic = Encoding.ASCII.GetString(prefix, 0, 4),
            ControlHeaderWord = ToUInt16(prefix, headerOffset),
            Commands = commands,
            Branches = branches,
            Tail = tail
        };
    }

    private static AnmAnimationControlInfo? TryParseLegacyAnimationControl(byte[] prefix)
    {
        var pos = 20;
        if (prefix.Length < pos || !TryReadUInt16(prefix, ref pos, out var pairCount) || pos + 2 > prefix.Length)
        {
            return null;
        }

        var headerOffset = pos;
        pos += 2;
        var pairs = new List<AnmLegacyControlPairInfo>();
        for (var i = 0; i < pairCount; i++)
        {
            if (!TryReadUInt16(prefix, ref pos, out var value0) || !TryReadUInt16(prefix, ref pos, out var rawValue1))
            {
                return null;
            }

            pairs.Add(new AnmLegacyControlPairInfo
            {
                DelayTicks = value0,
                FrameIndex = rawValue1 == 0xFFFF ? -1 : rawValue1
            });
        }

        var tailBytes = prefix.AsSpan(pos).ToArray();
        var tail = TryParseLegacyAnimationTail(tailBytes)
            ?? throw new InvalidDataException("ANM legacy animation tail could not be fully parsed.");
        return new AnmAnimationControlInfo
        {
            Format = "LegacyControlPairs",
            Magic = Encoding.ASCII.GetString(prefix, 0, 4),
            ControlHeaderWord = ToUInt16(prefix, headerOffset),
            LegacyCanvas = new AnmControlCanvasInfo
            {
                OffsetX = ReadInt32LE(prefix, 4),
                OffsetY = ReadInt32LE(prefix, 8),
                Width = ReadUInt32LE(prefix, 12),
                Height = ReadUInt32LE(prefix, 16)
            },
            LegacyPairs = pairs,
            Tail = tail
        };
    }

    private static AnmAnimationTailInfo? TryParseNewAnimationTail(byte[] tail)
    {
        if (tail.Length == 0 || (tail.Length == 2 && BitConverter.ToUInt16(tail, 0) == 0))
        {
            return new AnmAnimationTailInfo
            {
                Format = "NewNoImageTail",
                ImageCount = 0
            };
        }

        var pos = 0;
        string? picSubVersion = null;
        if (tail.Length >= 7
            && tail[0] == (byte)'['
            && tail[1] == (byte)'P'
            && tail[2] == (byte)'I'
            && tail[3] == (byte)'C'
            && tail[4] == (byte)']')
        {
            picSubVersion = Encoding.ASCII.GetString(tail, 5, 2);
            pos = 7;
        }

        if (!TryReadUInt16(tail, ref pos, out var imageCount)
            || !TryReadInt32(tail, ref pos, out var offsetX)
            || !TryReadInt32(tail, ref pos, out var offsetY)
            || !TryReadUInt32(tail, ref pos, out var width)
            || !TryReadUInt32(tail, ref pos, out var height))
        {
            return null;
        }

        int? compressionMode = null;
        int? channels = null;
        int? frameOffsetX = null;
        int? frameOffsetY = null;
        uint? frameWidth = null;
        uint? frameHeight = null;
        int? frameChannels = null;
        var format = picSubVersion is null ? "NewRawImageHeader" : "NewPicImageHeader";
        if (pos + 20 == tail.Length)
        {
            if (!TryReadInt32(tail, ref pos, out var parsedFrameOffsetX)
                || !TryReadInt32(tail, ref pos, out var parsedFrameOffsetY)
                || !TryReadUInt32(tail, ref pos, out var parsedFrameWidth)
                || !TryReadUInt32(tail, ref pos, out var parsedFrameHeight)
                || !TryReadInt32(tail, ref pos, out var parsedFrameChannels))
            {
                return null;
            }

            frameOffsetX = parsedFrameOffsetX;
            frameOffsetY = parsedFrameOffsetY;
            frameWidth = parsedFrameWidth;
            frameHeight = parsedFrameHeight;
            frameChannels = parsedFrameChannels;
            format = "NewPicFrameImageHeader";
        }
        else if (pos + 2 == tail.Length)
        {
            _ = TryReadUInt16(tail, ref pos, out var mode);
            compressionMode = mode;
            format = "NewCompressedImageHeader";
        }
        else if (picSubVersion is not null)
        {
            if (pos + 4 <= tail.Length)
            {
                _ = TryReadInt32(tail, ref pos, out var parsedChannels);
                channels = parsedChannels;
            }
        }

        return pos == tail.Length
            ? new AnmAnimationTailInfo
        {
            Format = format,
            ImageCount = imageCount,
            PicSubVersion = picSubVersion,
            OffsetX = offsetX,
            OffsetY = offsetY,
            Width = width,
            Height = height,
            CompressionMode = compressionMode,
            PixelChannels = channels,
            FrameOffsetX = frameOffsetX,
            FrameOffsetY = frameOffsetY,
            FrameWidth = frameWidth,
            FrameHeight = frameHeight,
            FramePixelChannels = frameChannels
        }
            : null;
    }

    private static bool TryParseNewAnimationTailAt(byte[] data, int start, out AnmAnimationTailInfo tail, out int end)
    {
        tail = new AnmAnimationTailInfo();
        end = start;
        if (start < 0 || start >= data.Length)
        {
            return false;
        }

        var pos = start;
        string? picSubVersion = null;
        if (data.Length >= pos + 7
            && data[pos] == (byte)'['
            && data[pos + 1] == (byte)'P'
            && data[pos + 2] == (byte)'I'
            && data[pos + 3] == (byte)'C'
            && data[pos + 4] == (byte)']')
        {
            picSubVersion = Encoding.ASCII.GetString(data, pos + 5, 2);
            pos += 7;
        }

        if (!TryReadUInt16(data, ref pos, out var imageCount)
            || !TryReadInt32(data, ref pos, out var offsetX)
            || !TryReadInt32(data, ref pos, out var offsetY)
            || !TryReadUInt32(data, ref pos, out var width)
            || !TryReadUInt32(data, ref pos, out var height))
        {
            return false;
        }

        tail = new AnmAnimationTailInfo
        {
            Format = picSubVersion is null ? "NewRawImageHeader" : "NewPicImageHeader",
            ImageCount = imageCount,
            PicSubVersion = picSubVersion,
            OffsetX = offsetX,
            OffsetY = offsetY,
            Width = width,
            Height = height
        };

        if (picSubVersion is not null &&
            TryReadPicFrameImageHeader(data, pos, out var frameOffsetX, out var frameOffsetY, out var frameWidth, out var frameHeight, out var frameChannels))
        {
            tail.Format = "NewPicFrameImageHeader";
            tail.FrameOffsetX = frameOffsetX;
            tail.FrameOffsetY = frameOffsetY;
            tail.FrameWidth = frameWidth;
            tail.FrameHeight = frameHeight;
            tail.FramePixelChannels = frameChannels;
            end = pos + 20;
            return true;
        }

        if (picSubVersion is not null && data.Length >= pos + 4)
        {
            var channels = BitConverter.ToInt32(data, pos);
            if (channels is 1 or 3 or 4)
            {
                tail.PixelChannels = channels;
                end = pos + 4;
                return true;
            }
        }

        if (data.Length >= pos + 2)
        {
            tail.CompressionMode = BitConverter.ToUInt16(data, pos);
            tail.Format = "NewCompressedImageHeader";
            end = pos + 2;
            return true;
        }

        end = pos;
        return true;
    }

    private static bool TryReadPicFrameImageHeader(
        byte[] data,
        int pos,
        out int offsetX,
        out int offsetY,
        out uint width,
        out uint height,
        out int channels)
    {
        offsetX = 0;
        offsetY = 0;
        width = 0;
        height = 0;
        channels = 0;
        if (pos + 20 > data.Length)
        {
            return false;
        }

        offsetX = BitConverter.ToInt32(data, pos);
        offsetY = BitConverter.ToInt32(data, pos + 4);
        width = BitConverter.ToUInt32(data, pos + 8);
        height = BitConverter.ToUInt32(data, pos + 12);
        channels = BitConverter.ToInt32(data, pos + 16);
        return width > 0
            && height > 0
            && width <= 10000
            && height <= 10000
            && channels is 1 or 3 or 4;
    }

    private static AnmAnimationTailInfo? TryParseLegacyAnimationTail(byte[] tail)
    {
        var pos = 0;
        if (!TryReadUInt16(tail, ref pos, out var imageCount))
        {
            return null;
        }

        return pos == tail.Length
            ? new AnmAnimationTailInfo
        {
            Format = "LegacyImageCount",
            ImageCount = imageCount
        }
            : null;
    }

    private static byte[] BuildAnimationControlPrefix(AnmAnimationControlInfo control)
    {
        return control.Format switch
        {
            "NewControlCommands" => BuildNewAnimationControlPrefix(control),
            "LegacyControlPairs" => BuildLegacyAnimationControlPrefix(control),
            _ => throw new InvalidDataException($"Unsupported ANM animation control format: {control.Format}")
        };
    }

    private static byte[] BuildNewAnimationControlPrefix(AnmAnimationControlInfo control)
    {
        var tail = BuildAnimationTail(control.Tail);
        var magic = Encoding.ASCII.GetBytes(control.Magic);
        if (magic.Length != 4)
        {
            throw new InvalidDataException("ANM new control metadata has an invalid 4-byte magic.");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(magic);
        WriteUInt16Checked(writer, control.Commands.Count, "ANM command count");
        WriteUInt16Checked(writer, control.ControlHeaderWord, "ANM control header word");
        foreach (var command in control.Commands)
        {
            switch (command.Command)
            {
                case "NoOp":
                    writer.Write((byte)0);
                    break;
                case "SetFrameMapping":
                    writer.Write((byte)1);
                    writer.Write(RequireCommandValue(command.FrameSlot, "ANM SetFrameMapping.FrameSlot"));
                    writer.Write(RequireCommandValue(command.FrameIndex, "ANM SetFrameMapping.FrameIndex"));
                    break;
                case "WaitTicks":
                    writer.Write((byte)2);
                    writer.Write(RequireCommandValue(command.WaitTicks, "ANM WaitTicks.WaitTicks"));
                    break;
                case "LoadBranchRepeatCounter":
                    writer.Write((byte)3);
                    writer.Write(RequireCommandValue(command.BranchIndex, "ANM LoadBranchRepeatCounter.BranchIndex"));
                    break;
                case "DecrementBranchAndJumpIfPositive":
                    writer.Write((byte)4);
                    writer.Write(RequireCommandValue(command.BranchIndex, "ANM DecrementBranchAndJumpIfPositive.BranchIndex"));
                    break;
                case "SetAnimationResultCode":
                    writer.Write((byte)5);
                    writer.Write(RequireCommandValue(command.ResultCode, "ANM SetAnimationResultCode.ResultCode"));
                    break;
                default:
                    throw new InvalidDataException($"Unsupported ANM new control command: {command.Command}");
            }
        }

        WriteUInt16Checked(writer, control.Branches.Count, "ANM branch count");
        foreach (var branch in control.Branches)
        {
            writer.Write(branch.JumpCommandIndex);
            writer.Write(branch.RepeatCount);
        }

        writer.Write(tail);
        return stream.ToArray();
    }

    private static byte[] BuildLegacyAnimationControlPrefix(AnmAnimationControlInfo control)
    {
        var tail = BuildAnimationTail(control.Tail);
        var magic = Encoding.ASCII.GetBytes(control.Magic);
        if (magic.Length != 4)
        {
            throw new InvalidDataException("ANM legacy control metadata has an invalid 4-byte magic.");
        }

        var canvas = control.LegacyCanvas
            ?? throw new InvalidDataException("ANM legacy control metadata is missing LegacyCanvas.");
        if (control.Magic is not ("AN00" or "AN01"))
        {
            throw new InvalidDataException($"ANM legacy control metadata has invalid magic: {control.Magic}.");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(magic);
        WriteBaseInfo(writer, canvas.Width, canvas.Height, canvas.OffsetX, canvas.OffsetY);
        WriteUInt16Checked(writer, control.LegacyPairs.Count, "ANM legacy control pair count");
        WriteUInt16Checked(writer, control.ControlHeaderWord, "ANM legacy control header word");
        foreach (var pair in control.LegacyPairs)
        {
            WriteUInt16Checked(writer, pair.DelayTicks, "ANM legacy control pair delay ticks");
            writer.Write(pair.FrameIndex == -1 ? ushort.MaxValue : CheckedUInt16(pair.FrameIndex, "ANM legacy control pair frame index"));
        }

        writer.Write(tail);
        return stream.ToArray();
    }

    private static byte[] BuildAnimationTail(AnmAnimationTailInfo? tail)
    {
        if (tail is null)
        {
            throw new InvalidDataException("ANM metadata is missing structured AnimationControl.Tail.");
        }

        return tail.Format switch
        {
            "LegacyImageCount" => BuildLegacyAnimationTail(tail),
            "NewNoImageTail" => [0, 0],
            "NewRawImageHeader" or "NewPicImageHeader" or "NewCompressedImageHeader" or "NewPicFrameImageHeader" => BuildNewAnimationTail(tail),
            _ => throw new InvalidDataException($"Unsupported ANM animation tail format: {tail.Format}")
        };
    }

    private static byte[] BuildLegacyAnimationTail(AnmAnimationTailInfo tail)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteUInt16Checked(writer, tail.ImageCount, "ANM legacy image count");
        return stream.ToArray();
    }

    private static byte[] BuildNewAnimationTail(AnmAnimationTailInfo tail)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (!string.IsNullOrEmpty(tail.PicSubVersion))
        {
            if (tail.PicSubVersion.Length != 2)
            {
                throw new InvalidDataException($"ANM [PIC] sub-version must be 2 ASCII bytes: {tail.PicSubVersion}");
            }

            writer.Write(Encoding.ASCII.GetBytes("[PIC]"));
            writer.Write(Encoding.ASCII.GetBytes(tail.PicSubVersion));
        }

        WriteUInt16Checked(writer, tail.ImageCount, "ANM image count");
        writer.Write(tail.OffsetX);
        writer.Write(tail.OffsetY);
        writer.Write(tail.Width);
        writer.Write(tail.Height);
        if (tail.Format == "NewPicFrameImageHeader")
        {
            writer.Write(tail.FrameOffsetX ?? throw new InvalidDataException("ANM frame image header is missing FrameOffsetX."));
            writer.Write(tail.FrameOffsetY ?? throw new InvalidDataException("ANM frame image header is missing FrameOffsetY."));
            writer.Write(tail.FrameWidth ?? throw new InvalidDataException("ANM frame image header is missing FrameWidth."));
            writer.Write(tail.FrameHeight ?? throw new InvalidDataException("ANM frame image header is missing FrameHeight."));
            writer.Write(tail.FramePixelChannels ?? throw new InvalidDataException("ANM frame image header is missing FramePixelChannels."));
            return stream.ToArray();
        }

        if (tail.CompressionMode is not null)
        {
            WriteUInt16Checked(writer, tail.CompressionMode.Value, "ANM compression mode");
        }

        if (tail.PixelChannels is not null)
        {
            writer.Write(tail.PixelChannels.Value);
        }
        return stream.ToArray();
    }

    private static bool TryReadUInt16(byte[] data, ref int pos, out int value)
    {
        if (pos + 2 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToUInt16(data, pos);
        pos += 2;
        return true;
    }

    private static bool TryReadInt32(byte[] data, ref int pos, out int value)
    {
        if (pos + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToInt32(data, pos);
        pos += 4;
        return true;
    }

    private static bool TryReadUInt32(byte[] data, ref int pos, out uint value)
    {
        if (pos + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToUInt32(data, pos);
        pos += 4;
        return true;
    }

    private static void WriteUInt16Checked(BinaryWriter writer, int value, string label)
    {
        writer.Write(CheckedUInt16(value, label));
    }

    private static ushort CheckedUInt16(int value, string label)
    {
        if (value < 0 || value > ushort.MaxValue)
        {
            throw new InvalidDataException($"{label} is out of u16 range: {value}.");
        }

        return (ushort)value;
    }

    private static int RequireCommandValue(int? value, string label)
    {
        if (!value.HasValue)
        {
            throw new InvalidDataException($"{label} is required.");
        }

        return value.Value;
    }

    private static void CreateWithPreservedRecords(List<string> imagePaths, string anmPath, AnmInfo canvasInfo, bool hasChannels)
    {
        if (imagePaths.Count != canvasInfo.Frames.Count)
        {
            throw new InvalidDataException($"ANM frame count changed: metadata has {canvasInfo.Frames.Count}, PNG directory has {imagePaths.Count}.");
        }

        var prefix = GetStructurePrefixBytes(canvasInfo);
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
                writer.Write(frame.PixelChannels);
            }

            writer.Write(ConvertBgra32ToChannels(ReadPixelsFromPng(imagePaths[i]), frame.PixelChannels));
        }
    }

    private static void CreateAN20Preserved(List<string> imagePaths, string anmPath, AnmInfo canvasInfo)
    {
        if (imagePaths.Count != canvasInfo.Frames.Count)
        {
            throw new InvalidDataException($"AN20 frame count changed: metadata has {canvasInfo.Frames.Count}, PNG directory has {imagePaths.Count}.");
        }

        var prefix = GetStructurePrefixBytes(canvasInfo);
        var compressionMode = canvasInfo.GlobalCompressionMode ?? 4;
        if (compressionMode == 0)
        {
            using var rawWriter = new BinaryWriter(File.Create(anmPath));
            rawWriter.Write(prefix);

            for (var i = 0; i < imagePaths.Count; i++)
            {
                var frame = canvasInfo.Frames[i];
                var (w, h) = GetImageDimensions(imagePaths[i]);
                if (w != frame.Width || h != frame.Height)
                {
                    throw new InvalidDataException($"AN20 frame {i:D4} size changed: metadata is {frame.Width}x{frame.Height}, PNG is {w}x{h}.");
                }

                var pixels = ConvertBgra32ToChannels(ReadPixelsFromPng(imagePaths[i]), frame.PixelChannels);
                rawWriter.Write(frame.OffsetX);
                rawWriter.Write(frame.OffsetY);
                rawWriter.Write(frame.Width);
                rawWriter.Write(frame.Height);
                rawWriter.Write(frame.PixelChannels);
                rawWriter.Write(pixels);
            }

            return;
        }

        if (prefix.Length < 2)
        {
            throw new InvalidDataException("AN20 metadata prefix is too short.");
        }

        if (compressionMode is not (3 or 4))
        {
            throw new NotSupportedException($"Unsupported AN20 repack compression mode: {compressionMode}");
        }

        prefix[^2] = (byte)compressionMode;
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

            var pixels = ConvertBgra32ToChannels(ReadPixelsFromPng(imagePaths[i]), frame.PixelChannels);
            var payload = compressionMode == 3
                ? BmrEncoder.PackAn20(pixels)
                : BuildAn20LzssBlock(pixels);
            writer.Write(frame.OffsetX);
            writer.Write(frame.OffsetY);
            writer.Write(frame.Width);
            writer.Write(frame.Height);
            writer.Write(frame.PixelChannels);
            writer.Write(payload.Length);
            writer.Write(payload);
        }
    }

    private static byte[] BuildAn20LzssBlock(byte[] pixels)
    {
        var lzssStream = CompressLzss(pixels);
        var output = new byte[4 + lzssStream.Length];
        WriteInt32LE(output, 0, pixels.Length);
        lzssStream.CopyTo(output.AsSpan(4));
        return output;
    }

    private static void CreateAN21Preserved(List<string> imagePaths, string anmPath, AnmInfo canvasInfo)
    {
        if (imagePaths.Count != canvasInfo.Frames.Count)
        {
            throw new InvalidDataException($"AN21 frame count changed: metadata has {canvasInfo.Frames.Count}, PNG directory has {imagePaths.Count}.");
        }

        var firstFrame = canvasInfo.Frames[0];
        var channels = canvasInfo.GlobalPixelChannels ?? firstFrame.PixelChannels;
        using var writer = new BinaryWriter(File.Create(anmPath));
        writer.Write(GetStructurePrefixBytes(canvasInfo));

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
                var rleStep = frame.RleInterleaveStep;
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

    private static int CheckedPixelByteCount(uint width, uint height, int channels, string context)
    {
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException($"{context} has invalid dimensions: {width}x{height}.");
        }

        if (channels is not (1 or 3 or 4))
        {
            throw new InvalidDataException($"{context} has unsupported channel count: {channels}.");
        }

        var byteCount = (ulong)width * height * (uint)channels;
        if (byteCount > int.MaxValue)
        {
            throw new InvalidDataException($"{context} pixel payload is too large: {byteCount} bytes.");
        }

        return (int)byteCount;
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

