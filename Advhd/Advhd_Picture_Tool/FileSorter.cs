using AdvhdPictureTool.Handlers;
using System.Text.Json;

namespace AdvhdPictureTool
{
    public static class FileSorter
    {
        private const string PathSeparatorReplacement = "__";

        private class BaseMetadata
        {
            public string OriginalRelativePath { get; set; } = "";
        }

        // 在此定义被-sort忽略的文件后缀
        private static readonly HashSet<string> ArchiveExtensionsToIgnore = new(StringComparer.OrdinalIgnoreCase)
        {
            ".arc",
            ".dat",
            ".bin"
        };

        private static readonly List<IFormatHandler> Handlers = new()
        {
            new PnaHandler(),
            new MosHandler(),
            new PngHandler(),
        };

        public static void Sort(string sourceDir, string workDir)
        {
            Console.WriteLine($"开始从 \"{sourceDir}\" 排序文件到 \"{workDir}\"...");
            sourceDir = Path.GetFullPath(sourceDir);
            workDir = Path.GetFullPath(workDir);

            if (!Handlers.Any())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("错误: 尚未注册任何格式处理器 (Handler)。请在 FileSorter.cs 中添加。");
                Console.ResetColor();
                return;
            }

            var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
            int total = allFiles.Length;
            int success = 0;
            int failure = 0;
            int skipped = 0;

            var handledFilesByFormat = new Dictionary<string, HashSet<string>>();

            foreach (var file in allFiles)
            {
                // 在处理前检查文件扩展名
                string extension = Path.GetExtension(file);
                if (ArchiveExtensionsToIgnore.Contains(extension))
                {
                    skipped++; // 计数器加一
                    continue;  // 直接跳过当前文件，进行下一次循环
                }

                try
                {
                    using var stream = File.OpenRead(file);
                    using var reader = new BinaryReader(stream);

                    foreach (var handler in Handlers)
                    {
                        reader.BaseStream.Position = 0; // 确保每次识别都从文件头开始
                        if (handler.Identify(reader, file))
                        {
                            string relativePath = Path.GetRelativePath(sourceDir, file);
                            string originalFileName = Path.GetFileName(file);
                            string currentMangledName = originalFileName;

                            if (!handledFilesByFormat.ContainsKey(handler.Tag))
                            {
                                handledFilesByFormat[handler.Tag] = new HashSet<string>();
                            }
                            var handledSet = handledFilesByFormat[handler.Tag];

                            if (handledSet.Contains(currentMangledName))
                            {
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"警告: 发现同名文件 '{currentMangledName}'，尝试附加目录以解决冲突...");
                                Console.ResetColor();

                                var pathParts = relativePath.Split(Path.DirectorySeparatorChar);
                                for (int i = pathParts.Length - 2; i >= 0; i--)
                                {
                                    currentMangledName = $"{pathParts[i]}{PathSeparatorReplacement}{currentMangledName}";
                                    if (!handledSet.Contains(currentMangledName)) break;
                                }
                            }

                            if (handledSet.Contains(currentMangledName))
                            {
                                currentMangledName = $"{Path.GetFileNameWithoutExtension(currentMangledName)}_{Guid.NewGuid().ToString().Substring(0, 8)}{Path.GetExtension(currentMangledName)}";
                            }

                            handledSet.Add(currentMangledName);

                            string formatDir = Path.Combine(workDir, handler.Tag);
                            string origDir = Path.Combine(formatDir, "orig");
                            string metaDir = Path.Combine(formatDir, "metadata");
                            Directory.CreateDirectory(origDir);
                            Directory.CreateDirectory(metaDir);

                            string destOrigPath = Path.Combine(origDir, currentMangledName);
                            File.Copy(file, destOrigPath, true);

                            var metadata = new BaseMetadata { OriginalRelativePath = relativePath };
                            var serializerOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                            var json = JsonSerializer.Serialize(metadata, serializerOptions);

                            string destMetaPath;
                            if (handler is PnaHandler)
                            {
                                destMetaPath = Path.Combine(metaDir, currentMangledName + ".json");
                            }
                            else
                            {
                                destMetaPath = Path.Combine(metaDir, Path.ChangeExtension(currentMangledName, ".json"));
                            }

                            File.WriteAllText(destMetaPath, json);

                            Console.WriteLine($"  [{handler.Tag.ToUpper()}] {relativePath} -> {currentMangledName}");
                            success++;
                            goto nextFile;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"处理文件 \"{Path.GetFileName(file)}\" 时出错: {ex.Message}");
                    failure++;
                }

            nextFile:;
            }
            Console.WriteLine("--------------------------");
            Console.WriteLine($"排序完成！共计: {total}, 成功: {success}, 失败: {failure}, 跳过封包: {skipped}, 未识别: {total - success - failure - skipped}");
        }
    }
}