// ============================================================================
// MessageScriptLinker.cs
// 消息与 .scr 脚本的关联/拆分/合并工具
//
// 功能概述:
//   BuildMap()  - 扫描 scrDirectory 下所有 .scr 文件, 解析 opcode=7 指令
//                 提取 commandIndex, 建立脚本->命令->消息的完整映射
//                 同时检测共享消息 (多脚本引用) 和孤立消息 (无脚本引用)
//   Split()     - 按映射将 message.dat 拆分为多个文本文件:
//                 _names.txt, _commands.txt, _shared.txt, _orphan.txt,
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
using Kaguya_YaneKit.Text.MessageDat.Model;
using Kaguya_YaneKit.Script.Params;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Text.MessageDat;

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

            var instructions = document.Script.Instructions.ToList();
            var commandToMessages = new Dictionary<int, List<int>>();
            for (var i = 0; i < message.Commands.Count; i++)
            {
                commandToMessages[i] = message.Commands[i].Params
                    .Where(messageIndex => messageIndex >= 0 && messageIndex < message.Messages.Count)
                    .ToList();
            }

            var choiceRanges = BuildChoiceRanges(document.Script.Elements, commandToMessages, message.Choices.Count);

            foreach (var instruction in instructions)
            {
                if (instruction.Opcode == 7 && instruction.Body.Length >= 4)
                {
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
                            entry.Items.Add(MessageScriptItem.Message(messageIndex));
                        }
                    }
                }
                else if (instruction.Opcode == 8)
                {
                    var choiceIndices = ReadMenuChoiceIndices(instruction.Body, message.Choices.Count)
                        .Distinct()
                        .ToList();
                    if (choiceIndices.Count == 0)
                    {
                        continue;
                    }

                    foreach (var choiceIndex in choiceIndices)
                    {
                        entry.ChoiceIndices.Add(choiceIndex);
                    }

                    entry.Items.Add(MessageScriptItem.ChoiceGroup(choiceIndices));
                    foreach (var choiceIndex in choiceIndices)
                    {
                        if (choiceRanges.TryGetValue(choiceIndex, out var range))
                        {
                            entry.ChoiceRanges[choiceIndex] = range;
                        }
                    }
                }
            }

            entry.CommandIndices.Sort();
            entry.MessageIndices.Sort();
            entry.ChoiceIndices.Sort();
            entry.CommandIndices = entry.CommandIndices.Distinct().ToList();
            entry.MessageIndices = entry.MessageIndices.Distinct().ToList();
            entry.ChoiceIndices = entry.ChoiceIndices.Distinct().ToList();
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

        File.WriteAllText(Path.Combine(outputDirectory, "_names.txt"), blockWriter.WriteNames(), Encoding.UTF8);
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
            if (ownedMessages.Count == 0 && script.ChoiceIndices.Count == 0)
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(script.ScriptFile);
            var outputPath = Path.Combine(outputDirectory, $"{SanitizeFileName(name)}.txt");
            var builder = new StringBuilder();
            builder.AppendLine("//==================================================");
            builder.AppendLine($"; Dialogue from: {script.ScriptFile}");
            builder.AppendLine($"; Exclusive message count: {ownedMessages.Count}");
            builder.AppendLine($"; Choice count: {script.ChoiceIndices.Count}");
            builder.AppendLine($"; Shared messages are in _shared.txt");
            builder.AppendLine("//==================================================");
            builder.AppendLine();
            var writtenMessages = new HashSet<int>();
            var writtenChoices = new HashSet<int>();
            foreach (var item in script.Items)
            {
                if (item.Kind == "message")
                {
                    if (!shared.Contains(item.Index) && writtenMessages.Add(item.Index))
                    {
                        blockWriter.WriteMessageBlock(builder, item.Index);
                    }
                }
                else if (item.Kind == "choice-group")
                {
                    var choiceIndices = item.Indices
                        .Where(index => writtenChoices.Add(index))
                        .ToList();
                    if (choiceIndices.Count > 0)
                    {
                        blockWriter.WriteChoiceGroup(builder, choiceIndices, script.ChoiceRanges);
                    }
                }
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
            var fileName = Path.GetFileName(file);
            if (fileName is "_base_message.txt" or "_base_message_ver3.txt")
            {
                continue;
            }

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
        if (line.StartsWith("◆V3B", StringComparison.Ordinal))
        {
            return line[..(third + 1)];
        }

        return kind is "name" or "msg"
            ? line[..(third + 1)]
            : line[..(second + 1)];
    }

    private static IEnumerable<int> ReadMenuChoiceIndices(byte[] body, int choiceCount)
    {
        if (body.Length < 8)
        {
            yield break;
        }

        var first = (int)(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4)) >> 16);
        if (first >= 0 && first < choiceCount)
        {
            yield return first;
        }

        var second = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6, 2));
        if (second < choiceCount)
        {
            yield return second;
        }
    }

    private static Dictionary<int, ChoiceMessageRange> BuildChoiceRanges(
        IReadOnlyList<ScriptElement> elements,
        IReadOnlyDictionary<int, List<int>> commandToMessages,
        int choiceCount)
    {
        var ranges = new Dictionary<int, ChoiceMessageRange>();
        var instructions = elements.OfType<ScriptInstruction>().ToList();
        var labelToInstructionIndex = BuildLabelToInstructionIndex(elements);
        var instructionOffsets = BuildInstructionOffsets(elements);

        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.Opcode != 8)
            {
                continue;
            }

            var choices = ReadMenuChoiceIndices(instruction.Body, choiceCount)
                .Distinct()
                .ToList();
            if (choices.Count == 0)
            {
                continue;
            }

            AddBinaryChoiceRanges(ranges, instructions, instructionOffsets, labelToInstructionIndex, i, choices, commandToMessages);
        }

        return ranges;
    }

    private static Dictionary<string, int> BuildLabelToInstructionIndex(IReadOnlyList<ScriptElement> elements)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var instructionIndex = 0;
        foreach (var element in elements)
        {
            switch (element)
            {
                case ScriptLabel label:
                    result[label.Name] = instructionIndex;
                    break;
                case ScriptInstruction:
                    instructionIndex++;
                    break;
            }
        }

        return result;
    }

    private static List<int> BuildInstructionOffsets(IReadOnlyList<ScriptElement> elements)
    {
        var result = new List<int>();
        var offset = 0;
        foreach (var element in elements)
        {
            switch (element)
            {
                case ScriptInstruction instruction:
                    result.Add(offset);
                    offset += instruction.DeclaredLength;
                    break;
                case ScriptTail tail:
                    offset += tail.Data.Length;
                    break;
            }
        }

        return result;
    }

    private static void AddBinaryChoiceRanges(
        Dictionary<int, ChoiceMessageRange> ranges,
        IReadOnlyList<ScriptInstruction> instructions,
        IReadOnlyList<int> instructionOffsets,
        IReadOnlyDictionary<string, int> labelToInstructionIndex,
        int menuInstructionIndex,
        IReadOnlyList<int> choices,
        IReadOnlyDictionary<int, List<int>> commandToMessages)
    {
        if (choices.Count != 2)
        {
            return;
        }

        var conditionIndex = FindChoiceCondition(instructions, menuInstructionIndex + 1);
        if (conditionIndex < 0)
        {
            return;
        }

        var condition = instructions[conditionIndex];
        if (!TryResolveBranchTarget(instructions, instructionOffsets, labelToInstructionIndex, condition, BranchTargetMode.Containing, out var targetStart))
        {
            return;
        }

        var compareChoiceOrdinal = ReadCompareChoiceOrdinal(instructions, menuInstructionIndex + 1, conditionIndex);
        if (compareChoiceOrdinal < 0 || compareChoiceOrdinal >= choices.Count)
        {
            return;
        }

        var fallthroughStart = conditionIndex + 1;
        var jumpIndex = FindBranchExitJump(
            instructions,
            instructionOffsets,
            labelToInstructionIndex,
            fallthroughStart,
            targetStart);
        var joinIndex = instructions.Count;
        if (jumpIndex >= 0 &&
            TryResolveBranchTarget(instructions, instructionOffsets, labelToInstructionIndex, instructions[jumpIndex], BranchTargetMode.NextStart, out var resolvedJoin))
        {
            joinIndex = resolvedJoin;
        }

        var otherChoiceOrdinal = compareChoiceOrdinal == 0 ? 1 : 0;
        var fallthroughChoice = condition.Opcode == 15 ? choices[compareChoiceOrdinal] : choices[otherChoiceOrdinal];
        var targetChoice = condition.Opcode == 15 ? choices[otherChoiceOrdinal] : choices[compareChoiceOrdinal];

        AddChoiceRange(ranges, fallthroughChoice, instructions, fallthroughStart, jumpIndex >= 0 ? jumpIndex : targetStart, commandToMessages);
        AddChoiceRange(ranges, targetChoice, instructions, targetStart, joinIndex, commandToMessages);
    }

    private static bool TryResolveBranchTarget(
        IReadOnlyList<ScriptInstruction> instructions,
        IReadOnlyList<int> instructionOffsets,
        IReadOnlyDictionary<string, int> labelToInstructionIndex,
        ScriptInstruction instruction,
        BranchTargetMode mode,
        out int instructionIndex)
    {
        instructionIndex = -1;
        if (instruction.TargetLabel is not null &&
            labelToInstructionIndex.TryGetValue(instruction.TargetLabel, out instructionIndex))
        {
            return true;
        }

        if (!ScrOpcodeInfo.TryGetPcTargetOffset(instruction.Opcode, instruction.Body.Length, out var operandOffset))
        {
            return false;
        }

        var pc = BinaryPrimitives.ReadUInt32LittleEndian(instruction.Body.AsSpan(operandOffset, 4));
        if (pc > int.MaxValue)
        {
            return false;
        }

        instructionIndex = mode == BranchTargetMode.Containing
            ? FindContainingInstruction(instructionOffsets, (int)pc)
            : FindNextInstructionStart(instructionOffsets, (int)pc);
        return instructionIndex >= 0 && instructionIndex < instructions.Count;
    }

    private static int FindContainingInstruction(IReadOnlyList<int> instructionOffsets, int pc)
    {
        for (var i = 0; i < instructionOffsets.Count; i++)
        {
            var start = instructionOffsets[i];
            var end = i + 1 < instructionOffsets.Count ? instructionOffsets[i + 1] : int.MaxValue;
            if (pc >= start && pc < end)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindNextInstructionStart(IReadOnlyList<int> instructionOffsets, int pc)
    {
        for (var i = 0; i < instructionOffsets.Count; i++)
        {
            if (instructionOffsets[i] >= pc)
            {
                return i;
            }
        }

        return instructionOffsets.Count;
    }

    private static int FindChoiceCondition(IReadOnlyList<ScriptInstruction> instructions, int startIndex)
    {
        for (var i = startIndex; i < instructions.Count; i++)
        {
            if (instructions[i].Opcode == 7 || instructions[i].Opcode == 8)
            {
                return -1;
            }

            if (instructions[i].Opcode is 14 or 15)
            {
                return i;
            }
        }

        return -1;
    }

    private static int ReadCompareChoiceOrdinal(IReadOnlyList<ScriptInstruction> instructions, int startIndex, int endIndex)
    {
        for (var i = endIndex - 1; i >= startIndex; i--)
        {
            var instruction = instructions[i];
            if (instruction.Opcode == 13 && instruction.Body.Length >= 8)
            {
                return BinaryPrimitives.ReadInt32LittleEndian(instruction.Body.AsSpan(4, 4));
            }
        }

        return -1;
    }

    private static int FindBranchExitJump(
        IReadOnlyList<ScriptInstruction> instructions,
        IReadOnlyList<int> instructionOffsets,
        IReadOnlyDictionary<string, int> labelToInstructionIndex,
        int startIndex,
        int stopIndex)
    {
        var end = Math.Min(stopIndex, instructions.Count);
        for (var i = startIndex; i < end; i++)
        {
            if (instructions[i].Opcode == 11 &&
                TryResolveBranchTarget(instructions, instructionOffsets, labelToInstructionIndex, instructions[i], BranchTargetMode.NextStart, out var jumpTarget) &&
                jumpTarget > stopIndex)
            {
                return i;
            }
        }

        return -1;
    }

    private static void AddChoiceRange(
        Dictionary<int, ChoiceMessageRange> ranges,
        int choiceIndex,
        IReadOnlyList<ScriptInstruction> instructions,
        int startIndex,
        int stopIndex,
        IReadOnlyDictionary<int, List<int>> commandToMessages)
    {
        var messages = CollectMessageIndices(instructions, startIndex, stopIndex, commandToMessages);
        if (messages.Count == 0)
        {
            return;
        }

        ranges[choiceIndex] = new ChoiceMessageRange(messages.Min(), messages.Max());
    }

    private static List<int> CollectMessageIndices(
        IReadOnlyList<ScriptInstruction> instructions,
        int startIndex,
        int stopIndex,
        IReadOnlyDictionary<int, List<int>> commandToMessages)
    {
        var result = new List<int>();
        var end = Math.Min(stopIndex, instructions.Count);
        for (var i = Math.Max(startIndex, 0); i < end; i++)
        {
            if (TryReadCommandIndex(instructions[i], out var commandIndex)
                && commandToMessages.TryGetValue(commandIndex, out var messages))
            {
                result.AddRange(messages);
            }
        }

        return result;
    }

    private static bool TryReadCommandIndex(ScriptInstruction instruction, out int commandIndex)
    {
        commandIndex = -1;
        if (instruction.Opcode != 7 || instruction.Body.Length < 4)
        {
            return false;
        }

        commandIndex = BinaryPrimitives.ReadInt32LittleEndian(instruction.Body.AsSpan(0, 4));
        return commandIndex >= 0;
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
    public List<int> ChoiceIndices { get; set; } = [];
    public Dictionary<int, ChoiceMessageRange> ChoiceRanges { get; set; } = [];
    public List<MessageScriptItem> Items { get; } = [];
}

public sealed record MessageScriptItem(string Kind, int Index, List<int> Indices)
{
    public static MessageScriptItem Message(int index) => new("message", index, []);

    public static MessageScriptItem ChoiceGroup(IEnumerable<int> indices) =>
        new("choice-group", -1, indices.ToList());
}

public sealed record ChoiceMessageRange(int StartMessageIndex, int EndMessageIndex);

public sealed record MergeResult(int Collected, int Replaced, int MissingInBase, int Conflicts);

internal enum BranchTargetMode
{
    Containing,
    NextStart
}

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

    public string WriteNames()
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

        return builder.ToString();
    }

    public void WriteChoiceGroup(
        StringBuilder builder,
        IReadOnlyList<int> indices,
        IReadOnlyDictionary<int, ChoiceMessageRange>? ranges = null)
    {
        foreach (var index in indices)
        {
            ChoiceMessageRange? range = null;
            if (ranges is not null)
            {
                ranges.TryGetValue(index, out range);
            }

            WriteChoiceBlock(builder, index, range);
        }
    }

    private void WriteChoiceBlock(StringBuilder builder, int index, ChoiceMessageRange? range)
    {
        var text = Escape(_message.Choices[index]);
        if (range is not null)
        {
            builder.AppendLine($"// choice-range: C{range.StartMessageIndex:X8} -> C{range.EndMessageIndex:X8}");
        }
        builder.AppendLine($"◇B{index:X8}◇choice◇{text}");
        builder.AppendLine($"◆B{index:X8}◆choice◆{text}");
        builder.AppendLine();
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
