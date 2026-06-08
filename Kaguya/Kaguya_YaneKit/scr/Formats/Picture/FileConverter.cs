// ============================================================================
// FileConverter.cs
// 图片格式批量转换与重打包引擎
//
// 职责:
//   ConvertAll  -- 遍历工作目录, 将 orig/ 中的原始文件转换为 PNG 到 png/
//   RepackAll   -- 遍历工作目录, 将 fix/ 中修改后的 PNG 重打包为原始格式到 new/
//   RepackPngAll -- 遍历工作目录, 将 png/ 中的 PNG 直接重打包为原始格式到 new/
//   RepackFix   -- 对指定的单个 fix/ 目录执行重打包
//
// 目录结构 (每个格式子目录):
//   {format}/orig/      原始游戏文件
//   {format}/png/       转换后的 PNG
//   {format}/metadata/  每个文件的 JSON 元数据 (偏移/尺寸/原始路径等)
//   {format}/fix/       用户修改后的 PNG (手动放置)
//   {format}/new/       重打包后的原始格式文件
//
// 转换算法: 通过 IFormatHandler 接口分派, 支持 AP0/AP2/AP3/ANM/BMP/AP 六种格式
// 并行处理: 使用 Parallel.ForEach + PictureProcessing.ParallelOptions
// 进度汇报: 使用 PictureProcessing.StartProgress, 格式 [CONVERT arc/FMT] N/M (X%)
//
// 依赖: IFormatHandler (各 Handler 实现), PictureProcessing, PicturePathHelper,
//        Core.ReadableUnicodeJson
// ============================================================================
using Kaguya_YaneKit.Formats.Picture.Handlers;
using Kaguya_YaneKit.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kaguya_YaneKit.Formats.Picture;

public static class FileConverter
{
    public readonly record struct ConversionSummary(int Batches, int Total, int Success, int Failure)
    {
        public bool HasWork => Batches > 0;

        public ConversionSummary Add(ConversionSummary other) => new(
            Batches + other.Batches,
            Total + other.Total,
            Success + other.Success,
            Failure + other.Failure);
    }

