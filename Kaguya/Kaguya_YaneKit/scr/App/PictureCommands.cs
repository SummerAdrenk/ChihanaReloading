// ============================================================================
// PictureCommands.cs
// CLI 子命令: 图片处理流水线
//
// 命令列表:
//   sort                    -- 按文件格式签名分拣到子目录
//   convert                 -- 将原始格式 (AP/AP2/AP3/ANM/BMP) 转换为 PNG
//   repack                  -- 将修改后的 PNG 重新打包为原始格式
//   repack-png              -- 测试入口: 直接将 png/ 中的 PNG 重新打包到 new/
//   repack-fix              -- 对单个 fix 目录执行重打包
//   restore                 -- 将 new/ 中的文件按元数据还原到输出目录
//   restore-with-replenish  -- 还原 + 补充未修改的原始文件
//   export-game             -- 一键流水线: 解包 -> 分拣 -> 转换
//
// 图片处理流水线 (每个档案):
//   link6_unpack/{arc}/ -> pic/{arc}/{format}/orig/     [sort]
//                       -> pic/{arc}/{format}/png/      [convert]
//                       -> pic/{arc}/{format}/fix/      [用户手动修改]
//                       -> pic/{arc}/{format}/new/      [repack]
//                       -> link6_pack/{arc}/             [restore]
//
// 非图片档案过滤: scr, bgm, sed, voice* 自动跳过
//
// 依赖: Formats.Picture (FileSorter, FileConverter, Restorer, PictureProcessing),
//        Formats.Archive.LinkArchiveCodec
// ============================================================================

using Kaguya_YaneKit.Formats.Archive;
using Kaguya_YaneKit.Formats.Params;
using Kaguya_YaneKit.Formats.Picture;
using System.Text.RegularExpressions;

namespace Kaguya_YaneKit.App;

