// ============================================================================
// FileSorter.cs
// 按文件格式签名将解包后的游戏文件分拣到对应子目录
//
// 职责:
//   Sort                   -- 扫描源目录所有文件, 按 IFormatHandler.Identify 分拣
//   SortArchiveDirectories -- 检测 _link_manifest.json 自动识别档案子目录并逐个分拣
//
// 分拣算法:
//   对每个文件, 依次用所有注册的 Handler 尝试识别 (读文件头魔数)
//   匹配成功后复制到 {workDir}/{format}/orig/ 并生成 metadata JSON
//   跳过 .json/.txt/.xml/.ini/.csv/.log 等非二进制文件
//
// 并行处理: Parallel.ForEach + PictureProcessing.ParallelOptions
//
// 依赖: IFormatHandler (各 Handler 实现), PictureProcessing,
//        Core.ReadableUnicodeJson
// ============================================================================
using Kaguya_YaneKit.Formats.Picture.Handlers;
using System.Text.Json;
using Kaguya_YaneKit.Core;

namespace Kaguya_YaneKit.Formats.Picture;

public static class FileSorter
{
    private sealed class BaseMetadata
    {
        public string OriginalRelativePath { get; set; } = "";
        public string? SourceArchive { get; set; }
    }

    private static readonly List<IFormatHandler> Handlers = new()
    {
        new Ap0Handler(),
        new Ap2Handler(),
        new Ap3Handler(),
        new AnmHandler(),
        new BmpHandler(),
        new ApHandler(),
    };

    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".txt", ".xml", ".ini", ".csv", ".log"
    };

    public static void Sort(string sourceDir, string workDir)
    {
        Sort(sourceDir, workDir, originalPathPrefix: null, sourceArchive: null);
    }

    public static bool SortArchiveDirectories(string sourceRoot, string workRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        workRoot = Path.GetFullPath(workRoot);

        var archiveDirs = Directory.EnumerateFiles(sourceRoot, "_link_manifest.json", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(dir => dir!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (archiveDirs.Count == 0)
        {
            return false;
        }

        foreach (var archiveDir in archiveDirs)
        {
            var archiveName = new DirectoryInfo(archiveDir).Name;
            Sort(archiveDir, Path.Combine(workRoot, archiveName), archiveName, archiveName);
        }

        return true;
    }

    public static void Sort(string sourceDir, string workDir, string? originalPathPrefix, string? sourceArchive)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        workDir = Path.GetFullPath(workDir);

        var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
        int success = 0, failure = 0, skipped = 0, unrecognized = 0;

        Parallel.ForEach(allFiles, PictureProcessing.ParallelOptions, file =>
        {
            var ext = Path.GetExtension(file);
            if (SkipExtensions.Contains(ext))
            {
                Interlocked.Increment(ref skipped);
                return;
            }

            try
            {
                using var stream = File.OpenRead(file);
                using var reader = new BinaryReader(stream);
                bool matched = false;
                foreach (var handler in Handlers)
                {
                    if (!handler.Identify(reader))
                    {
                        continue;
                    }

                    matched = true;
                    var relativePath = Path.GetRelativePath(sourceDir, file);

                    var formatDir = Path.Combine(workDir, handler.Tag);
                    var origDir = Path.Combine(formatDir, "orig");
                    var metaDir = Path.Combine(formatDir, "metadata");

                    var destOrigPath = Path.Combine(origDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destOrigPath)!);
                    File.Copy(file, destOrigPath, true);

                    var originalRelativePath = string.IsNullOrWhiteSpace(originalPathPrefix)
                        ? relativePath
                        : Path.Combine(originalPathPrefix, relativePath);
                    var metadata = new BaseMetadata
                    {
                        OriginalRelativePath = originalRelativePath,
                        SourceArchive = sourceArchive
                    };
                    var serializerOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    var json = JsonSerializer.Serialize(metadata, serializerOptions);

                    string destMetaPath;
                    if (handler is AnmHandler)
                    {
                        destMetaPath = Path.Combine(metaDir, relativePath + ".json");
                    }
                    else
                    {
                        destMetaPath = Path.Combine(metaDir, Path.ChangeExtension(relativePath, ".json"));
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destMetaPath)!);
                    ReadableUnicodeJson.WriteAllText(destMetaPath, json);
                    Interlocked.Increment(ref success);
                    break;
                }

                if (!matched)
                {
                    PictureProcessing.WriteLine($"[Unrecognized] {Path.GetRelativePath(sourceDir, file)}");
                    Interlocked.Increment(ref unrecognized);
                }
            }
            catch (Exception ex)
            {
                PictureProcessing.WriteLine($"Failed to process \"{Path.GetFileName(file)}\": {ex.Message}");
                Interlocked.Increment(ref failure);
            }
        });

        Console.WriteLine($"  [SORT] done: {allFiles.Length} total, {success} success, {failure} failure, {skipped} skipped, {unrecognized} unrecognized.");
    }
}
