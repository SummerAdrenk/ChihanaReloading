// ============================================================================
// AnmHandler.cs
// ANM 动画精灵格式处理器 (IFormatHandler 实现)
//
// 格式识别: 魔数 "AN00" / "AN01" / "AN20" / "AN21" (ASCII, 文件头 4 字节)
//
// 转换 (Convert):
//   调用 ArcANM.Extract 将 ANM 文件解包为多帧 PNG 到目标目录
//   返回 Metadata: Version, CanvasWidth/Height, CanvasOffsetX/Y
//
// 重打包 (Repack):
//   读取 metadata JSON 恢复版本与画布信息
//   调用 ArcANM.Create 从 PNG 帧序列重建 ANM 文件
//
// 依赖: ArcANM (底层实现), PicturePathHelper (元数据路径)
// ============================================================================
using System.Text;
using System.Text.Json;

namespace Kaguya_YaneKit.Formats.Picture.Handlers;

public sealed class AnmHandler : IFormatHandler
{
    public string Tag => "anm";

    public sealed class Metadata
    {
        public string Version { get; set; } = "";
        public uint CanvasWidth { get; set; }
        public uint CanvasHeight { get; set; }
        public int CanvasOffsetX { get; set; }
        public int CanvasOffsetY { get; set; }
        public AnmAnimationControlInfo? AnimationControl { get; set; }
        public int? GlobalPixelChannels { get; set; }
        public int? GlobalCompressionMode { get; set; }
        public List<AnmFrameInfo> Frames { get; set; } = [];
    }

    public bool Identify(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 4) return false;
        reader.BaseStream.Position = 0;
        var signature = Encoding.ASCII.GetString(reader.ReadBytes(4));
        return signature is "AN00" or "AN01" or "AN20" or "AN21";
    }

    public object Convert(string sourceFile, string destPath)
    {
        Directory.CreateDirectory(destPath);
        var (version, canvasInfo) = ArcANM.Extract(sourceFile, destPath);
        return new Metadata
        {
            Version = version,
            CanvasWidth = canvasInfo.Width,
            CanvasHeight = canvasInfo.Height,
            CanvasOffsetX = canvasInfo.OffsetX,
            CanvasOffsetY = canvasInfo.OffsetY,
            AnimationControl = canvasInfo.AnimationControl,
            GlobalPixelChannels = canvasInfo.GlobalPixelChannels,
            GlobalCompressionMode = canvasInfo.GlobalCompressionMode,
            Frames = canvasInfo.Frames
        };
    }

    public void Repack(string sourcePath, string destFile)
    {
        var originalFileName = new DirectoryInfo(sourcePath).Name;
        var jsonPath = PicturePathHelper.GetMetadataPathForSource(sourcePath);
        if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException($"Missing ANM frame directory: {sourcePath}");
        if (!File.Exists(jsonPath)) throw new FileNotFoundException($"Missing JSON metadata: {jsonPath}");

        var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(jsonPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Failed to parse ANM JSON metadata.");

        ArcANM.Create(sourcePath, destFile, metadata.Version, new AnmInfo
        {
            Width = metadata.CanvasWidth,
            Height = metadata.CanvasHeight,
            OffsetX = metadata.CanvasOffsetX,
            OffsetY = metadata.CanvasOffsetY,
            AnimationControl = metadata.AnimationControl,
            GlobalPixelChannels = metadata.GlobalPixelChannels,
            GlobalCompressionMode = metadata.GlobalCompressionMode,
            Frames = metadata.Frames
        });
    }
}
