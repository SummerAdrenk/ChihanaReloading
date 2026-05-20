// ============================================================================
// MessageScriptLinker.cs
// 消息与 .scr 脚本的关联/拆分/合并工具
//
// 功能概述:
//   BuildMap()  - 扫描 scrDirectory 下所有 .scr 文件, 解析 opcode=7 指令
//                 提取 commandIndex, 建立脚本->命令->消息的完整映射
//                 同时检测共享消息 (多脚本引用) 和孤立消息 (无脚本引用)
//   Split()     - 按映射将 message.dat 拆分为多个文本文件:
//                 _names_choices.txt, _commands.txt, _shared.txt, _orphan.txt,
//                 以及每个 .scr 对应的独占消息文件
//   Merge()     - 从拆分目录收集 ◆ 行译文, 合并回基础文本文件
//                 通过 TranslationKey() 匹配行标识, 支持冲突检测
//
// 辅助类:
//   MessageScriptMap   - 映射结果 (脚本列表, 共享/孤立索引)
//   MessageScriptEntry - 单个脚本的命令索引和消息索引
//   MergeResult        - 合并统计 (收集数/替换数/缺失数/冲突数)
//   MessageBlockWriter - 消息块文本输出工具 (含角色名和分支标记)
//
// 依赖: ScrContainerCodec (.scr 解析), MessageDatDocument, ReadableUnicodeJson
// 被依赖: InteractiveSession (split/merge 命令)
// ============================================================================
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Kaguya_YaneKit.Core;
using Kaguya_YaneKit.Message.Model;
using Kaguya_YaneKit.Scr;

namespace Kaguya_YaneKit.Message;

public sealed class MessageScriptLinker
{
    private readonly ScrContainerCodec _scrCodec = new();

    public MessageScriptMap BuildMap(MessageDatDocument message, string scrDirectory)
    {
        var result = new MessageScriptMap
        {
            NameCount = message.Names.Count,
            ChoiceCount = message.Choices.Count,
            MessageCount = message.Messages.Count,
            CommandCount = message.Commands.Count
        };

        foreach (var file in Directory.EnumerateFiles(scrDirectory, "*.scr", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName))
        {
            var document = _scrCodec.Read(File.ReadAllBytes(file), Path.GetFileName(file));
            var entry = new MessageScriptEntry
            {
                ScriptFile = Path.GetFileName(file)
            };

            foreach (var instruction in document.Script.Instructions)
            {
                if (instruction.Opcode != 7 || instruction.Body.Length < 4)
                {
                    continue;
                }

                var commandIndex = BinaryPrimitives.ReadInt32LittleEndian(instruction.Body.AsSpan(0, 4));
                if (commandIndex < 0)
                {
                    continue;
                }

                entry.CommandIndices.Add(commandIndex);
                if (commandIndex >= message.Commands.Count)
                {
                    continue;
                }

                foreach (var messageIndex in message.Commands[commandIndex].Params)
                {
                    if (messageIndex >= 0 && messageIndex < message.Messages.Count)
                    {
                        entry.MessageIndices.Add(messageIndex);
                    }
                }
            }

            entry.CommandIndices.Sort();
            entry.MessageIndices.Sort();
            entry.CommandIndices = entry.CommandIndices.Distinct().ToList();
            entry.MessageIndices = entry.MessageIndices.Distinct().ToList();
            result.Scripts.Add(entry);
        }

        var referenced = result.Scripts.SelectMany(x => x.MessageIndices).ToHashSet();
        var ownerMap = new Dictionary<int, List<string>>();
        foreach (var script in result.Scripts)
        {
            foreach (var messageIndex in script.MessageIndices)
            {
                if (!ownerMap.TryGetValue(messageIndex, out var owners))
                {
                    owners = [];
                    ownerMap[messageIndex] = owners;
                }

                owners.Add(script.ScriptFile);
            }
        }

        foreach (var pair in ownerMap.OrderBy(x => x.Key))
        {
            if (pair.Value.Count > 1)
            {
                result.SharedMessageIndices.Add(pair.Key);
                result.SharedMessageOwners[pair.Key] = pair.Value.OrderBy(x => x, StringComparer.Ordinal).ToList();
            }
        }

        for (var i = 0; i < message.Messages.Count; i++)
        {
            if (!referenced.Contains(i))
            {
                result.OrphanMessageIndices.Add(i);
            }
        }

        return result;
    }

    public void WriteMapJson(MessageScriptMap map, string outputPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        ReadableUnicodeJson.WriteAllText(outputPath, JsonSerializer.Serialize(map, options));
    }

