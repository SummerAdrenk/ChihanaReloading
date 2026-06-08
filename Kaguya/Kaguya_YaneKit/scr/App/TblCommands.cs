using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kaguya_YaneKit.Core;
using Kaguya_YaneKit.Text.Tblstr;

namespace Kaguya_YaneKit.App;

public static class TblCommands
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
            "verify" => Verify(args.Skip(1).ToArray()),
            "--help" or "-h" => PrintHelpAndReturn(),
            _ => Unknown(args[0])
        };
    }

    private static int Export(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            PrintHelp();
            return 1;
        }

        var writeJson = args.Length == 3 && args[2].Equals("--json", StringComparison.OrdinalIgnoreCase);
        if (args.Length == 3 && !writeJson)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            var inputPath = args[0];
            var outputDirectory = args[1];
            Directory.CreateDirectory(outputDirectory);
            var result = ExportTables(inputPath, outputDirectory, writeJson);
            Console.WriteLine($"TBL exported: {result.Success} success, {result.Skipped} skipped");
            return result.Success > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Import(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            var inputPath = args[0];
            var outputDirectory = args[1];
            Directory.CreateDirectory(outputDirectory);
            var result = ImportTables(inputPath, outputDirectory, Console.WriteLine);
            Console.WriteLine($"TBL imported: {result.Success} success, {result.Skipped} skipped, {result.Failure} failed");
            return result.Success > 0 && result.Failure == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Verify(string[] args)
    {
        if (args.Length != 1)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            var result = VerifyTables(args[0], Console.WriteLine);
            return result.Failure == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static TblCommandResult ExportTables(string inputPath, string outputDirectory, bool writeJson)
    {
        var files = EnumerateTblFiles(inputPath).ToArray();
        var codec = new TblSupportCodec();
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var success = 0;
        var skipped = 0;

        foreach (var file in files)
        {
            TblSupportDocument document;
            try
            {
                document = codec.Read(Path.GetFileName(file), File.ReadAllBytes(file));
            }
            catch (InvalidDataException)
            {
                skipped++;
                continue;
            }

            var baseName = GetOutputBaseName(document);
            File.WriteAllText(Path.Combine(outputDirectory, baseName + ".txt"), TblSupportTextWriter.Write(document), Encoding.UTF8);
            if (writeJson)
            {
                ReadableUnicodeJson.WriteAllText(Path.Combine(outputDirectory, baseName + ".json"), JsonSerializer.Serialize(document, options));
            }

            success++;
        }

        return new TblCommandResult(success, skipped, 0);
    }

    public static TblCommandResult ImportTables(string inputPath, string outputDirectory, Action<string>? log = null)
    {
        var files = EnumerateJsonFiles(inputPath).ToArray();
        var codec = new TblSupportCodec();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var success = 0;
        var skipped = 0;
        var failure = 0;

        foreach (var file in files)
        {
            TblSupportDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<TblSupportDocument>(File.ReadAllText(file, Encoding.UTF8), options);
                if (document is null || !string.Equals(document.Format, "TBL", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    log?.Invoke($"SKIP {Path.GetFileName(file)}: not a TBL support JSON");
                    continue;
                }

                var outputName = GetOutputFileName(document);
                File.WriteAllBytes(Path.Combine(outputDirectory, outputName), codec.Write(document));
                success++;
                log?.Invoke($"OK {Path.GetFileName(file)} -> {outputName}");
            }
            catch (Exception ex)
            {
                failure++;
                log?.Invoke($"FAILED {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return new TblCommandResult(success, skipped, failure);
    }

    public static TblCommandResult VerifyTables(string inputPath, Action<string>? log = null)
    {
        var files = EnumerateTblFiles(inputPath).ToArray();
        var codec = new TblSupportCodec();
        var success = 0;
        var skipped = 0;
        var failure = 0;

        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);
                var document = codec.Read(Path.GetFileName(file), data);
                var rebuilt = codec.Write(document);
                if (!data.SequenceEqual(rebuilt))
                {
                    failure++;
                    log?.Invoke($"FAILED {Path.GetFileName(file)}: roundtrip bytes differ");
                    continue;
                }

                success++;
                log?.Invoke($"OK {Path.GetFileName(file)} ({document.Kind})");
            }
            catch (InvalidDataException)
            {
                skipped++;
            }
            catch (Exception ex)
            {
                failure++;
                log?.Invoke($"FAILED {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        log?.Invoke($"TBL verify: {success} success, {skipped} skipped, {failure} failure");
        return new TblCommandResult(success, skipped, failure);
    }

    private static IEnumerable<string> EnumerateTblFiles(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            yield return inputPath;
            yield break;
        }

        if (!Directory.Exists(inputPath))
        {
            throw new DirectoryNotFoundException(inputPath);
        }

        foreach (var file in Directory.EnumerateFiles(inputPath, "*.tbl", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            yield return file;
        }
    }

    private static IEnumerable<string> EnumerateJsonFiles(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            yield return inputPath;
            yield break;
        }

        if (!Directory.Exists(inputPath))
        {
            throw new DirectoryNotFoundException(inputPath);
        }

        foreach (var file in Directory.EnumerateFiles(inputPath, "tbl_*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            yield return file;
        }
    }

    private static string GetOutputBaseName(TblSupportDocument document) =>
        document.Kind switch
        {
            "value" => "tbl_value",
            "globalvalue" => "tbl_globalvalue",
            "label" => "tbl_label",
            "eventfg" => "tbl_eventfg",
            _ => "tbl_" + Path.GetFileNameWithoutExtension(document.FileName)
        };

    private static string GetOutputFileName(TblSupportDocument document)
    {
        var fileName = Path.GetFileName(document.FileName);
        if (!string.IsNullOrWhiteSpace(fileName) &&
            fileName.EndsWith(".tbl", StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        return document.Kind switch
        {
            "value" => "value.tbl",
            "globalvalue" => "globalvalue.tbl",
            "label" => "label.tbl",
            "eventfg" => "eventfg.tbl",
            _ => throw new InvalidDataException($"Unsupported TBL kind: {document.Kind}")
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown tbl command: {command}");
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
        Console.WriteLine("tbl commands:");
        Console.WriteLine("  tbl export <tbl-file|tbl-dir> <output-dir> [--json]");
        Console.WriteLine("  tbl import <tbl-json-file|tbl-json-dir> <output-dir>");
        Console.WriteLine("  tbl verify <tbl-file|tbl-dir>");
    }
}

public readonly record struct TblCommandResult(int Success, int Skipped, int Failure);
