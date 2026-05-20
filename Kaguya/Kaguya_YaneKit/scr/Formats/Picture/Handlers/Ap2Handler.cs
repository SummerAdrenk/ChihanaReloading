// ============================================================================
// Ap2Handler.cs
// AP-2 Alpha Plane 格式处理器 (IFormatHandler 实现)
//
// 格式识别: 魔数 0x322D5041 ("AP-2", 小端序, 文件头 4 字节)
//
// 二进制结构:
//   [0x00] 4B  魔数 "AP-2"
//   [0x04] 4B  OffsetX (int32)
//   [0x08] 4B  OffsetY (int32)
//   [0x0C] 4B  Width (int32)
//   [0x10] 4B  Height (int32)
//   [0x14] 4B  HeaderExtra (int32, 用途未知, 重打包时原样写回)
//   [0x18] ...  BGRA 像素数据 (每像素 4 字节, 自底向上排列)
//
// 转换 (Convert):
//   读取 BGRA 像素数据, 直接保存为 PNG
//   返回 Metadata: OffsetX/Y, Width/Height, HeaderExtra
//
// 重打包 (Repack):
//   从 PNG 读取 BGRA 像素, 写入 AP-2 头 + 像素数据
//
// 依赖: BitmapHelpers, PicturePathHelper, System.Drawing
// ============================================================================
using System.Drawing;

namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class Ap2Handler : IFormatHandler
{
    public string Tag => "ap2";

    public sealed class Metadata
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public int HeaderExtra { get; set; }
    }

    public bool Identify(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 4) return false;
        reader.BaseStream.Position = 0;
        return reader.ReadUInt32() == 0x322D5041;
    }

    public object Convert(string sourceFile, string destPath)
    {
        using var stream = File.OpenRead(sourceFile);
        using var reader = new BinaryReader(stream);
        reader.ReadInt32();
        var metadata = new Metadata
        {
            OffsetX = reader.ReadInt32(),
            OffsetY = reader.ReadInt32(),
            Width = reader.ReadInt32(),
            Height = reader.ReadInt32()
        };
        metadata.HeaderExtra = reader.ReadInt32();
        stream.Position = 0x18;
        var pixelData = reader.ReadBytes(metadata.Width * metadata.Height * 4);
        BitmapHelpers.SavePngFromBottomUpPixels(pixelData, metadata.Width, metadata.Height, Path.ChangeExtension(destPath, ".png"));
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
        writer.Write(0x322D5041);
        writer.Write(metadata.OffsetX);
        writer.Write(metadata.OffsetY);
        writer.Write(image.Width);
        writer.Write(image.Height);
        writer.Write(metadata.HeaderExtra);
        writer.Write(BitmapHelpers.ReadBottomUpPixelsFromImage(pngPath));
    }
}
