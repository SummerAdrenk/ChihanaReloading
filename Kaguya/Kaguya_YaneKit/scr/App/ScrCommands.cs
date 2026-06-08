// ============================================================================
// ScrCommands.cs
// CLI 子命令: .scr 脚本文件操作
//
// 命令列表:
//   decompile    -- 默认高级解析为 HLS (.hls.txt)
//   hls-asm      -- 默认从 HLS 回编 .scr 二进制
//   disasm       -- 低级 SCRASM 反汇编为可编辑文本 (.disasm.txt)
//   asm          -- 低级 SCRASM 汇编回 .scr 二进制
//   verify       -- 二进制回环校验 (read -> write -> compare)
//   verify-text  -- 文本回环校验 (binary -> text -> binary -> compare)
//   dump         -- 打印可读的指令码清单
//
// 可选参数:
//   --read-encoding   读取 .scr 时的字符编码 (默认 cp932)
//   --write-encoding  写入 .scr 时的字符编码 (默认 cp932)
//
// 依赖: Scr.ScrContainerCodec, Scr.ScrHighLevelDecompiler, Scr.ScrHighLevelTextCodec,
//       Scr.ScrTextCodec, Scr.ScrListingFormatter
// ============================================================================

using Kaguya_YaneKit.Script.Params;
using Kaguya_YaneKit.Formats.Params;
using Kaguya_YaneKit.Script.Tblstr;
using System.Text;
using System.Text.Json;

namespace Kaguya_YaneKit.App;

