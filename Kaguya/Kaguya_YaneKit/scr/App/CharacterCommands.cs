// ============================================================================
// CharacterCommands.cs
// CLI 子命令: CG/立绘合成
//
// 命令列表:
//   compose -- 从 pic/ 目录读取 pattern 数据, 合成 CG 和立绘到输出目录
//
// 合成流程 (CharacterComposer.ComposeAll):
//   1. 扫描 pic/ 下 cg*/sp* 目录, 建立静态素材索引 (bmp/ap/ap2)
//   2. 扫描 anm/ 目录, 建立动画素材索引
//   3. 根据 params.dat 的 Pattern.GroupTable1 生成 CG 合成计划
//   4. 根据 Pattern.IntArrays 生成 SP 合成计划
//   5. 并行合成所有计划, 输出 PNG 到 character/cg/ 和 character/sp/
//   6. 生成 _resource_usage_report.txt 统计素材使用率
//
// 依赖: Formats.Character.CharacterComposer, Formats.Character.CharacterComposeResult
// ============================================================================

using Kaguya_YaneKit.Formats.Character;

namespace Kaguya_YaneKit.App;

public static class CharacterCommands
{
    public static int Run(string[] args, KaguyaRuntimeContext? context = null)
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
                "compose" => Compose(args, context),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Compose(string[] args, KaguyaRuntimeContext? context)
    {
        if (args.Length is not (2 or 3))
        {
            PrintHelp();
            return 1;
        }

        var picDir = Path.GetFullPath(args[1]);
        var outputDir = args.Length >= 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(Path.GetDirectoryName(picDir) ?? Environment.CurrentDirectory, "character");

        var width = (int)(context?.Params?.GameSystem.Width ?? 1280);
        var height = (int)(context?.Params?.GameSystem.Height ?? 720);
        var result = CharacterComposer.ComposeAll(picDir, outputDir, context?.Params, width, height);
        PrintResult(outputDir, result);
        return result.FailureCount == 0 ? 0 : 1;
    }

    public static void PrintResult(string outputDir, CharacterComposeResult result)
    {
        Console.WriteLine($"  Composed character images into {outputDir}");
        Console.WriteLine($"    CG: {result.CgBaseCount} base, {result.CgComposedCount} composed, {result.CgCopiedCount} copied");
        Console.WriteLine($"    SP: {result.SpBaseCount} base, {result.SpComposedCount} composed, {result.SpCopiedCount} base-only");
        Console.WriteLine($"    Assets: {result.StaticUsedCount}/{result.StaticAssetCount} static, {result.AnimatedUsedCount}/{result.AnimatedAssetCount} animated");
        Console.WriteLine($"    Missing refs: {result.MissingReferenceCount}");
        Console.WriteLine($"    Failures: {result.FailureCount}");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown character command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("character commands:");
        Console.WriteLine("  character compose <pic-dir> [output-dir]");
        Console.WriteLine("    Compose CG and standing pictures into character/cg and character/sp.");
    }
}
