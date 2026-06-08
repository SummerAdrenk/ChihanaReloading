using System.Text;
using System.Text.Json;

namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class PltHandler : IFormatHandler
{
    public string Tag => "plt";

    public sealed class Metadata
    {
        public string Version { get; set; } = "";
        public int CanvasOffsetX { get; set; }
        public int CanvasOffsetY { get; set; }
        public uint CanvasWidth { get; set; }
        public uint CanvasHeight { get; set; }
        public int PixelChannels { get; set; } = 4;
        public int? GlobalCompressionMode { get; set; }
        public int? BlockSize { get; set; }
        public string ExtraHeaderBase64 { get; set; } = "";
        public string ReservedHeaderBase64 { get; set; } = "";
        public List<AnmFrameInfo> Frames { get; set; } = [];
    }

    public bool Identify(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 4)
        {
            return false;
        }

        reader.BaseStream.Position = 0;
        var signature = Encoding.ASCII.GetString(reader.ReadBytes(4));
        return signature is "PL00" or "PL01" or "PL10" or "PL11" or "PL20" or "PL30";
    }

    public object Convert(string sourceFile, string destPath)
    {
        Directory.CreateDirectory(destPath);
        var (version, info) = ArcPLT.Extract(sourceFile, destPath);
        return new Metadata
        {
            Version = version,
            CanvasOffsetX = info.OffsetX,
            CanvasOffsetY = info.OffsetY,
            CanvasWidth = info.Width,
            CanvasHeight = info.Height,
            PixelChannels = info.PixelChannels,
            GlobalCompressionMode = info.GlobalCompressionMode,
            BlockSize = info.BlockSize,
            ExtraHeaderBase64 = info.ExtraHeaderBase64,
            ReservedHeaderBase64 = info.ReservedHeaderBase64,
            Frames = info.Frames
        };
    }

    public void Repack(string sourcePath, string destFile)
    {
        var jsonPath = PicturePathHelper.GetMetadataPathForSource(sourcePath);
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Missing PLT frame directory: {sourcePath}");
        }

        if (!File.Exists(jsonPath))
        {
            throw new FileNotFoundException($"Missing JSON metadata: {jsonPath}");
        }

        var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(jsonPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Failed to parse PLT JSON metadata.");

        ArcPLT.Create(sourcePath, destFile, new PltInfo
        {
            Version = metadata.Version,
            OffsetX = metadata.CanvasOffsetX,
            OffsetY = metadata.CanvasOffsetY,
            Width = metadata.CanvasWidth,
            Height = metadata.CanvasHeight,
            PixelChannels = metadata.PixelChannels,
            GlobalCompressionMode = metadata.GlobalCompressionMode,
            BlockSize = metadata.BlockSize,
            ExtraHeaderBase64 = metadata.ExtraHeaderBase64,
            ReservedHeaderBase64 = metadata.ReservedHeaderBase64,
            Frames = metadata.Frames
        });
    }
}
