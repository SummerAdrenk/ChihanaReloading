// ============================================================================
// MessageCommands.cs
// CLI 子命令: message.dat 操作
//
// 命令列表:
//   export      -- 导出 message.dat 为 MsgTool 风格文本
//   import      -- 从文本导入并重建 message.dat
//   verify      -- 二进制回环校验
//   verify-text -- 文本回环校验
//   dump        -- 打印结构摘要和命令列表
//   map         -- 建立 .scr -> message 映射关系 (JSON)
//   split       -- 按 .scr 使用情况拆分消息文本
//   merge       -- 将拆分文件合并回完整文本
//
// 可选参数:
//   --ini            指定 message_config.ini (占位符/编码配置)
//   --read-encoding  读取编码 (默认由 INI 或 cp932 决定)
//   --write-encoding 写入编码
//   --encrypt        是否加密输出 (true/false)
//   --xor-key        XOR 加密密钥 (十六进制)
//   --no-workflow    跳过工作流变换 (长度修正/占位符替换等)
//
// 工作流变换 (MessageDatWorkflowProcessor):
//   导出时: 占位符替换, 文本标准化
//   导入时: 占位符还原, 长度修正, 生成 message_fix.txt 预览
//
// 依赖: Message.MessageDatCodec, Message.MessageTextCodec,
//        Message.MessageScriptLinker, Message.MessageDatWorkflowProcessor,
//        Message.MessagePlaceholderConfig
// ============================================================================

using System.Text;
using Kaguya_YaneKit.Text.MessageDat;
using Kaguya_YaneKit.Text.MessageDat.Model;

namespace Kaguya_YaneKit.App;

