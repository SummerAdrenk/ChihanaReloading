// ============================================================================
// LinkCommands.cs
// CLI 子命令: LINK 档案操作
//
// 命令列表:
//   list     -- 列出档案中所有条目 (偏移/大小/标志/时间戳/名称)
//   extract  -- 解包档案, 支持自动解密 (需要 params.dat 密钥)
//   pack6    -- 从目录创建 LINK6 格式档案
//   repack6  -- 根据 _link_manifest.json 重建 LINK6 档案
//   verify   -- 校验档案 chunk 布局和终止符
//
// LINK 档案版本: LINK3, LINK4, LINK5, LINK6
//   每个条目包含: 名称, 数据大小, chunk 大小, 标志, 时间戳
//   标志位 4 表示条目已加密 (XOR 密钥来自 params.dat 的 RawBlob)
//
// 依赖: Formats.Archive.LinkArchiveCodec, Formats.Archive.LinkArchiveModels
// ============================================================================

using System.Diagnostics;
using Kaguya_YaneKit.Formats.Archive;
using Kaguya_YaneKit.Formats.Params;

namespace Kaguya_YaneKit.App;

public static class LinkCommands
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
                "list" => List(args),
                "extract" => Extract(args, context),
                "pack6" => Pack6(args),
                "repack6" => Repack6(args, context),
                "verify" => Verify(args),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int List(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        using var stream = File.OpenRead(args[1]);
        var manifest = new LinkArchiveCodec().ReadManifest(stream);
        PrintSummary(manifest, new FileInfo(args[1]).Length);
        foreach (var entry in manifest.Entries)
        {
            Console.WriteLine($"{entry.EntryOffset:X8} size={entry.DataSize,10} flags={entry.EntryFlags} time={FormatTimestamp(entry)} {entry.Name}");
        }

        return 0;
    }

    private static int Extract(string[] args, KaguyaRuntimeContext? context)
    {
        if (args.Length < 3 || args.Length > 6 || !ValidateExtractOptions(args))
        {
            PrintHelp();
            return 1;
        }

        var paramsPath = GetOption(args, "--params");
        var decrypt = !HasFlag(args, "--no-decrypt") && !HasFlag(args, "--raw");
        var codec = new LinkArchiveCodec();
        var stopwatch = Stopwatch.StartNew();
        codec.Extract(args[1], args[2], paramsPath ?? context?.ParamsPath, context?.LinkEncryptionKey, decrypt);
        using var stream = File.OpenRead(args[1]);
        var manifest = codec.ReadManifest(stream);
        Console.WriteLine($"Extracted {manifest.Entries.Count} entries to {args[2]} in {stopwatch.Elapsed.TotalSeconds:F2}s");
        if (decrypt && manifest.Entries.Any(entry => (entry.EntryFlags & 4) != 0))
        {
            Console.WriteLine($"Decrypted encrypted entries with {paramsPath ?? context?.ParamsPath ?? "params.dat"}");
        }
        else if (!decrypt)
        {
            Console.WriteLine("Raw extraction: encrypted entries were left untouched.");
        }
        Console.WriteLine($"Wrote {Path.Combine(args[2], "_link_manifest.json")}");
        return 0;
    }

    private static int Pack6(string[] args)
    {
        if (args.Length < 3 || args.Length > 8 || !ValidateOptions(args, 3))
        {
            PrintHelp();
            return 1;
        }

        var inputDirectory = args[1];
        var outputPath = args[2];
        var archiveName = GetOption(args, "--name") ?? Path.GetFileNameWithoutExtension(outputPath);
        var flags = ReadU16Option(args, "--flags", 0);
        var recursive = HasFlag(args, "--recursive");
        new LinkArchiveCodec().PackLink6(inputDirectory, outputPath, archiveName, flags, recursive);
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    private static int Repack6(string[] args, KaguyaRuntimeContext? context)
    {
        if (args.Length < 4 || args.Length > 9 || !ValidateOptions(args, 4))
        {
            PrintHelp();
            return 1;
        }

        var compressPackedEntries = !HasFlag(args, "--no-compress") &&
            (HasFlag(args, "--compress") || LinkManifestHasCompressedEntries(args[2]));
        var encryptEncryptedEntries = !HasFlag(args, "--no-encrypt") &&
            (HasFlag(args, "--encrypt") || HasFlag(args, "--keep-encryption-flags") || LinkManifestHasEncryptedEntries(args[2]));
        var paramsPath = GetOption(args, "--params");
        var key = encryptEncryptedEntries
            ? paramsPath is not null ? ReadLinkEncryptionKey(paramsPath) : context?.LinkEncryptionKey ?? ReadLinkEncryptionKey(context?.ParamsPath)
            : null;

        new LinkArchiveCodec().PackLink6FromManifest(args[1], args[2], args[3], new LinkArchivePackOptions
        {
            CompressPackedEntries = compressPackedEntries,
            EncryptEncryptedEntries = encryptEncryptedEntries,
            EncryptionKey = key
        });
        Console.WriteLine($"Wrote {args[3]}");
        return 0;
    }

    // 校验档案完整性: chunk 连续性 + 4 字节零终止符
    private static int Verify(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        using var stream = File.OpenRead(args[1]);
        var manifest = new LinkArchiveCodec().ReadManifest(stream);
        var lastEnd = manifest.Header.HeaderSize;
        foreach (var entry in manifest.Entries)
        {
            if (entry.EntryOffset != lastEnd)
            {
                Console.Error.WriteLine($"LINK verify FAILED: gap/overlap before {entry.Name} at 0x{entry.EntryOffset:X}.");
                return 2;
            }

            lastEnd = entry.EntryOffset + entry.ChunkSize;
        }

        if (stream.Length - lastEnd != 4)
        {
            Console.Error.WriteLine($"LINK verify FAILED: expected 4-byte terminator at 0x{lastEnd:X}, file size={stream.Length}.");
            return 2;
        }

        stream.Position = lastEnd;
        Span<byte> terminator = stackalloc byte[4];
        stream.ReadExactly(terminator);
        if (terminator[0] != 0 || terminator[1] != 0 || terminator[2] != 0 || terminator[3] != 0)
        {
            Console.Error.WriteLine("LINK verify FAILED: terminator is not u32 zero.");
            return 2;
        }

        Console.WriteLine("LINK verify OK: chunks cover the archive and terminator is valid.");
        PrintSummary(manifest, stream.Length);
        return 0;
    }

    private static void PrintSummary(LinkArchiveManifest manifest, long size)
    {
        Console.WriteLine($"Magic: {manifest.Header.Magic}");
        Console.WriteLine($"Version: {manifest.Header.Version}");
        Console.WriteLine($"ArchiveName: {manifest.Header.ArchiveName}");
        Console.WriteLine($"HeaderFlags: {manifest.Header.Flags}");
        Console.WriteLine($"Size: {size} bytes");
        Console.WriteLine($"Entries: {manifest.Entries.Count}");
        Console.WriteLine($"PayloadBytes: {manifest.Entries.Aggregate(0UL, (total, entry) => total + entry.DataSize)}");
    }

    private static string FormatTimestamp(LinkArchiveEntry entry) =>
        $"{entry.Year:D4}-{entry.Month:D2}-{entry.Day:D2} {entry.Hour:D2}:{entry.Minute:D2}:{entry.Second:D2}";

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown link command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("link commands:");
        Console.WriteLine("  link list <archive.arc>");
        Console.WriteLine("  link extract <archive.arc> <output-dir> [--params params.dat] [--no-decrypt|--raw]");
        Console.WriteLine("  link verify <archive.arc>");
        Console.WriteLine("  link pack6 <input-dir> <output.arc> [--name archiveName] [--flags 0] [--recursive]");
        Console.WriteLine("  link repack6 <input-dir> <_link_manifest.json> <output.arc> [--compress|--no-compress] [--encrypt|--no-encrypt] [--params params.dat]");
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 3; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static ushort ReadU16Option(string[] args, string name, ushort fallback)
    {
        var value = GetOption(args, name);
        if (value is null)
        {
            return fallback;
        }

        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt16(value[2..], 16)
            : Convert.ToUInt16(value);
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static bool ValidateExtractOptions(string[] args)
    {
        for (var i = 3; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--no-decrypt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--raw", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(args[i], "--params", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                return false;
            }

            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Missing value for option: {args[i]}");
                return false;
            }

            i++;
        }

        return true;
    }

    private static bool ValidateOptions(string[] args, int start)
    {
        for (var i = start; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--recursive", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(args[i], "--keep-encryption-flags", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(args[i], "--compress", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--no-compress", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--encrypt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--no-encrypt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(args[i], "--params", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"Missing value for option: {args[i]}");
                    return false;
                }

                i++;
                continue;
            }

            if (!string.Equals(args[i], "--name", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(args[i], "--flags", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                return false;
            }

            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Missing value for option: {args[i]}");
                return false;
            }

            i++;
        }

        return true;
    }

    private static bool LinkManifestHasCompressedEntries(string manifestPath) =>
        LinkArchiveManifestWriter.Read(manifestPath).Entries.Any(entry => (entry.EntryFlags & 3) != 0);

    private static bool LinkManifestHasEncryptedEntries(string manifestPath) =>
        LinkArchiveManifestWriter.Read(manifestPath).Entries.Any(entry => (entry.EntryFlags & 4) != 0);

    private static byte[] ReadLinkEncryptionKey(string? paramsPath)
    {
        if (string.IsNullOrWhiteSpace(paramsPath))
        {
            throw new InvalidDataException("LINK repack needs params.dat to re-encrypt entries. Pass --params <params.dat>, use --game-root, or choose --no-encrypt.");
        }

        var fullPath = Path.GetFullPath(paramsPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"params.dat was not found for LINK encryption: {fullPath}", fullPath);
        }

        var document = new ParamsDatCodec().Read(File.ReadAllBytes(fullPath));
        return Convert.FromBase64String(document.GameSystem.RawBlob.LinkXorKeyBase64);
    }
}
