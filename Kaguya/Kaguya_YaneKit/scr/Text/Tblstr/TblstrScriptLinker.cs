using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Kaguya_YaneKit.Core;
using Kaguya_YaneKit.Script.Tblstr;

namespace Kaguya_YaneKit.Text.Tblstr;

public sealed class TblstrScriptLinker
{
    private readonly TblstrScrCodec _scrCodec = new();

    public TblstrScriptMap BuildMap(TblstrDocument document, string scrDirectory)
    {
        var map = new TblstrScriptMap
        {
            EntryCount = document.Entries.Count
        };

        foreach (var file in Directory.EnumerateFiles(scrDirectory, "*.scr", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var script = new TblstrScriptEntry
            {
                ScriptFile = Path.GetFileName(file)
            };

            var scr = _scrCodec.Read(File.ReadAllBytes(file), Path.GetFileName(file), TblstrScrCodec.TryReadSiblingLabels(file));
            var currentChoiceRange = -1;
            TblstrChoiceRange? activeChoiceRange = null;
            for (var instructionIndex = 0; instructionIndex < scr.Instructions.Count; instructionIndex++)
            {
                var instruction = scr.Instructions[instructionIndex];
                var baseSpan = instruction.RawBytes.AsSpan(0, instruction.BaseLength);
                switch (instruction.Opcode)
                {
                    case 9:
                        currentChoiceRange++;
                        activeChoiceRange = new TblstrChoiceRange
                        {
                            RangeId = currentChoiceRange,
                            StartOffset = instruction.Offset
                        };
                        script.ChoiceRanges.Add(activeChoiceRange);
                        break;

                    case 10 when baseSpan.Length >= 8:
                    {
                        if (activeChoiceRange is null)
                        {
                            currentChoiceRange++;
                            activeChoiceRange = new TblstrChoiceRange
                            {
                                RangeId = currentChoiceRange,
                                StartOffset = instruction.Offset
                            };
                            script.ChoiceRanges.Add(activeChoiceRange);
                        }

                        var choiceIndex = I32(baseSpan, 4);
                        var site = new TblstrTextSite
                        {
                            Offset = instruction.Offset,
                            Kind = "choice",
                            Index = choiceIndex,
                            ChoiceId = baseSpan[2],
                            ChoiceRangeId = activeChoiceRange.RangeId
                        };
                        script.TextSites.Add(site);
                        activeChoiceRange.ChoiceIndices.Add(choiceIndex);
                        activeChoiceRange.Choices.Add(new TblstrChoiceEntry
                        {
                            Offset = instruction.Offset,
                            ChoiceId = baseSpan[2],
                            TextIndex = choiceIndex
                        });
                        AddIndex(map.ChoiceIndices, choiceIndex, document.Entries.Count);
                        break;
                    }

                    case 11 when baseSpan.Length >= 4:
                        if (activeChoiceRange is not null)
                        {
                            activeChoiceRange.EndOffset = instruction.Offset;
                            FillChoiceBranchRanges(activeChoiceRange, scr.Instructions, instructionIndex + 1, document.Entries.Count);
                            AttachChoiceBranchRanges(script, activeChoiceRange);
                            activeChoiceRange = null;
                        }
                        break;

                    case 19 when baseSpan.Length >= 12:
                    {
                        var speakerIndex = I32(baseSpan, 4);
                        var messageIndex = I32(baseSpan, 8);
                        var site = new TblstrTextSite
                        {
                            Offset = instruction.Offset,
                            Kind = "message",
                            Index = messageIndex,
                            SpeakerIndex = IsValidIndex(speakerIndex, document.Entries.Count) ? speakerIndex : null
                        };
                        script.TextSites.Add(site);
                        AddIndex(map.NameIndices, speakerIndex, document.Entries.Count);
                        AddIndex(map.MessageIndices, messageIndex, document.Entries.Count);

                        if (baseSpan.Length >= 16)
                        {
                            var alternateIndex = I32(baseSpan, 12);
                            if (AddIndex(map.MessageIndices, alternateIndex, document.Entries.Count))
                            {
                                script.TextSites.Add(new TblstrTextSite
                                {
                                    Offset = instruction.Offset,
                                    Kind = "alternate-message",
                                    Index = alternateIndex,
                                    SpeakerIndex = IsValidIndex(speakerIndex, document.Entries.Count) ? speakerIndex : null
                                });
                            }
                        }
                        break;
                    }
                }
            }

            var referencedIndices = new List<int>();
            foreach (var site in script.TextSites)
            {
                if (site.SpeakerIndex is int speakerIndex)
                {
                    referencedIndices.Add(speakerIndex);
                }

                referencedIndices.Add(site.Index);
            }

            script.ReferencedIndices = referencedIndices
                .Where(index => index >= 0 && index < document.Entries.Count)
                .Distinct()
                .OrderBy(index => index)
                .ToList();
            map.Scripts.Add(script);
        }

        var referenced = map.NameIndices
            .Concat(map.MessageIndices)
            .Concat(map.ChoiceIndices)
            .ToHashSet();
        for (var i = 0; i < document.Entries.Count; i++)
        {
            if (!referenced.Contains(i))
            {
                map.UnreferencedIndices.Add(i);
            }
        }

        return map;
    }

    public void WriteMapJson(TblstrScriptMap map, string outputPath)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        ReadableUnicodeJson.WriteAllText(outputPath, JsonSerializer.Serialize(map, options));
    }

