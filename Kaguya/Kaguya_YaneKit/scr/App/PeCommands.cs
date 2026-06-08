using Kaguya_YaneKit.Formats.Pe;

namespace Kaguya_YaneKit.App;

public static class PeCommands
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0].Trim().ToLowerInvariant() switch
            {
                "string-dump" => StringDump(args),
                "string-import" => StringImport(args),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int StringDump(string[] args)
    {
        if (args.Length < 3 || !ValidateOptions(args, 3))
        {
            PrintHelp();
            return 1;
        }

        var options = new PeStringDumpOptions
        {
            EncodingName = GetOption(args, "--encoding"),
            MinBytes = ReadIntOption(args, "--min-bytes", 4),
            IncludeText = HasFlag(args, "--include-text"),
            IncludeAsciiOnly = HasFlag(args, "--include-ascii") && !HasFlag(args, "--japanese-only"),
            IncludeUnreferenced = HasFlag(args, "--include-unreferenced"),
            IncludeDiagnostics = HasFlag(args, "--include-diagnostics"),
            Sections = SplitCsv(GetOption(args, "--sections"))
        };

        var tool = new PeStringTableTool();
        var document = tool.Dump(args[1], options);
        tool.WriteDocument(args[2], document);
        Console.WriteLine($"Wrote {args[2]}");
        Console.WriteLine($"Entries: {document.Entries.Count}");
        Console.WriteLine($"Referenced entries: {document.Entries.Count(entry => entry.Refs.Count > 0)}");
        Console.WriteLine($"Length-patch entries: {document.Entries.Count(entry => entry.NeedsLengthPatch)}");
        return 0;
    }

    private static int StringImport(string[] args)
    {
        if (args.Length < 4 || !ValidateOptions(args, 4))
        {
            PrintHelp();
            return 1;
        }

        var options = new PeStringImportOptions
        {
            EncodingName = GetOption(args, "--encoding"),
            SectionName = GetOption(args, "--section") ?? ".yktxt",
            AllowEmptyTranslation = HasFlag(args, "--allow-empty")
        };

        var result = new PeStringTableTool().Import(args[1], args[2], args[3], options);
        Console.WriteLine($"Wrote {args[3]}");
        Console.WriteLine($"Changed entries: {result.ChangedEntries}");
        Console.WriteLine($"In-place entries: {result.InPlaceEntries}");
        Console.WriteLine($"Moved entries: {result.MovedEntries}");
        Console.WriteLine($"Patched references: {result.PatchedReferences}");
        Console.WriteLine($"Patched length immediates: {result.PatchedLengths}");
        if (result.MovedEntries > 0)
        {
            Console.WriteLine($"New section raw offset: 0x{result.NewSectionRawOffset:X8}");
        }

        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown pe command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("pe commands:");
        Console.WriteLine("  pe string-dump <input.exe> <output.json> [--encoding cp932] [--min-bytes 4]");
        Console.WriteLine("                                      [--sections .rdata,.data,_RDATA,.rsrc] [--include-text]");
        Console.WriteLine("                                      [--include-ascii] [--include-unreferenced] [--include-diagnostics]");
        Console.WriteLine("      Dump referenced Japanese/full-width editable PE strings with RVA/VA/file offsets and absolute-VA references.");
        Console.WriteLine("  pe string-import <input.exe> <strings.json> <output.exe> [--encoding cp932] [--section .yktxt] [--allow-empty]");
        Console.WriteLine("      Add a translated string section and patch recorded references.");
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
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

    private static int ReadIntOption(string[] args, string name, int defaultValue)
    {
        var value = GetOption(args, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, out var result) && result > 0
            ? result
            : throw new ArgumentException($"Invalid {name}: {value}");
    }

    private static List<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool ValidateOptions(string[] args, int start)
    {
        var optionsWithValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--encoding",
            "--min-bytes",
            "--sections",
            "--section"
        };
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--include-text",
            "--include-ascii",
            "--include-unreferenced",
            "--include-diagnostics",
            "--japanese-only",
            "--allow-empty"
        };

        var index = start;
        while (index < args.Length)
        {
            var arg = args[index];
            if (optionsWithValue.Contains(arg))
            {
                if (index + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"Missing value for {arg}");
                    return false;
                }

                index += 2;
                continue;
            }

            if (flags.Contains(arg))
            {
                index++;
                continue;
            }

            Console.Error.WriteLine($"Unknown option: {arg}");
            return false;
        }

        return true;
    }
}
