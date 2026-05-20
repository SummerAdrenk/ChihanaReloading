using System.Collections.Concurrent;

namespace CxdecReloading.Steps;

/// <summary>
/// Step 2: 高速并行扫描 Extractor_Output 中所有文件的签名 → 将 PSB/MDF 文件复制到 SCN/
/// </summary>
public static class Step2_ScanAndExtractPsb
{
    private static readonly (byte[] Magic, FileType Type, string Label)[] Signatures =
    [
        ([0x50, 0x53, 0x42, 0x00],                          FileType.PSB,  "PSB"),
        ([0x6D, 0x64, 0x66, 0x00],                          FileType.MDF,  "MDF"),
        ([0x54, 0x4C, 0x47, 0x35, 0x2E, 0x30, 0x00],        FileType.TLG5, "TLG5"),
        ([0x54, 0x4C, 0x47, 0x36, 0x2E, 0x30, 0x00],        FileType.TLG6, "TLG6"),
        ([0x4F, 0x67, 0x67, 0x53],                          FileType.OGG,  "OGG"),
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],  FileType.PNG,  "PNG"),
        ([0xFF, 0xD8, 0xFF],                                FileType.JPEG, "JPEG"),
        ([0x42, 0x4D],                                      FileType.BMP,  "BMP"),
        ([0x52, 0x49, 0x46, 0x46],                          FileType.RIFF, "RIFF"),
        ([0x54, 0x4A, 0x53, 0x32],                          FileType.TJS2, "TJS2"),
        ([0x4F, 0x54, 0x54, 0x4F],                          FileType.OTF,  "OTF"),
        ([0x00, 0x01, 0x00, 0x00],                          FileType.TTF,  "TTF"),
    ];

    public static Task RunAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(2, "正在扫描文件签名 & 提取 PSB/MDF 到 SCN/");

        if (!Directory.Exists(ctx.ExtractOutputDir))
        {
            ConsoleHelper.PrintError($"Extractor_Output 不存在: {ctx.ExtractOutputDir}");
            return Task.CompletedTask;
        }

        var allFiles = Directory.GetFiles(ctx.ExtractOutputDir, "*", SearchOption.AllDirectories);
        ConsoleHelper.PrintInfo($"共 {allFiles.Length} 个文件，开始并行扫描...");

        Directory.CreateDirectory(ctx.ScnDir);

        var typeCounts = new ConcurrentDictionary<FileType, int>();
        var psbCount = 0;
        var scanned = 0;
        var total = allFiles.Length;
        var lastReport = 0;

        Parallel.ForEach(allFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => new byte[16],
            (filePath, _, buffer) =>
            {
                var type = IdentifyFileType(filePath, buffer);
                typeCounts.AddOrUpdate(type, 1, (_, c) => c + 1);

                if (type is FileType.PSB or FileType.MDF)
                {
                    Interlocked.Increment(ref psbCount);
                    CopyPsbToScnDir(filePath, ctx);
                }

                var done = Interlocked.Increment(ref scanned);
                if (done - Volatile.Read(ref lastReport) >= 5000 || done == total)
                {
                    Interlocked.Exchange(ref lastReport, done);
                    ConsoleHelper.PrintProgress(done, total, $"已扫描，PSB/MDF: {Volatile.Read(ref psbCount)}");
                }

                return buffer;
            },
            _ => { });

        Console.WriteLine();

        // 保存统计到 ctx
        ctx.TotalFilesScanned = total;
        ctx.PsbFilesFound = psbCount;
        ctx.FileTypeCounts = new Dictionary<FileType, int>(typeCounts);

        // 打印统计
        ConsoleHelper.PrintSuccess($"扫描完成: {total} 个文件");
        foreach (var (type, count) in typeCounts.OrderByDescending(kv => kv.Value))
        {
            var label = type == FileType.Unknown ? "未知" : type.ToString();
            ConsoleHelper.PrintInfo($"  {label,-8} {count,8} 个");
        }

        ConsoleHelper.PrintInfo($"\n  PSB/MDF 文件已复制到: {ctx.ScnDir} ({psbCount} 个)");

        if (psbCount == 0)
            ConsoleHelper.PrintWarning("未发现 PSB/MDF 文件，后续步骤将无法提取文件名");

        return Task.CompletedTask;
    }

    private static FileType IdentifyFileType(string filePath, byte[] buffer)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.SequentialScan);
            var bytesRead = fs.Read(buffer, 0, buffer.Length);

            if (bytesRead < 2) return FileType.Unknown;

            foreach (var (magic, type, _) in Signatures)
            {
                if (bytesRead >= magic.Length && buffer.AsSpan(0, magic.Length).SequenceEqual(magic))
                {
                    if (type == FileType.RIFF && bytesRead >= 12 &&
                        buffer[8] == 0x57 && buffer[9] == 0x41 &&
                        buffer[10] == 0x56 && buffer[11] == 0x45)
                        return FileType.WAV;
                    return type;
                }
            }

            if (buffer[0] == 0xFF && buffer[1] == 0xFE)
                return FileType.UnicodeText;

            return FileType.Unknown;
        }
        catch
        {
            return FileType.Unknown;
        }
    }

    private static void CopyPsbToScnDir(string srcPath, PipelineContext ctx)
    {
        try
        {
            var relativePath = Path.GetRelativePath(ctx.ExtractOutputDir, srcPath);
            var destPath = Path.Combine(ctx.ScnDir, relativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir != null)
                Directory.CreateDirectory(destDir);
            File.Copy(srcPath, destPath, overwrite: true);
        }
        catch { }
    }
}