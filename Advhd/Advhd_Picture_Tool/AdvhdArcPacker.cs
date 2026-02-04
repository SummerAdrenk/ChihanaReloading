using System.Text;

namespace AdvhdPictureTool
{
    internal class ArcEntry
    {
        public string Name { get; set; } = "";
        public uint Size { get; set; }
        public long Offset { get; set; }
    }

    public static class AdvhdArcPacker
    {
        #region --- NEW: Unpack Functionality ---
        public static void Unpack(string[] args)
        {
            string targetPath = args[1];

            if (File.Exists(targetPath))
            {
                // 单一文件模式
                string outputDir = Path.Combine(Path.GetDirectoryName(targetPath)!, Path.GetFileNameWithoutExtension(targetPath));
                ExecuteUnpack(targetPath, outputDir, true);
            }
            else if (Directory.Exists(targetPath))
            {
                // 批量目录模式
                Console.WriteLine($"\n-- 开始以批量模式解包目录: {targetPath} --");
                // 允许解包其他常见扩展名，而不仅仅是 .arc
                var filesToUnpack = Directory.EnumerateFiles(targetPath)
                    .Where(f => f.EndsWith(".arc", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (filesToUnpack.Count == 0)
                {
                    Console.WriteLine("未在该目录中找到任何 .arc, .dat, 或 .bin 文件。");
                    return;
                }

                int successCount = 0;
                int failureCount = 0;
                foreach (var arcFile in filesToUnpack)
                {
                    try
                    {
                        string outputDir = Path.Combine(targetPath, Path.GetFileNameWithoutExtension(arcFile));
                        ExecuteUnpack(arcFile, outputDir, true);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n解包文件 '{Path.GetFileName(arcFile)}' 时发生严重错误: {ex.Message}");
                        Console.ResetColor();
                        failureCount++;
                    }
                }
                Console.WriteLine($"\n批量解包完成！成功: {successCount}, 失败: {failureCount}");
            }
            else
            {
                Console.WriteLine($"错误: 路径 '{targetPath}' 不是一个有效的文件或目录。");
            }
        }

        private static void ExecuteUnpack(string arcPath, string outputDir, bool doDecrypt = false)
        {
            Console.WriteLine($"\n--- 开始解包文件: {Path.GetFileName(arcPath)} ---");
            ClearDirectory(outputDir);
            Directory.CreateDirectory(outputDir);

            using (var fs = new FileStream(arcPath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                int fileCount = reader.ReadInt32();
                if (fileCount <= 0 || fileCount > 100000)
                {
                    throw new InvalidDataException($"文件数量 '{fileCount}' 无效，这可能不是一个有效的 .arc 文件。");
                }
                uint indexSize = reader.ReadUInt32();
                long baseOffset = 8 + indexSize;
                if (baseOffset > fs.Length)
                {
                    throw new InvalidDataException($"索引大小 '{indexSize}' 无效，超出了文件长度。");
                }


                Console.WriteLine($"[INFO] 发现 {fileCount} 个文件，索引大小 {indexSize} 字节。");

                var directory = new List<ArcEntry>(fileCount);
                long currentIndexEnd = fs.Position + indexSize;
                var nameBuilder = new StringBuilder();

                // 读取索引
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

                    // 直接读取2个字节并转换为char。
                    // 确保无论BinaryReader的默认编码是什么，我们都能正确读取UTF-16字符。
                    while ((c = (char)reader.ReadUInt16()) != 0)
                    {
                        nameBuilder.Append(c);
                    }
                    entry.Name = nameBuilder.ToString();
                    directory.Add(entry);
                }

                // 提取文件
                Console.WriteLine("--- 开始提取文件 ---");
                foreach (var entry in directory)
                {
                    Console.Write($"  -> 提取中: {entry.Name}");
                    if (entry.Offset + entry.Size > fs.Length)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"...错误！文件偏移量或大小超出范围。");
                        Console.ResetColor();
                        continue;
                    }

                    fs.Seek(entry.Offset, SeekOrigin.Begin);
                    byte[] data = reader.ReadBytes((int)entry.Size);

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
                Console.WriteLine($"[INFO] 正在清空已存在的输出目录: {path}");
                Directory.Delete(path, true);
            }
        }

        #endregion

        #region --- Existing: Pack Functionality ---
        public static void Pack(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("错误: 指令至少需要一个[目标目录]参数。");
                return;
            }

            string targetPath = args[1];
            bool isBatchMode = args.Contains("-all");
            string extension = ".arc"; // AdvHD 通常是 .arc

            // 查找自定义后缀
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-ext" && i + 1 < args.Length)
                {
                    extension = args[i + 1];
                    if (!extension.StartsWith("."))
                    {
                        extension = "." + extension;
                    }
                    break;
                }
            }


