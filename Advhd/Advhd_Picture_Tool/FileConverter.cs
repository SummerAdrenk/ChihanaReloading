using AdvhdPictureTool.Handlers;
using System.Text.Json;

namespace AdvhdPictureTool;

public static class FileConverter
{
    private static readonly Dictionary<string, IFormatHandler> Handlers = new()
    {
            { "pna", new PnaHandler() },
            { "mos", new MosHandler() },
            { "png", new PngHandler() },
    };
    public static void ConvertAll(string path)
    {
        Console.WriteLine($"开始在 \"{path}\" 中执行转换...");
        path = Path.GetFullPath(path);

        if (Directory.Exists(Path.Combine(path, "orig")))
        {
            string formatTag = new DirectoryInfo(path).Name;
            if (Handlers.TryGetValue(formatTag, out var handler))
            {
                ProcessConvertFormat(path, handler);
            }
            else
            {
                Console.WriteLine($"[ERROR] 未知的格式目录 '{formatTag}'");
            }
        }
        else
        {
            foreach (var handler in Handlers.Values)
            {
                string formatDir = Path.Combine(path, handler.Tag);
                if (Directory.Exists(formatDir))
                {
                    ProcessConvertFormat(formatDir, handler);
                }
            }
        }
    }
    public static void RepackAll(string path)
    {
        Console.WriteLine($"开始在 '{path}' 中执行重新打包...");
        path = Path.GetFullPath(path);

        bool isSingleFormatDir = Handlers.Keys.Contains(new DirectoryInfo(path).Name);

        if (isSingleFormatDir)
        {
            // 处理单个格式目录
            string formatTag = new DirectoryInfo(path).Name;
            if (Handlers.TryGetValue(formatTag, out var handler))
            {
                string sourceDir = Path.Combine(path, "fix");
                if (Directory.Exists(sourceDir) && Directory.EnumerateFileSystemEntries(sourceDir).Any())
                {
                    ProcessRepackFormat(path, handler, sourceDir);
                }
                else
                {
                    Console.WriteLine($"\n[WARNNING] 在 {handler.Tag.ToUpper()} 目录中未找到 'fix' 文件夹或该文件夹为空，已跳过。");
                }
            }
        }
        else
        {
            // 处理总目录
            foreach (var handler in Handlers.Values)
            {
                string formatDir = Path.Combine(path, handler.Tag);
                if (Directory.Exists(formatDir))
                {
                    string sourceDir = Path.Combine(formatDir, "fix");
                    if (Directory.Exists(sourceDir) && Directory.EnumerateFileSystemEntries(sourceDir).Any())
                    {
                        ProcessRepackFormat(formatDir, handler, sourceDir);
                    }
                }
            }
        }
    }
    public static void RepackFix(string fixDir)
    {
        Console.WriteLine($"开始从 '{fixDir}' 中执行定向重新打包...");
        fixDir = Path.GetFullPath(fixDir);

        if (!Directory.Exists(fixDir) || !new DirectoryInfo(fixDir).Name.Equals("fix", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[ERROR] 提供的路径不是一个有效的 'fix' 文件夹。");
            return;
        }

        DirectoryInfo formatDirInfo = new DirectoryInfo(fixDir).Parent!;
        string formatTag = formatDirInfo.Name;

        if (Handlers.TryGetValue(formatTag, out var handler))
        {
            ProcessRepackFormat(formatDirInfo.FullName, handler, fixDir);
        }
        else
        {
            Console.WriteLine($"[ERROR] 未能根据目录 '{formatTag}' 识别出对应的处理器。");
        }
    }
    private static void ProcessConvertFormat(string formatDir, IFormatHandler handler)
    {
        string origDir = Path.Combine(formatDir, "orig");
        string pngDir = Path.Combine(formatDir, "png");
        string metaDir = Path.Combine(formatDir, "metadata");

        if (!Directory.Exists(origDir)) return;
        Directory.CreateDirectory(pngDir);

        Console.WriteLine($"\n>> 正在处理 {handler.Tag.ToUpper()} 文件");

        var files = Directory.GetFiles(origDir, "*.*", SearchOption.AllDirectories).ToList();
        int total = files.Count;
        int success = 0;
        int failure = 0;

        Parallel.ForEach(files, file =>
        {
            try
            {
                string originalFileName = Path.GetFileName(file);
                string destPathBase;
                string metaPath;

                if (handler is PnaHandler)
                {
                    destPathBase = Path.Combine(pngDir, originalFileName);
                    metaPath = Path.Combine(metaDir, originalFileName + ".json");
                }
                else
                {
                    destPathBase = Path.Combine(pngDir, Path.ChangeExtension(originalFileName, null));
                    metaPath = Path.Combine(metaDir, Path.ChangeExtension(originalFileName, ".json"));
                }

                if (!File.Exists(metaPath))
                {
                    throw new FileNotFoundException($"[ERROR] 找不到由sorter创建的基础元数据文件: {metaPath}");
                }

                object handlerMetadata = handler.Convert(file, destPathBase);
                var baseNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
                var handlerNode = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(handlerMetadata))!.AsObject();

                var propertiesToMove = handlerNode.ToList();
                foreach (var property in propertiesToMove)
                {
                    var node = property.Value;
                    handlerNode.Remove(property.Key);
                    baseNode[property.Key] = node;
                }

                var serializerOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                File.WriteAllText(metaPath, baseNode.ToJsonString(serializerOptions));

                Interlocked.Increment(ref success);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 转换文件 '{Path.GetFileName(file)}' 时出错: {ex.Message}");
                Interlocked.Increment(ref failure);
            }
        });

        Console.WriteLine($"处理完成! 共计: {total}, 成功: {success}, 失败: {failure}");
    }
    private static void ProcessRepackFormat(string formatDir, IFormatHandler handler, string sourceDir)
    {
        string metaDir = Path.Combine(formatDir, "metadata");
        string newDir = Path.Combine(formatDir, "new");

        if (!Directory.Exists(sourceDir) || !Directory.Exists(metaDir)) return;
        Directory.CreateDirectory(newDir);

        Console.WriteLine($"\n>> 正在从 \"{new DirectoryInfo(sourceDir).Name}\" 目录为 {handler.Tag.ToUpper()} 格式重新打包");

        if (handler is PnaHandler)
        {
            var sourceDirs = Directory.GetDirectories(sourceDir).ToList();
            int success = 0;
            int failure = 0;

            Parallel.ForEach(sourceDirs, dir =>
            {
                try
                {
                    string mangledFileName = new DirectoryInfo(dir).Name;
                    string metadataPath = Path.Combine(metaDir, mangledFileName + ".json");

                    if (!File.Exists(metadataPath))
                    {
                        Console.WriteLine($"[WARNNING] 找不到项目 \"{mangledFileName}\" 的元数据，已跳过。");
                        Interlocked.Increment(ref failure);
                        return;
                    }

                    var json = File.ReadAllText(metadataPath);
                    var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
                    string originalRelativePath = metadata["OriginalRelativePath"].GetString()!;
                    string finalFileName = Path.GetFileName(originalRelativePath);

                    if (string.IsNullOrEmpty(finalFileName) || finalFileName == ".")
                    {
                        finalFileName = mangledFileName;
                    }

                    string destFile = Path.Combine(newDir, finalFileName);
                    handler.Repack(dir, destFile);
                    Interlocked.Increment(ref success);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] 重新打包 PNA \"{Path.GetFileName(dir)}\" 时出错: {ex.Message}");
                    Interlocked.Increment(ref failure);
                }
            });
            Console.WriteLine($"重新打包完成! 共计: {sourceDirs.Count}, 成功: {success}, 失败: {failure}");
        }
        else
        {
            var sourceFiles = Directory.GetFiles(sourceDir, "*.png").ToList();
            int success = 0;
            int failure = 0;

            Parallel.ForEach(sourceFiles, file =>
            {
                try
                {
                    string baseName = Path.GetFileNameWithoutExtension(file);
                    string metadataPath = Path.Combine(metaDir, baseName + ".json");

                    if (!File.Exists(metadataPath))
                    {
                        Console.WriteLine($"[WARNNING] 找不到项目 \"{Path.GetFileName(file)}\" 的元数据，已跳过。");
                        Interlocked.Increment(ref failure);
                        return;
                    }

                    var json = File.ReadAllText(metadataPath);
                    var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
                    string originalRelativePath = metadata["OriginalRelativePath"].GetString()!;
                    string finalFileName = Path.GetFileName(originalRelativePath);

                    string destFile = Path.Combine(newDir, finalFileName);
                    string sourcePathBase = Path.Combine(sourceDir, baseName);

                    handler.Repack(sourcePathBase, destFile);
                    Interlocked.Increment(ref success);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] 重新打包文件 \"{Path.GetFileName(file)}\" 时出错: {ex.Message}");
                    Interlocked.Increment(ref failure);
                }
            });
            Console.WriteLine($"重新打包完成! 共计: {sourceFiles.Count}, 成功: {success}, 失败: {failure}");
        }
    }
}