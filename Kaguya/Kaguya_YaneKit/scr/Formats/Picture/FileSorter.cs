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
using System.Text;
using Kaguya_YaneKit.Core;

namespace Kaguya_YaneKit.Formats.Picture;

public static class FileSorter
{
    public readonly record struct SortSummary(int Total, int Success, int Failure, int Skipped, int Unrecognized)
    {
        public SortSummary Add(SortSummary other) => new(
            Total + other.Total,
            Success + other.Success,
            Failure + other.Failure,
            Skipped + other.Skipped,
            Unrecognized + other.Unrecognized);
    }

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
        new PltHandler(),
        new BmpHandler(),
        new ApHandler(),
    };

    private static readonly Dictionary<string, IFormatHandler> HandlersByTag = Handlers
        .ToDictionary(handler => handler.Tag, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".txt", ".xml", ".ini", ".csv", ".log"
    };

    public static SortSummary Sort(string sourceDir, string workDir)
    {
        return Sort(sourceDir, workDir, originalPathPrefix: null, sourceArchive: null);
    }

    public static bool SortArchiveDirectories(string sourceRoot, string workRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        workRoot = Path.GetFullPath(workRoot);

        var archiveDirs = Directory.EnumerateFiles(sourceRoot, "_link_manifest.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(sourceRoot, "_archive_manifest.json", SearchOption.AllDirectories))
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

    public static SortSummary Sort(string sourceDir, string workDir, string? originalPathPrefix, string? sourceArchive, bool quiet = false, Action<int, int>? progress = null)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        workDir = Path.GetFullPath(workDir);

        var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
        int success = 0, failure = 0, skipped = 0, unrecognized = 0;
        var completed = 0;
        var total = allFiles.Length;

        Parallel.ForEach(allFiles, PictureProcessing.ParallelOptions, file =>
        {
            try
            {
                var ext = Path.GetExtension(file);
                if (SkipExtensions.Contains(ext))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                using var stream = File.OpenRead(file);
                using var reader = new BinaryReader(stream);
                var matched = TryIdentifyHandler(reader, out var handler);
                if (matched)
                {
                    var relativePath = Path.GetRelativePath(sourceDir, file);

                    var formatDir = Path.Combine(workDir, handler.Tag);
                    var origDir = Path.Combine(formatDir, "orig");
                    var metaDir = Path.Combine(formatDir, "metadata");

                    var destOrigPath = Path.Combine(origDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destOrigPath)!);
                    CopyIfChanged(file, destOrigPath);

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
                    if (handler is AnmHandler or PltHandler)
                    {
                        destMetaPath = Path.Combine(metaDir, relativePath + ".json");
                    }
                    else
                    {
                        destMetaPath = Path.Combine(metaDir, PicturePathHelper.ChangeExtensionPreservingName(relativePath, ".json"));
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destMetaPath)!);
                    WriteTextIfChanged(destMetaPath, json);
                    Interlocked.Increment(ref success);
                }

                if (!matched)
                {
                    if (!quiet)
                    {
                        PictureProcessing.WriteLine($"[Unrecognized] {Path.GetRelativePath(sourceDir, file)}");
                    }
                    Interlocked.Increment(ref unrecognized);
                }
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    PictureProcessing.WriteLine($"Failed to process \"{Path.GetFileName(file)}\": {ex.Message}");
                }
                Interlocked.Increment(ref failure);
            }
            finally
            {
                progress?.Invoke(Interlocked.Increment(ref completed), total);
            }
        });

        var summary = new SortSummary(allFiles.Length, success, failure, skipped, unrecognized);
        if (!quiet)
        {
            Console.WriteLine($"  [SORT] done: {summary.Total} total, {summary.Success} success, {summary.Failure} failure, {summary.Skipped} skipped, {summary.Unrecognized} unrecognized.");
        }

        return summary;
    }

    private static void CopyIfChanged(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            var source = new FileInfo(sourcePath);
            var destination = new FileInfo(destinationPath);
            if (source.Length == destination.Length &&
                source.LastWriteTimeUtc == destination.LastWriteTimeUtc)
            {
                return;
            }
        }

        File.Copy(sourcePath, destinationPath, true);
    }

    private static void WriteTextIfChanged(string path, string text)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal))
        {
            return;
        }

        ReadableUnicodeJson.WriteAllText(path, text);
    }

    private static bool TryIdentifyHandler(BinaryReader reader, out IFormatHandler handler)
    {
        handler = null!;
        var stream = reader.BaseStream;
        if (stream.Length < 2)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[5];
        stream.Position = 0;
        var read = stream.Read(header);
        stream.Position = 0;

        var tag = GetTagFromHeader(header[..read]);
        if (tag is null || !HandlersByTag.TryGetValue(tag, out var candidate))
        {
            return false;
        }

        handler = candidate;
        var identified = handler.Identify(reader);
        stream.Position = 0;
        if (!identified)
        {
            handler = null!;
        }

        return identified;
    }

    private static string? GetTagFromHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4)
        {
            var signature = Encoding.ASCII.GetString(header[..4]);
            return signature switch
            {
                "AP-0" => "ap0",
                "AP-2" => "ap2",
                "AP-3" => "ap3",
                "AN00" or "AN01" or "AN20" or "AN21" => "anm",
                "PL00" or "PL01" or "PL10" or "PL11" or "PL20" or "PL30" => "plt",
                _ => GetContainerTagFromHeader(header)
            };
        }

        return GetContainerTagFromHeader(header);
    }

    private static string? GetContainerTagFromHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 5 && header[0] == 4)
        {
            var signature = Encoding.ASCII.GetString(header.Slice(1, 4));
            if (signature is "APS3" or "APS4")
            {
                return "ap3";
            }
        }

        if (header.Length >= 2)
        {
            var magic = (ushort)(header[0] | (header[1] << 8));
            return magic switch
            {
                0x4D42 => "bmp",
                0x5041 or 0x4F41 => "ap",
                _ => null
            };
        }

        return null;
    }
}
