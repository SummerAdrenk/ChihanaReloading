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

    public readonly record struct RestoreSummary(int Total, int Restored, int Replenished, int Skipped, int Failed)
    {
        public int Copied => Restored + Replenished;
    }

    private sealed class RestoreProgress
    {
        private readonly Action<int, int>? _progress;
        private int _done;
        private int _restored;
        private int _replenished;
        private int _skipped;
        private int _failed;

        public RestoreProgress(int total, Action<int, int>? progress)
        {
            Total = total;
            _progress = progress;
            _progress?.Invoke(0, Total);
        }

        public int Total { get; }
        public void MarkRestored() => Interlocked.Increment(ref _restored);

        public void MarkReplenished() => Interlocked.Increment(ref _replenished);

        public void MarkSkipped() => Interlocked.Increment(ref _skipped);

        public void MarkFailed() => Interlocked.Increment(ref _failed);

        public void Advance()
        {
            var done = Interlocked.Increment(ref _done);
            _progress?.Invoke(done, Total);
        }

        public RestoreSummary ToSummary() => new(
            Total,
            Volatile.Read(ref _restored),
            Volatile.Read(ref _replenished),
            Volatile.Read(ref _skipped),
            Volatile.Read(ref _failed));
    }

    private static readonly ParallelOptions RestoreParallelOptions = new()
    {
        MaxDegreeOfParallelism = ReadRestoreParallelism()
    };

    public static RestoreSummary Restore(string workDir, string outputDir, Action<int, int>? progress = null)
    {
        workDir = Path.GetFullPath(workDir);
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);
        var files = EnumerateNamedDirFiles(workDir, "new", null).ToList();
        var restoreProgress = new RestoreProgress(files.Count, progress);
        RestoreFromNewFolders(files, outputDir, restoreProgress);
        return restoreProgress.ToSummary();
    }

    public static RestoreSummary RestoreWithReplenish(string workDir, string outputDir, HashSet<string>? excludeFormats = null, Action<int, int>? progress = null)
    {
        workDir = Path.GetFullPath(workDir);
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);
        var newFiles = EnumerateNamedDirFiles(workDir, "new", null).ToList();
        var origFiles = EnumerateNamedDirFiles(workDir, "orig", excludeFormats).ToList();
        var restoreProgress = new RestoreProgress(newFiles.Count + origFiles.Count, progress);
        RestoreFromNewFolders(newFiles, outputDir, restoreProgress);

        Parallel.ForEach(origFiles, RestoreParallelOptions, origFile =>
        {
            try
            {
                var (formatDir, formatTag, relPath, jsonFile) = ResolveMetadataPath(origFile.RootDir, origFile.FilePath);

                if (!File.Exists(jsonFile))
                {
                    throw new FileNotFoundException($"Metadata not found for replenish source: {relPath}", jsonFile);
                }

                var json = File.ReadAllText(jsonFile);
                var metadata = JsonSerializer.Deserialize<BaseMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (metadata == null || string.IsNullOrEmpty(metadata.OriginalRelativePath))
                {
                    restoreProgress.MarkSkipped();
                    return;
                }

                var destFile = Path.Combine(outputDir, metadata.OriginalRelativePath);
                if (!File.Exists(destFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    try
                    {
                        File.Copy(origFile.FilePath, destFile, false);
                        restoreProgress.MarkReplenished();
                    }
                    catch (IOException) when (File.Exists(destFile))
                    {
                        restoreProgress.MarkSkipped();
                    }
                }
                else
                {
                    restoreProgress.MarkSkipped();
                }
            }
            catch (Exception ex)
            {
                restoreProgress.MarkFailed();
                PictureProcessing.WriteLine($"Failed to replenish \"{Path.GetFileName(origFile.FilePath)}\": {ex.Message}");
            }
            finally
            {
                restoreProgress.Advance();
            }
        });

        return restoreProgress.ToSummary();
    }

    private readonly record struct RestoreSourceFile(string RootDir, string FilePath);

    private static IEnumerable<RestoreSourceFile> EnumerateNamedDirFiles(string workDir, string dirName, HashSet<string>? excludeFormats)
    {
        foreach (var dir in Directory.EnumerateDirectories(workDir, dirName, SearchOption.AllDirectories))
        {
            var formatDir = new DirectoryInfo(dir).Parent!.FullName;
            var formatTag = new DirectoryInfo(formatDir).Name;
            if (excludeFormats != null && excludeFormats.Contains(formatTag.ToLowerInvariant()))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                yield return new RestoreSourceFile(dir, file);
            }
        }
    }

    private static void RestoreFromNewFolders(IReadOnlyList<RestoreSourceFile> files, string outputDir, RestoreProgress restoreProgress)
    {
        Parallel.ForEach(files, RestoreParallelOptions, source =>
        {
            try
            {
                var (_, _, relPath, jsonFile) = ResolveMetadataPath(source.RootDir, source.FilePath);

                if (!File.Exists(jsonFile))
                {
                    throw new FileNotFoundException($"Metadata not found for restore source: {relPath}", jsonFile);
                }

                var json = File.ReadAllText(jsonFile);
                var metadata = JsonSerializer.Deserialize<BaseMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (metadata == null || string.IsNullOrEmpty(metadata.OriginalRelativePath))
                {
                    PictureProcessing.WriteLine($"Warning: invalid metadata for \"{relPath}\", skipped.");
                    restoreProgress.MarkSkipped();
                    return;
                }

                var destFile = Path.Combine(outputDir, metadata.OriginalRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(source.FilePath, destFile, true);
                restoreProgress.MarkRestored();
            }
            catch (Exception ex)
            {
                restoreProgress.MarkFailed();
                PictureProcessing.WriteLine($"Failed to restore \"{Path.GetFileName(source.FilePath)}\": {ex.Message}");
            }
            finally
            {
                restoreProgress.Advance();
            }
        });
    }

    private static int ReadRestoreParallelism()
    {
        var value = Environment.GetEnvironmentVariable("KAGUYA_RESTORE_PARALLELISM");
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return Math.Clamp(parsed, 1, 16);
        }

        return Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    }

    private static (string FormatDir, string FormatTag, string RelPath, string JsonFile) ResolveMetadataPath(string rootDir, string file)
    {
        var formatDir = new DirectoryInfo(rootDir).Parent!.FullName;
        var formatTag = new DirectoryInfo(formatDir).Name;
        var metaDir = Path.Combine(formatDir, "metadata");
        var relPath = Path.GetRelativePath(rootDir, file);
        var jsonFile = formatTag.Equals("anm", StringComparison.OrdinalIgnoreCase) ||
                       formatTag.Equals("plt", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(metaDir, relPath + ".json")
            : Path.Combine(metaDir, PicturePathHelper.ChangeExtensionPreservingName(relPath, ".json"));
        return (formatDir, formatTag, relPath, jsonFile);
    }
}