public static class PictureCommands
{
    public static int Run(string[] args, KaguyaRuntimeContext? context = null)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            return args[0].Trim().ToLowerInvariant() switch
            {
                "--help" => Help(),
                "-h" => Help(),
                "help" => Help(),
                "sort" => Sort(args),
                "convert" => Convert(args),
                "repack" => Repack(args),
                "repack-png" => RepackPng(args),
                "repack-fix" => RepackFix(args),
                "restore" => Restore(args),
                "restore-with-replenish" => RestoreWithReplenish(args),
                "export-game" => ExportGame(args, context),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Sort(string[] args)
    {
        if (args.Length != 3)
        {
            PrintHelp();
            return 1;
        }

        if (!FileSorter.SortArchiveDirectories(args[1], args[2]))
        {
            FileSorter.Sort(args[1], args[2]);
        }
        Console.WriteLine($"Sorted picture files into {args[2]}");
        return 0;
    }

    private static int Convert(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        FileConverter.ConvertAll(args[1]);
        Console.WriteLine($"Converted picture files in {args[1]}");
        return 0;
    }

    private static int Repack(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        FileConverter.RepackAll(args[1]);
        Console.WriteLine($"Repacked picture files in {args[1]}");
        return 0;
    }

    private static int RepackPng(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        FileConverter.RepackPngAll(args[1]);
        Console.WriteLine($"Repacked PNG source files in {args[1]}");
        return 0;
    }

    private static int RepackFix(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        FileConverter.RepackFix(args[1]);
        return 0;
    }

    private static int Restore(string[] args)
    {
        if (args.Length != 3)
        {
            PrintHelp();
            return 1;
        }

        var summary = Restorer.Restore(args[1], args[2]);
        Console.WriteLine($"Restore done: {FormatRestoreSummary(summary)}");
        return 0;
    }

    private static int RestoreWithReplenish(string[] args)
    {
        if (args.Length < 3)
        {
            PrintHelp();
            return 1;
        }

        HashSet<string>? exclude = null;
        var excludeIndex = Array.FindIndex(args, 0, args.Length, arg => string.Equals(arg, "-exclude", StringComparison.OrdinalIgnoreCase));
        if (excludeIndex >= 0 && excludeIndex + 1 < args.Length && !string.IsNullOrWhiteSpace(args[excludeIndex + 1]))
        {
            exclude = args[excludeIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();
        }

        var summary = Restorer.RestoreWithReplenish(args[1], args[2], exclude);
        Console.WriteLine($"Restore with replenish done: {FormatRestoreSummary(summary)}");
        return 0;
    }

    private static string FormatRestoreSummary(Restorer.RestoreSummary summary)
    {
        return $"{summary.Copied}/{summary.Total} copied, {summary.Restored} restored, {summary.Replenished} replenished, {summary.Skipped} skipped, {summary.Failed} failed";
    }

    // 一键流水线: 遍历 params 安装表中的图片档案, 逐个解包 -> 分拣 -> 转换
    private static int ExportGame(string[] args, KaguyaRuntimeContext? context)
    {
        if (args.Length != 3)
        {
            PrintHelp();
            return 1;
        }

        context ??= KaguyaRuntimeContext.Create(args[1], args[2], null);
        if (context.Params is null)
        {
            throw new InvalidDataException("params.dat is required for pic export-game.");
        }

        var gameRoot = Path.GetFullPath(args[1]);
        var workDir = Path.GetFullPath(args[2]);
        var arcDir = gameRoot;
        var extractedDir = Path.Combine(workDir, "picture_arc");
        var sortDir = Path.Combine(workDir, "picture");
        Directory.CreateDirectory(extractedDir);
        Directory.CreateDirectory(sortDir);

        var pictureArchives = GetPictureArchives(context.Params.GameSystem.InstallTable);
        var linkCodec = new LinkArchiveCodec();
        int index = 0;
        foreach (var archiveName in pictureArchives)
        {
            index++;
            var arcPath = Path.Combine(arcDir, archiveName);
            if (!File.Exists(arcPath))
            {
                PictureProcessing.WriteLine($"  Warning: archive not found, skipped: {archiveName}");
                continue;
            }

            Console.WriteLine($"  [EXPORT-GAME {archiveName}] {index}/{pictureArchives.Count}");
            var archiveTag = Path.GetFileNameWithoutExtension(archiveName);
            var outputDir = Path.Combine(extractedDir, archiveTag);
            var archiveSortDir = Path.Combine(sortDir, archiveTag);
            linkCodec.Extract(arcPath, outputDir, context.ParamsPath, context.LinkEncryptionKey, decrypt: true);
            FileSorter.Sort(outputDir, archiveSortDir, archiveTag, archiveTag);
            FileConverter.ConvertAll(archiveSortDir);
        }

        Console.WriteLine($"Exported picture arcs into {sortDir}");
        return 0;
    }

    private static IReadOnlyList<string> GetPictureArchives(List<ParamsInstallEntry> installTable)
    {
        return installTable
            .Select(entry => entry.File)
            .Where(f => !IsNonPictureArchive(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsNonPictureArchive(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return name == "scr" ||
               name == "bgm" ||
               name == "sed" ||
               name == "se" ||
               name == "wav" ||
               name.StartsWith("voice") ||
               IsVoiceArchiveName(name);
    }

    private static bool IsVoiceArchiveName(string name) =>
        name.Length > 2 &&
        name.StartsWith("vo", StringComparison.OrdinalIgnoreCase) &&
        name.Skip(2).All(char.IsDigit);

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown pic command: {command}");
        PrintHelp();
        return 1;
    }

    private static int Help()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("pic commands:");
        Console.WriteLine("  pic sort <source-dir> <work-dir>");
        Console.WriteLine("  pic convert <work-dir>");
        Console.WriteLine("  pic repack <work-dir>");
        Console.WriteLine("  pic repack-png <work-dir>");
        Console.WriteLine("  pic repack-fix <fix-dir>");
        Console.WriteLine("  pic restore <work-dir> <output-dir>");
        Console.WriteLine("  pic restore-with-replenish <work-dir> <output-dir> [-exclude bmp,ap2]");
        Console.WriteLine("  pic export-game <game-root> <work-dir>");
    }
}