public static class MessageCommands
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
                "export" => Export(args),
                "import" => Import(args),
                "verify" => Verify(args),
                "verify-text" => VerifyText(args),
                "dump" => Dump(args),
                "map" => Map(args),
                "split" => Split(args),
                "merge" => Merge(args),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Export(string[] args)
    {
        if (args.Length < 3 || !ValidateOptions(args, 3))
        {
            PrintHelp();
            return 1;
        }

        var context = CreateContext(args);
        var input = File.ReadAllBytes(args[1]);
        if (MessageVer3DatCodec.IsLegacyVersion(input))
        {
            var document3 = CreateVer3Codec(context).Read(input);
            var text3 = new MessageVer3TextCodec().Write(document3);
            File.WriteAllText(args[2], text3, Encoding.UTF8);
            Console.WriteLine($"Wrote {args[2]}");
            PrintVer3Summary(document3);
            return 0;
        }

        var codec = context.Codec;
        var document = codec.Read(input);
        if (!HasFlag(args, "--no-workflow"))
        {
            new MessageDatWorkflowProcessor(context.Config).ApplyExportTransforms(document);
        }
        var text = new MessageTextCodec().Write(document);
        File.WriteAllText(args[2], text, Encoding.UTF8);
        Console.WriteLine($"Wrote {args[2]}");
        PrintSummary(document);
        return 0;
    }

    // 导入流程: 读取原始 dat -> 预处理 -> 应用文本 -> 后处理 -> 写出
    private static int Import(string[] args)
    {
        if (args.Length < 4 || !ValidateOptions(args, 4))
        {
            PrintHelp();
            return 1;
        }

        var context = CreateContext(args);
        var input = File.ReadAllBytes(args[1]);
        if (MessageVer3DatCodec.IsLegacyVersion(input))
        {
            var codec3 = CreateVer3Codec(context);
            var document3 = codec3.Read(input);
            new MessageVer3TextCodec().Apply(document3, File.ReadAllText(args[2], Encoding.UTF8));
            var encrypt3 = ReadEncryptOption(args, context.Config.EncryptEnabled ?? document3.Encrypted);
            var key3 = ReadKeyOption(args, context.Config.EncryptKey ?? document3.XorKey);
            File.WriteAllBytes(args[3], codec3.Write(document3, encrypt3, key3));
            Console.WriteLine($"Wrote {args[3]}");
            PrintVer3Summary(document3);
            return 0;
        }

        var codec = context.Codec;
        var document = codec.Read(input);
        var workflow = new MessageDatWorkflowProcessor(context.Config);
        if (!HasFlag(args, "--no-workflow"))
        {
            workflow.ApplyPreImportTransforms(document);
        }
        new MessageTextCodec().Apply(document, File.ReadAllText(args[2], Encoding.UTF8));
        if (!HasFlag(args, "--no-workflow"))
        {
            workflow.ApplyImportTransforms(document, context.WriteEncoding);
            WriteFixedTextPreviewIfNeeded(args[2], context.Config, document);
        }
        var encrypt = ReadEncryptOption(args, context.Config.EncryptEnabled ?? document.Encrypted);
        var key = ReadKeyOption(args, context.Config.EncryptKey ?? document.XorKey);
        File.WriteAllBytes(args[3], codec.Write(document, encrypt, key));
        Console.WriteLine($"Wrote {args[3]}");
        PrintSummary(document);
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 2 || !ValidateOptions(args, 2))
        {
            PrintHelp();
            return 1;
        }

        var context = CreateContext(args);
        var codec = context.Codec;
        var original = File.ReadAllBytes(args[1]);
        if (MessageVer3DatCodec.IsLegacyVersion(original))
        {
            var codec3 = CreateVer3Codec(context);
            var document3 = codec3.Read(original);
            var rebuilt3 = codec3.Write(document3, document3.Encrypted, document3.XorKey);
            var equal3 = original.SequenceEqual(rebuilt3);
            Console.WriteLine(equal3
                ? $"message.dat ver{document3.Version} verify OK: byte-for-byte roundtrip matched."
                : $"message.dat ver{document3.Version} verify FAILED: original={original.Length}, rebuilt={rebuilt3.Length}.");
            return equal3 ? 0 : 2;
        }

        var document = codec.Read(original);
        PrintFormat();
        var rebuilt = codec.Write(document, document.Encrypted, document.XorKey);
        var equal = original.SequenceEqual(rebuilt);
        Console.WriteLine(equal
            ? "message.dat verify OK: byte-for-byte roundtrip matched."
            : $"message.dat verify FAILED: original={original.Length}, rebuilt={rebuilt.Length}.");
        return equal ? 0 : 2;
    }

    private static int Dump(string[] args)
    {
        if (args.Length < 2 || !ValidateOptions(args, 2))
        {
            PrintHelp();
            return 1;
        }

        var context = CreateContext(args);
        var input = File.ReadAllBytes(args[1]);
        if (MessageVer3DatCodec.IsLegacyVersion(input))
        {
            var document3 = CreateVer3Codec(context).Read(input);
            PrintVer3Summary(document3);
            Console.WriteLine();
            var shown = Math.Min(document3.Blocks.Count, 200);
            for (var i = 0; i < shown; i++)
            {
                var block = document3.Blocks[i];
                Console.WriteLine($"Block[{i:D4}] format={block.FormatName} items={block.Items.Count}");
            }
            if (shown < document3.Blocks.Count)
            {
                Console.WriteLine($"... omitted {document3.Blocks.Count - shown} blocks");
            }

            return 0;
        }

        var codec = context.Codec;
        var document = codec.Read(input);
        PrintSummary(document);
        Console.WriteLine();
        for (var i = 0; i < document.Commands.Count; i++)
        {
            var command = document.Commands[i];
            Console.WriteLine($"Command[{i:D4}] id={command.Id} params=[{string.Join(",", command.Params)}]");
        }

        return 0;
    }

    // 建立 .scr 到 message 的引用映射, 输出为 JSON
    private static int Map(string[] args)
    {
        if (args.Length < 4 || !ValidateOptions(args, 4))
        {
            PrintHelp();
            return 1;
        }

        var context = CreateContext(args);
        var codec = context.Codec;
        var input = File.ReadAllBytes(args[1]);
        if (MessageVer3DatCodec.IsLegacyVersion(input))
        {
            var document3 = CreateVer3Codec(context).Read(input);
            PrintVer3Format(document3);
            var linker3 = new MessageVer3ScriptLinker();
            var map3 = linker3.BuildMap(document3, args[2]);
            linker3.WriteMapJson(map3, args[3]);
            Console.WriteLine($"Wrote {args[3]}");
            Console.WriteLine($"Scripts: {map3.Scripts.Count}");
            Console.WriteLine($"Referenced blocks: {map3.ReferencedBlockCount}/{map3.BlockCount}");
            Console.WriteLine($"Shared blocks: {map3.SharedBlockIndices.Count}");
            Console.WriteLine($"Orphan blocks: {map3.OrphanBlockIndices.Count}");
            return 0;
        }

        var document = codec.Read(input);
        PrintFormat();
        if (!HasFlag(args, "--no-workflow"))
        {
            new MessageDatWorkflowProcessor(context.Config).ApplyExportTransforms(document);
        }
        var linker = new MessageScriptLinker();
        var map = linker.BuildMap(document, args[2]);
        linker.WriteMapJson(map, args[3]);
        Console.WriteLine($"Wrote {args[3]}");
        Console.WriteLine($"Scripts: {map.Scripts.Count}");
        Console.WriteLine($"Shared messages: {map.SharedMessageIndices.Count}");
        Console.WriteLine($"Orphan messages: {map.OrphanMessageIndices.Count}");
        return 0;
    }

    private static int Split(string[] args)
    {
        if (args.Length < 4 || !ValidateOptions(args, 4))
        {
            PrintHelp();
            return 1;
        }

        var context = CreateContext(args);
        var codec = context.Codec;
        var input = File.ReadAllBytes(args[1]);
        if (MessageVer3DatCodec.IsLegacyVersion(input))
        {
            var document3 = CreateVer3Codec(context).Read(input);
            PrintVer3Format(document3);
            var linker3 = new MessageVer3ScriptLinker();
            var map3 = linker3.BuildMap(document3, args[2]);
            Directory.CreateDirectory(args[3]);
            linker3.Split(document3, map3, args[3]);
            linker3.WriteMapJson(map3, Path.Combine(args[3], "_map.json"));
            File.WriteAllText(Path.Combine(args[3], "_base_message_ver3.txt"), new MessageVer3TextCodec().Write(document3), Encoding.UTF8);
            Console.WriteLine($"Wrote split files to {args[3]}");
            Console.WriteLine($"Scripts: {map3.Scripts.Count}");
            Console.WriteLine($"Referenced blocks: {map3.ReferencedBlockCount}/{map3.BlockCount}");
            Console.WriteLine($"Shared blocks: {map3.SharedBlockIndices.Count}");
            Console.WriteLine($"Orphan blocks: {map3.OrphanBlockIndices.Count}");
            return 0;
        }

        var document = codec.Read(input);
        PrintFormat();
        if (!HasFlag(args, "--no-workflow"))
        {
            new MessageDatWorkflowProcessor(context.Config).ApplyExportTransforms(document);
        }
        var linker = new MessageScriptLinker();
        var map = linker.BuildMap(document, args[2]);
        Directory.CreateDirectory(args[3]);
        linker.Split(document, map, args[3]);
        linker.WriteMapJson(map, Path.Combine(args[3], "_map.json"));
        File.WriteAllText(Path.Combine(args[3], "_base_message.txt"), new MessageTextCodec().Write(document), Encoding.UTF8);
        Console.WriteLine($"Wrote split files to {args[3]}");
        Console.WriteLine($"Scripts: {map.Scripts.Count}");
        Console.WriteLine($"Shared messages: {map.SharedMessageIndices.Count}");
        Console.WriteLine($"Orphan messages: {map.OrphanMessageIndices.Count}");
        return 0;
    }

    private static int Merge(string[] args)
    {
        if (args.Length != 4)
        {
            PrintHelp();
            return 1;
        }

        var result = new MessageScriptLinker().Merge(args[1], args[2], args[3]);
        Console.WriteLine($"Wrote {args[3]}");
        Console.WriteLine($"Collected: {result.Collected}");
        Console.WriteLine($"Replaced: {result.Replaced}");
        Console.WriteLine($"Missing in base: {result.MissingInBase}");
        Console.WriteLine($"Conflicts: {result.Conflicts}");
        return 0;
    }

    private static int VerifyText(string[] args)
    {
        if (args.Length < 2 || !ValidateOptions(args, 2))
        {
            PrintHelp();
            return 1;
        }

        var context = CreateContext(args);
        var codec = context.Codec;
        var textCodec = new MessageTextCodec();
        var original = File.ReadAllBytes(args[1]);
        if (MessageVer3DatCodec.IsLegacyVersion(original))
        {
            var codec3 = CreateVer3Codec(context);
            var textCodec3 = new MessageVer3TextCodec();
            var document3 = codec3.Read(original);
            var text3 = textCodec3.Write(document3);
            textCodec3.Apply(document3, text3);
            var rebuilt3 = codec3.Write(document3, document3.Encrypted, document3.XorKey);
            var equal3 = original.SequenceEqual(rebuilt3);
            Console.WriteLine(equal3
                ? $"message.dat ver{document3.Version} text verify OK: binary -> text -> binary matched."
                : $"message.dat ver{document3.Version} text verify FAILED: original={original.Length}, rebuilt={rebuilt3.Length}.");
            return equal3 ? 0 : 2;
        }

        var document = codec.Read(original);
        PrintFormat();
        var text = textCodec.Write(document);
        textCodec.Apply(document, text);
        var rebuilt = codec.Write(document, document.Encrypted, document.XorKey);
        var equal = original.SequenceEqual(rebuilt);
        Console.WriteLine(equal
            ? "message.dat text verify OK: binary -> text -> binary matched."
            : $"message.dat text verify FAILED: original={original.Length}, rebuilt={rebuilt.Length}.");
        return equal ? 0 : 2;
    }

    // 构建命令上下文: 解析 INI 配置 -> 确定编码 -> 创建 codec
    private static MessageCommandContext CreateContext(string[] args)
    {
        var iniOption = GetOption(args, "--ini");
        if (!string.IsNullOrWhiteSpace(iniOption) && !File.Exists(iniOption))
        {
            throw new FileNotFoundException($"INI file was specified but does not exist: {iniOption}");
        }

        var placeholders = MessagePlaceholderConfig.Load(iniOption ?? FindDefaultIni());
        var readEncodingName = placeholders.ReadEncodingName ?? GetOption(args, "--read-encoding");
        var writeEncodingName = placeholders.WriteEncodingName ?? GetOption(args, "--write-encoding") ?? readEncodingName;
        var readEncoding = MessageDatCodec.ResolveEncoding(readEncodingName);
        var writeEncoding = MessageDatCodec.ResolveEncoding(writeEncodingName);
        return new MessageCommandContext(
            new MessageDatCodec(readEncoding, writeEncoding, placeholders),
            placeholders,
            readEncoding,
            writeEncoding);
    }

    private static MessageVer3DatCodec CreateVer3Codec(MessageCommandContext context) =>
        new(context.ReadEncoding, context.WriteEncoding, context.Config);

    private static bool ReadEncryptOption(string[] args, bool fallback)
    {
        var value = GetOption(args, "--encrypt");
        if (value is null)
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => throw new FormatException($"Invalid --encrypt value: {value}")
        };
    }

    private static byte ReadKeyOption(string[] args, byte fallback)
    {
        var value = GetOption(args, "--xor-key");
        if (value is null)
        {
            return fallback;
        }

        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToByte(value[2..], 16)
            : Convert.ToByte(value, 16);
    }

    private static void PrintSummary(MessageDatDocument document)
    {
        PrintFormat();
        Console.WriteLine($"Encrypted: {document.Encrypted}");
        Console.WriteLine($"XorKey: 0x{document.XorKey:X2}");
        Console.WriteLine($"Names: {document.Names.Count}");
        Console.WriteLine($"Choices: {document.Choices.Count}");
        Console.WriteLine($"Messages: {document.Messages.Count}");
        Console.WriteLine($"Commands: {document.Commands.Count}");
        Console.WriteLine($"RawTail: {document.RawTail.Length} bytes");
    }

    private static void PrintVer3Summary(MessageVer3Document document)
    {
        var itemCount = document.Blocks.Sum(block => block.Items.Count);
        var voiceCount = document.Blocks.Sum(block => block.Items.Sum(item => item.Voices.Count));
        PrintVer3Format(document);
        Console.WriteLine($"Encrypted: {document.Encrypted} (flag=0x{document.EncryptionFlag:X2})");
        Console.WriteLine($"XorKey: 0x{document.XorKey:X2}");
        Console.WriteLine($"Blocks: {document.Blocks.Count}");
        Console.WriteLine($"Items: {itemCount}");
        Console.WriteLine($"Voices: {voiceCount}");
    }

    private static void PrintFormat()
    {
        Console.WriteLine($"Format: {MessageDatDocument.Magic}");
    }

    private static void PrintVer3Format(MessageVer3Document document)
    {
        Console.WriteLine($"Format: [SCR-MESSAGE]ver{document.Version}");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown msg command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("msg commands:");
        Console.WriteLine("  msg export <message.dat> <message.txt> [--read-encoding cp932] [--ini config.ini] [--no-workflow]");
        Console.WriteLine("  msg import <message.dat> <message.txt> <output.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini] [--encrypt true|false] [--xor-key FF] [--no-workflow]");
        Console.WriteLine("  msg verify <message.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini]");
        Console.WriteLine("  msg verify-text <message.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini]");
        Console.WriteLine("  msg dump <message.dat> [--read-encoding cp932] [--ini config.ini]");
        Console.WriteLine("  msg map <message.dat> <scr-dir> <output.json> [--read-encoding cp932] [--ini config.ini] [--no-workflow]");
        Console.WriteLine("  msg split <message.dat> <scr-dir> <output-dir> [--read-encoding cp932] [--ini config.ini] [--no-workflow]");
        Console.WriteLine("  msg merge <base-message.txt> <split-dir> <output-message.txt>");
    }

    // 当 INI 启用了长度修正时, 输出修正后的文本预览文件
    // 保留 message.orig.txt 中的参考行以便人工对比
    private static void WriteFixedTextPreviewIfNeeded(
        string inputTextPath,
        MessagePlaceholderConfig config,
        MessageDatDocument document)
    {
        if (!config.MsgLengthFix)
        {
            return;
        }

        var directory = Path.GetDirectoryName(inputTextPath);
        var outputPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, "message_fix.txt");
        var fixedText = PreserveReferenceLines(new MessageTextCodec().Write(document), inputTextPath);
        File.WriteAllText(outputPath, fixedText, Encoding.UTF8);
        Console.WriteLine($"Wrote {outputPath}");
    }

    // 将修正后文本中的 name/msg 参考行替换回原始文件中的版本
    // 用于保留翻译者的原始注释行
    private static string PreserveReferenceLines(string fixedText, string inputTextPath)
    {
        var directory = Path.GetDirectoryName(inputTextPath);
        var referencePath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, "message.orig.txt");
        if (!File.Exists(referencePath))
        {
            referencePath = inputTextPath;
        }

        if (!File.Exists(referencePath))
        {
            return fixedText;
        }

        var referenceLines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(referencePath, Encoding.UTF8))
        {
            var key = ReferenceLineKey(line);
            if (key is not null)
            {
                referenceLines[key] = line;
            }
        }

        if (referenceLines.Count == 0)
        {
            return fixedText;
        }

        var output = new List<string>();
        using var reader = new StringReader(fixedText);
        string? fixedLine;
        while ((fixedLine = reader.ReadLine()) is not null)
        {
            var key = ReferenceLineKey(fixedLine);
            output.Add(key is not null && referenceLines.TryGetValue(key, out var referenceLine)
                ? referenceLine
                : fixedLine);
        }

        return string.Join(Environment.NewLine, output) + Environment.NewLine;
    }

    // 从 MsgTool 格式行中提取唯一键: "ID-kind" (name/msg 区分)
    private static string? ReferenceLineKey(string line)
    {
        if (!line.StartsWith('◇'))
        {
            return null;
        }

        var second = line.IndexOf('◇', 1);
        if (second < 0)
        {
            return null;
        }

        var id = line[1..second];
        var third = line.IndexOf('◇', second + 1);
        if (third < 0)
        {
            return id;
        }

        var kind = line[(second + 1)..third];
        return kind is "name" or "msg" ? $"{id}-{kind}" : id;
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
                if (IsKnownFlag(args[i]))
                {
                    continue;
                }

                Console.Error.WriteLine($"Missing value for option: {args[i]}");
                return false;
            }

            if (IsKnownFlag(args[i]))
            {
                i--;
                continue;
            }

            if (!IsKnownOption(args[i]))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                return false;
            }
        }

        return true;
    }

    private static bool IsKnownOption(string value) =>
        string.Equals(value, "--read-encoding", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "--write-encoding", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "--ini", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "--encrypt", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "--xor-key", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownFlag(string value) =>
        string.Equals(value, "--no-workflow", StringComparison.OrdinalIgnoreCase);

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    // 按优先级搜索默认 INI: 工具目录/ini > CWD/Kaguya_YaneKit/ini > CWD/ini
    private static string? FindDefaultIni()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ini", "message_config.ini"),
            Path.Combine(Environment.CurrentDirectory, "Kaguya_YaneKit", "ini", "message_config.ini"),
            Path.Combine(Environment.CurrentDirectory, "ini", "message_config.ini")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record MessageCommandContext(
        MessageDatCodec Codec,
        MessagePlaceholderConfig Config,
        Encoding ReadEncoding,
        Encoding WriteEncoding);
}
