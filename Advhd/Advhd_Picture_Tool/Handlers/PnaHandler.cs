using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Text.Json;

namespace AdvhdPictureTool.Handlers
{
    public class PnaHandler : IFormatHandler
    {
        public string Tag => "pna";

        #region --- Metadata Classes ---

        // 这个内部类仅用于在JSON中提供一个清晰可读的摘要信息
        public class FrameMetadata
        {
            public int OffsetX { get; set; }
            public int OffsetY { get; set; }
            public uint Width { get; set; }
            public uint Height { get; set; }
        }

        // 核心数据结构：用于存储每一帧的完整信息
        public class PnaFrameEntry
        {
            // 存储从文件中读取的原始40字节索引块
            public byte[] RawIndexData { get; set; } = new byte[40];

            // 存储可读的元数据摘要，方便JSON查看和代码使用
            public FrameMetadata Info { get; set; } = new();
        }

        // PNA文件的整体元数据结构
        public class PnaMetadata
        {
            public List<PnaFrameEntry> Frames { get; set; } = new();
        }

        #endregion

        public bool Identify(BinaryReader reader, string filePath)
        {
            // 优先检查文件扩展名，提高效率
            if (!filePath.EndsWith(".pna", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 然后再验证文件头签名
            if (reader.BaseStream.Length < 4) return false;
            reader.BaseStream.Position = 0;
            return reader.ReadUInt32() == 0x50414E50; // 'PNAP'
        }

        public object Convert(string sourceFile, string destPath)
        {
            // destPath 在PNA处理中代表一个输出目录
            string outputDir = destPath;
            Directory.CreateDirectory(outputDir);

            var pnaMetadata = new PnaMetadata();

            using var fs = File.OpenRead(sourceFile);
            using var reader = new BinaryReader(fs);

            fs.Position = 0x10;
            int count = reader.ReadInt32();
            uint indexOffset = 0x14;
            // 图像数据区的起始位置 = 文件头(0x14) + 所有索引块的总大小
            uint currentDataOffset = indexOffset + (uint)count * 40;

            for (int i = 0; i < count; i++)
            {
                fs.Position = indexOffset;

                // 读取并保存完整的40字节索引块
                byte[] rawIndexData = reader.ReadBytes(40);
                var entry = new PnaFrameEntry { RawIndexData = rawIndexData };

                uint frameSize;
                // 从刚读取的字节数组中解析所需信息，而不是直接从文件流中读取
                using (var ms = new MemoryStream(rawIndexData))
                using (var entryReader = new BinaryReader(ms))
                {
                    ms.Position = 8; // OffsetX, OffsetY, Width, Height 从第8字节开始
                    entry.Info.OffsetX = entryReader.ReadInt32();
                    entry.Info.OffsetY = entryReader.ReadInt32();
                    entry.Info.Width = entryReader.ReadUInt32();
                    entry.Info.Height = entryReader.ReadUInt32();

                    ms.Position = 36; // size 从第36字节开始
                    frameSize = entryReader.ReadUInt32();
                }

                pnaMetadata.Frames.Add(entry);

                if (frameSize > 0)
                {
                    fs.Position = currentDataOffset;
                    byte[] frameData = reader.ReadBytes((int)frameSize); // frameData本身就是个PNG文件

                    // 直接保存，删除整个 image.ProcessPixelRows(...) 代码块
                    string framePath = Path.Combine(outputDir, $"{i:D3}.png");
                    File.WriteAllBytes(framePath, frameData);

                    currentDataOffset += frameSize;
                }

                indexOffset += 40; // 每个索引块固定40字节
            }
            return pnaMetadata;
        }

        public void Repack(string sourcePath, string destFile)
        {
            // 初始化路径和加载元数据
            string inputDir = sourcePath;
            string originalFileName = new DirectoryInfo(sourcePath).Name;
            string formatDir = new DirectoryInfo(sourcePath).Parent!.Parent!.FullName;
            string jsonPath = Path.Combine(formatDir, "metadata", originalFileName + ".json");

            if (!File.Exists(jsonPath))
            {
                throw new FileNotFoundException($"找不到元数据文件: {jsonPath}");
            }

            var json = File.ReadAllText(jsonPath);
            var metadata = JsonSerializer.Deserialize<PnaMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (metadata == null)
            {
                throw new InvalidDataException("无法解析元数据文件或文件为空。");
            }

            var frameFiles = Directory.GetFiles(inputDir, "*.png").OrderBy(f => f).ToList();

            if (frameFiles.Count == 0 && metadata.Frames.Any(f => f.Info.Width > 0))
            {
                throw new FileNotFoundException($"fix 目录 '{inputDir}' 中缺少 PNG 文件，无法回封。");
            }

            // 准备写入文件
            using var fs = File.Create(destFile);
            using var writer = new BinaryWriter(fs);

            // 写入图像数据部分
            fs.Seek(0x14 + metadata.Frames.Count * 40, SeekOrigin.Begin);

            var frameDataList = new List<byte[]>();
            for (int i = 0; i < metadata.Frames.Count; i++)
            {
                if (metadata.Frames[i].Info.Width == 0 || metadata.Frames[i].Info.Height == 0) continue;

                var frameFile = frameFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == $"{i:D3}");
                if (frameFile == null)
                {
                    throw new FileNotFoundException($"在 '{inputDir}' 中找不到 PNA 第 {i} 帧对应的文件: {i:D3}.png");
                }

                // 直接读取整个PNG文件的字节，删除Image.Load和ProcessPixelRows
                var pngData = File.ReadAllBytes(frameFile);

                writer.Write(pngData);
                frameDataList.Add(pngData);
            }

            // 写入文件头和索引块部分
            fs.Position = 0;

            writer.Write(0x50414E50); // 'PNAP'
            writer.Write(new byte[12]);
            writer.Write(metadata.Frames.Count);

            int dataIndex = 0;
            for (int i = 0; i < metadata.Frames.Count; i++)
            {
                var frameEntry = metadata.Frames[i];

                // 基于原始索引块进行修改
                if (frameEntry.Info.Width > 0 && frameEntry.Info.Height > 0)
                {
                    var pngData = frameDataList[dataIndex++];
                    // 在内存中修改原始索引块的 size 字段
                    using (var ms = new MemoryStream(frameEntry.RawIndexData))
                    using (var entryWriter = new BinaryWriter(ms))
                    {
                        ms.Position = 36; // size 字段在40字节块的第36字节处
                        entryWriter.Write((uint)pngData.Length);
                    }
                }

                // 将这个（可能已被修改的）完整的40字节索引块写入文件
                writer.Write(frameEntry.RawIndexData);
            }
        }
    }
}