// ============================================================================
// ParamsCommands.cs
// CLI 子命令: params.dat 操作
//
// 命令列表:
//   dump         -- 打印 params.dat 结构摘要
//   export-json  -- 导出为可编辑 JSON AST
//   import-json  -- 从 JSON AST 重建 params.dat
//   verify       -- 二进制回环校验
//   verify-json  -- JSON 回环校验 (binary -> JSON -> binary -> compare)
//   extract-raw  -- 提取 RawBlob 为二进制文件
//   replace-raw  -- 替换 RawBlob 并重建 params.dat
//
// params.dat 结构概要:
//   Header, GameSystem (画布/安装表/设置/演示/缩略图/场景名/CG注册/RawBlob),
//   Pattern (素材引用/int 数组/分组表 x2), SceneLabels
//
// 依赖: Formats.Params.ParamsDatCodec, Core.ReadableUnicodeJson
// ============================================================================

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kaguya_YaneKit.Core;
using Kaguya_YaneKit.Formats.Params;

namespace Kaguya_YaneKit.App;

public static class ParamsCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
                "dump" => Dump(args),
                "export-json" => ExportJson(args),
                "import-json" => ImportJson(args),
                "verify" => Verify(args),
                "verify-json" => VerifyJson(args),
                "extract-raw" => ExtractRaw(args),
                "replace-raw" => ReplaceRaw(args),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Dump(string[] args)
    {
        var (positional, readEncoding, writeEncoding) = ParseOptions(args);
        if (positional.Count != 2)
        {
            PrintHelp();
            return 1;
        }

        var document = ReadParams(positional[1], readEncoding, writeEncoding);
        PrintSummary(document, new FileInfo(positional[1]).Length);
        return 0;
    }

    private static int ExportJson(string[] args)
    {
        var (positional, readEncoding, writeEncoding) = ParseOptions(args);
        if (positional.Count != 3)
        {
            PrintHelp();
            return 1;
        }

        var document = ReadParams(positional[1], readEncoding, writeEncoding);
        ReadableUnicodeJson.WriteAllText(positional[2], JsonSerializer.Serialize(document, JsonOptions));
        Console.WriteLine($"Wrote {positional[2]}");
        PrintSummary(document, new FileInfo(positional[1]).Length);
        return 0;
    }

    private static int ImportJson(string[] args)
    {
        var (positional, readEncoding, writeEncoding) = ParseOptions(args);
        if (positional.Count != 3)
        {
            PrintHelp();
            return 1;
        }

        var document = JsonSerializer.Deserialize<ParamsDatDocument>(File.ReadAllText(positional[1], Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("JSON did not contain a params document.");
        var codec = new ParamsDatCodec(
            readEncoding ?? document.LegacyReadEncoding,
            writeEncoding ?? document.LegacyWriteEncoding ?? readEncoding ?? document.LegacyReadEncoding);
        File.WriteAllBytes(positional[2], codec.Write(document));
        Console.WriteLine($"Wrote {positional[2]}");
        PrintSummary(document, new FileInfo(positional[2]).Length);
        return 0;
    }

    private static int Verify(string[] args)
    {
        var (positional, readEncoding, writeEncoding) = ParseOptions(args);
        if (positional.Count != 2)
        {
            PrintHelp();
            return 1;
        }

        var original = File.ReadAllBytes(positional[1]);
        var codec = new ParamsDatCodec(readEncoding, writeEncoding);
        var document = codec.Read(original);
        PrintVersion(document);
        var rebuilt = codec.Write(document);
        var equal = original.SequenceEqual(rebuilt);
        Console.WriteLine(equal
            ? "params.dat verify OK: byte-for-byte roundtrip matched."
            : $"params.dat verify FAILED: original={original.Length}, rebuilt={rebuilt.Length}.");
        return equal ? 0 : 2;
    }

    // JSON 回环校验: binary -> JSON -> deserialize -> binary -> compare
    private static int VerifyJson(string[] args)
    {
        var (positional, readEncoding, writeEncoding) = ParseOptions(args);
        if (positional.Count != 2)
        {
            PrintHelp();
            return 1;
        }

        var original = File.ReadAllBytes(positional[1]);
        var codec = new ParamsDatCodec(readEncoding, writeEncoding);
        var document = codec.Read(original);
        PrintVersion(document);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var reparsed = JsonSerializer.Deserialize<ParamsDatDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("JSON did not contain a params document.");
        var rebuilt = codec.Write(reparsed);
        var equal = original.SequenceEqual(rebuilt);
        Console.WriteLine(equal
            ? "params.dat JSON verify OK: binary -> JSON -> binary matched."
            : $"params.dat JSON verify FAILED: original={original.Length}, rebuilt={rebuilt.Length}.");
        return equal ? 0 : 2;
    }

    private static int ExtractRaw(string[] args)
    {
        var (positional, readEncoding, writeEncoding) = ParseOptions(args);
        if (positional.Count != 3)
        {
            PrintHelp();
            return 1;
        }

        var document = ReadParams(positional[1], readEncoding, writeEncoding);
        PrintVersion(document);
        File.WriteAllBytes(positional[2], Convert.FromBase64String(document.GameSystem.RawBlob.LinkXorKeyBase64));
        Console.WriteLine($"Wrote {positional[2]}");
        return 0;
    }

    // 替换 RawBlob 并自动计算 BPP (每像素字节数)
    private static int ReplaceRaw(string[] args)
    {
        var (positional, readEncoding, writeEncoding) = ParseOptions(args);
        if (positional.Count != 4)
        {
            PrintHelp();
            return 1;
        }

        var codec = new ParamsDatCodec(readEncoding, writeEncoding);
        var document = codec.Read(File.ReadAllBytes(positional[1]));
        PrintVersion(document);
        var raw = File.ReadAllBytes(positional[2]);
        document.GameSystem.RawBlob.KeyByteLength = raw.Length;
        document.GameSystem.RawBlob.LinkXorKeyBase64 = Convert.ToBase64String(raw);
        document.GameSystem.RawBlob.ExpectedBytesPerPixel = CalculateBytesPerPixel(
            raw.Length,
            document.GameSystem.Width,
            document.GameSystem.Height);
        File.WriteAllBytes(positional[3], codec.Write(document));
        Console.WriteLine($"Wrote {positional[3]}");
        Console.WriteLine($"RawBlob: {raw.Length} bytes");
        return 0;
    }

    private static ParamsDatDocument ReadParams(string path, string? readEncoding = null, string? writeEncoding = null) =>
        new ParamsDatCodec(readEncoding, writeEncoding).Read(File.ReadAllBytes(path));

    private static (List<string> Positional, string? ReadEncoding, string? WriteEncoding) ParseOptions(string[] args)
    {
        var positional = new List<string>();
        string? readEncoding = null;
        string? writeEncoding = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--read-encoding":
                    if (++i >= args.Length)
                    {
                        throw new ArgumentException("--read-encoding requires a value.");
                    }
                    readEncoding = args[i];
                    break;
                case "--write-encoding":
                    if (++i >= args.Length)
                    {
                        throw new ArgumentException("--write-encoding requires a value.");
                    }
                    writeEncoding = args[i];
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        return (positional, readEncoding, writeEncoding);
    }

    private static uint? CalculateBytesPerPixel(int byteLength, uint width, uint height)
    {
        var pixels = (ulong)width * height;
        if (pixels == 0 || (ulong)byteLength % pixels != 0)
        {
            return null;
        }

        return (uint)((ulong)byteLength / pixels);
    }

    private static void PrintSummary(ParamsDatDocument document, long size)
    {
        var rawBlobBytes = Convert.FromBase64String(document.GameSystem.RawBlob.LinkXorKeyBase64).Length;
        Console.WriteLine($"Header: {document.Header}");
        Console.WriteLine($"Version: {ParamsDatCodec.DescribeVersion(document.Header)}");
        Console.WriteLine($"Size: {size} bytes");
        Console.WriteLine($"Canvas: {document.GameSystem.Width}x{document.GameSystem.Height}");
        Console.WriteLine($"Install entries: {document.GameSystem.InstallTable.Count}");
        Console.WriteLine($"Setting roots: {document.GameSystem.SettingTags.Count(x => x.Present)}");
        if (document.GameSystem.V51VoiceEntries.Count > 0 ||
            document.GameSystem.V51ByteGroups.Count > 0 ||
            document.GameSystem.V51SoundGroups.Count > 0)
        {
            Console.WriteLine($"v05.1 voice entries: {document.GameSystem.V51VoiceEntries.Count}");
            Console.WriteLine($"v05.1 byte groups: {document.GameSystem.V51ByteGroups.Count}");
            Console.WriteLine($"v05.1 sound groups: {document.GameSystem.V51SoundGroups.Count}");
        }
        Console.WriteLine($"RawBlob: {rawBlobBytes} bytes");
        Console.WriteLine($"Demos: {document.GameSystem.Demos.Count}");
        Console.WriteLine($"Thumbnails: {document.GameSystem.Thumbnails.Count}");
        Console.WriteLine($"Scene names: {document.GameSystem.SceneNames.Count}");
        Console.WriteLine($"Regist CG groups: {document.GameSystem.RegistCg.Count}");
        Console.WriteLine($"Regist scene groups: {document.GameSystem.RegistScene.Count}");
        Console.WriteLine($"Pattern items: {document.Pattern.Items.Count}");
        Console.WriteLine($"Pattern int arrays: {document.Pattern.IntArrays.Count}");
        Console.WriteLine($"Pattern group table 1: {document.Pattern.GroupTable1.Groups.Count}");
        Console.WriteLine($"Pattern group table 2: {document.Pattern.GroupTable2.Groups.Count}");
        Console.WriteLine($"Scene labels: {document.SceneLabels.Count}");
    }

    private static void PrintVersion(ParamsDatDocument document)
    {
        Console.WriteLine($"Header: {document.Header}");
        Console.WriteLine($"Version: {ParamsDatCodec.DescribeVersion(document.Header)}");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown params command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("params commands:");
        Console.WriteLine("  params dump <params.dat>");
        Console.WriteLine("  params export-json <params.dat> <output.json> [--read-encoding cp932] [--write-encoding cp932]");
        Console.WriteLine("  params import-json <input.json> <output.dat> [--read-encoding cp932] [--write-encoding cp932]");
        Console.WriteLine("  params verify <params.dat> [--read-encoding cp932] [--write-encoding cp932]");
        Console.WriteLine("  params verify-json <params.dat> [--read-encoding cp932] [--write-encoding cp932]");
        Console.WriteLine("  params extract-raw <params.dat> <raw.bin> [--read-encoding cp932]");
        Console.WriteLine("  params replace-raw <params.dat> <raw.bin> <output.dat> [--read-encoding cp932] [--write-encoding cp932]");
    }
}
