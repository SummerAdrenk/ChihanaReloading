using System.Text.Json;
using System.Diagnostics;
using Kaguya_YaneKit.Formats.Archive;
using Kaguya_YaneKit.Formats.Params;

namespace Kaguya_YaneKit.App;

public static class ArchiveCommands
{
    public static int Unpack(string[] args, KaguyaRuntimeContext? context = null)
    {
        if (args.Length < 2 || args.Length > 5 || !ValidateUnpackOptions(args))
        {
            PrintHelp();
            return 1;
        }

        try
        {
            var archivePath = args[0];
            var outputDirectory = args[1];
            var magic = ReadMagic(archivePath);
            if (magic.StartsWith("AF01", StringComparison.Ordinal))
            {
                var codec = new Af01ArchiveCodec();
                using var stream = File.OpenRead(archivePath);
                var manifest = codec.ReadManifest(stream);
                PrintAf01Info(manifest);
                var stopwatch = Stopwatch.StartNew();
                codec.Extract(archivePath, outputDirectory);
                Console.WriteLine($"Extracted {manifest.Entries.Count} AF01 entries to {outputDirectory} in {stopwatch.Elapsed.TotalSeconds:F2}s");
                Console.WriteLine($"Wrote {Path.Combine(outputDirectory, Af01ArchiveCodec.ManifestFileName)}");
                return 0;
            }

            if (magic.StartsWith("LINK", StringComparison.Ordinal))
            {
                var paramsPath = GetOption(args, "--params");
                var decrypt = !HasFlag(args, "--no-decrypt") && !HasFlag(args, "--raw");
                var codec = new LinkArchiveCodec();
                using var stream = File.OpenRead(archivePath);
                var manifest = codec.ReadManifest(stream);
                PrintLinkInfo(manifest);
                var stopwatch = Stopwatch.StartNew();
                codec.Extract(archivePath, outputDirectory, paramsPath ?? context?.ParamsPath, context?.LinkEncryptionKey, decrypt);
                Console.WriteLine($"Extracted {manifest.Entries.Count} LINK entries to {outputDirectory} in {stopwatch.Elapsed.TotalSeconds:F2}s");
                Console.WriteLine($"Wrote {Path.Combine(outputDirectory, "_link_manifest.json")}");
                return 0;
            }

            if (magic.StartsWith("UF01", StringComparison.Ordinal))
            {
                Console.WriteLine("Archive format: UF01");
                Console.WriteLine("Type: TBLSTR text resource package");
                Console.WriteLine("Use Text Processing -> TBLSTR for this file.");
                return 0;
            }

            Console.Error.WriteLine($"Unsupported archive magic: {magic}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static int Pack(string[] args, KaguyaRuntimeContext? context = null)
    {
        if (args.Length < 3 || !ValidatePackOptions(args))
        {
            PrintHelp();
            return 1;
        }

        try
        {
            var inputDirectory = args[0];
            var manifestPath = args[1];
            var outputPath = args[2];
            var format = ReadManifestFormat(manifestPath);
            if (string.Equals(format, "AF01", StringComparison.OrdinalIgnoreCase))
            {
                if (HasLinkEncryptionOption(args) || GetOption(args, "--params") is not null)
                {
                    Console.Error.WriteLine("AF01 pack does not use LINK encryption options.");
                    return 1;
                }

                var compressPackedEntries = ResolveAf01CompressionChoice(args, manifestPath);
                Console.WriteLine($"AF01 compression: {(compressPackedEntries ? "enabled" : "disabled")}");
                new Af01ArchiveCodec().PackFromManifest(inputDirectory, manifestPath, outputPath, compressPackedEntries);
                Console.WriteLine($"Wrote {outputPath}");
                return 0;
            }

            if (string.Equals(format, "LINK", StringComparison.OrdinalIgnoreCase))
            {
                var compressPackedEntries = ResolveLinkCompressionChoice(args, manifestPath);
                var encryptEncryptedEntries = ResolveLinkEncryptionChoice(args, manifestPath);
                var paramsPath = GetOption(args, "--params");
                var key = encryptEncryptedEntries
                    ? paramsPath is not null ? ReadLinkEncryptionKey(paramsPath) : context?.LinkEncryptionKey ?? ReadLinkEncryptionKey(context?.ParamsPath)
                    : null;

                Console.WriteLine($"LINK compression: {(compressPackedEntries ? "enabled" : "disabled")}");
                Console.WriteLine($"LINK encryption: {(encryptEncryptedEntries ? "enabled" : "disabled")}");
                new LinkArchiveCodec().PackLink6FromManifest(inputDirectory, manifestPath, outputPath, new LinkArchivePackOptions
                {
                    CompressPackedEntries = compressPackedEntries,
                    EncryptEncryptedEntries = encryptEncryptedEntries,
                    EncryptionKey = key
                });
                Console.WriteLine($"Wrote {outputPath}");
                return 0;
            }

            Console.Error.WriteLine($"Unsupported archive manifest format: {format}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string ReadMagic(string archivePath)
    {
        Span<byte> buffer = stackalloc byte[4];
        using var input = File.OpenRead(archivePath);
        var read = input.Read(buffer);
        return System.Text.Encoding.ASCII.GetString(buffer[..read]);
    }

    private static string ReadManifestFormat(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.TryGetProperty("Format", out var format))
        {
            return format.GetString() ?? "";
        }

        if (root.TryGetProperty("Header", out var header) &&
            header.TryGetProperty("Magic", out var magic) &&
            (magic.GetString() ?? "").StartsWith("LINK", StringComparison.OrdinalIgnoreCase))
        {
            return "LINK";
        }

        return "";
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static bool ValidateUnpackOptions(string[] args)
    {
        for (var i = 2; i < args.Length; i++)
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

    private static bool ValidatePackOptions(string[] args)
    {
        var compressSeen = false;
        var noCompressSeen = false;
        var encryptSeen = false;
        var noEncryptSeen = false;
        for (var i = 3; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--keep-encryption-flags", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--encrypt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--no-encrypt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--compress", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--no-compress", StringComparison.OrdinalIgnoreCase))
            {
                compressSeen |= string.Equals(args[i], "--compress", StringComparison.OrdinalIgnoreCase);
                noCompressSeen |= string.Equals(args[i], "--no-compress", StringComparison.OrdinalIgnoreCase);
                encryptSeen |= string.Equals(args[i], "--encrypt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[i], "--keep-encryption-flags", StringComparison.OrdinalIgnoreCase);
                noEncryptSeen |= string.Equals(args[i], "--no-encrypt", StringComparison.OrdinalIgnoreCase);
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

            Console.Error.WriteLine($"Unknown option: {args[i]}");
            return false;
        }

        if (compressSeen && noCompressSeen)
        {
            Console.Error.WriteLine("--compress and --no-compress cannot be used together.");
            return false;
        }

        if (encryptSeen && noEncryptSeen)
        {
            Console.Error.WriteLine("--encrypt and --no-encrypt cannot be used together.");
            return false;
        }

        return true;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("archive commands:");
        Console.WriteLine("  archive_unpack <archive.arc> <output-dir> [--params params.dat] [--no-decrypt|--raw]");
        Console.WriteLine("  archive_pack <input-dir> <manifest.json> <output.arc> [--compress|--no-compress] [--encrypt|--no-encrypt] [--params params.dat]");
    }

    private static bool ResolveAf01CompressionChoice(string[] args, string manifestPath)
    {
        if (HasFlag(args, "--compress"))
        {
            return true;
        }

        if (HasFlag(args, "--no-compress"))
        {
            return false;
        }

        if (Console.IsInputRedirected)
        {
            return true;
        }

        var manifest = Af01ArchiveManifestWriter.Read(manifestPath);
        var packedCount = manifest.Entries.Count(entry => entry.IsPacked);
        if (packedCount == 0)
        {
            return false;
        }

        Console.Write($"AF01 manifest has {packedCount} packed entries. Compress them during pack? [Y/n]: ");
        var answer = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(answer) ||
            answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ResolveLinkCompressionChoice(string[] args, string manifestPath)
    {
        if (HasFlag(args, "--compress"))
        {
            return true;
        }

        if (HasFlag(args, "--no-compress"))
        {
            return false;
        }

        var manifest = LinkArchiveManifestWriter.Read(manifestPath);
        var packedCount = manifest.Entries.Count(entry => (entry.EntryFlags & 3) != 0);
        if (packedCount == 0)
        {
            return false;
        }

        if (Console.IsInputRedirected)
        {
            return true;
        }

        Console.Write($"LINK manifest has {packedCount} compressed entries. Recompress them during pack? [Y/n]: ");
        var answer = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(answer) ||
            answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ResolveLinkEncryptionChoice(string[] args, string manifestPath)
    {
        if (HasFlag(args, "--encrypt") || HasFlag(args, "--keep-encryption-flags"))
        {
            return true;
        }

        if (HasFlag(args, "--no-encrypt"))
        {
            return false;
        }

        var manifest = LinkArchiveManifestWriter.Read(manifestPath);
        var encryptedCount = manifest.Entries.Count(entry => (entry.EntryFlags & 4) != 0);
        if (encryptedCount == 0)
        {
            return false;
        }

        if (Console.IsInputRedirected)
        {
            return true;
        }

        Console.Write($"LINK manifest has {encryptedCount} encrypted entries. Re-encrypt them during pack? [Y/n]: ");
        var answer = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(answer) ||
            answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLinkEncryptionOption(string[] args) =>
        HasFlag(args, "--encrypt") ||
        HasFlag(args, "--no-encrypt") ||
        HasFlag(args, "--keep-encryption-flags");

    private static byte[] ReadLinkEncryptionKey(string? paramsPath)
    {
        if (string.IsNullOrWhiteSpace(paramsPath))
        {
            throw new InvalidDataException("LINK pack needs params.dat to re-encrypt entries. Pass --params <params.dat>, use --game-root, or choose --no-encrypt.");
        }

        var fullPath = Path.GetFullPath(paramsPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"params.dat was not found for LINK encryption: {fullPath}", fullPath);
        }

        var document = new ParamsDatCodec().Read(File.ReadAllBytes(fullPath));
        return Convert.FromBase64String(document.GameSystem.RawBlob.LinkXorKeyBase64);
    }

    private static void PrintAf01Info(Af01ArchiveManifest manifest)
    {
        Console.WriteLine("Archive format: AF01");
        Console.WriteLine($"Version: {manifest.Header.Version}");
        Console.WriteLine($"IndexBaseOffset: 0x{manifest.Header.IndexBaseOffset:X8}");
        Console.WriteLine($"IndexOffset: 0x{manifest.Header.IndexOffset:X8}");
    }

    private static void PrintLinkInfo(LinkArchiveManifest manifest)
    {
        Console.WriteLine($"Archive format: LINK{manifest.Header.Version}");
        Console.WriteLine($"Magic: {manifest.Header.Magic}");
        Console.WriteLine($"Flags: 0x{manifest.Header.Flags:X4}");
        Console.WriteLine($"ArchiveName: {manifest.Header.ArchiveName}");
        Console.WriteLine($"HeaderSize: {manifest.Header.HeaderSize}");
    }
}
