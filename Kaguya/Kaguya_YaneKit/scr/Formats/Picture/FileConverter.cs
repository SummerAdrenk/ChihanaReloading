// ============================================================================
// FileConverter.cs
// 图片格式批量转换与重打包引擎
//
// 职责:
//   ConvertAll  -- 遍历工作目录, 将 orig/ 中的原始文件转换为 PNG 到 png/
//   RepackAll   -- 遍历工作目录, 将 fix/ 中修改后的 PNG 重打包为原始格式到 new/
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
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kaguya_YaneKit.Formats.Picture;

public static class FileConverter
{
    private static readonly Dictionary<string, IFormatHandler> Handlers = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ap0", new Ap0Handler() },
        { "ap2", new Ap2Handler() },
        { "ap3", new Ap3Handler() },
        { "anm", new AnmHandler() },
        { "bmp", new BmpHandler() },
        { "ap", new ApHandler() },
    };

    public static void ConvertAll(string path)
    {
        path = Path.GetFullPath(path);

        if (TryGetFormatHandler(path, out var singleHandler) && Directory.Exists(Path.Combine(path, "orig")))
        {
            ProcessConvertFormat(path, singleHandler);
            return;
        }

        if (ProcessConvertWorkDir(path))
        {
            return;
        }

        foreach (var archiveDir in Directory.GetDirectories(path).OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase))
        {
            ProcessConvertWorkDir(archiveDir);
        }
    }

    public static void RepackAll(string path)
    {
        path = Path.GetFullPath(path);

        if (TryGetFormatHandler(path, out var singleHandler))
        {
            var sourceDir = Path.Combine(path, "fix");
            if (Directory.Exists(sourceDir) && Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                ProcessRepackFormat(path, singleHandler, sourceDir);
            }
            return;
        }

        if (ProcessRepackWorkDir(path))
        {
            return;
        }

        foreach (var archiveDir in Directory.GetDirectories(path).OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase))
        {
            ProcessRepackWorkDir(archiveDir);
        }
    }

    private static bool ProcessConvertWorkDir(string path)
    {
        var processed = false;
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (!Directory.Exists(formatDir))
            {
                continue;
            }

            ProcessConvertFormat(formatDir, handler);
            processed = true;
        }

        return processed;
    }

    private static bool ProcessRepackWorkDir(string path)
    {
        var processed = false;
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (!Directory.Exists(formatDir))
            {
                continue;
            }

            var sourceDir = Path.Combine(formatDir, "fix");
            if (Directory.Exists(sourceDir) && Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                ProcessRepackFormat(formatDir, handler, sourceDir);
                processed = true;
            }
        }

        return processed;
    }

    private static bool TryGetFormatHandler(string path, out IFormatHandler handler)
    {
        return Handlers.TryGetValue(new DirectoryInfo(path).Name, out handler!);
    }

    public static void RepackFix(string fixDir)
    {
        fixDir = Path.GetFullPath(fixDir);
        if (!Directory.Exists(fixDir) || !new DirectoryInfo(fixDir).Name.Equals("fix", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var formatDirInfo = new DirectoryInfo(fixDir).Parent!;
        var formatTag = formatDirInfo.Name;
        if (Handlers.TryGetValue(formatTag, out var handler))
        {
            ProcessRepackFormat(formatDirInfo.FullName, handler, fixDir);
        }
    }

    private static void ProcessConvertFormat(string formatDir, IFormatHandler handler)
    {
        var origDir = Path.Combine(formatDir, "orig");
        var pngDir = Path.Combine(formatDir, "png");
        var metaDir = Path.Combine(formatDir, "metadata");
        if (!Directory.Exists(origDir)) return;
        Directory.CreateDirectory(pngDir);

        var files = Directory.GetFiles(origDir, "*.*", SearchOption.AllDirectories);
        int success = 0, failure = 0;

        var archiveLabel = new DirectoryInfo(formatDir).Parent?.Name ?? new DirectoryInfo(formatDir).Name;
        using var progress = PictureProcessing.StartProgress($"CONVERT {archiveLabel}/{handler.Tag.ToUpperInvariant()}", files.Length);
        Parallel.ForEach(Enumerable.Range(0, files.Length), PictureProcessing.ParallelOptions, i =>
        {
            var file = files[i];
            try
            {
                var relPath = Path.GetRelativePath(origDir, file);
                string destPathBase;
                string metaPath;
                if (handler is AnmHandler)
                {
                    destPathBase = Path.Combine(pngDir, relPath);
                    metaPath = Path.Combine(metaDir, relPath + ".json");
                }
                else
                {
                    destPathBase = Path.Combine(pngDir, Path.ChangeExtension(relPath, null));
                    metaPath = Path.Combine(metaDir, Path.ChangeExtension(relPath, ".json"));
                }

                if (!File.Exists(metaPath))
                {
                    PictureProcessing.WriteLine($"Warning: metadata not found for \"{relPath}\", skipped.");
                    Interlocked.Increment(ref failure);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPathBase)!);
                var handlerMetadata = handler.Convert(file, destPathBase);
                var baseNode = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
                var handlerNode = JsonNode.Parse(JsonSerializer.Serialize(handlerMetadata))!.AsObject();

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
                PictureProcessing.WriteLine($"Failed to convert \"{Path.GetFileName(file)}\": {ex.Message}");
                Interlocked.Increment(ref failure);
            }
            finally
            {
                progress.Increment();
            }
        });

        Console.WriteLine($"  [CONVERT {archiveLabel}/{handler.Tag.ToUpperInvariant()}] done: {files.Length} total, {success} success, {failure} failure.");
    }

    private static void ProcessRepackFormat(string formatDir, IFormatHandler handler, string sourceDir)
    {
        var metaDir = Path.Combine(formatDir, "metadata");
        var newDir = Path.Combine(formatDir, "new");
        if (!Directory.Exists(sourceDir) || !Directory.Exists(metaDir)) return;
        Directory.CreateDirectory(newDir);

        int success = 0, failure = 0;

        var archiveLabel = new DirectoryInfo(formatDir).Parent?.Name ?? new DirectoryInfo(formatDir).Name;

        if (handler is AnmHandler)
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
                        PictureProcessing.WriteLine($"Warning: metadata not found for \"{relPath}\", skipped.");
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
                    PictureProcessing.WriteLine($"Failed to repack ANM \"{Path.GetFileName(dir)}\": {ex.Message}");
                    Interlocked.Increment(ref failure);
                }
            });

            Console.WriteLine($"  [REPACK {archiveLabel}/ANM] done: {dirs.Length} total, {success} success, {failure} failure.");
            return;
        }

        var files = Directory.GetFiles(sourceDir, "*.png", SearchOption.AllDirectories);
        Parallel.ForEach(files, PictureProcessing.ParallelOptions, file =>
        {
            try
            {
                var relPath = Path.GetRelativePath(sourceDir, file);
                var baseName = Path.ChangeExtension(relPath, null);
                var metadataPath = Path.Combine(metaDir, baseName + ".json");
                if (!File.Exists(metadataPath))
                {
                    PictureProcessing.WriteLine($"Warning: metadata not found for \"{relPath}\", skipped.");
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
                PictureProcessing.WriteLine($"Failed to repack \"{Path.GetFileName(file)}\": {ex.Message}");
                Interlocked.Increment(ref failure);
            }
        });

        Console.WriteLine($"  [REPACK {archiveLabel}/{handler.Tag.ToUpperInvariant()}] done: {files.Length} total, {success} success, {failure} failure.");
    }
}
