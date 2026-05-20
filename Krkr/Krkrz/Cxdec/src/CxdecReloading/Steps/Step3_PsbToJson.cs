using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CxdecReloading.Steps;

/// <summary>
/// Step 3: PSB/MDF 文件名还原
///
/// 流程：
///   1. 对 SCN/ 下每个 PSB/MDF 调用 PsbDecompile.exe，JSON 输出到 SCN_Temp/
///   2. 解析 JSON 提取 "name" 字段，得到原始文件名
///   3. 将原始文件以还原的文件名（{name}.scn）复制到 SCN_PURE/
///   4. 没有 name 字段的文件（图片/动画等）计为跳过
///   5. 清理 SCN_Temp/
/// </summary>
public static class Step3_PsbToJson
{
    private static readonly object ProgressLock = new();

    public static async Task RunAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(3, "PSB/MDF 文件名还原");

        if (!File.Exists(ctx.PsbDecompilePath))
        {
            ConsoleHelper.PrintError($"找不到 PsbDecompile.exe: {ctx.PsbDecompilePath}");
            ConsoleHelper.PrintHint("请将 FreeMote 编译产物放入 scr/FreeMote/ 目录");
            return;
        }

        if (!Directory.Exists(ctx.ScnDir))
        {
            ConsoleHelper.PrintError($"SCN 目录不存在: {ctx.ScnDir}");
            ConsoleHelper.PrintHint("请先完成 Step 2 扫描");
            return;
        }

        var allPsbFiles = Directory.GetFiles(ctx.ScnDir, "*", SearchOption.AllDirectories);
        if (allPsbFiles.Length == 0)
        {
            ConsoleHelper.PrintWarning("SCN/ 目录为空，无文件可处理");
            return;
        }

        // 扫描子目录结构，让用户选择要处理的目录
        var psbFiles = SelectDirectories(ctx.ScnDir, allPsbFiles);
        if (psbFiles.Length == 0)
        {
            ConsoleHelper.PrintWarning("没有选中任何文件");
            return;
        }

        ConsoleHelper.PrintInfo($"共 {psbFiles.Length} 个 PSB/MDF 文件待处理");
        Directory.CreateDirectory(ctx.ScnTempDir);
        Directory.CreateDirectory(ctx.ScnPureDir);

        var successCount = 0;
        var skipCount = 0;
        var processed = 0;
        var total = psbFiles.Length;

        var maxParallel = ConsoleHelper.EnsureParallelism(ctx);

        var semaphore = new SemaphoreSlim(maxParallel);
        var tasks = new List<Task>();

