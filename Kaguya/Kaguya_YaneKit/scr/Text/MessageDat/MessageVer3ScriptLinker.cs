// ============================================================================
// MessageVer3ScriptLinker.cs
// message ver3 与 [SCR-Ver5.x] .scr 的关联/拆分工具
//
// IDA 链路确认:
//   opcode 7 -> 文本运行时对象 -> sub_4289F0(..., Destination, ...)
//   sub_4289F0 再调用 CMessage::ReadBlock(sub_41EDD0), Destination 即 block index。
//
// ver3 没有 ver4 的 Commands 表，因此这里按 opcode 7 的首个 i32 直接建立
// .scr -> message block 映射。其余 dword 只作为站点元数据保留，避免误命名。
// ============================================================================
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Kaguya_YaneKit.Core;
using Kaguya_YaneKit.Text.MessageDat.Model;
using Kaguya_YaneKit.Script.Params;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Text.MessageDat;

public sealed class MessageVer3ScriptLinker
{
    private readonly ScrContainerCodec _scrCodec = new();
    private readonly MessageVer3TextCodec _textCodec = new();

    public MessageVer3ScriptMap BuildMap(MessageVer3Document message, string scrDirectory)
    {
        var result = new MessageVer3ScriptMap
        {
            BlockCount = message.Blocks.Count,
            ChoiceOrLabelBlockCount = message.Blocks.Count(IsChoiceOrLabelBlock),
            DialogueBlockCount = message.Blocks.Count(x => x.Items.Count > 0),
            MultiItemBlockCount = message.Blocks.Count(x => x.Items.Count > 1),
            VoiceBlockCount = message.Blocks.Count(x => x.Items.Any(i => i.Voices.Count > 0))
        };

        foreach (var file in Directory.EnumerateFiles(scrDirectory, "*.scr", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName))
        {
            var document = _scrCodec.Read(File.ReadAllBytes(file), Path.GetFileName(file));
            var entry = new MessageVer3ScriptEntry
            {
                ScriptFile = Path.GetFileName(file)
            };

            var offset = 0;
            foreach (var element in document.Script.Elements)
            {
                if (element is not ScriptInstruction instruction)
                {
                    if (element is ScriptTail tail)
                    {
                        offset += tail.Data.Length;
                    }
                    continue;
                }

                if (TryReadTextSite(instruction, offset, message.Blocks.Count, out var site))
                {
                    entry.TextSites.Add(site);
                    entry.BlockIndices.Add(site.BlockIndex);
                }

                offset += instruction.DeclaredLength;
            }

            entry.BlockIndices.Sort();
            entry.BlockIndices = entry.BlockIndices.Distinct().ToList();
            result.Scripts.Add(entry);
        }

        var referenced = result.Scripts.SelectMany(x => x.BlockIndices).ToHashSet();
        result.ReferencedBlockCount = referenced.Count;
        var ownerMap = new Dictionary<int, List<string>>();
        foreach (var script in result.Scripts)
        {
            foreach (var blockIndex in script.BlockIndices)
            {
                if (!ownerMap.TryGetValue(blockIndex, out var owners))
                {
                    owners = [];
                    ownerMap[blockIndex] = owners;
                }

                owners.Add(script.ScriptFile);
            }
        }

        foreach (var pair in ownerMap.OrderBy(x => x.Key))
        {
            if (pair.Value.Count > 1)
            {
                result.SharedBlockIndices.Add(pair.Key);
                result.SharedBlockOwners[pair.Key] = pair.Value.OrderBy(x => x, StringComparer.Ordinal).ToList();
            }
        }

        for (var i = 0; i < message.Blocks.Count; i++)
        {
            if (!referenced.Contains(i))
            {
                result.OrphanBlockIndices.Add(i);
            }
        }

        foreach (var script in result.Scripts)
        {
            foreach (var site in script.TextSites)
            {
                site.Semantic = Classify(message.Blocks[site.BlockIndex]);
            }
        }

        return result;
    }

