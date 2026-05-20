using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CxdecReloading.Steps;

/// <summary>
/// Step 1: CxdecExtractorLoader 解包 → 解析 Extractor.log 汇总 → 移动 Extractor_Output 到根目录
/// </summary>
public static partial class Step1_ExtractPackage
{
    // [data.xp3] 解包完成: 总计 1198, 成功 1197, 跳过 1, 错误 0
    [GeneratedRegex(@"\[(.+?)\] 解包完成: 总计 (\d+), 成功 (\d+), 跳过 (\d+), 错误 (\d+)")]
    private static partial Regex SummaryPattern();

    public static async Task RunAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(1, "提取封包文件");

        if (!File.Exists(ctx.CxdecLoaderPath))
        {
            ConsoleHelper.PrintError($"找不到 CxdecExtractorLoader: {ctx.CxdecLoaderPath}");
            ConsoleHelper.PrintHint("请将 KrkrExtractForCxdecV2 的编译产物放入 scr/KrkrExtractForCxdecV2/ 目录");
            return;
        }

        // 清除旧日志
        if (File.Exists(ctx.ExtractorLogPath))
            File.Delete(ctx.ExtractorLogPath);

        ConsoleHelper.PrintInfo("即将启动 CxdecExtractorLoader，请在弹出的窗口中：");
        ConsoleHelper.PrintInfo("  1. 点击「加载解包模块」");
        ConsoleHelper.PrintInfo("  2. 在游戏启动后，将 .xp3 封包文件拖入解包窗口");
        ConsoleHelper.PrintInfo("  3. 等待解包完成后，关闭游戏");
        Console.WriteLine();

        var useLE = false;
        if (File.Exists(ctx.LoaderDllPath))
        {
            useLE = ConsoleHelper.AskYesNo("是否使用 Locale Emulator 转区启动（日语区域）？");
        }

        ConsoleHelper.WaitForEnter("准备好后按回车启动...");

