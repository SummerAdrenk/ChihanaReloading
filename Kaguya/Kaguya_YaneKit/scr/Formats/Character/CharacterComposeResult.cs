// ============================================================================
// CharacterComposeResult.cs
// CG/立绘合成结果统计容器
//
// 统计项:
//   CG: 基底数/合成数/直接复制数
//   SP: 基底数/合成数/仅基底数
//   素材: 静态素材总数及使用数, 动画素材总数及使用数
//   缺失引用: 计数 + 最多 20 个样本路径
//   每个档案/格式的详细使用率报告 (CharacterResourceUsageReport)
//
// 被依赖: CharacterComposer, CharacterCommands
// ============================================================================
namespace Kaguya_YaneKit.Formats.Character;

public sealed class CharacterComposeResult
{
    public int CgBaseCount { get; set; }
    public int CgComposedCount { get; set; }
    public int CgCopiedCount { get; set; }
    public int SpBaseCount { get; set; }
    public int SpComposedCount { get; set; }
    public int SpCopiedCount { get; set; }
    public int FailureCount { get; set; }

    public int StaticAssetCount { get; set; }
    public int AnimatedAssetCount { get; set; }
    public int StaticUsedCount { get; set; }
    public int AnimatedUsedCount { get; set; }
    public int MissingReferenceCount { get; set; }

    public List<string> MissingReferenceSamples { get; } = [];
    public List<CharacterResourceUsageReport> ResourceUsageReports { get; } = [];
}

public sealed class CharacterResourceUsageReport
{
    public string ArchiveName { get; set; } = "";
    public string FormatTag { get; set; } = "";
    public int TotalCount { get; set; }
    public int UsedCount { get; set; }
    public List<string> UnusedSamples { get; set; } = [];

    public int UnusedCount => Math.Max(0, TotalCount - UsedCount);
}
