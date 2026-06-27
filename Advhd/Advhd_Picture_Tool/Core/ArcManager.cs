using System.Text;

namespace ws2Parse
{

    internal class ArcEntry
    {
        public string Name { get; set; } = "";
        public uint Size { get; set; }
        public long Offset { get; set; }
    }

    public static class ArcManager
    {
        public static void Unpack(string arcPath, string outputDir, bool doDecrypt = false)
        {
            Console.WriteLine($"--- 开始解包文件: {Path.GetFileName(arcPath)} ---");
            ClearDirectory(outputDir);
            Directory.CreateDirectory(outputDir);

            using (var fs = new FileStream(arcPath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                int fileCount = reader.ReadInt32();
                uint indexSize = reader.ReadUInt32();
                long baseOffset = 8 + indexSize;

                Console.WriteLine($"[INFO] 发现 {fileCount} 个文件，索引大小 {indexSize} 字节。");

                var directory = new List<ArcEntry>(fileCount);
                long currentIndexEnd = fs.Position + indexSize;
                var nameBuilder = new StringBuilder();

                // 1. 读取索引
                for (int i = 0; i < fileCount; i++)
                {
                    if (fs.Position >= currentIndexEnd) throw new InvalidDataException("索引读取超出边界。");

                    var entry = new ArcEntry
                    {
                        Size = reader.ReadUInt32(),
                        Offset = baseOffset + reader.ReadUInt32()
                    };

                    nameBuilder.Clear();
                    char c;
                    while ((c = (char)reader.ReadUInt16()) != 0)
                    {
                        nameBuilder.Append(c);
                    }
                    entry.Name = nameBuilder.ToString();
                    directory.Add(entry);
                }

                // 2. 提取文件
                Console.WriteLine("--- 开始提取文件 ---");
                foreach (var entry in directory)
                {
                    Console.Write($"  -> 提取中: {entry.Name}");
                    fs.Seek(entry.Offset, SeekOrigin.Begin);
                    byte[] data = reader.ReadBytes((int)entry.Size);

                    // 只有当 doDecrypt 为 true 且文件是 .ws2 时，才执行解密
                    if (doDecrypt && entry.Name.EndsWith(".ws2", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Write(" (解密中)");
                        for (int i = 0; i < data.Length; i++)
                        {
                            data[i] = (byte)((data[i] << 6) | (data[i] >> 2));
                        }
                    }

                    string outputPath = Path.Combine(outputDir, entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllBytes(outputPath, data);
                    Console.WriteLine("...完成");
                }
            }
        }

        private static void ClearDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Console.WriteLine($"[INFO] 正在清空输出目录: {path}");
                DirectoryInfo di = new DirectoryInfo(path);
                foreach (FileInfo file in di.GetFiles()) { file.Delete(); }
                foreach (DirectoryInfo dir in di.GetDirectories()) { dir.Delete(true); }
            }
        }

        public static void Pack(string inputDir, string arcPath, bool doEncrypt = false)
        {
            Console.WriteLine($"--- 开始封包文件夹: {inputDir} ---");
            string[] filesToPack = Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories);
            if (filesToPack.Length == 0)
            {
                Console.WriteLine("[警告] 输入文件夹为空，未执行任何操作。");
                return;
            }

            // 只有当输出路径包含目录信息时，才去创建目录
            string? outputDirectory = Path.GetDirectoryName(arcPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var directory = new List<ArcEntry>(filesToPack.Length);
            uint indexSize = 0;

            // 1. 预计算索引大小
            foreach (var filePath in filesToPack)
            {
                string fileName = Path.GetFileName(filePath);
                indexSize += 8; // Size (4 bytes) + Offset (4 bytes)
                indexSize += (uint)(Encoding.Unicode.GetByteCount(fileName) + 2); // Filename + Null Terminator
            }

            long baseOffset = 8 + indexSize;
            long currentDataOffset = 0;

            using (var fs = new FileStream(arcPath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(fs, Encoding.Unicode))
            {
                // 2. 先跳过头部和索引区，直接写入文件数据
                fs.Seek(baseOffset, SeekOrigin.Begin);

                Console.WriteLine("--- 开始写入文件数据 ---");
                foreach (var filePath in filesToPack)
                {
                    string fileName = Path.GetFileName(filePath);
                    Console.Write($"  -> 打包中: {fileName}");

                    byte[] data = File.ReadAllBytes(filePath);

                    // 只有当 doEncrypt 为 true 且文件是 .ws2 时，才执行加密
                    if (doEncrypt && fileName.EndsWith(".ws2", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Write(" (加密中)");
                        for (int i = 0; i < data.Length; i++)
                        {
                            // 循环左移2位 (ROL 2)
                            data[i] = (byte)((data[i] << 2) | (data[i] >> 6));
                        }
                    }

                    writer.Write(data);

                    directory.Add(new ArcEntry
                    {
                        Name = fileName,
                        Size = (uint)data.Length,
                        Offset = currentDataOffset
                    });
                    currentDataOffset += data.Length;
                    Console.WriteLine("...完成");
                }

                // 3. 回到文件开头，写入头部和索引
                Console.WriteLine("--- 开始写入索引 ---");
                fs.Seek(0, SeekOrigin.Begin);
                writer.Write(directory.Count);
                writer.Write(indexSize);

                foreach (var entry in directory)
                {
                    writer.Write(entry.Size);
                    writer.Write((uint)entry.Offset);
                    writer.Write(entry.Name.ToCharArray());
                    writer.Write((short)0);
                }
            }
        }
    }
}