            if (!Directory.Exists(targetPath))
            {
                Console.WriteLine($"错误: 目录 '{targetPath}' 不存在。");
                return;
            }

            if (isBatchMode)
            {
                PackAll(targetPath, extension);
            }
            else
            {
                PackSingle(targetPath, extension);
            }
        }

        private static void PackSingle(string directoryPath, string extension)
        {
            Console.WriteLine($"\n-- 开始以单一模式封包: {directoryPath} --");
            string dirName = new DirectoryInfo(directoryPath).Name;
            string parentDir = Directory.GetParent(directoryPath)?.FullName ?? "";
            string outputPath = Path.Combine(parentDir, dirName + extension);

            // 直接调用核心封包方法
            ExecutePack(directoryPath, outputPath, false);
        }

        private static void PackAll(string parentDirectoryPath, string extension)
        {
            Console.WriteLine($"\n-- 开始以批量模式封包: {parentDirectoryPath} --");
            var subDirectories = Directory.GetDirectories(parentDirectoryPath);

            if (subDirectories.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("错误: 批量模式下，目标目录中必须包含至少一个子文件夹用于封包。");
                Console.ResetColor();
                return;
            }

            int successCount = 0;
            int failureCount = 0;

            foreach (var dir in subDirectories)
            {
                try
                {
                    string dirName = new DirectoryInfo(dir).Name;
                    string outputPath = Path.Combine(parentDirectoryPath, dirName + extension);
                    Console.WriteLine($"\n正在处理子目录: {dirName}");
                    ExecutePack(dir, outputPath, false);
                    successCount++;
                }
                catch (Exception)
                {
                    failureCount++;
                }
            }
            Console.WriteLine($"\n批量封包完成！成功: {successCount}, 失败: {failureCount}");
        }
        private static void ExecutePack(string inputDir, string arcPath, bool doEncrypt = false)
        {
            Console.WriteLine($"--- 开始封包文件夹: {inputDir} ---");
            string[] filesToPack = Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories);
            if (filesToPack.Length == 0)
            {
                Console.WriteLine("[警告] 输入文件夹为空，未执行任何操作。");
                return;
            }
            string? outputDirectory = Path.GetDirectoryName(arcPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var directory = new List<ArcEntry>(filesToPack.Length);
            uint indexSize = 0;
            foreach (var filePath in filesToPack)
            {
                string relativePath = Path.GetRelativePath(inputDir, filePath).Replace('\\', '/');
                indexSize += 8; // Size + Offset
                indexSize += (uint)(Encoding.Unicode.GetByteCount(relativePath) + 2); // Filename + Null
            }

            long baseOffset = 8 + indexSize;
            long currentDataOffset = 0;
            using (var fs = new FileStream(arcPath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(fs, Encoding.Unicode))
            {
                fs.Seek(baseOffset, SeekOrigin.Begin);
                Console.WriteLine("--- 开始写入文件数据 ---");
                foreach (var filePath in filesToPack)
                {
                    string relativePath = Path.GetRelativePath(inputDir, filePath).Replace('\\', '/');
                    Console.Write($"  -> 打包中: {relativePath}");
                    byte[] data = File.ReadAllBytes(filePath);
                    if (doEncrypt && filePath.EndsWith(".ws2", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Write(" (加密中)");
                        for (int i = 0; i < data.Length; i++) { data[i] = (byte)((data[i] << 2) | (data[i] >> 6)); }
                    }
                    writer.Write(data);
                    directory.Add(new ArcEntry { Name = relativePath, Size = (uint)data.Length, Offset = currentDataOffset });
                    currentDataOffset += data.Length;
                    Console.WriteLine("...完成");
                }
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
        #endregion
    }
}