    public void Split(TblstrDocument document, TblstrScriptMap map, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var script in map.Scripts)
        {
            if (script.TextSites.Count == 0)
            {
                continue;
            }

            var builder = new StringBuilder();
            WriteHeader(builder, $"TBLSTR from: {script.ScriptFile}");
            foreach (var site in script.TextSites)
            {
                if (site.Kind == "choice")
                {
                    TblstrTextWriter.WriteChoice(builder, document, site.Index, site.ChoiceBranchRange);
                }
                else
                {
                    if (site.SpeakerIndex is int speakerIndex)
                    {
                        TblstrTextWriter.WriteDialogueName(builder, document, speakerIndex);
                    }

                    TblstrTextWriter.WriteDialogueMessage(builder, document, site.Index, site.Kind);
                    builder.AppendLine();
                }
            }

            File.WriteAllText(
                Path.Combine(outputDirectory, $"{SanitizeFileName(Path.GetFileNameWithoutExtension(script.ScriptFile))}.txt"),
                builder.ToString(),
                Encoding.UTF8);
        }

        if (map.UnreferencedIndices.Count > 0)
        {
            var builder = new StringBuilder();
            WriteHeader(builder, "Unreferenced TBLSTR entries");
            builder.AppendLine("; These entries were not referenced by opcode 10/19 in the supplied .scr directory.");
            builder.AppendLine();
            foreach (var index in map.UnreferencedIndices)
            {
                TblstrTextWriter.WriteUnknown(builder, document, index);
            }

            File.WriteAllText(Path.Combine(outputDirectory, "_unreferenced.txt"), builder.ToString(), Encoding.UTF8);
        }
    }

    private static bool AddIndex(HashSet<int> indices, int index, int entryCount)
    {
        if (!IsValidIndex(index, entryCount))
        {
            return false;
        }

        indices.Add(index);
        return true;
    }

    private static void FillChoiceBranchRanges(
        TblstrChoiceRange choiceRange,
        IReadOnlyList<TblstrScrInstruction> instructions,
        int startIndex,
        int entryCount)
    {
        if (choiceRange.Choices.Count < 2 || startIndex < 0 || startIndex >= instructions.Count)
        {
            return;
        }

        var offsetToIndex = instructions
            .Select((instruction, index) => new { instruction.Offset, Index = index })
            .ToDictionary(item => item.Offset, item => item.Index);
        var remaining = choiceRange.Choices.ToDictionary(choice => choice.ChoiceId);
        var currentIndex = startIndex;
        int? joinOffset = null;
        var visitedOffsets = new HashSet<int>();

        while (remaining.Count > 1
               && currentIndex >= 0
               && currentIndex < instructions.Count
               && visitedOffsets.Add(instructions[currentIndex].Offset))
        {
            var instruction = instructions[currentIndex];
            if (!TryReadIfNotEqualChoice(instruction, remaining.Keys, out var choiceId, out var nextBranchOffset))
            {
                break;
            }

            var blockStartIndex = currentIndex + 1;
            var blockEndIndex = FindBranchBlockEnd(instructions, blockStartIndex, nextBranchOffset, out var blockJoinOffset);
            if (blockJoinOffset is int detectedJoinOffset)
            {
                joinOffset ??= detectedJoinOffset;
            }

            AddChoiceBranchRange(choiceRange, remaining[choiceId], instructions, blockStartIndex, blockEndIndex, entryCount);
            remaining.Remove(choiceId);

            if (!offsetToIndex.TryGetValue(nextBranchOffset, out currentIndex))
            {
                return;
            }

            if (joinOffset is int knownJoinOffset && instructions[currentIndex].Offset == knownJoinOffset)
            {
                return;
            }
        }

        if (remaining.Count != 1 || currentIndex < 0 || currentIndex >= instructions.Count)
        {
            return;
        }

        var lastChoice = remaining.Values.Single();
        var lastEndIndex = joinOffset is int finalJoinOffset && offsetToIndex.TryGetValue(finalJoinOffset, out var joinIndex)
            ? joinIndex
            : FindBranchBlockEnd(instructions, currentIndex, null, out _);
        AddChoiceBranchRange(choiceRange, lastChoice, instructions, currentIndex, lastEndIndex, entryCount);
    }

    private static bool TryReadIfNotEqualChoice(
        TblstrScrInstruction instruction,
        IEnumerable<int> choiceIds,
        out int choiceId,
        out int targetOffset)
    {
        choiceId = 0;
        targetOffset = 0;
        if (instruction.Opcode != 4 || instruction.BaseLength < 16)
        {
            return false;
        }

        var baseSpan = instruction.RawBytes.AsSpan(0, instruction.BaseLength);
        var left = I32(baseSpan, 4);
        var right = I32(baseSpan, 8);
        var ids = choiceIds.ToHashSet();
        var candidates = new List<int>();
        if (ids.Contains(left))
        {
            candidates.Add(left);
        }

        if (ids.Contains(right) && !candidates.Contains(right))
        {
            candidates.Add(right);
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        choiceId = candidates[0];
        targetOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(baseSpan.Slice(12, 4)));
        return true;
    }

    private static int FindBranchBlockEnd(
        IReadOnlyList<TblstrScrInstruction> instructions,
        int startIndex,
        int? stopOffset,
        out int? jumpTargetOffset)
    {
        jumpTargetOffset = null;
        for (var i = startIndex; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (stopOffset is int stop && instruction.Offset == stop)
            {
                return i;
            }

            if (instruction.Opcode == 9)
            {
                return i;
            }

            if (instruction.Opcode == 2 && instruction.BaseLength >= 8)
            {
                var baseSpan = instruction.RawBytes.AsSpan(0, instruction.BaseLength);
                jumpTargetOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(baseSpan.Slice(4, 4)));
                return i;
            }
        }

        return instructions.Count;
    }

    private static void AddChoiceBranchRange(
        TblstrChoiceRange choiceRange,
        TblstrChoiceEntry choice,
        IReadOnlyList<TblstrScrInstruction> instructions,
        int startIndex,
        int endIndex,
        int entryCount)
    {
        var messageIndices = new List<int>();
        var end = Math.Min(endIndex, instructions.Count);
        for (var i = Math.Max(startIndex, 0); i < end; i++)
        {
            var instruction = instructions[i];
            if (instruction.Opcode != 19 || instruction.BaseLength < 12)
            {
                continue;
            }

            var baseSpan = instruction.RawBytes.AsSpan(0, instruction.BaseLength);
            AddMessageIndex(messageIndices, I32(baseSpan, 8), entryCount);
            if (baseSpan.Length >= 16)
            {
                AddMessageIndex(messageIndices, I32(baseSpan, 12), entryCount);
            }
        }

        if (messageIndices.Count == 0)
        {
            return;
        }

        choiceRange.BranchRanges.Add(new TblstrChoiceBranchRange
        {
            ChoiceId = choice.ChoiceId,
            ChoiceIndex = choice.TextIndex,
            StartOffset = startIndex >= 0 && startIndex < instructions.Count ? instructions[startIndex].Offset : choice.Offset,
            EndOffset = end > startIndex && end - 1 < instructions.Count ? instructions[end - 1].Offset : null,
            StartMessageIndex = messageIndices[0],
            EndMessageIndex = messageIndices[^1],
            MessageIndices = messageIndices
        });
    }

    private static void AddMessageIndex(List<int> messageIndices, int index, int entryCount)
    {
        if (IsValidIndex(index, entryCount))
        {
            messageIndices.Add(index);
        }
    }

    private static void AttachChoiceBranchRanges(TblstrScriptEntry script, TblstrChoiceRange choiceRange)
    {
        foreach (var site in script.TextSites)
        {
            if (site.Kind != "choice" || site.ChoiceRangeId != choiceRange.RangeId || site.ChoiceId is not int choiceId)
            {
                continue;
            }

            site.ChoiceBranchRange = choiceRange.BranchRanges.FirstOrDefault(range => range.ChoiceId == choiceId);
        }
    }

    private static bool IsValidIndex(int index, int entryCount) =>
        index >= 0 && index < entryCount;

    private static int I32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));

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