        foreach (var psbFile in psbFiles)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (await ProcessOneAsync(psbFile, ctx))
                        Interlocked.Increment(ref successCount);
                    else
                        Interlocked.Increment(ref skipCount);
                }
                finally
                {
                    semaphore.Release();
                    Interlocked.Increment(ref processed);
                    lock (ProgressLock)
                    {
                        ConsoleHelper.PrintProgress(
                            Volatile.Read(ref processed), total,
                            $"SCN: {Volatile.Read(ref successCount)}, " +
                            $"跳过: {Volatile.Read(ref skipCount)}");
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine();

        ConsoleHelper.PrintSuccess($"处理完成: SCN {successCount}, 跳过 {skipCount} (非文本), 共 {total}");

        var scnCount = Directory.GetFiles(ctx.ScnPureDir, "*.scn", SearchOption.AllDirectories).Length;
        ConsoleHelper.PrintInfo($"SCN_PURE/ 中共 {scnCount} 个已还原文件名的 SCN 文件");

        // 清理 SCN_Temp
        Console.WriteLine();
        if (Directory.Exists(ctx.ScnTempDir))
        {
            if (ConsoleHelper.AskYesNo("是否删除 SCN_Temp/（PsbDecompile 中间产物）？"))
            {
                try
                {
                    Directory.Delete(ctx.ScnTempDir, recursive: true);
                    ConsoleHelper.PrintSuccess("SCN_Temp/ 已删除");
                }
                catch (Exception ex)
                {
                    ConsoleHelper.PrintWarning($"删除 SCN_Temp/ 失败: {ex.Message}");
                }
            }
        }

        if (ConsoleHelper.AskYesNo("是否删除 SCN/ 文件夹？", defaultYes: false))
        {
            try
            {
                Directory.Delete(ctx.ScnDir, recursive: true);
                ConsoleHelper.PrintSuccess("SCN/ 已删除");
            }
            catch (Exception ex)
            {
                ConsoleHelper.PrintWarning($"删除 SCN/ 失败: {ex.Message}");
            }
        }
    }

    private static string[] SelectDirectories(string scnDir, string[] allFiles)
    {
        // 统计根目录直属文件和各子目录的文件数
        var rootCount = 0;
        var subdirs = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in allFiles)
        {
            var relative = Path.GetRelativePath(scnDir, file);
            var sep = relative.IndexOfAny(['\\', '/']);
            if (sep < 0)
            {
                rootCount++;
            }
            else
            {
                var topDir = relative[..sep];
                subdirs[topDir] = subdirs.GetValueOrDefault(topDir) + 1;
            }
        }

        if (subdirs.Count == 0)
        {
            ConsoleHelper.PrintInfo($"SCN/ 下共 {rootCount} 个文件（无子目录）");
            return allFiles;
        }

        ConsoleHelper.PrintInfo("SCN/ 目录结构:");
        if (rootCount > 0)
            ConsoleHelper.PrintInfo($"  (根目录)          {rootCount,6} 个文件");
        foreach (var (dir, count) in subdirs)
            ConsoleHelper.PrintInfo($"  {dir,-20} {count,6} 个文件");
        ConsoleHelper.PrintInfo($"  {"合计",-20} {allFiles.Length,6} 个文件");

        Console.WriteLine();
        var input = ConsoleHelper.AskInput(
            "输入要处理的目录名（逗号分隔），或 all 处理全部", "all");

        if (input.Equals("all", StringComparison.OrdinalIgnoreCase))
            return allFiles;

        var selected = new HashSet<string>(
            input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        var filtered = allFiles.Where(f =>
        {
            var relative = Path.GetRelativePath(scnDir, f);
            var sep = relative.IndexOfAny(['\\', '/']);
            if (sep < 0)
                return selected.Contains(".");
            return selected.Contains(relative[..sep]);
        }).ToArray();

        ConsoleHelper.PrintInfo($"已选择 {filtered.Length} 个文件（来自: {string.Join(", ", selected)}）");
        return filtered;
    }

    private static async Task<bool> ProcessOneAsync(string psbFilePath, PipelineContext ctx)
    {
        try
        {
            var relativePath = Path.GetRelativePath(ctx.ScnDir, psbFilePath);
            var relativeDir = Path.GetDirectoryName(relativePath) ?? "";
            var outputDir = Path.Combine(ctx.ScnTempDir, relativeDir);
            Directory.CreateDirectory(outputDir);

            var psi = new ProcessStartInfo
            {
                FileName = ctx.PsbDecompilePath,
                Arguments = $"-o \"{outputDir}\" \"{psbFilePath}\"",
                WorkingDirectory = Path.GetDirectoryName(psbFilePath) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            await proc.WaitForExitAsync();

            var psbName = Path.GetFileNameWithoutExtension(psbFilePath);
            var jsonPath = Path.Combine(outputDir, psbName + ".json");
            if (!File.Exists(jsonPath))
                return false;

            var jsonText = await File.ReadAllTextAsync(jsonPath);
            using var doc = JsonDocument.Parse(jsonText);

            if (!doc.RootElement.TryGetProperty("name", out var nameProp))
                return false;

            var scnName = nameProp.GetString();
            if (string.IsNullOrEmpty(scnName))
                return false;

            var destDir = Path.Combine(ctx.ScnPureDir, relativeDir);
            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, scnName + ".scn");
            File.Copy(psbFilePath, destPath, overwrite: true);

            return true;
        }
        catch
        {
            return false;
        }
    }
}