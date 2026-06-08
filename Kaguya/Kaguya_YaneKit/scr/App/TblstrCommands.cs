using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kaguya_YaneKit.Core;
using Kaguya_YaneKit.Text.MessageDat;
using Kaguya_YaneKit.Text.Tblstr;

namespace Kaguya_YaneKit.App;

public static class TblstrCommands
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            PrintHelp();
            return 1;
        }

        return args[0].Trim().ToLowerInvariant() switch
        {
            "export" => Export(args.Skip(1).ToArray()),
            "import" => Import(args.Skip(1).ToArray()),
            "split" => Split(args.Skip(1).ToArray()),
            "merge" => Merge(args.Skip(1).ToArray()),
            "verify-text" => VerifyText(args.Skip(1).ToArray()),
            "--help" or "-h" => PrintHelpAndReturn(),
            _ => Unknown(args[0])
        };
    }

    private static int Export(string[] args)
    {
        if (args.Length < 2)
        {
            PrintHelp();
            return 1;
        }

        var writeJson = false;
        string? scrDirectory = null;
        string? iniPath = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i].Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                writeJson = true;
            }
            else if (args[i].Equals("--scr", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                scrDirectory = args[++i];
            }
            else if (args[i].Equals("--ini", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                iniPath = args[++i];
            }
            else
            {
                PrintHelp();
                return 1;
            }
        }

        try
        {
            var inputPath = args[0];
            var outputDirectory = args[1];
            var config = LoadConfig(iniPath);
            var document = CreateCodec(config).Read(File.ReadAllBytes(inputPath));
            TblstrScriptMap? map = null;
            if (!string.IsNullOrWhiteSpace(scrDirectory))
            {
                map = new TblstrScriptLinker().BuildMap(document, scrDirectory);
            }

            Directory.CreateDirectory(outputDirectory);
            var textPath = Path.Combine(outputDirectory, "tblstr.txt");
            File.WriteAllText(textPath, TblstrTextWriter.Write(document, map), Encoding.UTF8);
            Console.WriteLine($"TBLSTR format: {document.Version}");
            Console.WriteLine($"Entries: {document.Entries.Count}");
            Console.WriteLine($"Text: {textPath}");
            if (map is not null)
            {
                Console.WriteLine($"SCR map: name={map.NameIndices.Count}, msg={map.MessageIndices.Count}, choice={map.ChoiceIndices.Count}");
            }

            if (writeJson)
            {
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                var jsonPath = Path.Combine(outputDirectory, "tblstr.json");
                ReadableUnicodeJson.WriteAllText(jsonPath, JsonSerializer.Serialize(document, options));
                Console.WriteLine($"JSON: {jsonPath}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Split(string[] args)
    {
        if (args.Length < 3)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            var inputPath = args[0];
            var scrDirectory = args[1];
            var outputDirectory = args[2];
            var writeJson = false;
            string? iniPath = null;
            for (var i = 3; i < args.Length; i++)
            {
                if (args[i].Equals("--json", StringComparison.OrdinalIgnoreCase))
                {
                    writeJson = true;
                }
                else if (args[i].Equals("--ini", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    iniPath = args[++i];
                }
                else
                {
                    PrintHelp();
                    return 1;
                }
            }

            var config = LoadConfig(iniPath);
            var document = CreateCodec(config).Read(File.ReadAllBytes(inputPath));
            var linker = new TblstrScriptLinker();
            var map = linker.BuildMap(document, scrDirectory);
            linker.Split(document, map, outputDirectory);
            if (writeJson)
            {
                linker.WriteMapJson(map, Path.Combine(outputDirectory, "_map.json"));
            }

            Console.WriteLine($"Split -> {outputDirectory}");
            Console.WriteLine($"SCR map: name={map.NameIndices.Count}, msg={map.MessageIndices.Count}, choice={map.ChoiceIndices.Count}, unreferenced={map.UnreferencedIndices.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Import(string[] args)
    {
        if (args.Length < 3)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            var inputPath = args[0];
            var textPath = args[1];
            var outputPath = args[2];
            var iniPath = ParseOptionalIni(args, 3);
            var config = LoadConfig(iniPath);
            var writeEncoding = MessageDatCodec.ResolveEncoding(config.WriteEncodingName);
            var codec = CreateCodec(config);
            var document = codec.Read(File.ReadAllBytes(inputPath));
            var text = File.ReadAllText(textPath, Encoding.UTF8);
            text = new TblstrTextWorkflowProcessor(config).ApplyPreImportTransforms(text, writeEncoding);
            var applied = new TblstrTextCodec().Apply(document, text);
            File.WriteAllBytes(outputPath, codec.Write(document));
            Console.WriteLine($"Imported -> {outputPath}");
            Console.WriteLine($"Applied={applied} Entries={document.Entries.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Merge(string[] args)
    {
        if (args.Length != 3)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            var result = new TblstrTextCodec().Merge(args[0], args[1], args[2]);
            Console.WriteLine($"Merged -> {args[2]}");
            Console.WriteLine($"Collected={result.Collected} Replaced={result.Replaced} Missing={result.MissingInBase} Conflicts={result.Conflicts}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int VerifyText(string[] args)
    {
        if (args.Length < 1)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            var iniPath = ParseOptionalIni(args, 1);
            var original = File.ReadAllBytes(args[0]);
            var codec = CreateCodec(LoadConfig(iniPath));
            var document = codec.Read(original);
            var text = TblstrTextWriter.Write(document);
            var applied = new TblstrTextCodec().Apply(document, text);
            var rebuilt = codec.Write(document);
            var matched = original.SequenceEqual(rebuilt);
            Console.WriteLine(matched
                ? $"TBLSTR text verify OK: entries={document.Entries.Count}, applied={applied}, byte-for-byte roundtrip matched."
                : $"TBLSTR text verify FAILED: original={original.Length}, rebuilt={rebuilt.Length}, applied={applied}.");
            return matched ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown tblstr command: {command}");
        PrintHelp();
        return 1;
    }

    private static int PrintHelpAndReturn()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("tblstr commands:");
        Console.WriteLine("  tblstr export <tblstr.arc> <output-dir> [--json] [--scr scr-dir] [--ini tblstr_config.ini]");
        Console.WriteLine("  tblstr import <tblstr.arc> <tblstr.txt> <output.arc> [--ini tblstr_config.ini]");
        Console.WriteLine("  tblstr split <tblstr.arc> <scr-dir> <output-dir> [--json] [--ini tblstr_config.ini]");
        Console.WriteLine("  tblstr merge <base-tblstr.txt> <split-dir> <output-tblstr.txt>");
        Console.WriteLine("  tblstr verify-text <tblstr.arc> [--ini tblstr_config.ini]");
    }

    private static string? ParseOptionalIni(string[] args, int startIndex)
    {
        string? iniPath = null;
        for (var i = startIndex; i < args.Length; i++)
        {
            if (args[i].Equals("--ini", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                iniPath = args[++i];
                continue;
            }

            throw new ArgumentException($"Unknown option: {args[i]}");
        }

        return iniPath;
    }

    private static MessagePlaceholderConfig LoadConfig(string? iniPath)
    {
        var path = iniPath ?? FindDefaultTblstrIni();
        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            throw new FileNotFoundException($"TBLSTR INI file was specified but does not exist: {path}");
        }

        return MessagePlaceholderConfig.Load(path);
    }

    private static TblstrCodec CreateCodec(MessagePlaceholderConfig config)
    {
        var readEncoding = MessageDatCodec.ResolveEncoding(config.ReadEncodingName);
        var writeEncoding = MessageDatCodec.ResolveEncoding(config.WriteEncodingName);
        return new TblstrCodec(readEncoding, writeEncoding, config);
    }

    private static string? FindDefaultTblstrIni()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ini", "tblstr_config.ini"),
            Path.Combine(Environment.CurrentDirectory, "Kaguya_YaneKit", "ini", "tblstr_config.ini"),
            Path.Combine(Environment.CurrentDirectory, "ini", "tblstr_config.ini")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
