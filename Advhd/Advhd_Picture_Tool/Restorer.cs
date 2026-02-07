using System.Text.Json;

namespace AdvhdPictureTool;

public static class Restorer
{
    private class BaseMetadata
    {
        public string OriginalRelativePath { get; set; } = "";
    }

    public static void Restore(string workDir, string sourceGameDir)
    {
        string finishDir = PrepareFinishDirectory(sourceGameDir);
        workDir = Path.GetFullPath(workDir);

        RestoreFromNewFolders(workDir, finishDir);

        Console.WriteLine("所有文件已合并到 finish 目录！");
    }

    public static void RestoreWithReplenish(string workDir, string sourceGameDir)
    {
        string finishDir = PrepareFinishDirectory(sourceGameDir);
        workDir = Path.GetFullPath(workDir);

        RestoreFromNewFolders(workDir, finishDir);

        Console.WriteLine("\n>>> 开始补充未修改的原始文件");
        int replenishedCount = 0;
        var origDirs = Directory.EnumerateDirectories(workDir, "orig", SearchOption.AllDirectories);

        foreach (var origDir in origDirs)
        {
            string formatDir = new DirectoryInfo(origDir).Parent!.FullName;
            string formatTag = new DirectoryInfo(formatDir).Name;
            string metaDir = Path.Combine(formatDir, "metadata");

            foreach (var origFile in Directory.EnumerateFiles(origDir, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    string mangledFileName = Path.GetFileName(origFile);

                    string jsonFile;
                    if (formatTag.Equals("pna", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonFile = Path.Combine(metaDir, mangledFileName + ".json");
                    }
                    else
                    {
                        jsonFile = Path.Combine(metaDir, Path.ChangeExtension(mangledFileName, ".json"));
                    }

                    if (!File.Exists(jsonFile)) continue;

                    var json = File.ReadAllText(jsonFile);
                    var metadata = JsonSerializer.Deserialize<BaseMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (metadata == null || string.IsNullOrEmpty(metadata.OriginalRelativePath)) continue;

                    string destFile = Path.Combine(finishDir, metadata.OriginalRelativePath);

                    if (!File.Exists(destFile))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                        File.Copy(origFile, destFile, false);
                        replenishedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNNING] 补充文件 '{Path.GetFileName(origFile)}' 时失败: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"补充完成！共补充 {replenishedCount} 个文件。");
        Console.WriteLine("所有文件已合并到 finish 目录！");
    }

    private static string PrepareFinishDirectory(string sourceGameDir)
    {
        sourceGameDir = Path.GetFullPath(sourceGameDir);
        string finishDir = Path.Combine(new DirectoryInfo(sourceGameDir).Parent!.FullName, new DirectoryInfo(sourceGameDir).Name + "_finish");
        Console.WriteLine($"开始收集文件到最终目录: {finishDir}");

        if (Directory.Exists(finishDir))
        {
            Console.WriteLine("[WARNNING] finish 目录已存在，其中的内容可能会被覆盖。");
        }
        Directory.CreateDirectory(finishDir);
        return finishDir;
    }

    private static void RestoreFromNewFolders(string workDir, string finishDir)
    {
        var newDirs = Directory.EnumerateDirectories(workDir, "new", SearchOption.AllDirectories);
        Console.WriteLine(">> 正在还原已修改的文件");

        foreach (var newDir in newDirs)
        {
            if (!Directory.EnumerateFiles(newDir, "*", SearchOption.AllDirectories).Any()) continue;
            string formatTag = new DirectoryInfo(newDir).Parent!.Name;
            string metaDir = Path.Combine(workDir, formatTag, "metadata");

            foreach (var file in Directory.EnumerateFiles(newDir, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    string baseName = Path.GetFileName(file);
                    string jsonFile;

                    if (formatTag.Equals("pna", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonFile = Path.Combine(metaDir, baseName + ".json");
                    }
                    else
                    {
                        jsonFile = Path.Combine(metaDir, Path.ChangeExtension(baseName, ".json"));
                    }

                    if (!File.Exists(jsonFile))
                    {
                        Console.WriteLine($"[WARNNING] 找不到 \"{baseName}\" 的元数据，将直接复制到根目录。");
                        string destFileDirect = Path.Combine(finishDir, baseName);
                        File.Copy(file, destFileDirect, true);
                        continue;
                    }

                    var json = File.ReadAllText(jsonFile);
                    var metadata = JsonSerializer.Deserialize<BaseMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (metadata == null || string.IsNullOrEmpty(metadata.OriginalRelativePath))
                        throw new Exception("[WARNNING] 元数据中缺少 OriginalRelativePath 字段。");

                    string destFile = Path.Combine(finishDir, metadata.OriginalRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    File.Copy(file, destFile, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNNING] 还原文件 \"{Path.GetFileName(file)}\" 时失败: {ex.Message}");
                }
            }
        }
    }
}