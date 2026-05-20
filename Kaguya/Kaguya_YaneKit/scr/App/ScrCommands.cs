// ============================================================================
// ScrCommands.cs
// CLI 子命令: .scr 脚本文件操作
//
// 命令列表:
//   disasm       -- 将 .scr 二进制反汇编为可编辑文本 (.disasm.txt)
//   asm          -- 将 .disasm.txt 汇编回 .scr 二进制
//   verify       -- 二进制回环校验 (read -> write -> compare)
//   verify-text  -- 文本回环校验 (binary -> text -> binary -> compare)
//   dump         -- 打印可读的指令码清单
//
// 可选参数:
//   --read-encoding   读取 .scr 时的字符编码 (默认 cp932)
//   --write-encoding  写入 .scr 时的字符编码 (默认 cp932)
//
// 依赖: Scr.ScrContainerCodec, Scr.ScrTextCodec, Scr.ScrListingFormatter
// ============================================================================

using Kaguya_YaneKit.Scr;
using System.Text;

namespace Kaguya_YaneKit.App;

public static class ScrCommands
{
    public static int Run(string[] args)
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
                "disasm" => Disassemble(args),
                "asm" => Assemble(args),
                "verify" => Verify(args),
                "verify-text" => VerifyText(args),
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

    private static int Dump(string[] args)
    {
        if (args.Length != 2)
        {
            PrintHelp();
            return 1;
        }

        var containerCodec = new ScrContainerCodec();
        var formatter = new ScrListingFormatter();
        var document = containerCodec.Read(File.ReadAllBytes(args[1]), Path.GetFileName(args[1]));
        Console.Write(formatter.Format(document));
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown scr command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("scr commands:");
        Console.WriteLine("  scr disasm <input.scr> <output.disasm.txt> [--read-encoding cp932]");
        Console.WriteLine("  scr asm <input.disasm.txt> <output.scr> [--write-encoding cp932]");
        Console.WriteLine("  scr verify <input.scr>");
        Console.WriteLine("  scr verify-text <input.scr> [--read-encoding cp932] [--write-encoding cp932]");
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
                !string.Equals(args[i], "--write-encoding", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                return false;
            }
        }

        return true;
    }
}
