using CxdecReloading.Steps;

namespace CxdecReloading.Functions;

/// <summary>
/// Function 1: 提取并恢复 SCN 文件
/// 解包 → 文件签名扫描 → PSB 文件名还原
/// </summary>
public static class Function1_ExtractScn
{
    public static async Task RunAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "Function 1: 提取并恢复 SCN 文件");

        // 获取游戏路径
        var gameArg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (!string.IsNullOrEmpty(gameArg) && File.Exists(gameArg))
        {
            ctx.GameExePath = Path.GetFullPath(gameArg);
            ConsoleHelper.PrintInfo("从命令行参数获取游戏路径");
        }
        else
        {
            ctx.GameExePath = ConsoleHelper.AskPath("请输入游戏 exe 路径（或将 exe 拖拽到本窗口）");
        }

        if (!File.Exists(ctx.GameExePath))
        {
            ConsoleHelper.PrintError($"游戏文件不存在: {ctx.GameExePath}");
            ConsoleHelper.PrintHint("提示: 路径含日文时，建议将游戏 exe 拖到 run.bat 上启动");
            return;
        }

        ConsoleHelper.PrintSuccess($"游戏路径: {ctx.GameExePath}");
        ConsoleHelper.PrintInfo($"游戏目录: {ctx.GameDirectory}");
        Console.WriteLine();

        // Step 1-3 流水线
        var steps = new (string Name, Func<Task> Action)[]
        {
            ("提取封包文件",            () => Step1_ExtractPackage.RunAsync(ctx)),
            ("扫描文件签名 & 提取 PSB/MDF", () => Step2_ScanAndExtractPsb.RunAsync(ctx)),
            ("PSB/MDF 文件名还原",         () => Step3_PsbToJson.RunAsync(ctx)),
        };

        for (var i = 0; i < steps.Length; i++)
        {
            var (name, action) = steps[i];

            try
            {
                await action();
            }
            catch (Exception ex)
            {
                ConsoleHelper.PrintError($"Step {i + 1} 执行出错: {ex.Message}");
                ConsoleHelper.PrintInfo(ex.StackTrace ?? "");

                if (!ConsoleHelper.AskYesNo("是否继续执行后续步骤?", defaultYes: false))
                    break;
            }

            if (i < steps.Length - 1)
            {
                Console.WriteLine();
                if (!ConsoleHelper.AskYesNo($"继续执行 Step {i + 2}: {steps[i + 1].Name}?"))
                {
                    ConsoleHelper.PrintInfo("流水线已暂停");
                    break;
                }
            }
        }

        // 完成总结
        Console.WriteLine();
        ConsoleHelper.PrintSuccess("Function 1 执行完毕");
        ConsoleHelper.PrintInfo($"Extractor_Output: {ctx.ExtractOutputDir}");
        if (ctx.TotalFilesScanned > 0)
            ConsoleHelper.PrintInfo($"文件总数: {ctx.TotalFilesScanned}, 其中 PSB/MDF: {ctx.PsbFilesFound}");
        if (Directory.Exists(ctx.ScnPureDir))
        {
            var scnCount = Directory.GetFiles(ctx.ScnPureDir, "*.scn", SearchOption.AllDirectories).Length;
            ConsoleHelper.PrintInfo($"SCN_PURE: {scnCount} 个已还原文件名的 SCN 文件");
        }
    }
}
