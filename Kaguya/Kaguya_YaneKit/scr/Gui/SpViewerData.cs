// ============================================================================
// SpViewerData.cs
// SP 立绘查看器数据模型
//
// SpCharacterGroup  -- 角色分组 (按 ArchiveName)
// SpExpressionEntry -- 表情条目 (一个 SP 合成计划)
// SpBackgroundEntry -- 背景条目 (来自 pic/bgd 的 PNG)
// ============================================================================
using Kaguya_YaneKit.Formats.Character;

namespace Kaguya_YaneKit.Gui;

internal sealed class SpCharacterGroup
{
    public string Name { get; }
    public List<SpExpressionEntry> Expressions { get; }

    public SpCharacterGroup(string name, List<SpExpressionEntry> expressions)
    {
        Name = name;
        Expressions = expressions;
    }

    public override string ToString() => Name;
}

internal sealed class SpExpressionEntry
{
    public int Index { get; }
    public string Label { get; }
    public IReadOnlyList<CharacterComposer.LayerAsset> Layers { get; }
    public bool RequiresFrames { get; }

    public SpExpressionEntry(int index, string label, IReadOnlyList<CharacterComposer.LayerAsset> layers, bool requiresFrames)
    {
        Index = index;
        Label = label;
        Layers = layers;
        RequiresFrames = requiresFrames;
    }

    public override string ToString() => $"{Index:D4}_{Label}";
}

internal sealed class SpBackgroundEntry
{
    public static readonly SpBackgroundEntry None = new("(none)", "");

    public string Name { get; }
    public string PngPath { get; }

    public SpBackgroundEntry(string name, string pngPath)
    {
        Name = name;
        PngPath = pngPath;
    }

    public override string ToString() => Name;
}