    public void Split(MessageDatDocument message, MessageScriptMap map, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var blockWriter = new MessageBlockWriter(message);

        File.WriteAllText(Path.Combine(outputDirectory, "_names_choices.txt"), blockWriter.WriteNamesAndChoices(), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "_commands.txt"), blockWriter.WriteCommands(), Encoding.UTF8);

        var shared = map.SharedMessageIndices.ToHashSet();
        if (map.SharedMessageIndices.Count > 0)
        {
            var builder = new StringBuilder();
            builder.AppendLine("//==================================================");
            builder.AppendLine($"; Shared messages: {map.SharedMessageIndices.Count}");
            builder.AppendLine("; These messages are referenced by multiple .scr files.");
            builder.AppendLine("//==================================================");
            builder.AppendLine();
            foreach (var messageIndex in map.SharedMessageIndices)
            {
                if (map.SharedMessageOwners.TryGetValue(messageIndex, out var owners))
                {
                    builder.AppendLine($"; C{messageIndex:X8} used by: {string.Join(", ", owners)}");
                }

                blockWriter.WriteMessageBlock(builder, messageIndex);
            }

            File.WriteAllText(Path.Combine(outputDirectory, "_shared.txt"), builder.ToString(), Encoding.UTF8);
        }

        foreach (var script in map.Scripts)
        {
            var ownedMessages = script.MessageIndices.Where(x => !shared.Contains(x)).ToList();
            if (ownedMessages.Count == 0)
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(script.ScriptFile);
            var outputPath = Path.Combine(outputDirectory, $"{SanitizeFileName(name)}.txt");
            var builder = new StringBuilder();
            builder.AppendLine("//==================================================");
            builder.AppendLine($"; Dialogue from: {script.ScriptFile}");
            builder.AppendLine($"; Exclusive message count: {ownedMessages.Count}");
            builder.AppendLine($"; Shared messages are in _shared.txt");
            builder.AppendLine("//==================================================");
            builder.AppendLine();
            foreach (var messageIndex in ownedMessages)
            {
                blockWriter.WriteMessageBlock(builder, messageIndex);
            }

            File.WriteAllText(outputPath, builder.ToString(), Encoding.UTF8);
        }

        if (map.OrphanMessageIndices.Count > 0)
        {
            var builder = new StringBuilder();
            builder.AppendLine("//==================================================");
            builder.AppendLine($"; Orphan messages: {map.OrphanMessageIndices.Count}");
            builder.AppendLine("//==================================================");
            builder.AppendLine();
            foreach (var messageIndex in map.OrphanMessageIndices)
            {
                blockWriter.WriteMessageBlock(builder, messageIndex);
            }

            File.WriteAllText(Path.Combine(outputDirectory, "_orphan.txt"), builder.ToString(), Encoding.UTF8);
        }
    }

    public MergeResult Merge(string baseTextPath, string splitDirectory, string outputTextPath)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = 0;
        foreach (var file in Directory.EnumerateFiles(splitDirectory, "*.txt", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName))
        {
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                var key = TranslationKey(line);
                if (key is null)
                {
                    continue;
                }

                if (replacements.TryGetValue(key, out var existing) && existing != line)
                {
                    conflicts++;
                }

                replacements[key] = line;
            }
        }

        var replaced = 0;
        var missing = replacements.Keys.ToHashSet(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var line in File.ReadLines(baseTextPath, Encoding.UTF8))
        {
            var key = TranslationKey(line);
            if (key is not null && replacements.TryGetValue(key, out var replacement))
            {
                output.Add(replacement);
                replaced++;
                missing.Remove(key);
            }
            else
            {
                output.Add(line);
            }
        }

        File.WriteAllLines(outputTextPath, output, Encoding.UTF8);
        return new MergeResult(replacements.Count, replaced, missing.Count, conflicts);
    }

    private static string? TranslationKey(string line)
    {
        if (!line.StartsWith('◆'))
        {
            return null;
        }

        var second = line.IndexOf('◆', 1);
        if (second < 0)
        {
            return null;
        }

        var third = line.IndexOf('◆', second + 1);
        if (third < 0)
        {
            return line[..(second + 1)];
        }

        var kind = line[(second + 1)..third];
        return kind is "name" or "msg"
            ? line[..(third + 1)]
            : line[..(second + 1)];
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(ch, '_');
        }

        return value;
    }
}

