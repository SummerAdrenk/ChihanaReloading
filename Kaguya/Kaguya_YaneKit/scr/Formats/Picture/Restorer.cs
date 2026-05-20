// ============================================================================
// Restorer.cs
// 将处理后的文件还原到 link6_pack/ 目录, 准备重新封包
//
// 职责:
//   Restore              -- 将 new/ 中的文件按元数据中的 OriginalRelativePath 还原
//   RestoreWithReplenish -- 还原 new/ + 补充 orig/ 中未被修改的文件
//
// 还原流程:
//   1. 遍历所有 {format}/new/ 子目录
//   2. 读取每个文件的 metadata JSON 获取 OriginalRelativePath
//   3. 复制到 outputDir/{OriginalRelativePath}
//   4. (WithReplenish) 再遍历所有 orig/, 补充 outputDir/ 中不存在的文件
//
// 依赖: System.Text.Json, System.IO
// ============================================================================
using System.Text.Json;

namespace Kaguya_YaneKit.Formats.Picture;

public static class Restorer
{
    private sealed class BaseMetadata
    {
        public string OriginalRelativePath { get; set; } = "";
    }

    public static void Restore(string workDir, string outputDir)
    {
        workDir = Path.GetFullPath(workDir);
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);
        RestoreFromNewFolders(workDir, outputDir);
    }

    public static void RestoreWithReplenish(string workDir, string outputDir, HashSet<string>? excludeFormats = null)
    {
        workDir = Path.GetFullPath(workDir);
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);
        RestoreFromNewFolders(workDir, outputDir);

        var origDirs = Directory.EnumerateDirectories(workDir, "orig", SearchOption.AllDirectories);
        foreach (var origDir in origDirs)
        {
            var formatDir = new DirectoryInfo(origDir).Parent!.FullName;
            var formatTag = new DirectoryInfo(formatDir).Name;
            if (excludeFormats != null && excludeFormats.Contains(formatTag.ToLowerInvariant()))
            {
                continue;
            }

            var metaDir = Path.Combine(formatDir, "metadata");
            foreach (var origFile in Directory.EnumerateFiles(origDir, "*.*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(origDir, origFile);
                var jsonFile = formatTag.Equals("anm", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(metaDir, relPath + ".json")
                    : Path.Combine(metaDir, Path.ChangeExtension(relPath, ".json"));

                if (!File.Exists(jsonFile))
                {
                    continue;
                }

                var json = File.ReadAllText(jsonFile);
                var metadata = JsonSerializer.Deserialize<BaseMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (metadata == null || string.IsNullOrEmpty(metadata.OriginalRelativePath))
                {
                    continue;
                }

                var destFile = Path.Combine(outputDir, metadata.OriginalRelativePath);
                if (!File.Exists(destFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    File.Copy(origFile, destFile, false);
                }
            }
        }
    }

    private static void RestoreFromNewFolders(string workDir, string outputDir)
    {
        var newDirs = Directory.EnumerateDirectories(workDir, "new", SearchOption.AllDirectories);
        foreach (var newDir in newDirs)
        {
            foreach (var file in Directory.EnumerateFiles(newDir, "*.*", SearchOption.AllDirectories))
            {
                try
                {
                    var formatDir = new DirectoryInfo(newDir).Parent!.FullName;
                    var formatTag = new DirectoryInfo(formatDir).Name;
                    var metaDir = Path.Combine(formatDir, "metadata");
                    var relPath = Path.GetRelativePath(newDir, file);
                    var jsonFile = formatTag.Equals("anm", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(metaDir, relPath + ".json")
                        : Path.Combine(metaDir, Path.ChangeExtension(relPath, ".json"));

                    if (!File.Exists(jsonFile))
                    {
                        Console.WriteLine($"  Warning: metadata not found for \"{relPath}\", copying to root.");
                        var destFileDirect = Path.Combine(outputDir, relPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destFileDirect)!);
                        File.Copy(file, destFileDirect, true);
                        continue;
                    }

                    var json = File.ReadAllText(jsonFile);
                    var metadata = JsonSerializer.Deserialize<BaseMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (metadata == null || string.IsNullOrEmpty(metadata.OriginalRelativePath))
                    {
                        Console.WriteLine($"  Warning: invalid metadata for \"{relPath}\", skipped.");
                        continue;
                    }

                    var destFile = Path.Combine(outputDir, metadata.OriginalRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    File.Copy(file, destFile, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Failed to restore \"{Path.GetFileName(file)}\": {ex.Message}");
                }
            }
        }
    }
}
