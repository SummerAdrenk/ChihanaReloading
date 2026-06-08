// ============================================================================
// Ap0Handler.cs
// AP-0 Alpha Plane 格式处理器 (IFormatHandler 实现)
//
// 格式识别: 魔数 0x302D5041 ("AP-0", 小端序, 文件头 4 字节)
//
// 二进制结构:
//   [0x00] 4B  魔数 "AP-0"
//   [0x04] 4B  Width (uint32)
//   [0x08] 4B  Height (uint32)
//   [0x0C] ...  灰度像素数据 (每像素 1 字节, 自底向上排列)
//
// 转换 (Convert):
//   读取灰度数据, 扩展为 BGRA (R=G=B=灰度值, A=255), 保存为 PNG
//
// 重打包 (Repack):
//   读取 PNG 的 BGRA 像素, 通过 BitmapHelpers.ToGrayscale 转回灰度
//   写入 AP-0 头 + 灰度数据
//
// 依赖: BitmapHelpers, PicturePathHelper, System.Drawing
// ============================================================================
namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class Ap0Handler : IFormatHandler
{
    public string Tag => "ap0";

    public sealed class Metadata
    {
        public uint Width { get; set; }
        public uint Height { get; set; }
    }

    public bool Identify(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 4) return false;
        reader.BaseStream.Position = 0;
        return reader.ReadUInt32() == 0x302D5041;
    }

    public object Convert(string sourceFile, string destPath)
    {
        using var stream = File.OpenRead(sourceFile);
        using var reader = new BinaryReader(stream);
        reader.BaseStream.Position = 4;
        var metadata = new Metadata
        {
            Width = reader.ReadUInt32(),
            Height = reader.ReadUInt32()
        };

        var dataSize = checked((int)(metadata.Width * metadata.Height));
        stream.Position = 12;
        var gray = reader.ReadBytes(dataSize);
        if (gray.Length != dataSize)
        {
            throw new EndOfStreamException("Failed to read AP0 grayscale payload.");
        }

        var bgra = new byte[dataSize * 4];
        int i = 0;
        int j = 0;
        for (; i < gray.Length; i++, j += 4)
        {
            bgra[j + 0] = gray[i];
            bgra[j + 1] = gray[i];
            bgra[j + 2] = gray[i];
            bgra[j + 3] = 255;
        }

        BitmapHelpers.SavePngFromBottomUpPixels(bgra, (int)metadata.Width, (int)metadata.Height, PicturePathHelper.ChangeExtensionPreservingName(destPath, ".png"));
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
        writer.Write(0x302D5041);
        writer.Write((uint)width);
        writer.Write((uint)height);
        writer.Write(BitmapHelpers.ToGrayscale(bgra));
    }
}
