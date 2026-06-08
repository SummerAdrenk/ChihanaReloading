using Kaguya_YaneKit.Script.Tblstr;

namespace Kaguya_YaneKit.Formats.Character;

internal static class TblstrSpPlanBuilder
{
    private const int FirstAdvSpSlot = 7;
    private const int LastAdvSpSlot = 11;

    public static CharacterComposer.SpCompositionPlan[] BuildPlansFromScrDirectory(
        string scrDirectory,
        IReadOnlyDictionary<string, CharacterComposer.LayerAsset> staticAssets,
        IReadOnlyDictionary<string, CharacterComposer.LayerAsset> animatedAssets,
        ISet<string> usedStaticKeys,
        ISet<string> usedAnimatedKeys,
        CharacterComposeResult result)
    {
        if (!Directory.Exists(scrDirectory))
        {
            return [];
        }

        var codec = new TblstrScrCodec();
        var groups = new List<CharacterComposer.SpLayerGroup>();
        var planIndex = 0;

        foreach (var file in Directory.GetFiles(scrDirectory, "*.scr", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var input = File.ReadAllBytes(file);
            if (!TblstrScrCodec.IsTblstrScr(input))
            {
                continue;
            }

            var document = codec.Read(input, Path.GetFileName(file), TblstrScrCodec.TryReadSiblingLabels(file));
            BuildScriptGroups(
                document,
                staticAssets,
                animatedAssets,
                result,
                groups,
                ref planIndex);
        }

        return CharacterComposer.BuildSpPlansFromLayerGroups(groups, usedStaticKeys, usedAnimatedKeys).ToArray();
    }

    private static void BuildScriptGroups(
        TblstrScrDocument document,
        IReadOnlyDictionary<string, CharacterComposer.LayerAsset> staticAssets,
        IReadOnlyDictionary<string, CharacterComposer.LayerAsset> animatedAssets,
        CharacterComposeResult result,
        List<CharacterComposer.SpLayerGroup> groups,
        ref int planIndex)
    {
        var slots = new SortedDictionary<int, SpriteSlotState>();
        var pendingOffsets = new Dictionary<int, (int X, int Y)>();

        foreach (var instruction in document.Instructions)
        {
            var raw = instruction.RawBytes.AsSpan(0, Math.Min(instruction.BaseLength, instruction.RawBytes.Length));
            switch (instruction.Opcode)
            {
                case 23 when raw.Length >= 3:
                    ClearSlotIfSprite(raw[2], slots, pendingOffsets);
                    break;

                case 96:
                    slots.Clear();
                    pendingOffsets.Clear();
                    break;

                case 114 when raw.Length >= 12:
                    SetSlotOffset(raw, slots, pendingOffsets);
                    break;

                case 121:
                case 143:
                case 144:
                    AddSpriteSnapshot(document.SourceName, instruction, raw, staticAssets, animatedAssets, result, slots, pendingOffsets, groups, ref planIndex);
                    break;
            }
        }
    }

    private static void AddSpriteSnapshot(
        string sourceName,
        TblstrScrInstruction instruction,
        ReadOnlySpan<byte> raw,
        IReadOnlyDictionary<string, CharacterComposer.LayerAsset> staticAssets,
        IReadOnlyDictionary<string, CharacterComposer.LayerAsset> animatedAssets,
        CharacterComposeResult result,
        SortedDictionary<int, SpriteSlotState> slots,
        Dictionary<int, (int X, int Y)> pendingOffsets,
        List<CharacterComposer.SpLayerGroup> groups,
        ref int planIndex)
    {
        if (raw.Length < 17)
        {
            return;
        }

        var slot = raw[2];
        if (!IsSpriteSlot(slot))
        {
            return;
        }

        var objectName = GetString(instruction, "object_name");
        var patternName = GetString(instruction, "pattern_name");
        if (string.IsNullOrWhiteSpace(objectName) || string.IsNullOrWhiteSpace(patternName))
        {
            return;
        }

        if (!TryResolveSpriteAsset(objectName, patternName, staticAssets, animatedAssets, out var asset))
        {
            CharacterComposer.RecordMissingReference(result, $"SP_:{objectName}\\{patternName}");
            return;
        }

        if (pendingOffsets.TryGetValue(slot, out var offset))
        {
            asset = asset.WithOffset(offset.X, offset.Y);
        }

        slots[slot] = new SpriteSlotState(slot, objectName, patternName, asset);

        var snapshot = slots.Values
            .OrderBy(state => state.Slot)
            .ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        var layers = snapshot.Select(state => state.Asset).ToArray();
        var labelParts = snapshot.Length == 1
            ? [snapshot[0].ObjectName, snapshot[0].PatternName]
            : snapshot.Select(state => $"{state.ObjectName}:{state.PatternName}").ToArray();
        var characterHint = DeriveCharacterHint(objectName, patternName);

        groups.Add(new CharacterComposer.SpLayerGroup(
            ArchiveName: "",
            Index: planIndex++,
            LabelParts: labelParts,
            Layers: layers,
            CharacterHint: characterHint,
            SourceName: sourceName));
    }

    private static void SetSlotOffset(
        ReadOnlySpan<byte> raw,
        SortedDictionary<int, SpriteSlotState> slots,
        Dictionary<int, (int X, int Y)> pendingOffsets)
    {
        var slot = raw[2];
        if (!IsSpriteSlot(slot))
        {
            return;
        }

        var x = BitConverter.ToInt32(raw.Slice(4, 4));
        var y = BitConverter.ToInt32(raw.Slice(8, 4));
        pendingOffsets[slot] = (x, y);
        if (slots.TryGetValue(slot, out var state))
        {
            slots[slot] = state with { Asset = state.Asset.WithOffset(x, y) };
        }
    }

    private static void ClearSlotIfSprite(
        byte slot,
        SortedDictionary<int, SpriteSlotState> slots,
        Dictionary<int, (int X, int Y)> pendingOffsets)
    {
        if (!IsSpriteSlot(slot))
        {
            return;
        }

        slots.Remove(slot);
        pendingOffsets.Remove(slot);
    }

    private static bool TryResolveSpriteAsset(
        string objectName,
        string patternName,
        IReadOnlyDictionary<string, CharacterComposer.LayerAsset> staticAssets,
        IReadOnlyDictionary<string, CharacterComposer.LayerAsset> animatedAssets,
        out CharacterComposer.LayerAsset asset)
    {
        var candidates = staticAssets.Values
            .Concat(animatedAssets.Values)
            .Where(IsSpAsset)
            .Select(candidate => new AssetCandidate(candidate, ScoreAsset(candidate, objectName, patternName)))
            .Where(candidate => candidate.Score < int.MaxValue)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => FormatRank(candidate.Asset.FormatTag))
            .ThenBy(candidate => candidate.Asset.RelativeName.Length)
            .ThenBy(candidate => candidate.Asset.ResourceKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
        {
            asset = null!;
            return false;
        }

        var bestScore = candidates[0].Score;
        var best = candidates.Where(candidate => candidate.Score == bestScore).ToArray();
        if (bestScore >= 10 && best.Select(candidate => candidate.Asset.ResourceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            asset = null!;
            return false;
        }

        asset = best[0].Asset;
        return true;
    }

    private static int ScoreAsset(CharacterComposer.LayerAsset asset, string objectName, string patternName)
    {
        var relative = NormalizePath(Path.ChangeExtension(asset.RelativeName, null));
        var fileName = Path.GetFileNameWithoutExtension(asset.RelativeName);
        var objectPattern = NormalizePath(Path.Combine(objectName, patternName));

        if (relative.Equals(objectPattern, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (relative.EndsWith("\\" + objectPattern, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (fileName.Equals(patternName, StringComparison.OrdinalIgnoreCase) &&
            HasPathSegment(relative, objectName))
        {
            return 2;
        }

        if (fileName.Equals(patternName, StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        return int.MaxValue;
    }

    private static bool IsSpAsset(CharacterComposer.LayerAsset asset) =>
        asset.ArchiveName.StartsWith("sp", StringComparison.OrdinalIgnoreCase) ||
        asset.ArchiveName.Equals("spd", StringComparison.OrdinalIgnoreCase);

    private static int FormatRank(string formatTag) =>
        formatTag.Equals("anm", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static bool HasPathSegment(string path, string segment) =>
        path.Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));

    private static string NormalizePath(string path) =>
        path.Replace('/', '\\').Trim('\\');

    private static string GetString(TblstrScrInstruction instruction, string name) =>
        instruction.Strings.FirstOrDefault(str => str.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Text ?? "";

    private static string? DeriveCharacterHint(string objectName, string patternName) =>
        ExtractNameToken(patternName) ?? ExtractNameToken(objectName);

    private static string? ExtractNameToken(string value)
    {
        var underscore = value.IndexOf('_');
        if (underscore < 0 || underscore >= value.Length - 1)
        {
            return null;
        }

        var start = underscore + 1;
        var end = start;
        while (end < value.Length && char.IsLower(value[end]))
        {
            end++;
        }

        return end > start ? value[start..end] : null;
    }

    private static bool IsSpriteSlot(byte slot) =>
        slot is >= FirstAdvSpSlot and <= LastAdvSpSlot;

    private sealed record AssetCandidate(CharacterComposer.LayerAsset Asset, int Score);

    private sealed record SpriteSlotState(
        int Slot,
        string ObjectName,
        string PatternName,
        CharacterComposer.LayerAsset Asset);
}