    private static readonly Dictionary<string, IFormatHandler> Handlers = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ap0", new Ap0Handler() },
        { "ap2", new Ap2Handler() },
        { "ap3", new Ap3Handler() },
        { "anm", new AnmHandler() },
        { "plt", new PltHandler() },
        { "bmp", new BmpHandler() },
        { "ap", new ApHandler() },
    };

    public static ConversionSummary ConvertAll(string path, bool quiet = false, Action<int, int>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        path = Path.GetFullPath(path);
        var summary = new ConversionSummary();
        var progressTotal = progress is null ? 0 : CountConvertFiles(path);
        var progressCompleted = 0;
        Action? fileProgress = progress is null || progressTotal <= 0
            ? null
            : () => progress(Interlocked.Increment(ref progressCompleted), progressTotal);

        if (TryGetFormatHandler(path, out var singleHandler) && Directory.Exists(Path.Combine(path, "orig")))
        {
            summary = summary.Add(ProcessConvertFormat(path, singleHandler, quiet, fileProgress));
            PrintConvertSummary(summary, stopwatch.Elapsed, quiet);
            return summary;
        }

        var workDirSummary = ProcessConvertWorkDir(path, quiet, fileProgress);
        if (workDirSummary.HasWork)
        {
            summary = summary.Add(workDirSummary);
            PrintConvertSummary(summary, stopwatch.Elapsed, quiet);
            return summary;
        }

        foreach (var archiveDir in Directory.GetDirectories(path).OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase))
        {
            summary = summary.Add(ProcessConvertWorkDir(archiveDir, quiet, fileProgress));
        }

        PrintConvertSummary(summary, stopwatch.Elapsed, quiet);
        return summary;
    }

    public static ConversionSummary RepackAll(string path, bool quiet = false, Action<int, int>? progress = null)
    {
        return RepackFromSourceAll(path, "fix", quiet, progress);
    }

    public static ConversionSummary RepackPngAll(string path, bool quiet = false, Action<int, int>? progress = null)
    {
        return RepackFromSourceAll(path, "png", quiet, progress);
    }

    private static ConversionSummary RepackFromSourceAll(string path, string sourceDirectoryName, bool quiet = false, Action<int, int>? externalProgress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        path = Path.GetFullPath(path);
        var summary = new ConversionSummary();
        var total = CountRepackFiles(path, sourceDirectoryName);
        var completed = 0;
        Action? fileProgress = total <= 0
            ? null
            : () =>
            {
                var current = Interlocked.Increment(ref completed);
                externalProgress?.Invoke(current, total);
            };

        using var progress = !quiet && total > 0
            ? PictureProcessing.StartProgress($"REPACK {sourceDirectoryName.ToUpperInvariant()}", total)
            : PictureProcessing.NullProgress;
        if (fileProgress is not null && externalProgress is null)
        {
            fileProgress = () => progress.Increment();
        }
        else if (fileProgress is not null && externalProgress is not null && !quiet)
        {
            fileProgress = () =>
            {
                var current = Interlocked.Increment(ref completed);
                externalProgress(current, total);
                progress.Increment();
            };
        }

        if (TryGetFormatHandler(path, out var singleHandler))
        {
            var sourceDir = Path.Combine(path, sourceDirectoryName);
            if (Directory.Exists(sourceDir) && Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                summary = summary.Add(ProcessRepackFormat(path, singleHandler, sourceDir, quiet, fileProgress));
            }
            PrintRepackSummary(summary, stopwatch.Elapsed, sourceDirectoryName, quiet);
            return summary;
        }

        var workDirSummary = ProcessRepackWorkDir(path, sourceDirectoryName, quiet, fileProgress);
        if (workDirSummary.HasWork)
        {
            summary = summary.Add(workDirSummary);
            PrintRepackSummary(summary, stopwatch.Elapsed, sourceDirectoryName, quiet);
            return summary;
        }

        foreach (var archiveDir in Directory.GetDirectories(path).OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase))
        {
            summary = summary.Add(ProcessRepackWorkDir(archiveDir, sourceDirectoryName, quiet, fileProgress));
        }

        PrintRepackSummary(summary, stopwatch.Elapsed, sourceDirectoryName, quiet);
        return summary;
    }

    private static ConversionSummary ProcessConvertWorkDir(string path, bool quiet = false, Action? fileProgress = null)
    {
        var summary = new ConversionSummary();
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (!Directory.Exists(formatDir))
            {
                continue;
            }

            summary = summary.Add(ProcessConvertFormat(formatDir, handler, quiet, fileProgress));
        }

        return summary;
    }

    private static ConversionSummary ProcessRepackWorkDir(string path, string sourceDirectoryName, bool quiet = false, Action? fileProgress = null)
    {
        var summary = new ConversionSummary();
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (!Directory.Exists(formatDir))
            {
                continue;
            }

            var sourceDir = Path.Combine(formatDir, sourceDirectoryName);
            if (Directory.Exists(sourceDir) && Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                summary = summary.Add(ProcessRepackFormat(formatDir, handler, sourceDir, quiet, fileProgress));
            }
        }

        return summary;
    }

    private static bool TryGetFormatHandler(string path, out IFormatHandler handler)
    {
        return Handlers.TryGetValue(new DirectoryInfo(path).Name, out handler!);
    }

    public static ConversionSummary RepackFix(string fixDir, bool quiet = false, Action<int, int>? progress = null)
    {
        fixDir = Path.GetFullPath(fixDir);
        if (!Directory.Exists(fixDir) || !new DirectoryInfo(fixDir).Name.Equals("fix", StringComparison.OrdinalIgnoreCase))
        {
            return new ConversionSummary();
        }

        var formatDirInfo = new DirectoryInfo(fixDir).Parent!;
        var formatTag = formatDirInfo.Name;
        if (Handlers.TryGetValue(formatTag, out var handler))
        {
            var total = CountRepackFilesInFormat(formatDirInfo.FullName, "fix");
            var completed = 0;
            Action? fileProgress = total <= 0 || progress is null
                ? null
                : () => progress(Interlocked.Increment(ref completed), total);
            return ProcessRepackFormat(formatDirInfo.FullName, handler, fixDir, quiet, fileProgress);
        }

        return new ConversionSummary();
    }

    private static ConversionSummary ProcessConvertFormat(string formatDir, IFormatHandler handler, bool quiet = false, Action? fileProgress = null)
    {
        var origDir = Path.Combine(formatDir, "orig");
        var pngDir = Path.Combine(formatDir, "png");
        var metaDir = Path.Combine(formatDir, "metadata");
        if (!Directory.Exists(origDir)) return new ConversionSummary();
        Directory.CreateDirectory(pngDir);

        var files = Directory.GetFiles(origDir, "*.*", SearchOption.AllDirectories);
        int success = 0, failure = 0;

        var archiveLabel = new DirectoryInfo(formatDir).Parent?.Name ?? new DirectoryInfo(formatDir).Name;
        using var progress = quiet
            ? PictureProcessing.NullProgress
            : PictureProcessing.StartProgress($"CONVERT {archiveLabel}/{handler.Tag.ToUpperInvariant()}", files.Length);
        Parallel.ForEach(Enumerable.Range(0, files.Length), PictureProcessing.ParallelOptions, i =>
        {
            var file = files[i];
            try
            {
                var relPath = Path.GetRelativePath(origDir, file);
                string destPathBase;
                string metaPath;
                if (handler is AnmHandler or PltHandler)
                {
                    destPathBase = Path.Combine(pngDir, relPath);
                    metaPath = Path.Combine(metaDir, relPath + ".json");
                }
                else
                {
                    destPathBase = Path.Combine(pngDir, PicturePathHelper.RemoveExtensionPreservingName(relPath));
                    metaPath = Path.Combine(metaDir, PicturePathHelper.ChangeExtensionPreservingName(relPath, ".json"));
                }

                if (!File.Exists(metaPath))
                {
                    if (!quiet)
                    {
                        PictureProcessing.WriteLine($"Warning: metadata not found for \"{relPath}\", skipped.");
                    }
                    Interlocked.Increment(ref failure);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPathBase)!);
                var handlerMetadata = handler.Convert(file, destPathBase);
                var baseNode = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
                var handlerNode = JsonNode.Parse(JsonSerializer.Serialize(handlerMetadata))!.AsObject();

                if (handler is Ap2Handler)
                {
                    baseNode.Remove("HeaderExtra");
                }

                foreach (var property in handlerNode.ToList())
                {
                    var node = property.Value;
                    handlerNode.Remove(property.Key);
                    baseNode[property.Key] = node;
                }

                var serializerOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                ReadableUnicodeJson.WriteAllText(metaPath, baseNode.ToJsonString(serializerOptions));
                Interlocked.Increment(ref success);
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    PictureProcessing.WriteLine($"Failed to convert \"{Path.GetFileName(file)}\": {ex.Message}");
                }
                Interlocked.Increment(ref failure);
            }
            finally
            {
                progress.Increment();
                fileProgress?.Invoke();
            }
        });

        if (!quiet)
        {
            Console.WriteLine($"  [CONVERT {archiveLabel}/{handler.Tag.ToUpperInvariant()}] done: {files.Length} total, {success} success, {failure} failure.");
        }
        return new ConversionSummary(1, files.Length, success, failure);
    }

    private static int CountConvertFiles(string path)
    {
        if (TryGetFormatHandler(path, out _) && Directory.Exists(Path.Combine(path, "orig")))
        {
            return CountFormatFiles(path);
        }

        var workDirCount = CountConvertFilesInWorkDir(path);
        if (workDirCount > 0)
        {
            return workDirCount;
        }

        return Directory.GetDirectories(path)
            .Sum(CountConvertFilesInWorkDir);
    }

    private static int CountConvertFilesInWorkDir(string path)
    {
        var count = 0;
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (Directory.Exists(formatDir))
            {
                count += CountFormatFiles(formatDir);
            }
        }

        return count;
    }

    private static int CountFormatFiles(string formatDir)
    {
        var origDir = Path.Combine(formatDir, "orig");
        return Directory.Exists(origDir)
            ? Directory.GetFiles(origDir, "*.*", SearchOption.AllDirectories).Length
            : 0;
    }

    private static int CountRepackFiles(string path, string sourceDirectoryName)
    {
        if (TryGetFormatHandler(path, out _) && Directory.Exists(Path.Combine(path, sourceDirectoryName)))
        {
            return CountRepackFilesInFormat(path, sourceDirectoryName);
        }

        var workDirCount = CountRepackFilesInWorkDir(path, sourceDirectoryName);
        if (workDirCount > 0)
        {
            return workDirCount;
        }

        return Directory.GetDirectories(path)
            .Sum(archiveDir => CountRepackFilesInWorkDir(archiveDir, sourceDirectoryName));
    }

    private static int CountRepackFilesInWorkDir(string path, string sourceDirectoryName)
    {
        var count = 0;
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (Directory.Exists(formatDir))
            {
                count += CountRepackFilesInFormat(formatDir, sourceDirectoryName);
            }
        }

        return count;
    }

    private static int CountRepackFilesInFormat(string formatDir, string sourceDirectoryName)
    {
        var sourceDir = Path.Combine(formatDir, sourceDirectoryName);
        var metaDir = Path.Combine(formatDir, "metadata");
        if (!Directory.Exists(sourceDir) || !Directory.Exists(metaDir))
        {
            return 0;
        }

        return new DirectoryInfo(formatDir).Name.Equals("anm", StringComparison.OrdinalIgnoreCase) ||
               new DirectoryInfo(formatDir).Name.Equals("plt", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories)
                .Count(dir => !Directory.EnumerateDirectories(dir).Any())
            : Directory.GetFiles(sourceDir, "*.png", SearchOption.AllDirectories).Length;
    }

    private static void PrintConvertSummary(ConversionSummary summary, TimeSpan elapsed, bool quiet = false)
    {
        if (quiet)
        {
            return;
        }

        if (!summary.HasWork)
        {
            Console.WriteLine($"  [CONVERT SUMMARY] no picture format directories found. elapsed={elapsed.TotalSeconds:F2}s");
            return;
        }

        Console.WriteLine($"  [CONVERT SUMMARY] {summary.Batches} batches, {summary.Total} total, {summary.Success} success, {summary.Failure} failure, elapsed={elapsed.TotalSeconds:F2}s.");
    }

    private static void PrintRepackSummary(ConversionSummary summary, TimeSpan elapsed, string sourceDirectoryName, bool quiet = false)
    {
        if (quiet)
        {
            return;
        }

        var label = sourceDirectoryName.Equals("png", StringComparison.OrdinalIgnoreCase)
            ? "REPACK PNG SUMMARY"
            : "REPACK SUMMARY";

        if (!summary.HasWork)
        {
            Console.WriteLine($"  [{label}] no picture format directories found. elapsed={elapsed.TotalSeconds:F2}s");
            return;
        }

        Console.WriteLine($"  [{label}] {summary.Batches} batches, {summary.Total} total, {summary.Success} success, {summary.Failure} failure, elapsed={elapsed.TotalSeconds:F2}s.");
    }

    private static ConversionSummary ProcessRepackFormat(string formatDir, IFormatHandler handler, string sourceDir, bool quiet = false, Action? fileProgress = null)
    {
        var metaDir = Path.Combine(formatDir, "metadata");
        var newDir = Path.Combine(formatDir, "new");
        if (!Directory.Exists(sourceDir) || !Directory.Exists(metaDir)) return new ConversionSummary();
        Directory.CreateDirectory(newDir);

        int success = 0, failure = 0;

        if (handler is AnmHandler or PltHandler)
        {
            var dirs = Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories)
                .Where(d => !Directory.EnumerateDirectories(d).Any())
                .ToArray();
            Parallel.ForEach(dirs, PictureProcessing.ParallelOptions, dir =>
            {
                try
                {
                    var relPath = Path.GetRelativePath(sourceDir, dir);
                    var metadataPath = Path.Combine(metaDir, relPath + ".json");
                    if (!File.Exists(metadataPath))
                    {
                        if (!quiet)
                        {
                            PictureProcessing.WriteLine($"Warning: metadata not found for \"{relPath}\", skipped.");
                        }
                        Interlocked.Increment(ref failure);
                        return;
                    }

                    var json = File.ReadAllText(metadataPath);
                    var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
                    var originalRelativePath = metadata["OriginalRelativePath"].GetString()!;
                    var finalFileName = PicturePathHelper.GetArchiveRelativePath(metadata, originalRelativePath, formatDir);
                    if (string.IsNullOrEmpty(finalFileName) || finalFileName == ".")
                    {
                        finalFileName = new DirectoryInfo(dir).Name;
                    }

                    var destPath = Path.Combine(newDir, finalFileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    handler.Repack(dir, destPath);
                    Interlocked.Increment(ref success);
                }
                catch (Exception ex)
                {
                    if (!quiet)
                    {
                        PictureProcessing.WriteLine($"Failed to repack {handler.Tag.ToUpperInvariant()} \"{Path.GetFileName(dir)}\": {ex.Message}");
                    }
                    Interlocked.Increment(ref failure);
                }
                finally
                {
                    fileProgress?.Invoke();
                }
            });

            return new ConversionSummary(1, dirs.Length, success, failure);
        }

        var files = Directory.GetFiles(sourceDir, "*.png", SearchOption.AllDirectories);
        Parallel.ForEach(files, PictureProcessing.ParallelOptions, file =>
        {
            try
            {
                var relPath = Path.GetRelativePath(sourceDir, file);
                var baseName = PicturePathHelper.RemoveExtensionPreservingName(relPath, ".png");
                var metadataPath = Path.Combine(metaDir, baseName + ".json");
                if (!File.Exists(metadataPath))
                {
                    if (!quiet)
                    {
                        PictureProcessing.WriteLine($"Warning: metadata not found for \"{relPath}\", skipped.");
                    }
                    Interlocked.Increment(ref failure);
                    return;
                }

                var json = File.ReadAllText(metadataPath);
                var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
                var originalRelativePath = metadata["OriginalRelativePath"].GetString()!;
                var finalFileName = PicturePathHelper.GetArchiveRelativePath(metadata, originalRelativePath, formatDir);
                var destFile = Path.Combine(newDir, finalFileName);
                var sourcePathBase = Path.Combine(sourceDir, baseName);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                handler.Repack(sourcePathBase, destFile);
                Interlocked.Increment(ref success);
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    PictureProcessing.WriteLine($"Failed to repack \"{Path.GetFileName(file)}\": {ex.Message}");
                }
                Interlocked.Increment(ref failure);
            }
            finally
            {
                fileProgress?.Invoke();
            }
        });

        return new ConversionSummary(1, files.Length, success, failure);
    }

}