public sealed class TblstrScriptMap
{
    public int EntryCount { get; set; }
    public HashSet<int> NameIndices { get; } = [];
    public HashSet<int> MessageIndices { get; } = [];
    public HashSet<int> ChoiceIndices { get; } = [];
    public List<int> UnreferencedIndices { get; } = [];
    public List<TblstrScriptEntry> Scripts { get; } = [];
}

public sealed class TblstrScriptEntry
{
    public string ScriptFile { get; set; } = "";
    public List<TblstrTextSite> TextSites { get; } = [];
    public List<TblstrChoiceRange> ChoiceRanges { get; } = [];
    public List<int> ReferencedIndices { get; set; } = [];
}

public sealed class TblstrTextSite
{
    public int Offset { get; set; }
    public string Kind { get; set; } = "";
    public int Index { get; set; }
    public int? SpeakerIndex { get; set; }
    public int? ChoiceId { get; set; }
    public int? ChoiceRangeId { get; set; }
    public TblstrChoiceBranchRange? ChoiceBranchRange { get; set; }
}

public sealed class TblstrChoiceRange
{
    public int RangeId { get; set; }
    public int StartOffset { get; set; }
    public int? EndOffset { get; set; }
    public List<int> ChoiceIndices { get; } = [];
    public List<TblstrChoiceEntry> Choices { get; } = [];
    public List<TblstrChoiceBranchRange> BranchRanges { get; } = [];
}

public sealed class TblstrChoiceEntry
{
    public int Offset { get; set; }
    public int ChoiceId { get; set; }
    public int TextIndex { get; set; }
}

public sealed class TblstrChoiceBranchRange
{
    public int ChoiceId { get; set; }
    public int ChoiceIndex { get; set; }
    public int StartOffset { get; set; }
    public int? EndOffset { get; set; }
    public int StartMessageIndex { get; set; }
    public int EndMessageIndex { get; set; }
    public List<int> MessageIndices { get; set; } = [];
}