        try
        {
            Process? process;

            if (useLE)
            {
                ConsoleHelper.PrintInfo("正在通过 Locale Emulator 启动 CxdecExtractorLoader（日语区域）...");
                var (pid, error) = LocaleEmulatorHelper.LaunchWithLE(
                    ctx.CxdecLoaderPath, ctx.GameDirectory, ctx.LoaderDllPath,
                    $"\"{ctx.GameExePath}\"");

                if (error != null)
                {
                    ConsoleHelper.PrintError($"LE 启动失败: {error}");
                    return;
                }

                process = Process.GetProcessById(pid);
                ConsoleHelper.PrintInfo($"CxdecExtractorLoader 已通过 LE 启动 (PID: {pid})");
            }
            else
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = ctx.CxdecLoaderPath,
                    Arguments = $"\"{ctx.GameExePath}\"",
                    WorkingDirectory = ctx.GameDirectory,
                    UseShellExecute = false,
                });

                if (process == null)
                {
                    ConsoleHelper.PrintError("启动 CxdecExtractorLoader 失败");
                    return;
                }

                ConsoleHelper.PrintInfo($"CxdecExtractorLoader 已启动 (PID: {process.Id})");
            }

            await process.WaitForExitAsync();
            ConsoleHelper.PrintInfo("CxdecExtractorLoader 已退出");
        }
        catch (Exception ex)
        {
            ConsoleHelper.PrintError($"启动出错: {ex.Message}");
            return;
        }

        await WaitForGameExitAsync(ctx.GameExeName);

        // 解析 Extractor.log 汇总各封包提取情况
        ParseAndShowLog(ctx);

        // 在游戏目录下寻找 Extractor_Output
        var gameExtractDir = Path.Combine(ctx.GameDirectory, "Extractor_Output");
        if (!Directory.Exists(gameExtractDir))
        {
            ConsoleHelper.PrintWarning("未在游戏目录找到 Extractor_Output");
            var manualPath = ConsoleHelper.AskInput(
                "请手动输入解包输出目录路径（或回车跳过）");
            if (!string.IsNullOrEmpty(manualPath) && Directory.Exists(manualPath))
                gameExtractDir = manualPath;
            else
                return;
        }

        // 移动到根目录
        if (gameExtractDir != ctx.ExtractOutputDir)
        {
            if (Directory.Exists(ctx.ExtractOutputDir))
            {
                ConsoleHelper.PrintWarning($"根目录下已存在 Extractor_Output，将合并");
                MoveContents(gameExtractDir, ctx.ExtractOutputDir);
                TryDeleteEmpty(gameExtractDir);
            }
            else
            {
                ConsoleHelper.PrintInfo($"移动 Extractor_Output → {ctx.ExtractOutputDir}");
                Directory.Move(gameExtractDir, ctx.ExtractOutputDir);
            }
        }

        var fileCount = Directory.GetFiles(ctx.ExtractOutputDir, "*", SearchOption.AllDirectories).Length;
        ConsoleHelper.PrintSuccess($"Extractor_Output 就绪: {fileCount} 个文件");
    }

    private static void ParseAndShowLog(PipelineContext ctx)
    {
        if (!File.Exists(ctx.ExtractorLogPath))
        {
            ConsoleHelper.PrintWarning("未找到 Extractor.log，无法获取提取详情");
            return;
        }

        try
        {
            var lines = File.ReadAllLines(ctx.ExtractorLogPath, Encoding.UTF8);
            var summaries = new List<(string Pkg, int Total, int Success, int Skip, int Error)>();
            var regex = SummaryPattern();

            foreach (var line in lines)
            {
                var m = regex.Match(line);
                if (m.Success)
                {
                    summaries.Add((
                        m.Groups[1].Value,
                        int.Parse(m.Groups[2].Value),
                        int.Parse(m.Groups[3].Value),
                        int.Parse(m.Groups[4].Value),
                        int.Parse(m.Groups[5].Value)));
                }
            }

            if (summaries.Count == 0)
            {
                ConsoleHelper.PrintWarning("Extractor.log 中未找到提取摘要");
                return;
            }

            Console.WriteLine();
            ConsoleHelper.PrintSuccess("提取结果汇总：");
            foreach (var (pkg, total, success, skip, error) in summaries)
            {
                var errorStr = error > 0 ? $"  错误: {error}" : "";
                var skipStr = skip > 0 ? $"  跳过: {skip}" : "";
                ConsoleHelper.PrintInfo($"  {pkg,-20} 总计: {total}  成功: {success}{skipStr}{errorStr}");
            }

            var grandTotal = summaries.Sum(s => s.Total);
            var grandSuccess = summaries.Sum(s => s.Success);
            if (summaries.Count > 1)
                ConsoleHelper.PrintInfo($"  {"合计",-20} {grandTotal} 个文件, 成功 {grandSuccess}");
        }
        catch
        {
            ConsoleHelper.PrintWarning("解析 Extractor.log 时出错");
        }
    }

    private static async Task WaitForGameExitAsync(string gameExeName)
    {
        var processName = Path.GetFileNameWithoutExtension(gameExeName);
        var gameProcesses = Process.GetProcessesByName(processName);

        if (gameProcesses.Length > 0)
        {
            ConsoleHelper.PrintInfo($"检测到游戏进程 {processName} 仍在运行，等待退出...");
            foreach (var p in gameProcesses)
            {
                try { await p.WaitForExitAsync(); }
                catch { }
            }
            ConsoleHelper.PrintInfo("游戏进程已退出");
        }
    }

    private static void MoveContents(string src, string dst)
    {
        foreach (var dir in Directory.GetDirectories(src))
        {
            var destDir = Path.Combine(dst, Path.GetFileName(dir));
            if (Directory.Exists(destDir))
                MoveContents(dir, destDir);
            else
                Directory.Move(dir, destDir);
        }

        foreach (var file in Directory.GetFiles(src))
        {
            var destFile = Path.Combine(dst, Path.GetFileName(file));
            if (!File.Exists(destFile))
                File.Move(file, destFile);
        }
    }

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch { }
    }
}