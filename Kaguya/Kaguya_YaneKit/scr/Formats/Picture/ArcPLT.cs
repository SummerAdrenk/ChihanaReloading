using System.Drawing;
using System.Text;
using Kaguya_YaneKit.Formats.Archive;

namespace Kaguya_YaneKit.Formats.Picture;

public sealed class PltInfo
{
    public string Version { get; set; } = "";
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public int PixelChannels { get; set; } = 4;
    public int? GlobalCompressionMode { get; set; }
    public int? BlockSize { get; set; }
    public string ExtraHeaderBase64 { get; set; } = "";
    public string ReservedHeaderBase64 { get; set; } = "";
    public List<AnmFrameInfo> Frames { get; set; } = [];
}

public static class ArcPLT
{
    public static (string version, PltInfo info) Extract(string pltPath, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        using var reader = new BinaryReader(File.OpenRead(pltPath));
        var version = Encoding.ASCII.GetString(reader.ReadBytes(4));
        return version switch
        {
            "PL00" => (version, ExtractPl00(reader, outputDir)),
            "PL01" => (version, ExtractPl01(reader, outputDir)),
            "PL10" => (version, ExtractPl10(reader, outputDir)),
            "PL11" => (version, ExtractPl11(reader, outputDir)),
            "PL20" => (version, ExtractPl20(reader, outputDir)),
            "PL30" => (version, ExtractPl30(reader, outputDir)),
            _ => throw new NotSupportedException($"Unsupported PLT magic: {version}")
        };
    }