public sealed class MessageScriptMap
{
    public int NameCount { get; set; }
    public int ChoiceCount { get; set; }
    public int MessageCount { get; set; }
    public int CommandCount { get; set; }
    public List<MessageScriptEntry> Scripts { get; } = [];
    public List<int> SharedMessageIndices { get; } = [];
    public Dictionary<int, List<string>> SharedMessageOwners { get; } = [];
    public List<int> OrphanMessageIndices { get; } = [];
}

public sealed class MessageScriptEntry
{
    public string ScriptFile { get; set; } = string.Empty;
    public List<int> CommandIndices { get; set; } = [];
    public List<int> MessageIndices { get; set; } = [];
}

public sealed record MergeResult(int Collected, int Replaced, int MissingInBase, int Conflicts);

internal sealed class MessageBlockWriter
{
    private readonly MessageDatDocument _message;
    private readonly IReadOnlyDictionary<int, int> _messageToName;
    private readonly IReadOnlyDictionary<int, string> _messageToBranch;

    public MessageBlockWriter(MessageDatDocument message)
    {
        _message = message;
        _messageToName = BuildMessageToNameMap(message);
        _messageToBranch = BuildMessageToBranchMap(message);
    }

    public string WriteNamesAndChoices()
    {
        var builder = new StringBuilder();
        WriteSectionHeader(builder, "Section 1: Names");
        for (var i = 0; i < _message.Names.Count; i++)
        {
            var text = Escape(_message.Names[i]);
            builder.AppendLine($"◇A{i:X8}◇{text}");
            builder.AppendLine($"◆A{i:X8}◆{text}");
            builder.AppendLine();
        }

        WriteSectionHeader(builder, "Section 2: Choices");
        for (var i = 0; i < _message.Choices.Count; i++)
        {
            var text = Escape(_message.Choices[i]);
            builder.AppendLine($"◇B{i:X8}◇{text}");
            builder.AppendLine($"◆B{i:X8}◆{text}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public string WriteCommands()
    {
        var builder = new StringBuilder();
        WriteSectionHeader(builder, "Section 4: Commands (For analysis only)");
        for (var i = 0; i < _message.Commands.Count; i++)
        {
            var command = _message.Commands[i];
            builder.AppendLine($"// Command[{i:D4}]: Id={command.Id}, Params=[{string.Join(", ", command.Params)}]");
        }

        return builder.ToString();
    }

    public void WriteMessageBlock(StringBuilder builder, int index)
    {
        if (_messageToName.TryGetValue(index, out var nameId))
        {
            var speaker = Escape(_message.Names[nameId]);
            builder.AppendLine($"◇C{index:X8}◇name◇{speaker}");
            builder.AppendLine($"◆C{index:X8}◆name◆{speaker}");
            builder.AppendLine();
        }

        var text = Escape(_message.Messages[index].Text);
        if (_messageToBranch.TryGetValue(index, out var branch))
        {
            builder.AppendLine($"◇C{index:X8}◇msg◇{branch}◇{text}");
            builder.AppendLine($"◆C{index:X8}◆msg◆{branch}◆{text}");
        }
        else
        {
            builder.AppendLine($"◇C{index:X8}◇msg◇{text}");
            builder.AppendLine($"◆C{index:X8}◆msg◆{text}");
        }

        builder.AppendLine();
    }

    private static IReadOnlyDictionary<int, int> BuildMessageToNameMap(MessageDatDocument document)
    {
        var result = new Dictionary<int, int>();
        foreach (var command in document.Commands)
        {
            if (command.Id < 0 || command.Id >= document.Names.Count)
            {
                continue;
            }

            foreach (var messageId in command.Params)
            {
                if (messageId >= 0 && messageId < document.Messages.Count)
                {
                    result[messageId] = command.Id;
                }
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<int, string> BuildMessageToBranchMap(MessageDatDocument document)
    {
        var result = new Dictionary<int, string>();
        foreach (var command in document.Commands)
        {
            if (command.Params.Count < 2)
            {
                continue;
            }

            for (var i = 0; i < command.Params.Count; i++)
            {
                var messageId = command.Params[i];
                if (messageId >= 0 && messageId < document.Messages.Count)
                {
                    result[messageId] = $"branch{i + 1:D2}";
                }
            }
        }

        return result;
    }

    private static void WriteSectionHeader(StringBuilder builder, string title)
    {
        builder.AppendLine("//==================================================");
        builder.AppendLine($"; {title}");
        builder.AppendLine("//==================================================");
    }

    private static string Escape(string value) =>
        value.Replace("\n", "\\n", StringComparison.Ordinal);
}