    public void WriteMapJson(MessageVer3ScriptMap map, string outputPath)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        ReadableUnicodeJson.WriteAllText(outputPath, JsonSerializer.Serialize(map, options));
    }

    public void Split(MessageVer3Document message, MessageVer3ScriptMap map, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var shared = map.SharedBlockIndices.ToHashSet();
        if (map.SharedBlockIndices.Count > 0)
        {
            var builder = new StringBuilder();
            WriteHeader(builder, "Shared ver3 blocks");
            builder.AppendLine("; These blocks are referenced by multiple .scr files.");
            builder.AppendLine();
            foreach (var blockIndex in map.SharedBlockIndices)
            {
                if (map.SharedBlockOwners.TryGetValue(blockIndex, out var owners))
                {
                    builder.AppendLine($"; V3B{blockIndex:X4} used by: {string.Join(", ", owners)}");
                }
                builder.Append(_textCodec.WriteBlocks(message, [blockIndex]));
            }

            File.WriteAllText(Path.Combine(outputDirectory, "_shared.txt"), builder.ToString(), Encoding.UTF8);
        }

        foreach (var script in map.Scripts)
        {
            var ownedBlocks = script.BlockIndices
                .Where(x => !shared.Contains(x))
                .ToList();
            if (ownedBlocks.Count == 0)
            {
                continue;
            }

            var builder = new StringBuilder();
            WriteHeader(builder, $"Dialogue from: {script.ScriptFile}");
            builder.AppendLine($"; Exclusive block count: {ownedBlocks.Count}; shared blocks are in _shared.txt.");
            builder.AppendLine();
            var written = new HashSet<int>();
            foreach (var site in script.TextSites)
            {
                if (!shared.Contains(site.BlockIndex) && written.Add(site.BlockIndex))
                {
                    builder.Append(_textCodec.WriteBlocks(message, [site.BlockIndex]));
                }
            }

            File.WriteAllText(
                Path.Combine(outputDirectory, $"{SanitizeFileName(Path.GetFileNameWithoutExtension(script.ScriptFile))}.txt"),
                builder.ToString(),
                Encoding.UTF8);
        }

        var orphanTextBlocks = map.OrphanBlockIndices.ToList();
        if (orphanTextBlocks.Count > 0)
        {
            WriteBlockFile(
                Path.Combine(outputDirectory, "_orphan.txt"),
                "Orphan ver3 blocks",
                "These blocks were not referenced by opcode 7 in the supplied .scr directory.",
                message,
                orphanTextBlocks);
        }
    }

    private static bool TryReadTextSite(ScriptInstruction instruction, int offset, int blockCount, out MessageVer3TextSite site)
    {
        site = new MessageVer3TextSite();
        var body = instruction.Body;
        if (instruction.Opcode != 7 || body.Length < 4)
        {
            return false;
        }

        var blockIndex = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0, 4));
        if (blockIndex < 0 || blockIndex >= blockCount)
        {
            return false;
        }

        site = new MessageVer3TextSite
        {
            Offset = offset,
            BodyLength = body.Length,
            BlockIndex = blockIndex,
            Arg04 = ReadI32OrNull(body, 4),
            Arg08 = ReadI32OrNull(body, 8),
            Arg0C = ReadI32OrNull(body, 12),
            Arg10 = ReadI32OrNull(body, 16),
            Arg14 = ReadI32OrNull(body, 20),
            Tail = ReadI32OrNull(body, body.Length - 4)
        };
        return true;
    }

    private static int? ReadI32OrNull(byte[] body, int offset) =>
        offset >= 0 && body.Length >= offset + 4
            ? BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4))
            : null;

    private static bool IsChoiceOrLabelBlock(MessageVer3Block block) =>
        block.Items.Count == 0;

    private static string Classify(MessageVer3Block block)
    {
        if (block.Items.Count == 0)
        {
            return string.IsNullOrEmpty(block.FormatName) ? "empty-label" : "choice-or-label";
        }

        var hasVoice = block.Items.Any(x => x.Voices.Count > 0);
        var hasName = !string.IsNullOrEmpty(block.FormatName);
        var prefix = hasName ? "dialogue" : "narration";
        if (block.Items.Count > 1)
        {
            prefix += "-multi-item";
        }
        if (hasVoice)
        {
            prefix += "-voice";
        }
        return prefix;
    }

    private void WriteBlockFile(
        string path,
        string title,
        string note,
        MessageVer3Document message,
        IReadOnlyList<int> blockIndices)
    {
        var builder = new StringBuilder();
        WriteHeader(builder, title);
        builder.AppendLine($"; {note}");
        builder.AppendLine();
        builder.Append(_textCodec.WriteBlocks(message, blockIndices));
        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static void WriteHeader(StringBuilder builder, string title)
    {
        builder.AppendLine("//==================================================");
        builder.AppendLine($"; {title}");
        builder.AppendLine("//==================================================");
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

public sealed class MessageVer3ScriptMap
{
    public int BlockCount { get; set; }
    public int ReferencedBlockCount { get; set; }
    public int ChoiceOrLabelBlockCount { get; set; }
    public int DialogueBlockCount { get; set; }
    public int MultiItemBlockCount { get; set; }
    public int VoiceBlockCount { get; set; }
    public List<MessageVer3ScriptEntry> Scripts { get; } = [];
    public List<int> SharedBlockIndices { get; } = [];
    public Dictionary<int, List<string>> SharedBlockOwners { get; } = [];
    public List<int> OrphanBlockIndices { get; } = [];
}

public sealed class MessageVer3ScriptEntry
{
    public string ScriptFile { get; set; } = string.Empty;
    public List<MessageVer3TextSite> TextSites { get; } = [];
    public List<int> BlockIndices { get; set; } = [];
}

public sealed class MessageVer3TextSite
{
    public int Offset { get; set; }
    public int BodyLength { get; set; }
    public int BlockIndex { get; set; }
    public int? Arg04 { get; set; }
    public int? Arg08 { get; set; }
    public int? Arg0C { get; set; }
    public int? Arg10 { get; set; }
    public int? Arg14 { get; set; }
    public int? Tail { get; set; }
    public string Semantic { get; set; } = string.Empty;
}