    public static void Create(string pngDir, string pltPath, PltInfo info)
    {
        var imagePaths = Directory.GetFiles(pngDir, "*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (imagePaths.Count != info.Frames.Count)
        {
            throw new InvalidDataException($"PLT frame count changed: metadata has {info.Frames.Count}, PNG directory has {imagePaths.Count}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pltPath))!);
        using var writer = new BinaryWriter(File.Create(pltPath));
        switch (info.Version)
        {
            case "PL00":
                CreatePl00(writer, imagePaths, info);
                break;
            case "PL10":
                CreatePl10(writer, imagePaths, info);
                break;
            case "PL01":
                CreatePl01(writer, imagePaths, info);
                break;
            case "PL20":
                CreatePl20(writer, imagePaths, info);
                break;
            case "PL11":
                CreatePl11(writer, imagePaths, info);
                break;
            case "PL30":
                CreatePl30(writer, imagePaths, info);
                break;
            default:
                throw new NotSupportedException($"Unsupported PLT version: {info.Version}");
        }
    }

    private static PltInfo ExtractPl00(BinaryReader reader, string outputDir)
    {
        var frameCount = reader.ReadUInt16();
        var info = new PltInfo
        {
            Version = "PL00",
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32()
        };

        for (var i = 0; i < frameCount; i++)
        {
            var frame = new AnmFrameInfo
            {
                OffsetX = reader.ReadInt32(),
                OffsetY = reader.ReadInt32(),
                Width = reader.ReadUInt32(),
                Height = reader.ReadUInt32(),
                PixelChannels = reader.ReadInt32()
            };
            var pixelSize = CheckedPixelByteCount(frame.Width, frame.Height, frame.PixelChannels);
            var pixels = reader.ReadBytes(pixelSize);
            if (pixels.Length != pixelSize)
            {
                throw new EndOfStreamException("PL00 frame pixel payload is truncated.");
            }

            info.Frames.Add(frame);
            SaveFramePng(outputDir, i, pixels, frame);
        }

        if (info.Frames.Count > 0)
        {
            info.PixelChannels = info.Frames[0].PixelChannels;
        }

        return info;
    }

    private static PltInfo ExtractPl11(BinaryReader reader, string outputDir)
    {
        var frameCount = reader.ReadUInt16();
        var reserved = reader.ReadBytes(0x10);
        if (reserved.Length != 0x10)
        {
            throw new EndOfStreamException("PL11 reserved header is truncated.");
        }

        var info = new PltInfo
        {
            Version = "PL11",
            ReservedHeaderBase64 = Convert.ToBase64String(reserved),
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32(),
            PixelChannels = reader.ReadInt32()
        };

        var fullSize = CheckedPixelByteCount(info.Width, info.Height, info.PixelChannels);
        var previous = reader.ReadBytes(fullSize);
        if (previous.Length != fullSize)
        {
            throw new EndOfStreamException("PL11 first frame pixel payload is truncated.");
        }

        var extra = reader.ReadBytes(2);
        if (extra.Length != 2)
        {
            throw new EndOfStreamException("PL11 extra header is truncated.");
        }

        info.ExtraHeaderBase64 = Convert.ToBase64String(extra);
        var firstFrame = new AnmFrameInfo
        {
            OffsetX = info.OffsetX,
            OffsetY = info.OffsetY,
            Width = info.Width,
            Height = info.Height,
            PixelChannels = info.PixelChannels
        };
        info.Frames.Add(firstFrame);
        SaveFramePng(outputDir, 0, previous, firstFrame);

        for (var i = 1; i < frameCount; i++)
        {
            var packedSize = reader.ReadUInt32();
            var packed = reader.ReadBytes(checked((int)packedSize));
            if (packed.Length != packedSize)
            {
                throw new EndOfStreamException($"PL11 frame {i:D4} Huffman payload is truncated.");
            }

            var diff = DecodeHuffmanOnly(packed, fullSize, $"PL11 frame {i:D4}");
            var current = new byte[fullSize];
            for (var p = 0; p < current.Length; p++)
            {
                current[p] = (byte)(previous[p] + diff[p]);
            }

            var frame = new AnmFrameInfo
            {
                OffsetX = info.OffsetX,
                OffsetY = info.OffsetY,
                Width = info.Width,
                Height = info.Height,
                PixelChannels = info.PixelChannels,
                FrameImageDataByteCount = checked((int)packedSize)
            };
            info.Frames.Add(frame);
            SaveFramePng(outputDir, i, current, frame);
            previous = current;
        }

        return info;
    }

    private static PltInfo ExtractPl01(BinaryReader reader, string outputDir)
    {
        var frameCount = reader.ReadUInt16();
        var info = new PltInfo
        {
            Version = "PL01",
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32(),
            PixelChannels = reader.ReadInt32()
        };

        var fullSize = CheckedPixelByteCount(info.Width, info.Height, info.PixelChannels);
        var previous = reader.ReadBytes(fullSize);
        if (previous.Length != fullSize)
        {
            throw new EndOfStreamException("PL01 first frame pixel payload is truncated.");
        }

        var firstFrame = new AnmFrameInfo
        {
            OffsetX = info.OffsetX,
            OffsetY = info.OffsetY,
            Width = info.Width,
            Height = info.Height,
            PixelChannels = info.PixelChannels
        };
        info.Frames.Add(firstFrame);
        SaveFramePng(outputDir, 0, previous, firstFrame);

        for (var i = 1; i < frameCount; i++)
        {
            var diff = reader.ReadBytes(fullSize);
            if (diff.Length != fullSize)
            {
                throw new EndOfStreamException($"PL01 frame {i:D4} diff payload is truncated.");
            }

            var current = new byte[fullSize];
            for (var p = 0; p < current.Length; p++)
            {
                current[p] = (byte)(previous[p] + diff[p]);
            }

            var frame = new AnmFrameInfo
            {
                OffsetX = info.OffsetX,
                OffsetY = info.OffsetY,
                Width = info.Width,
                Height = info.Height,
                PixelChannels = info.PixelChannels,
                FrameImageDataByteCount = fullSize
            };
            info.Frames.Add(frame);
            SaveFramePng(outputDir, i, current, frame);
            previous = current;
        }

        return info;
    }

    private static PltInfo ExtractPl30(BinaryReader reader, string outputDir)
    {
        var frameCount = reader.ReadUInt16();
        var info = new PltInfo
        {
            Version = "PL30",
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32(),
            PixelChannels = reader.ReadInt32(),
            BlockSize = reader.ReadInt32()
        };

        var fullSize = CheckedPixelByteCount(info.Width, info.Height, info.PixelChannels);
        var previous = reader.ReadBytes(fullSize);
        if (previous.Length != fullSize)
        {
            throw new EndOfStreamException("PL30 first frame pixel payload is truncated.");
        }

        var firstFrame = new AnmFrameInfo
        {
            OffsetX = info.OffsetX,
            OffsetY = info.OffsetY,
            Width = info.Width,
            Height = info.Height,
            PixelChannels = info.PixelChannels
        };
        info.Frames.Add(firstFrame);
        SaveFramePng(outputDir, 0, previous, firstFrame);

        for (var i = 1; i < frameCount; i++)
        {
            var packedFlag = reader.ReadByte();
            byte[] current;
            if (packedFlag == 0)
            {
                var diff = reader.ReadBytes(fullSize);
                if (diff.Length != fullSize)
                {
                    throw new EndOfStreamException($"PL30 frame {i:D4} raw diff payload is truncated.");
                }

                current = new byte[fullSize];
                for (var p = 0; p < current.Length; p++)
                {
                    current[p] = (byte)(previous[p] + diff[p]);
                }
            }
            else
            {
                var packedSize = reader.ReadUInt32();
                var packed = reader.ReadBytes(checked((int)packedSize));
                if (packed.Length != packedSize)
                {
                    throw new EndOfStreamException($"PL30 frame {i:D4} block-convert payload is truncated.");
                }

                current = DecodeBlockConvert(packed, previous, (int)info.Width, (int)info.Height, info.PixelChannels, info.BlockSize ?? 8);
            }

            var frame = new AnmFrameInfo
            {
                OffsetX = info.OffsetX,
                OffsetY = info.OffsetY,
                Width = info.Width,
                Height = info.Height,
                PixelChannels = info.PixelChannels,
                FrameImageDataByteCount = fullSize
            };
            info.Frames.Add(frame);
            SaveFramePng(outputDir, i, current, frame);
            previous = current;
        }

        return info;
    }

    private static PltInfo ExtractPl10(BinaryReader reader, string outputDir)
    {
        var frameCount = reader.ReadUInt16();
        var reserved = reader.ReadBytes(0x10);
        if (reserved.Length != 0x10)
        {
            throw new EndOfStreamException("PL10 reserved header is truncated.");
        }

        var info = new PltInfo
        {
            Version = "PL10",
            ReservedHeaderBase64 = Convert.ToBase64String(reserved),
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32(),
            PixelChannels = reader.ReadInt32()
        };

        var fullFrame = new AnmFrameInfo
        {
            OffsetX = info.OffsetX,
            OffsetY = info.OffsetY,
            Width = info.Width,
            Height = info.Height,
            PixelChannels = info.PixelChannels,
            RleInterleaveStep = info.PixelChannels
        };
        var fullSize = CheckedPixelByteCount(info.Width, info.Height, info.PixelChannels);
        var previous = reader.ReadBytes(fullSize);
        if (previous.Length != fullSize)
        {
            throw new EndOfStreamException("PL10 first frame pixel payload is truncated.");
        }

        info.Frames.Add(fullFrame);
        SaveFramePng(outputDir, 0, previous, fullFrame);

        for (var i = 1; i < frameCount; i++)
        {
            var step = reader.ReadByte();
            if (step == 0)
            {
                throw new InvalidDataException($"PL10 frame {i:D4} has invalid RLE step 0.");
            }

            var packedSize = reader.ReadUInt32();
            var packed = reader.ReadBytes(checked((int)packedSize));
            if (packed.Length != packedSize)
            {
                throw new EndOfStreamException($"PL10 frame {i:D4} RLE payload is truncated.");
            }

            var diff = DecompressRle(packed, fullSize, step);
            if (diff.Length != previous.Length)
            {
                throw new InvalidDataException($"PL10 frame {i:D4} decoded size {diff.Length} does not match previous frame size {previous.Length}.");
            }

            var current = new byte[diff.Length];
            for (var p = 0; p < current.Length; p++)
            {
                current[p] = (byte)(diff[p] + previous[p]);
            }

            var frame = new AnmFrameInfo
            {
                OffsetX = info.OffsetX,
                OffsetY = info.OffsetY,
                Width = info.Width,
                Height = info.Height,
                PixelChannels = info.PixelChannels,
                RleInterleaveStep = step,
                FrameImageDataByteCount = checked((int)packedSize)
            };
            info.Frames.Add(frame);
            SaveFramePng(outputDir, i, current, frame);
            previous = current;
        }

        return info;
    }

    private static PltInfo ExtractPl20(BinaryReader reader, string outputDir)
    {
        var frameCount = reader.ReadUInt16();
        var info = new PltInfo
        {
            Version = "PL20",
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32(),
            GlobalCompressionMode = reader.ReadUInt16()
        };

        var compressionMode = info.GlobalCompressionMode.Value;
        for (var i = 0; i < frameCount; i++)
        {
            var frame = new AnmFrameInfo
            {
                OffsetX = reader.ReadInt32(),
                OffsetY = reader.ReadInt32(),
                Width = reader.ReadUInt32(),
                Height = reader.ReadUInt32(),
                PixelChannels = reader.ReadInt32()
            };
            var packedSize = reader.ReadUInt32();
            var payload = reader.ReadBytes(checked((int)packedSize));
            if (payload.Length != packedSize)
            {
                throw new EndOfStreamException($"PL20 frame {i:D4} payload is truncated.");
            }

            var pixelSize = CheckedPixelByteCount(frame.Width, frame.Height, frame.PixelChannels);
            var pixels = compressionMode switch
            {
                3 => DecompressBmrBlock(payload, pixelSize, $"PL20 frame {i:D4}"),
                4 => DecompressLzssBlock(payload, pixelSize, $"PL20 frame {i:D4}"),
                _ => throw new NotSupportedException($"Unsupported PL20 compression mode: {compressionMode}")
            };

            frame.FrameImageDataByteCount = checked((int)packedSize);
            info.Frames.Add(frame);
            SaveFramePng(outputDir, i, pixels, frame);
        }

        if (info.Frames.Count > 0)
        {
            info.PixelChannels = info.Frames[0].PixelChannels;
        }

        return info;
    }

    private static void CreatePl00(BinaryWriter writer, List<string> imagePaths, PltInfo info)
    {
        writer.Write(Encoding.ASCII.GetBytes("PL00"));
        writer.Write(checked((ushort)info.Frames.Count));
        writer.Write(info.OffsetX);
        writer.Write(info.OffsetY);
        writer.Write(info.Width);
        writer.Write(info.Height);

        for (var i = 0; i < imagePaths.Count; i++)
        {
            var frame = info.Frames[i];
            EnsureDimensions(imagePaths[i], frame, i);
            var pixels = ConvertBgra32ToChannels(BitmapHelpers.ReadBottomUpPixelsFromImage(imagePaths[i]), frame.PixelChannels);
            writer.Write(frame.OffsetX);
            writer.Write(frame.OffsetY);
            writer.Write(frame.Width);
            writer.Write(frame.Height);
            writer.Write(frame.PixelChannels);
            writer.Write(pixels);
        }
    }

    private static void CreatePl10(BinaryWriter writer, List<string> imagePaths, PltInfo info)
    {
        writer.Write(Encoding.ASCII.GetBytes("PL10"));
        writer.Write(checked((ushort)info.Frames.Count));
        var reserved = string.IsNullOrEmpty(info.ReservedHeaderBase64)
            ? new byte[0x10]
            : Convert.FromBase64String(info.ReservedHeaderBase64);
        if (reserved.Length != 0x10)
        {
            throw new InvalidDataException("PL10 reserved header must be 16 bytes.");
        }

        writer.Write(reserved);
        writer.Write(info.OffsetX);
        writer.Write(info.OffsetY);
        writer.Write(info.Width);
        writer.Write(info.Height);
        writer.Write(info.PixelChannels);

        byte[]? previous = null;
        for (var i = 0; i < imagePaths.Count; i++)
        {
            var frame = info.Frames[i];
            EnsureDimensions(imagePaths[i], frame, i);
            var current = ConvertBgra32ToChannels(BitmapHelpers.ReadBottomUpPixelsFromImage(imagePaths[i]), info.PixelChannels);
            if (i == 0)
            {
                writer.Write(current);
            }
            else
            {
                var diff = new byte[current.Length];
                for (var p = 0; p < diff.Length; p++)
                {
                    diff[p] = (byte)(current[p] - previous![p]);
                }

                var step = frame.RleInterleaveStep <= 0 ? info.PixelChannels : frame.RleInterleaveStep;
                var packed = CompressRle(diff, step);
                writer.Write((byte)step);
                writer.Write((uint)packed.Length);
                writer.Write(packed);
            }

            previous = current;
        }
    }

    private static void CreatePl01(BinaryWriter writer, List<string> imagePaths, PltInfo info)
    {
        writer.Write(Encoding.ASCII.GetBytes("PL01"));
        writer.Write(checked((ushort)info.Frames.Count));
        writer.Write(info.OffsetX);
        writer.Write(info.OffsetY);
        writer.Write(info.Width);
        writer.Write(info.Height);
        writer.Write(info.PixelChannels);

        byte[]? previous = null;
        for (var i = 0; i < imagePaths.Count; i++)
        {
            var frame = info.Frames[i];
            EnsureDimensions(imagePaths[i], frame, i);
            var current = ConvertBgra32ToChannels(BitmapHelpers.ReadBottomUpPixelsFromImage(imagePaths[i]), info.PixelChannels);
            if (i == 0)
            {
                writer.Write(current);
            }
            else
            {
                if (current.Length != previous!.Length)
                {
                    throw new InvalidDataException($"PL01 frame {i:D4} payload size changed.");
                }

                var diff = new byte[current.Length];
                for (var p = 0; p < diff.Length; p++)
                {
                    diff[p] = (byte)(current[p] - previous[p]);
                }

                writer.Write(diff);
            }

            previous = current;
        }
    }

    private static void CreatePl20(BinaryWriter writer, List<string> imagePaths, PltInfo info)
    {
        var compressionMode = info.GlobalCompressionMode ?? 4;
        if (compressionMode is not (3 or 4))
        {
            throw new NotSupportedException($"Unsupported PL20 repack compression mode: {compressionMode}");
        }

        writer.Write(Encoding.ASCII.GetBytes("PL20"));
        writer.Write(checked((ushort)info.Frames.Count));
        writer.Write(info.OffsetX);
        writer.Write(info.OffsetY);
        writer.Write(info.Width);
        writer.Write(info.Height);
        writer.Write(checked((ushort)compressionMode));

        for (var i = 0; i < imagePaths.Count; i++)
        {
            var frame = info.Frames[i];
            EnsureDimensions(imagePaths[i], frame, i);
            var pixels = ConvertBgra32ToChannels(BitmapHelpers.ReadBottomUpPixelsFromImage(imagePaths[i]), frame.PixelChannels);
            var payload = compressionMode == 3
                ? BmrEncoder.Pack(pixels)
                : BuildLzssBlock(pixels);

            writer.Write(frame.OffsetX);
            writer.Write(frame.OffsetY);
            writer.Write(frame.Width);
            writer.Write(frame.Height);
            writer.Write(frame.PixelChannels);
            writer.Write((uint)payload.Length);
            writer.Write(payload);
        }
    }

    private static void CreatePl11(BinaryWriter writer, List<string> imagePaths, PltInfo info)
    {
        writer.Write(Encoding.ASCII.GetBytes("PL11"));
        writer.Write(checked((ushort)info.Frames.Count));
        var reserved = string.IsNullOrEmpty(info.ReservedHeaderBase64)
            ? new byte[0x10]
            : Convert.FromBase64String(info.ReservedHeaderBase64);
        if (reserved.Length != 0x10)
        {
            throw new InvalidDataException("PL11 reserved header must be 16 bytes.");
        }

        writer.Write(reserved);
        writer.Write(info.OffsetX);
        writer.Write(info.OffsetY);
        writer.Write(info.Width);
        writer.Write(info.Height);
        writer.Write(info.PixelChannels);

        byte[]? previous = null;
        for (var i = 0; i < imagePaths.Count; i++)
        {
            var frame = info.Frames[i];
            EnsureDimensions(imagePaths[i], frame, i);
            var current = ConvertBgra32ToChannels(BitmapHelpers.ReadBottomUpPixelsFromImage(imagePaths[i]), info.PixelChannels);
            if (i == 0)
            {
                writer.Write(current);
                var extra = string.IsNullOrEmpty(info.ExtraHeaderBase64)
                    ? new byte[2]
                    : Convert.FromBase64String(info.ExtraHeaderBase64);
                if (extra.Length != 2)
                {
                    throw new InvalidDataException("PL11 extra header must be 2 bytes.");
                }

                writer.Write(extra);
            }
            else
            {
                var diff = new byte[current.Length];
                for (var p = 0; p < diff.Length; p++)
                {
                    diff[p] = (byte)(current[p] - previous![p]);
                }

                var huffman = BmrEncoder.PackHuffmanOnly(diff);
                writer.Write((uint)huffman.Length);
                writer.Write(huffman);
            }

            previous = current;
        }
    }

    private static void CreatePl30(BinaryWriter writer, List<string> imagePaths, PltInfo info)
    {
        writer.Write(Encoding.ASCII.GetBytes("PL30"));
        writer.Write(checked((ushort)info.Frames.Count));
        writer.Write(info.OffsetX);
        writer.Write(info.OffsetY);
        writer.Write(info.Width);
        writer.Write(info.Height);
        writer.Write(info.PixelChannels);
        writer.Write(info.BlockSize ?? 8);

        byte[]? previous = null;
        for (var i = 0; i < imagePaths.Count; i++)
        {
            var frame = info.Frames[i];
            EnsureDimensions(imagePaths[i], frame, i);
            var current = ConvertBgra32ToChannels(BitmapHelpers.ReadBottomUpPixelsFromImage(imagePaths[i]), info.PixelChannels);
            if (i == 0)
            {
                writer.Write(current);
            }
            else
            {
                writer.Write((byte)0);
                var diff = new byte[current.Length];
                for (var p = 0; p < diff.Length; p++)
                {
                    diff[p] = (byte)(current[p] - previous![p]);
                }

                writer.Write(diff);
            }

            previous = current;
        }
    }

    private static void SaveFramePng(string outputDir, int index, byte[] pixels, AnmFrameInfo frame)
    {
        var bgra = BitmapHelpers.ToBgra32(pixels, (int)frame.Width, (int)frame.Height, frame.PixelChannels * 8);
        BitmapHelpers.SavePngFromBottomUpPixels(bgra, (int)frame.Width, (int)frame.Height, Path.Combine(outputDir, $"{index:D4}.png"));
    }

    private static void EnsureDimensions(string pngPath, AnmFrameInfo frame, int index)
    {
        using var image = Image.FromFile(pngPath);
        if (image.Width != frame.Width || image.Height != frame.Height)
        {
            throw new InvalidDataException($"PLT frame {index:D4} size changed: metadata is {frame.Width}x{frame.Height}, PNG is {image.Width}x{image.Height}.");
        }
    }

    private static byte[] ConvertBgra32ToChannels(byte[] bgraPixels, int channels)
    {
        return channels switch
        {
            4 => bgraPixels,
            3 => ToBgr24(bgraPixels),
            1 => BitmapHelpers.ToGrayscale(bgraPixels),
            _ => throw new NotSupportedException($"Unsupported PLT channel count: {channels}")
        };
    }

    private static byte[] ToBgr24(byte[] bgraPixels)
    {
        var output = new byte[bgraPixels.Length / 4 * 3];
        for (int src = 0, dst = 0; src < bgraPixels.Length; src += 4)
        {
            output[dst++] = bgraPixels[src];
            output[dst++] = bgraPixels[src + 1];
            output[dst++] = bgraPixels[src + 2];
        }

        return output;
    }

    private static byte[] DecompressBmrBlock(byte[] payload, int expectedSize, string label)
    {
        if (!BmrDecoder.IsBmr(payload))
        {
            throw new InvalidDataException($"{label} mode 3 payload does not start with BMR.");
        }

        var pixels = new BmrDecoder(payload).Unpack();
        if (pixels.Length != expectedSize)
        {
            throw new InvalidDataException($"{label} BMR decoded size {pixels.Length} does not match expected {expectedSize}.");
        }

        return pixels;
    }

    private static byte[] DecompressLzssBlock(byte[] payload, int expectedSize, string label)
    {
        if (payload.Length < 4)
        {
            throw new InvalidDataException($"{label} LZSS payload is too short.");
        }

        var unpackedSize = BitConverter.ToInt32(payload, 0);
        if (unpackedSize != expectedSize)
        {
            throw new InvalidDataException($"{label} LZSS unpacked size mismatch: header is {unpackedSize}, expected {expectedSize}.");
        }

        return DecompressLzss(payload.AsSpan(4), expectedSize, label);
    }

    private static byte[] BuildLzssBlock(byte[] pixels)
    {
        var stream = CompressLzss(pixels);
        var output = new byte[4 + stream.Length];
        BitConverter.GetBytes(pixels.Length).CopyTo(output, 0);
        stream.CopyTo(output.AsSpan(4));
        return output;
    }

    private static byte[] DecodeBlockConvert(byte[] packed, byte[] previous, int width, int height, int channels, int blockSize)
    {
        if (blockSize <= 0)
        {
            throw new InvalidDataException("PL30 block size must be positive.");
        }

        if (packed.Length < 10)
        {
            throw new InvalidDataException("PL30 block-convert payload is too short.");
        }

        var output = new byte[previous.Length];
        var flagBytes = packed[4] | (packed[5] << 8);
        var controlOffset = 6;
        var tableOffset = flagBytes + 10;
        var rawOffset = flagBytes + 6 + ReadInt32FromBytes(packed, flagBytes + 6) + 4;
        var maskStep = (blockSize * blockSize) >> 3;
        var stride = checked(width * channels);
        var bitByte = 0;
        var bitIndex = 0;

        for (var blockY = 0; blockY < height; blockY += blockSize)
        {
            var blockHeight = Math.Min(blockSize, height - blockY);
            for (var blockX = 0; blockX < width; blockX += blockSize)
            {
                var blockWidth = Math.Min(blockSize, width - blockX);
                var commandIndex = 0;
                while (commandIndex < 3 && ReadControlBit(packed, controlOffset, flagBytes, ref bitByte, ref bitIndex))
                {
                    commandIndex++;
                }

                var command = packed[commandIndex];
                var blockByteWidth = checked(blockWidth * channels);
                switch (command)
                {
                    case 0:
                        CopyBlock(previous, output, width, channels, blockX, blockY, blockWidth, blockHeight);
                        break;
                    case 1:
                        EnsureRange(packed, rawOffset, checked(blockByteWidth * blockHeight), "PL30 raw block payload");
                        for (var row = 0; row < blockHeight; row++)
                        {
                            Buffer.BlockCopy(
                                packed,
                                rawOffset + row * blockByteWidth,
                                output,
                                ((blockY + row) * width + blockX) * channels,
                                blockByteWidth);
                        }

                        rawOffset += checked(blockByteWidth * blockHeight);
                        break;
                    case 2:
                        EnsureRange(packed, tableOffset, maskStep, "PL30 mask block table");
                        for (var row = 0; row < blockHeight; row++)
                        {
                            for (var col = 0; col < blockWidth; col++)
                            {
                                var compactIndex = col + row * blockSize;
                                var useRaw = (packed[tableOffset + compactIndex / 8] & (1 << (7 - compactIndex % 8))) != 0;
                                var dst = ((blockY + row) * width + blockX + col) * channels;
                                if (useRaw)
                                {
                                    EnsureRange(packed, rawOffset, channels, "PL30 mask raw pixel");
                                    Buffer.BlockCopy(packed, rawOffset, output, dst, channels);
                                    rawOffset += channels;
                                }
                                else
                                {
                                    Buffer.BlockCopy(previous, dst, output, dst, channels);
                                }
                            }
                        }

                        tableOffset += maskStep;
                        break;
                    case 3:
                        EnsureRange(packed, tableOffset, 1, "PL30 sparse block count");
                        var sparseCount = packed[tableOffset++];
                        EnsureRange(packed, tableOffset, sparseCount, "PL30 sparse block table");
                        var sparseIndex = 0;
                        for (var row = 0; row < blockHeight; row++)
                        {
                            for (var col = 0; col < blockWidth; col++)
                            {
                                var compactIndex = col + row * blockSize;
                                var dst = ((blockY + row) * width + blockX + col) * channels;
                                if (sparseIndex < sparseCount && packed[tableOffset + sparseIndex] == compactIndex)
                                {
                                    EnsureRange(packed, rawOffset, channels, "PL30 sparse raw pixel");
                                    Buffer.BlockCopy(packed, rawOffset, output, dst, channels);
                                    rawOffset += channels;
                                    sparseIndex++;
                                }
                                else
                                {
                                    Buffer.BlockCopy(previous, dst, output, dst, channels);
                                }
                            }
                        }

                        tableOffset += sparseCount;
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported PL30 block-convert command: {command}");
                }
            }
        }

        return output;
    }

    private static void CopyBlock(byte[] source, byte[] destination, int width, int channels, int blockX, int blockY, int blockWidth, int blockHeight)
    {
        var rowBytes = checked(blockWidth * channels);
        for (var row = 0; row < blockHeight; row++)
        {
            var offset = ((blockY + row) * width + blockX) * channels;
            Buffer.BlockCopy(source, offset, destination, offset, rowBytes);
        }
    }

    private static bool ReadControlBit(byte[] data, int offset, int length, ref int byteIndex, ref int bitIndex)
    {
        if (byteIndex >= length)
        {
            throw new InvalidDataException("PL30 block-convert control bitstream is truncated.");
        }

        var bit = (data[offset + byteIndex] & (1 << (7 - bitIndex))) != 0;
        bitIndex++;
        if (bitIndex == 8)
        {
            bitIndex = 0;
            byteIndex++;
        }

        return bit;
    }

    private static int ReadInt32FromBytes(byte[] data, int offset)
    {
        EnsureRange(data, offset, 4, "PL30 block-convert integer");
        return BitConverter.ToInt32(data, offset);
    }

    private static void EnsureRange(byte[] data, int offset, int length, string label)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
        {
            throw new EndOfStreamException($"{label} is truncated.");
        }
    }

    private static byte[] DecodeHuffmanOnly(byte[] payload, int expectedSize, string label)
    {
        var reader = new MsbBitReader(payload);
        var root = ReadHuffmanNode(ref reader);
        var output = new byte[expectedSize];
        for (var i = 0; i < output.Length; i++)
        {
            var node = root;
            while (node.Value is null)
            {
                node = reader.ReadBit() == 0 ? node.Left! : node.Right!;
            }

            output[i] = (byte)node.Value.Value;
        }

        return output;
    }

    private static HuffmanNode ReadHuffmanNode(ref MsbBitReader reader)
    {
        if (reader.ReadBit() == 0)
        {
            return new HuffmanNode(reader.ReadBits(8));
        }

        var left = ReadHuffmanNode(ref reader);
        var right = ReadHuffmanNode(ref reader);
        return new HuffmanNode(left, right);
    }

    private static byte[] DecompressLzss(ReadOnlySpan<byte> input, int expectedSize, string label)
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
            throw new InvalidDataException($"{label} LZSS decoded {dst} bytes, expected {output.Length}.");
        }

        return output;
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

    private static int Hash3(byte[] input, int offset) =>
        input[offset] | (input[offset + 1] << 8) | (input[offset + 2] << 16);

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

    private static byte[] DecompressRle(byte[] input, int unpackedSize, int step)
    {
        var result = new byte[unpackedSize];
        var src = 0;
        for (var i = 0; i < step; i++)
        {
            var v1 = input[src++];
            result[i] = v1;
            var dst = i + step;
            while (dst < result.Length)
            {
                var v2 = input[src++];
                result[dst] = v2;
                dst += step;
                if (v2 == v1)
                {
                    int count = input[src++];
                    if ((count & 0x80) != 0)
                    {
                        count = input[src++] + ((count & 0x7F) << 8) + 128;
                    }

                    while (count-- > 0 && dst < result.Length)
                    {
                        result[dst] = v2;
                        dst += step;
                    }

                    if (dst < result.Length)
                    {
                        v2 = input[src++];
                        result[dst] = v2;
                        dst += step;
                    }
                }

                v1 = v2;
            }
        }

        return result;
    }

    private static byte[] CompressRle(byte[] data, int step)
    {
        using var output = new MemoryStream(data.Length);
        for (var lane = 0; lane < step; lane++)
        {
            var values = new List<byte>((data.Length + step - 1) / step);
            for (var pos = lane; pos < data.Length; pos += step)
            {
                values.Add(data[pos]);
            }

            WriteRleLane(values, output);
        }

        return output.ToArray();
    }

    private static void WriteRleLane(List<byte> values, Stream output)
    {
        output.WriteByte(values[0]);
        var previous = values[0];
        var index = 1;
        while (index < values.Count)
        {
            var current = values[index++];
            output.WriteByte(current);
            if (current != previous)
            {
                previous = current;
                continue;
            }

            var repeated = 0;
            while (index < values.Count && values[index] == current)
            {
                repeated++;
                index++;
            }

            if (repeated > 0x7FFF)
            {
                throw new InvalidDataException("PLT RLE run is too long to encode.");
            }

            WriteRleCount(output, repeated);
            if (index < values.Count)
            {
                current = values[index++];
                output.WriteByte(current);
            }

            previous = current;
        }
    }

    private static void WriteRleCount(Stream output, int count)
    {
        if (count < 128)
        {
            output.WriteByte((byte)count);
            return;
        }

        var encoded = count - 128;
        output.WriteByte((byte)(0x80 | ((encoded >> 8) & 0x7F)));
        output.WriteByte((byte)(encoded & 0xFF));
    }

    private static int CheckedPixelByteCount(uint width, uint height, int channels)
    {
        if (channels is not (1 or 3 or 4))
        {
            throw new NotSupportedException($"Unsupported PLT channel count: {channels}");
        }

        return checked((int)(width * height * (uint)channels));
    }

    private ref struct MsbBitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;
        private int _mask;
        private int _current;

        public MsbBitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
            _mask = 0x80;
            _current = 0;
        }

        public int ReadBit()
        {
            if (_mask == 0x80)
            {
                if (_offset >= _data.Length)
                {
                    throw new EndOfStreamException("PLT LZSS bit stream ended unexpectedly.");
                }

                _current = _data[_offset++];
            }

            var bit = (_current & _mask) != 0 ? 1 : 0;
            _mask >>= 1;
            if (_mask == 0)
            {
                _mask = 0x80;
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
        private readonly List<byte> _data = [];
        private int _current;
        private int _mask = 0x80;

        public void WriteBit(int bit)
        {
            if (bit != 0)
            {
                _current |= _mask;
            }

            _mask >>= 1;
            if (_mask != 0)
            {
                return;
            }

            _data.Add((byte)_current);
            _current = 0;
            _mask = 0x80;
        }

        public void WriteBits(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                WriteBit((value >> i) & 1);
            }
        }

        public byte[] ToArray()
        {
            if (_mask != 0x80)
            {
                _data.Add((byte)_current);
                _current = 0;
                _mask = 0x80;
            }

            return [.. _data];
        }
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
        }

        public int? Value { get; }
        public HuffmanNode? Left { get; }
        public HuffmanNode? Right { get; }
    }
}
