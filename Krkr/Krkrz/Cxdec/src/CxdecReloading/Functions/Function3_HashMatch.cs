using System.Diagnostics;
using System.Text;

namespace CxdecReloading.Functions;

/// <summary>
/// Function 3: 撞库（哈希文件名匹配）
///
/// 流程：
///   1. 部署 version.dll + files.txt + dirs.txt 到游戏目录
///   2. 启动游戏 → version.dll 利用游戏自身哈希函数计算候选名的哈希
///      → 输出 files_match.txt / dirs_match.txt (原始名,哈希值)
///   3. 解析 match 文件，构建 哈希→真实名 映射
///   4. 用映射重命名 Extractor_Output 中的 hash 文件 → Fixname_Output
///   5. (自动二轮) 从 Fixname_Output 的 .dref 提取 dpak 名 → 更新字典 → 再次撞库
/// </summary>
public static class Function3_HashMatch
{
    public static async Task RunAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "Function 3: 撞库（哈希文件名匹配）");

        if (string.IsNullOrEmpty(ctx.GameExePath) || !File.Exists(ctx.GameExePath))
        {
            ConsoleHelper.PrintInfo("请先指定游戏可执行文件路径");
            var path = ConsoleHelper.AskPath("游戏 EXE 路径");
            if (!File.Exists(path))
            {
                ConsoleHelper.PrintError("文件不存在");
                return;
            }
            ctx.GameExePath = path;
        }

        ConsoleHelper.PrintInfo($"游戏目录: {ctx.GameDirectory}");

        while (true)
        {
            var choice = ConsoleHelper.AskMenu("请选择操作:",
                "一键撞库（自动进行2轮撞库，提取dpak的需要）",
                "部署撞库环境（version.dll + 字典）",
                "启动游戏进行撞库",
                "应用映射（hash文件名 → 真实文件名）",
                "清理游戏目录中的部署文件");

            switch (choice)
            {
                case 1:
                    await RunFullHashMatchAsync(ctx);
                    break;
                case 2:
                    Deploy(ctx);
                    break;
                case 3:
                    await LaunchGameAsync(ctx);
                    break;
                case 4:
                    ApplyMapping(ctx);
                    break;
                case 5:
                    Cleanup(ctx);
                    break;
                case 0:
                    return;
            }
        }
    }

    // ========== 一键撞库（自动二轮） ==========

    private static async Task RunFullHashMatchAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "一键撞库（自动进行2轮撞库，提取dpak的需要）");

        // 前置检查
        if (!File.Exists(ctx.VersionDllSourcePath))
        {
            ConsoleHelper.PrintError($"version.dll 不存在: {ctx.VersionDllSourcePath}");
            return;
        }
        if (!File.Exists(ctx.FilesListPath))
        {
            ConsoleHelper.PrintError("files.txt 不存在，请先执行 Function 2 生成 filedict");
            return;
        }
        if (!Directory.Exists(ctx.ExtractOutputDir))
        {
            ConsoleHelper.PrintError($"Extractor_Output 不存在: {ctx.ExtractOutputDir}");
            return;
        }

        // 询问 LE（只问一次，两轮复用）
        var useLE = false;
        if (File.Exists(ctx.LoaderDllPath))
            useLE = ConsoleHelper.AskYesNo("是否使用 Locale Emulator 转区启动（日语区域）？");

        // ===== 第一轮 =====
        ConsoleHelper.PrintStepHeader(0, "第一轮撞库");

        Deploy(ctx);
        if (!await LaunchGameCoreAsync(ctx, useLE))
            return;
        ApplyMapping(ctx);

        // ===== 从 Fixname_Output 提取 .dref → 更新字典 =====
        ConsoleHelper.PrintStepHeader(0, "提取 .dref → 更新字典");

        var drefRefs = Function2_ParseScn.ExtractDrefReferences(ctx.FixnameOutputDir);
        if (drefRefs.Count == 0)
        {
            ConsoleHelper.PrintInfo("Fixname_Output 中未找到 .dref 文件，无需第二轮撞库");
            PrintCleanupHint(ctx);
            return;
        }

        ConsoleHelper.PrintInfo($"从 .dref 中提取到 {drefRefs.Count} 个新候选文件名");
        var secondRoundDpakRefs = drefRefs
            .Where(x => x.EndsWith(".dpak", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (secondRoundDpakRefs.Count == 0)
        {
            ConsoleHelper.PrintInfo(".dref 中未提取到 .dpak 文件名，无需第二轮撞库");
            PrintCleanupHint(ctx);
            return;
        }

        // 读取当前 files.txt，合并 dref 提取的新名称，写回
        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(ctx.FilesListPath))
        {
            var text = File.ReadAllText(ctx.FilesListPath, Encoding.Unicode);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim('\r', ' ');
                if (!string.IsNullOrEmpty(trimmed))
                    existingFiles.Add(trimmed);
            }
        }

        var beforeCount = existingFiles.Count;
        existingFiles.UnionWith(drefRefs);
        var newCount = existingFiles.Count - beforeCount;

        if (newCount == 0)
        {
            ConsoleHelper.PrintInfo("所有 dpak 候选名已在字典中，无需第二轮撞库");
            PrintCleanupHint(ctx);
            return;
        }

        ConsoleHelper.PrintInfo($"字典新增 {newCount} 条，总计 {existingFiles.Count} 条");
        Function2_ParseScn.WriteUtf16LeList(ctx.FilesListPath, existingFiles.Order());
        ConsoleHelper.PrintSuccess("files.txt 已更新");

        // ===== 第二轮 =====
        ConsoleHelper.PrintStepHeader(0, "第二轮撞库（dpak）");

        // Deploy(ctx);
        // if (!await LaunchGameCoreAsync(ctx, useLE))
        //     return;
        // ApplyMapping(ctx);
        Deploy(ctx);
        WriteSecondRoundDpakOnlyFiles(ctx, secondRoundDpakRefs);
        if (!await LaunchGameCoreAsync(ctx, useLE))
            return;
        ApplyMapping(ctx);

        PrintCleanupHint(ctx);
    }

    private static void PrintCleanupHint(PipelineContext ctx)
    {
        Console.WriteLine();
        ConsoleHelper.PrintHint("撞库完成，如需清理游戏目录中的部署文件，请返回菜单手动清理");
    }

    // ========== 部署 ==========

    private static void Deploy(PipelineContext ctx)
    {
        var versionDllDest = Path.Combine(ctx.GameDirectory, "version.dll");
        File.Copy(ctx.VersionDllSourcePath, versionDllDest, overwrite: true);

        foreach (var dictFile in new[] { ctx.FilesListPath, ctx.DirsListPath })
        {
            if (!File.Exists(dictFile)) continue;
            var destPath = Path.Combine(ctx.GameDirectory, Path.GetFileName(dictFile));
            File.Copy(dictFile, destPath, overwrite: true);
        }

        ConsoleHelper.PrintSuccess("撞库环境已部署（version.dll + 字典）");
    }

    private static void WriteSecondRoundDpakOnlyFiles(PipelineContext ctx, IEnumerable<string> dpakRefs)
    {
        var gameFilesPath = Path.Combine(ctx.GameDirectory, "files.txt");
        var uniqueDpakRefs = new HashSet<string>(dpakRefs, StringComparer.OrdinalIgnoreCase);

        Function2_ParseScn.WriteUtf16LeList(gameFilesPath, uniqueDpakRefs.Order());
        ConsoleHelper.PrintInfo($"第二轮仅部署 dpak 候选: {uniqueDpakRefs.Count} 条");
    }

    // ========== 启动游戏 ==========

    private static async Task LaunchGameAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "启动游戏进行撞库");

        var versionDll = Path.Combine(ctx.GameDirectory, "version.dll");
        if (!File.Exists(versionDll))
        {
            ConsoleHelper.PrintWarning("游戏目录中没有 version.dll，请先部署撞库环境");
            return;
        }

        var useLE = false;
        if (File.Exists(ctx.LoaderDllPath))
            useLE = ConsoleHelper.AskYesNo("是否使用 Locale Emulator 转区启动（日语区域）？");

        if (!ConsoleHelper.AskYesNo("确认启动游戏？"))
            return;

        await LaunchGameCoreAsync(ctx, useLE);
    }

    /// <summary>
    /// 启动游戏核心逻辑，一键撞库和手动启动共用。
    /// 返回 true 表示游戏正常退出且 files_match.txt 已生成。
    /// </summary>
    private static async Task<bool> LaunchGameCoreAsync(PipelineContext ctx, bool useLE)
    {
        ConsoleHelper.PrintInfo($"即将启动: {ctx.GameExePath}");
        ConsoleHelper.PrintHint("游戏启动后 version.dll 会自动计算哈希并写入 files_match.txt / dirs_match.txt");
        ConsoleHelper.PrintHint("看到控制台输出 \"calculate finish\" 后即可关闭游戏");

        ConsoleHelper.WaitForEnter("准备好后按回车启动游戏...");

        try
        {
            Process? proc;

            if (useLE)
            {
                ConsoleHelper.PrintInfo("正在通过 Locale Emulator 启动（日语区域）...");
                var (pid, error) = LocaleEmulatorHelper.LaunchWithLE(
                    ctx.GameExePath, ctx.GameDirectory, ctx.LoaderDllPath);

                if (error != null)
                {
                    ConsoleHelper.PrintError($"LE 启动失败: {error}");
                    return false;
                }

                proc = Process.GetProcessById(pid);
                ConsoleHelper.PrintSuccess($"游戏已通过 LE 启动 (PID: {pid})");
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ctx.GameExePath,
                    WorkingDirectory = ctx.GameDirectory,
                    UseShellExecute = true,
                };

                proc = Process.Start(psi);
                if (proc == null)
                {
                    ConsoleHelper.PrintError("启动游戏失败");
                    return false;
                }

                ConsoleHelper.PrintInfo($"游戏进程已启动 (PID: {proc.Id})");
            }

            ConsoleHelper.PrintInfo("等待游戏退出...");
            await proc.WaitForExitAsync();
            ConsoleHelper.PrintSuccess($"游戏已退出 (ExitCode: {proc.ExitCode})");
        }
        catch (Exception ex)
        {
            ConsoleHelper.PrintError($"启动游戏出错: {ex.Message}");
            return false;
        }

        // 检查输出
        var filesMatch = Path.Combine(ctx.GameDirectory, "files_match.txt");
        var dirsMatch = Path.Combine(ctx.GameDirectory, "dirs_match.txt");

        if (File.Exists(filesMatch))
            ConsoleHelper.PrintSuccess($"files_match.txt 已生成 ({new FileInfo(filesMatch).Length / 1024} KB)");
        else
        {
            ConsoleHelper.PrintWarning("files_match.txt 未生成，可能需要等游戏加载完再关闭");
            return false;
        }

        if (File.Exists(dirsMatch))
            ConsoleHelper.PrintSuccess($"dirs_match.txt 已生成 ({new FileInfo(dirsMatch).Length / 1024} KB)");

        return true;
    }

    // ========== 应用映射 ==========

    private static void ApplyMapping(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "应用映射（hash → 真实文件名）");

        var filesMatchPath = Path.Combine(ctx.GameDirectory, "files_match.txt");

        if (!File.Exists(filesMatchPath))
        {
            ConsoleHelper.PrintError("files_match.txt 不存在");
            ConsoleHelper.PrintHint("请先启动游戏完成撞库");
            return;
        }

        if (!Directory.Exists(ctx.ExtractOutputDir))
        {
            ConsoleHelper.PrintError($"Extractor_Output 不存在: {ctx.ExtractOutputDir}");
            return;
        }

        // 解析 files_match.txt: "原始文件名,BLAKE2s哈希" (UTF-16LE BOM)
        var fileMapping = ParseMatchFile(filesMatchPath);
        ConsoleHelper.PrintInfo($"files_match.txt: {fileMapping.Count} 条文件映射");

        // 构建 hash → name 反向映射
        var hashToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in fileMapping)
            hashToFile.TryAdd(hash, name);

        ConsoleHelper.PrintInfo($"反向映射: {hashToFile.Count} 文件");

        // 排除 SCN_PURE 中已还原的文本 SCN
        var restoredScnHashes = CollectRestoredScnHashes(ctx, hashToFile);
        if (restoredScnHashes.Count > 0)
            ConsoleHelper.PrintInfo($"排除已还原的文本 SCN: {restoredScnHashes.Count} 个");

        // 遍历 Extractor_Output，匹配并复制（保持原始目录结构，目录名不还原）
        Directory.CreateDirectory(ctx.FixnameOutputDir);
        Directory.CreateDirectory(ctx.FixnameFailedDir);

        var extractFiles = Directory.GetFiles(ctx.ExtractOutputDir, "*", SearchOption.AllDirectories);
        var matchedCount = 0;
        var unmatchedCount = 0;
        var scnSkipCount = 0;

        foreach (var srcFile in extractFiles)
        {
            var hashFileName = Path.GetFileNameWithoutExtension(srcFile);
            var relativePath = Path.GetRelativePath(ctx.ExtractOutputDir, srcFile);

            if (restoredScnHashes.Contains(hashFileName))
            {
                scnSkipCount++;
                continue;
            }

            if (!hashToFile.TryGetValue(hashFileName, out var realName))
            {
                CopyUnmatchedFile(ctx, srcFile, relativePath);
                unmatchedCount++;
                continue;
            }

            var parentDirName = Path.GetDirectoryName(relativePath) ?? "";

            var outputPath = Path.Combine(ctx.FixnameOutputDir, parentDirName, realName);
            var outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir != null)
                Directory.CreateDirectory(outputDir);

            try
            {
                File.Copy(srcFile, outputPath, overwrite: true);
                matchedCount++;
            }
            catch { }
        }

        ConsoleHelper.PrintSuccess($"映射完成: 匹配 {matchedCount}, 未匹配 {unmatchedCount}, 排除文本SCN {scnSkipCount}");
        ConsoleHelper.PrintInfo($"输出目录: {ctx.FixnameOutputDir}");

        if (unmatchedCount > 0)
        {
            ConsoleHelper.PrintHint($"未匹配的 {unmatchedCount} 个文件可能是字典未覆盖的文件名或非文件资源");
            ConsoleHelper.PrintInfo($"未匹配输出目录: {ctx.FixnameFailedDir}");
        }

        // 统计文件类型分布
        if (matchedCount > 0)
        {
            var outputFiles = Directory.GetFiles(ctx.FixnameOutputDir, "*", SearchOption.AllDirectories);
            var extensions = outputFiles
                .Select(f => Path.GetExtension(f).ToLowerInvariant())
                .Where(e => !string.IsNullOrEmpty(e))
                .GroupBy(e => e)
                .OrderByDescending(g => g.Count())
                .Take(10);

            ConsoleHelper.PrintInfo("结果文件类型分布:");
            foreach (var g in extensions)
                ConsoleHelper.PrintInfo($"  {g.Key,-10} {g.Count(),6} 个");
        }
    }

    private static void CopyUnmatchedFile(PipelineContext ctx, string srcFile, string relativePath)
    {
        var failedPath = Path.Combine(ctx.FixnameFailedDir, relativePath);
        var failedDir = Path.GetDirectoryName(failedPath);
        if (failedDir != null)
            Directory.CreateDirectory(failedDir);

        try
        {
            File.Copy(srcFile, failedPath, overwrite: true);
        }
        catch { }
    }

    // ========== 清理 ==========

    private static void Cleanup(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "清理游戏目录中的部署文件");

        var filesToClean = new[]
        {
            Path.Combine(ctx.GameDirectory, "version.dll"),
            Path.Combine(ctx.GameDirectory, "files.txt"),
            Path.Combine(ctx.GameDirectory, "dirs.txt"),
            Path.Combine(ctx.GameDirectory, "files_match.txt"),
            Path.Combine(ctx.GameDirectory, "dirs_match.txt"),
        };

        ConsoleHelper.PrintInfo("将清理以下文件:");
        var existingFiles = 0;
        foreach (var f in filesToClean)
        {
            var exists = File.Exists(f);
            if (exists) existingFiles++;
            ConsoleHelper.PrintInfo($"  {Path.GetFileName(f),-20} ({(exists ? "存在" : "不存在")})");
        }

        if (existingFiles == 0)
        {
            ConsoleHelper.PrintInfo("没有需要清理的文件");
            return;
        }

        if (!ConsoleHelper.AskYesNo("确认清理？", defaultYes: false))
            return;

        var deletedCount = 0;
        foreach (var f in filesToClean)
        {
            if (!File.Exists(f)) continue;
            try
            {
                File.Delete(f);
                deletedCount++;
            }
            catch { }
        }

        ConsoleHelper.PrintSuccess($"清理完成: 删除 {deletedCount} 个文件");
    }

    // ========== 辅助方法 ==========

    private static Dictionary<string, string> ParseMatchFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = File.ReadAllText(path, Encoding.Unicode);

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ');
            if (string.IsNullOrEmpty(trimmed)) continue;

            var commaIdx = trimmed.IndexOf(',');
            if (commaIdx < 0) continue;

            var name = trimmed[..commaIdx];
            var hash = trimmed[(commaIdx + 1)..];

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(hash))
                result.TryAdd(name, hash);
        }

        return result;
    }

    private static HashSet<string> CollectRestoredScnHashes(
        PipelineContext ctx, Dictionary<string, string> hashToFile)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(ctx.ScnPureDir))
            return hashes;

        var nameToHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hash, name) in hashToFile)
            nameToHash.TryAdd(name, hash);

        foreach (var file in Directory.GetFiles(ctx.ScnPureDir, "*.scn", SearchOption.AllDirectories))
        {
            var scnName = Path.GetFileNameWithoutExtension(file);
            if (nameToHash.TryGetValue(scnName, out var hash))
                hashes.Add(hash);
        }

        return hashes;
    }
}