public static class ScrCommands
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
                "disasm" => Disassemble(args),
                "asm" => Assemble(args),
                "decompile" => Decompile(args, context),
                "hls-asm" => AssembleHighLevel(args),
                "opcodes" => Opcodes(args),
                "scan-opcodes" => ScanOpcodes(args),
                "verify" => Verify(args),
                "verify-text" => VerifyText(args),
                "verify-hls" => VerifyHighLevel(args, context),
                "dump" => Dump(args),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Disassemble(string[] args)
    {
        if (args.Length < 3 || args.Length > 7 || !ValidateOptions(args, 3))
        {
            PrintHelp();
            return 1;
        }

        var containerCodec = new ScrContainerCodec();
        var textCodec = new ScrTextCodec(GetOption(args, "--read-encoding"), GetOption(args, "--write-encoding"));
        var input = File.ReadAllBytes(args[1]);
        if (TblstrScrCodec.IsTblstrScr(input))
        {
            var codec = new TblstrScrCodec(GetOption(args, "--read-encoding"));
            var labels = TblstrScrCodec.TryReadSiblingLabels(args[1]);
            var tblstrDocument = codec.Read(input, Path.GetFileName(args[1]), labels);
            var tblstrOutputPath = ResolveDisasmOutputPath(args[1], args[2]);
            File.WriteAllText(tblstrOutputPath, new TblstrScrTextFormatter().WriteDisasm(tblstrDocument), Encoding.UTF8);
            Console.WriteLine($"Wrote {tblstrOutputPath}");
            return 0;
        }

        var document = containerCodec.Read(input, Path.GetFileName(args[1]));
        var outputPath = ResolveDisasmOutputPath(args[1], args[2]);
        File.WriteAllText(outputPath, textCodec.Write(document), Encoding.UTF8);
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    private static int Assemble(string[] args)
    {
        if (args.Length < 3 || args.Length > 7 || !ValidateOptions(args, 3))
        {
            PrintHelp();
            return 1;
        }

        var containerCodec = new ScrContainerCodec();
        var textCodec = new ScrTextCodec(GetOption(args, "--read-encoding"), GetOption(args, "--write-encoding"));
        var document = textCodec.Read(File.ReadAllText(args[1], Encoding.UTF8));
        File.WriteAllBytes(args[2], containerCodec.Write(document));
        Console.WriteLine($"Wrote {args[2]}");
        return 0;
    }

    private static int Decompile(string[] args, KaguyaRuntimeContext? context)
    {
        if (args.Length < 3 || args.Length > 7 || !ValidateOptions(args, 3))
        {
            PrintHelp();
            return 1;
        }

        var containerCodec = new ScrContainerCodec();
        var input = File.ReadAllBytes(args[1]);
        if (TblstrScrCodec.IsTblstrScr(input))
        {
            var codec = new TblstrScrCodec(GetOption(args, "--read-encoding"));
            var labels = TblstrScrCodec.TryReadSiblingLabels(args[1]);
            var tblstrDocument = codec.Read(input, Path.GetFileName(args[1]), labels);
            var tblstrOutputPath = ResolveDecompileOutputPath(args[1], args[2]);
            File.WriteAllText(tblstrOutputPath, new TblstrScrTextFormatter().WriteHls(tblstrDocument), Encoding.UTF8);
            Console.WriteLine($"Wrote {tblstrOutputPath}");
            return 0;
        }

        var document = containerCodec.Read(input, Path.GetFileName(args[1]));
        var outputPath = ResolveDecompileOutputPath(args[1], args[2]);
        var paramsDocument = LoadParamsForDecompile(args[1], GetOption(args, "--params-json"), context);
        File.WriteAllText(
            outputPath,
            new ScrHighLevelDecompiler(GetOption(args, "--read-encoding"), paramsDocument).Write(document),
            Encoding.UTF8);
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    private static int AssembleHighLevel(string[] args)
    {
        if (args.Length < 3 || args.Length > 5 || !ValidateOptions(args, 3))
        {
            PrintHelp();
            return 1;
        }

        var containerCodec = new ScrContainerCodec();
        var hlsText = File.ReadAllText(args[1], Encoding.UTF8);
        if (hlsText.StartsWith(".file kind=tblstr_scr_hls", StringComparison.Ordinal))
        {
            var tblstrDocument = new TblstrScrHlsTextCodec().Read(hlsText, Path.GetFileName(args[2]));
            File.WriteAllBytes(args[2], TblstrScrCodec.WriteRaw(tblstrDocument));
            Console.WriteLine($"Wrote {args[2]}");
            return 0;
        }

        var hlsCodec = new ScrHighLevelTextCodec(GetOption(args, "--write-encoding"));
        var document = hlsCodec.Read(hlsText);
        File.WriteAllBytes(args[2], containerCodec.Write(document));
        Console.WriteLine($"Wrote {args[2]}");
        return 0;
    }

    private static int Opcodes(string[] args)
    {
        if (args.Length > 2)
        {
            PrintHelp();
            return 1;
        }

        var text = new ScrOpcodeTableFormatter().FormatMarkdown();
        if (args.Length == 2)
        {
            File.WriteAllText(args[1], text, Encoding.UTF8);
            Console.WriteLine($"Wrote {args[1]}");
        }
        else
        {
            Console.Write(text);
        }

        return 0;
    }

    private static int ScanOpcodes(string[] args)
    {
        if (args.Length < 2 || args.Length > 3)
        {
            PrintHelp();
            return 1;
        }

        string text;
        int issueCount;
        if (ShouldUseTblstrScrBackend(args[1]))
        {
            var codec = new TblstrScrCodec();
            var summary = codec.ScanPath(args[1]);
            text = new TblstrScrTextFormatter().FormatScan(summary);
            issueCount = summary.Issues.Count;
        }
        else
        {
            var scanner = new ScrOpcodeScanner();
            var summary = scanner.ScanPath(args[1]);
            text = scanner.Format(summary);
            issueCount = summary.Issues.Count;
        }

        if (args.Length == 3)
        {
            File.WriteAllText(args[2], text, Encoding.UTF8);
            Console.WriteLine($"Wrote {args[2]}");
        }
        else
        {
            Console.Write(text);
        }

        return issueCount == 0 ? 0 : 2;
    }

    // 二进制回环校验: 读入 -> 写出 -> 逐字节比较
    private static int Verify(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        var containerCodec = new ScrContainerCodec();
        var original = File.ReadAllBytes(args[1]);
        if (TblstrScrCodec.IsTblstrScr(original))
        {
            var codec = new TblstrScrCodec();
            var tblstrDocument = codec.Read(original, Path.GetFileName(args[1]), TblstrScrCodec.TryReadSiblingLabels(args[1]));
            var tblstrRebuilt = TblstrScrCodec.WriteRaw(tblstrDocument);
            var matched = original.SequenceEqual(tblstrRebuilt);
            Console.WriteLine(matched
                ? "TBLSTR SCR verify OK: byte-for-byte roundtrip matched."
                : $"TBLSTR SCR verify FAILED: original={original.Length}, rebuilt={tblstrRebuilt.Length}.");
            return matched ? 0 : 2;
        }

        var document = containerCodec.Read(original, Path.GetFileName(args[1]));
        var rebuilt = containerCodec.Write(document);
        var equal = original.SequenceEqual(rebuilt);
        Console.WriteLine(equal
            ? "SCR verify OK: byte-for-byte roundtrip matched."
            : $"SCR verify FAILED: original={original.Length}, rebuilt={rebuilt.Length}.");
        return equal ? 0 : 2;
    }

    // 文本回环校验: binary -> text -> binary -> compare
    private static int VerifyText(string[] args)
    {
        if (args.Length < 2 || args.Length > 6 || !ValidateOptions(args, 2))
        {
            PrintHelp();
            return 1;
        }

        var containerCodec = new ScrContainerCodec();
        var textCodec = new ScrTextCodec(GetOption(args, "--read-encoding"), GetOption(args, "--write-encoding"));
        var original = File.ReadAllBytes(args[1]);
        if (TblstrScrCodec.IsTblstrScr(original))
        {
            Console.WriteLine("TBLSTR SCR text verify skipped: editable TBLSTR HLS/IR assembly is not implemented yet; use `scr verify` for binary roundtrip.");
            return 1;
        }

        var document = containerCodec.Read(original, Path.GetFileName(args[1]));
        var text = textCodec.Write(document);
        var reparsed = textCodec.Read(text);
        var rebuilt = containerCodec.Write(reparsed);
        var equal = original.SequenceEqual(rebuilt);
        Console.WriteLine(equal
            ? "SCR text verify OK: binary -> scrasm -> binary matched."
            : $"SCR text verify FAILED: original={original.Length}, rebuilt={rebuilt.Length}.");
        return equal ? 0 : 2;
    }

    private static int VerifyHighLevel(string[] args, KaguyaRuntimeContext? context)
    {
        if (args.Length < 2 || args.Length > 8 || !ValidateOptions(args, 2))
        {
            PrintHelp();
            return 1;
        }

        var containerCodec = new ScrContainerCodec();
        var original = File.ReadAllBytes(args[1]);
        if (TblstrScrCodec.IsTblstrScr(original))
        {
            var codec = new TblstrScrCodec(GetOption(args, "--read-encoding"));
            var tblstrDocument = codec.Read(original, Path.GetFileName(args[1]), TblstrScrCodec.TryReadSiblingLabels(args[1]));
            var tblstrText = new TblstrScrTextFormatter().WriteHls(tblstrDocument);
            var tblstrReparsed = new TblstrScrHlsTextCodec().Read(tblstrText, Path.GetFileName(args[1]));
            var tblstrRebuilt = TblstrScrCodec.WriteRaw(tblstrReparsed);
            var tblstrEqual = original.SequenceEqual(tblstrRebuilt);
            Console.WriteLine(tblstrEqual
                ? "TBLSTR SCR HLS verify OK: binary -> hls -> binary matched."
                : $"TBLSTR SCR HLS verify FAILED: original={original.Length}, rebuilt={tblstrRebuilt.Length}.");
            return tblstrEqual ? 0 : 2;
        }

        var document = containerCodec.Read(original, Path.GetFileName(args[1]));
        var paramsDocument = LoadParamsForDecompile(args[1], GetOption(args, "--params-json"), context);
        var text = new ScrHighLevelDecompiler(GetOption(args, "--read-encoding"), paramsDocument).Write(document);
        var reparsed = new ScrHighLevelTextCodec(GetOption(args, "--write-encoding")).Read(text);
        var rebuilt = containerCodec.Write(reparsed);
        var equal = original.SequenceEqual(rebuilt);
        Console.WriteLine(equal
            ? "SCR HLS verify OK: binary -> hls -> binary matched."
            : $"SCR HLS verify FAILED: original={original.Length}, rebuilt={rebuilt.Length}.");
        return equal ? 0 : 2;
    }

    private static int Dump(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        var containerCodec = new ScrContainerCodec();
        var formatter = new ScrListingFormatter();
        var input = File.ReadAllBytes(args[1]);
        if (TblstrScrCodec.IsTblstrScr(input))
        {
            var codec = new TblstrScrCodec(GetOption(args, "--read-encoding"));
            var tblstrDocument = codec.Read(input, Path.GetFileName(args[1]), TblstrScrCodec.TryReadSiblingLabels(args[1]));
            Console.Write(new TblstrScrTextFormatter().WriteDisasm(tblstrDocument));
            return 0;
        }

        var document = containerCodec.Read(input, Path.GetFileName(args[1]));
        Console.Write(formatter.Format(document));
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown scr command: {command}");
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
        Console.WriteLine("scr commands:");
        Console.WriteLine("  [--params params.dat] scr decompile <input.scr> <output.hls.txt> [--read-encoding cp932] [--params-json params.json]");
        Console.WriteLine("  scr hls-asm <input.hls.txt> <output.scr> [--write-encoding cp932]");
        Console.WriteLine("  scr disasm <input.scr> <output.disasm.txt> [--read-encoding cp932] [low-level SCRASM]");
        Console.WriteLine("  scr asm <input.disasm.txt> <output.scr> [--write-encoding cp932] [low-level SCRASM]");
        Console.WriteLine("  scr opcodes [output.md]");
        Console.WriteLine("  scr scan-opcodes <input.scr|directory> [output.txt]");
        Console.WriteLine("  scr verify <input.scr>");
        Console.WriteLine("  scr verify-text <input.scr> [--read-encoding cp932] [--write-encoding cp932]");
        Console.WriteLine("  [--params params.dat] scr verify-hls <input.scr> [--read-encoding cp932] [--write-encoding cp932] [--params-json params.json]");
        Console.WriteLine("  scr dump <input.scr>");
    }

    // 自动补全输出路径: 如果用户给的路径没有 .disasm.txt 后缀则自动追加
    private static string ResolveDisasmOutputPath(string inputPath, string requestedPath)
    {
        if (requestedPath.EndsWith(".disasm.txt", StringComparison.OrdinalIgnoreCase))
        {
            return requestedPath;
        }

        var directory = Path.GetDirectoryName(requestedPath);
        var name = Path.GetFileNameWithoutExtension(requestedPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(inputPath);
        }

        var outputName = name + ".disasm.txt";
        return string.IsNullOrWhiteSpace(directory)
            ? outputName
            : Path.Combine(directory, outputName);
    }

    private static string ResolveDecompileOutputPath(string inputPath, string requestedPath)
    {
        if (requestedPath.EndsWith(".hls.txt", StringComparison.OrdinalIgnoreCase))
        {
            return requestedPath;
        }

        var directory = Path.GetDirectoryName(requestedPath);
        var name = Path.GetFileNameWithoutExtension(requestedPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(inputPath);
        }

        var outputName = name + ".hls.txt";
        return string.IsNullOrWhiteSpace(directory)
            ? outputName
            : Path.Combine(directory, outputName);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool ShouldUseTblstrScrBackend(string path)
    {
        if (File.Exists(path))
        {
            return TblstrScrCodec.IsTblstrScr(File.ReadAllBytes(path));
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*.scr", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            return TblstrScrCodec.IsTblstrScr(File.ReadAllBytes(file));
        }

        return false;
    }

    private static ParamsDatDocument? LoadParamsForDecompile(string inputScrPath, string? explicitParamsJson, KaguyaRuntimeContext? context)
    {
        var paramsJson = explicitParamsJson;
        if (!string.IsNullOrWhiteSpace(paramsJson))
        {
            if (!File.Exists(paramsJson))
            {
                throw new FileNotFoundException($"params JSON was specified but not found: {paramsJson}", paramsJson);
            }

            return JsonSerializer.Deserialize<ParamsDatDocument>(
                    File.ReadAllText(paramsJson, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"JSON did not contain a params document: {paramsJson}");
        }

        if (context?.Params is not null)
        {
            return context.Params;
        }

        paramsJson = FindSiblingParamsJson(inputScrPath);
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ParamsDatDocument>(
                File.ReadAllText(paramsJson, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"JSON did not contain a params document: {paramsJson}");
    }

    private static string? FindSiblingParamsJson(string inputScrPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(inputScrPath));
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "scr", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(directory);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    var candidate = Path.Combine(parent, "params", "params.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static bool ValidateOptions(string[] args, int start)
    {
        for (var i = start; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Missing value for option: {args[i]}");
                return false;
            }

            if (!string.Equals(args[i], "--read-encoding", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(args[i], "--write-encoding", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(args[i], "--params-json", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                return false;
            }
        }

        return true;
    }
}
