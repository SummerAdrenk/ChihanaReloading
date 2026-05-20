using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CxdecReloading.Functions;

/// <summary>
/// Function 2: 解析 SCN 文件
/// 子功能：导出可翻译文本 / 生成 filedict
/// </summary>
public static class Function2_ParseScn
{
    private static readonly object ProgressLock = new();
    public static async Task RunAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "Function 2: 解析 SCN 文件");

        if (!Directory.Exists(ctx.ScnPureDir))
        {
            ConsoleHelper.PrintError($"SCN_PURE 目录不存在: {ctx.ScnPureDir}");
            ConsoleHelper.PrintHint("请先执行 Function 1 提取并恢复 SCN 文件");
            return;
        }

        var scnCount = Directory.GetFiles(ctx.ScnPureDir, "*.scn", SearchOption.AllDirectories).Length;
        ConsoleHelper.PrintInfo($"SCN_PURE/ 中共 {scnCount} 个 SCN 文件");

        while (true)
        {
            var choice = ConsoleHelper.AskMenu("请选择操作:",
                "导出可翻译文本",
                "生成 filedict（撞库用）");

            switch (choice)
            {
                case 1:
                    await TextPipelineAsync(ctx);
                    break;
                case 2:
                    await GenerateFileDictAsync(ctx);
                    break;
                case 0:
                    return;
            }
        }
    }

    // ========== 文本处理流水线 ==========

    private static async Task TextPipelineAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "导出可翻译文本");

        if (!File.Exists(ctx.PsbDecompilePath))
        {
            ConsoleHelper.PrintError($"找不到 PsbDecompile.exe: {ctx.PsbDecompilePath}");
            return;
        }

        if (!await EnsureWorkDirAsync(ctx))
            return;

        while (true)
        {
            var choice = ConsoleHelper.AskMenu("请选择操作:",
                "导出 TXT（提取双行文本）",
                "回注翻译文本（TXT → JSON → SCN）");

            switch (choice)
            {
                case 1:
                    ExportTxt(ctx);
                    break;
                case 2:
                    await InjectTranslationAsync(ctx);
                    break;
                case 0:
                    return;
            }
        }
    }

    private static async Task<bool> EnsureWorkDirAsync(PipelineContext ctx)
    {
        var jsonFiles = Directory.Exists(ctx.ScnPureWorkDir)
            ? Directory.GetFiles(ctx.ScnPureWorkDir, "*.json", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".resx.json"))
                .ToArray()
            : [];

        if (jsonFiles.Length > 0)
        {
            ConsoleHelper.PrintInfo($"SCN_PURE_WORK/ 已有 {jsonFiles.Length} 个 JSON 文件，跳过反编译");
            return true;
        }

        var scnFiles = Directory.GetFiles(ctx.ScnPureDir, "*.scn", SearchOption.AllDirectories);
        if (scnFiles.Length == 0)
        {
            ConsoleHelper.PrintWarning("SCN_PURE/ 中没有 SCN 文件");
            return false;
        }

        ConsoleHelper.PrintInfo($"共 {scnFiles.Length} 个 SCN 文件，开始反编译为 JSON...");
        Directory.CreateDirectory(ctx.ScnPureWorkDir);

        var maxParallel = ConsoleHelper.EnsureParallelism(ctx);

        var successCount = 0;
        var failCount = 0;
        var processed = 0;
        var total = scnFiles.Length;

        var semaphore = new SemaphoreSlim(maxParallel);
        var tasks = new List<Task>();

        foreach (var scnFile in scnFiles)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var ok = await DecompileScnAsync(scnFile, ctx);
                    if (ok)
                        Interlocked.Increment(ref successCount);
                    else
                        Interlocked.Increment(ref failCount);
                }
                finally
                {
                    semaphore.Release();
                    Interlocked.Increment(ref processed);
                    lock (ProgressLock)
                    {
                        ConsoleHelper.PrintProgress(
                            Volatile.Read(ref processed), total,
                            $"成功: {Volatile.Read(ref successCount)}, 跳过: {Volatile.Read(ref failCount)}");
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine();
        ConsoleHelper.PrintSuccess($"反编译完成: 成功 {successCount}, 跳过 {failCount}");
        return successCount > 0;
    }

    // ========== 导出 TXT ==========

    private static void ExportTxt(PipelineContext ctx)
    {
        ConsoleHelper.PrintInfo("正在提取文本...");
        Directory.CreateDirectory(ctx.ScnPureTxtDir);

        var jsonFiles = Directory.GetFiles(ctx.ScnPureWorkDir, "*.json", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".resx.json"))
            .ToArray();

        var txtCount = 0;
        var totalLines = 0;
        var mismatchFiles = new List<string>();

        foreach (var jsonFile in jsonFiles)
        {
            var result = ExtractTextFromJson(jsonFile);
            if (!result.Ok || (result.Lines.Count == 0 && result.Titles.Count == 0)) continue;

            var relativePath = Path.GetRelativePath(ctx.ScnPureWorkDir, jsonFile);
            var txtRelative = Path.ChangeExtension(relativePath, ".txt");
            var txtPath = Path.Combine(ctx.ScnPureTxtDir, txtRelative);
            var txtDir = Path.GetDirectoryName(txtPath);
            if (txtDir != null)
                Directory.CreateDirectory(txtDir);

            WriteDualLineText(txtPath, result.Lines, result.Titles);
            txtCount++;
            totalLines += result.Lines.Count;

            if (result.Lines.Count != result.TotalTexts)
                mismatchFiles.Add($"{result.ScnName}: 总计 {result.TotalTexts}, 提取 {result.Lines.Count}");
        }

        ConsoleHelper.PrintSuccess($"文本提取完成: {txtCount} 个文件, 共 {totalLines} 条对话");
        ConsoleHelper.PrintInfo($"输出目录: {ctx.ScnPureTxtDir}");

        if (mismatchFiles.Count > 0)
        {
            ConsoleHelper.PrintWarning($"以下 {mismatchFiles.Count} 个文件提取数量与总数不一致：");
            foreach (var m in mismatchFiles)
                ConsoleHelper.PrintHint($"  {m}");
        }
        else
        {
            ConsoleHelper.PrintSuccess("校验通过: 所有文件提取数量与 texts 总数一致");
        }
    }

    // ========== 回注翻译文本 ==========

    private record TranslatedEntry(string? Name, string? Msg);

    private record TranslationData(
        Dictionary<int, TranslatedEntry> Entries,
        Dictionary<int, string> Titles);

    private static async Task InjectTranslationAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "回注翻译文本");

        if (!Directory.Exists(ctx.ScnPureTxtTransDir))
        {
            ConsoleHelper.PrintError($"翻译文本目录不存在: {ctx.ScnPureTxtTransDir}");
            ConsoleHelper.PrintHint("请将翻译后的 TXT 文件放入 SCN_PURE_TXT_TRANS/，目录结构与 SCN_PURE_TXT/ 一致");
            return;
        }

        var txtFiles = Directory.GetFiles(ctx.ScnPureTxtTransDir, "*.txt", SearchOption.AllDirectories);
        if (txtFiles.Length == 0)
        {
            ConsoleHelper.PrintWarning("SCN_PURE_TXT_TRANS/ 中没有 TXT 文件");
            return;
        }

        ConsoleHelper.PrintInfo($"共 {txtFiles.Length} 个翻译文件待回注");
        Directory.CreateDirectory(ctx.ScnPureWorkTransDir);

        var injectedCount = 0;
        var skipCount = 0;
        var failCount = 0;
        var failedFiles = new List<string>();

        foreach (var txtFile in txtFiles)
        {
            var relativePath = Path.GetRelativePath(ctx.ScnPureTxtTransDir, txtFile);
            var jsonRelative = Path.ChangeExtension(relativePath, ".json");
            var jsonPath = Path.Combine(ctx.ScnPureWorkDir, jsonRelative);

            if (!File.Exists(jsonPath))
            {
                skipCount++;
                continue;
            }

            try
            {
                var data = ParseTranslatedTxt(txtFile);
                if (data.Entries.Count == 0 && data.Titles.Count == 0)
                {
                    skipCount++;
                    continue;
                }

                var modified = InjectIntoJson(jsonPath, data);

                var outJsonPath = Path.Combine(ctx.ScnPureWorkTransDir, jsonRelative);
                var outDir = Path.GetDirectoryName(outJsonPath);
                if (outDir != null)
                    Directory.CreateDirectory(outDir);

                File.WriteAllText(outJsonPath, modified, new UTF8Encoding(false));

                // 复制 .resx.json（PsBuild 编译需要）
                var resxName = Path.ChangeExtension(jsonRelative, null) + ".resx.json";
                var resxSrc = Path.Combine(ctx.ScnPureWorkDir, resxName);
                if (File.Exists(resxSrc))
                {
                    var resxDst = Path.Combine(ctx.ScnPureWorkTransDir, resxName);
                    File.Copy(resxSrc, resxDst, overwrite: true);
                }

                injectedCount++;
            }
            catch (Exception ex)
            {
                failCount++;
                failedFiles.Add($"{relativePath}: {ex.Message}");
            }
        }

        ConsoleHelper.PrintSuccess($"回注完成: 成功 {injectedCount}, 跳过 {skipCount}, 失败 {failCount}");
        ConsoleHelper.PrintInfo($"输出目录: {ctx.ScnPureWorkTransDir}");

        if (failedFiles.Count > 0)
        {
            ConsoleHelper.PrintWarning("以下文件回注失败：");
            foreach (var f in failedFiles)
                ConsoleHelper.PrintHint($"  {f}");
        }

        if (injectedCount > 0 && ConsoleHelper.AskYesNo("是否根据 SCN_PURE_WORK_TRANS/ 重建 SCN 到 SCN_PURE_NEW/？"))
            await RebuildScnAsync(ctx);
    }

    /// <summary>
    /// 解析翻译 TXT 文件的 ◆ 行，返回 {index → TranslatedEntry}
    /// </summary>
    private static TranslationData ParseTranslatedTxt(string txtPath)
    {
        var entries = new Dictionary<int, TranslatedEntry>();
        var titles = new Dictionary<int, string>();
        var lines = File.ReadAllLines(txtPath, Encoding.UTF8);

        foreach (var line in lines)
        {
            if (!line.StartsWith('◆')) continue;

            var parts = line.Split('◆', 4);
            if (parts.Length < 4) continue;

            if (!int.TryParse(parts[1], out var index)) continue;
            var type = parts[2];
            var content = parts[3];

            if (type == "title")
            {
                if (!string.IsNullOrEmpty(content))
                    titles[index] = content;
                continue;
            }

            if (!entries.TryGetValue(index, out var entry))
                entry = new TranslatedEntry(null, null);

            entries[index] = type switch
            {
                "name" => entry with { Name = content.TrimStart('【').TrimEnd('】') },
                "msg" => entry with { Msg = content },
                _ => entry
            };
        }

        return new TranslationData(entries, titles);
    }

    /// <summary>
    /// 将翻译内容注入 JSON，返回修改后的 JSON 字符串
    /// </summary>
    private static string InjectIntoJson(string jsonPath, TranslationData data)
    {
        var jsonText = File.ReadAllText(jsonPath, Encoding.UTF8);
        var root = JsonNode.Parse(jsonText)!;

        var scenes = root["scenes"]?.AsArray();
        if (scenes == null) return jsonText;

        for (var si = 0; si < scenes.Count; si++)
        {
            var scene = scenes[si];
            if (scene == null) continue;

            if (data.Titles.TryGetValue(si, out var newTitle))
                scene["title"] = JsonValue.Create(newTitle);

            var texts = scene["texts"]?.AsArray();
            if (texts == null) continue;

            for (var i = 0; i < texts.Count; i++)
            {
                if (!data.Entries.TryGetValue(i, out var trans)) continue;

                var entry = texts[i]?.AsArray();
                if (entry == null || entry.Count < 2) continue;

                // 回注角色名: entry[0]
                if (trans.Name != null)
                    entry[0] = JsonValue.Create(trans.Name);

                // 回注文本: entry[1][*][1]，同时更新 charCount 和展开形式
                if (trans.Msg != null)
                {
                    var textArray = entry[1]?.AsArray();
                    if (textArray == null) continue;

                    foreach (var sub in textArray)
                    {
                        var subArr = sub?.AsArray();
                        if (subArr == null || subArr.Count < 2) continue;

                        // [1]: 原始文本
                        subArr[1] = JsonValue.Create(trans.Msg);

                        // [2]: 字符数
                        if (subArr.Count >= 3)
                            subArr[2] = JsonValue.Create(trans.Msg.Length);

                        // [3],[4]: 展开形式（\n 去除，$f.name; → ${f.name}）
                        if (subArr.Count >= 5)
                        {
                            var expanded = trans.Msg
                                .Replace("\\n", "")
                                .Replace("$f.name;", "${f.name}");
                            subArr[3] = JsonValue.Create(expanded);
                            subArr[4] = JsonValue.Create(expanded);
                        }
                    }
                }
            }
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ========== 重建 SCN ==========

    private static async Task RebuildScnAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "重建 SCN 文件");

        if (!File.Exists(ctx.PsBuildPath))
        {
            ConsoleHelper.PrintError($"找不到 PsBuild.exe: {ctx.PsBuildPath}");
            return;
        }

        var jsonFiles = Directory.GetFiles(ctx.ScnPureWorkTransDir, "*.json", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".resx.json"))
            .ToArray();

        if (jsonFiles.Length == 0)
        {
            ConsoleHelper.PrintWarning("SCN_PURE_WORK_TRANS/ 中没有 JSON 文件");
            return;
        }

        ConsoleHelper.PrintInfo($"共 {jsonFiles.Length} 个 JSON 文件，开始编译为 SCN...");
        Directory.CreateDirectory(ctx.ScnPureNewDir);

        var maxParallel = ConsoleHelper.EnsureParallelism(ctx);

        var successCount = 0;
        var failCount = 0;
        var processed = 0;
        var total = jsonFiles.Length;
        var failedFiles = new ConcurrentBag<string>();

        var semaphore = new SemaphoreSlim(maxParallel);
        var tasks = new List<Task>();

        foreach (var jsonFile in jsonFiles)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var ok = await BuildScnAsync(jsonFile, ctx);
                    if (ok)
                        Interlocked.Increment(ref successCount);
                    else
                    {
                        Interlocked.Increment(ref failCount);
                        failedFiles.Add(Path.GetFileName(jsonFile));
                    }
                }
                finally
                {
                    semaphore.Release();
                    Interlocked.Increment(ref processed);
                    lock (ProgressLock)
                    {
                        ConsoleHelper.PrintProgress(
                            Volatile.Read(ref processed), total,
                            $"成功: {Volatile.Read(ref successCount)}, 失败: {Volatile.Read(ref failCount)}");
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine();
        ConsoleHelper.PrintSuccess($"重建完成: 成功 {successCount}, 失败 {failCount}");
        ConsoleHelper.PrintInfo($"输出目录: {ctx.ScnPureNewDir}");

        if (failedFiles.Count > 0 && failedFiles.Count <= 20)
        {
            ConsoleHelper.PrintWarning("以下文件编译失败：");
            foreach (var f in failedFiles)
                ConsoleHelper.PrintHint($"  {f}");
        }
    }

    private static async Task<bool> BuildScnAsync(string jsonFilePath, PipelineContext ctx)
    {
        try
        {
            var relativePath = Path.GetRelativePath(ctx.ScnPureWorkTransDir, jsonFilePath);
            var relativeDir = Path.GetDirectoryName(relativePath) ?? "";
            var scnName = Path.ChangeExtension(Path.GetFileName(relativePath), ".scn");

            var outputDir = Path.Combine(ctx.ScnPureNewDir, relativeDir);
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, scnName);

            var psi = new ProcessStartInfo
            {
                FileName = ctx.PsBuildPath,
                Arguments = $"-p krkr -o \"{outputPath}\" \"{jsonFilePath}\"",
                WorkingDirectory = Path.GetDirectoryName(jsonFilePath) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            await proc.WaitForExitAsync();
            return File.Exists(outputPath);
        }
        catch
        {
            return false;
        }
    }

    // ========== SCN → JSON 反编译 ==========

    private static async Task<bool> DecompileScnAsync(string scnFilePath, PipelineContext ctx)
    {
        try
        {
            var relativePath = Path.GetRelativePath(ctx.ScnPureDir, scnFilePath);
            var relativeDir = Path.GetDirectoryName(relativePath) ?? "";
            var outputDir = Path.Combine(ctx.ScnPureWorkDir, relativeDir);
            Directory.CreateDirectory(outputDir);

            var psi = new ProcessStartInfo
            {
                FileName = ctx.PsbDecompilePath,
                Arguments = $"-o \"{outputDir}\" \"{scnFilePath}\"",
                WorkingDirectory = Path.GetDirectoryName(scnFilePath) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            await proc.WaitForExitAsync();

            var scnName = Path.GetFileNameWithoutExtension(scnFilePath);
            var jsonPath = Path.Combine(outputDir, scnName + ".json");
            return File.Exists(jsonPath);
        }
        catch
        {
            return false;
        }
    }

    // ========== JSON 文本提取 ==========

    private record DialogueLine(int Index, string? Name, string Text);

    private record SceneTitle(int SceneIndex, string Title);

    private record ExtractionResult(
        string ScnName,
        List<DialogueLine> Lines,
        List<SceneTitle> Titles,
        int TotalTexts, bool Ok);

    // SCN JSON 结构:
    // {
    //   "name": "sc_2_st01.txt",               ← SCN 原始文件名
    //   "scenes": [                            ← 场景数组: 通常 scenes[0] 无文本，scenes[1] 有文本
    //     {
    //       "lines": [ ... ],                  ← 舞台指令流: 通过 int 引用 texts
    //       "texts": [                         ← 对话数据池: 被 lines 引用
    //         [                                ← texts[i]: 单条对话
    //           "角色名" | null,               ← [0]: speaker，null = 旁白
    //           [                              ← [1]: 文本片段数组（通常只有一个子项）
    //             [null, "对话文本", 21]                            ←   标准3元素: [null, 文本, 字符数]
    //             [null, "文本", 39, "展开后文本", "展开后文本"]    ←   扩展5元素: 含 $f.name; 或 \n 时
    //                                                                      [3]:  \n→实际换行
    //                                                                            $f.name;→${f.name} 的展开形式
    //                                                                      [4]: 同 [3]
    //           ],
    //           null,                     ← [2]: 固定 null
    //           192 | 208,                ← [3]: 显示宽度 (192=普通, 208=特殊)
    //           { "env_data": ... }       ← [4]: 环境数据（可选）
    //         ],
    //         ...
    //       ]
    //     }
    //   ]
    // }
    //
    // 文本内控制符（各指令独立，可紧邻组合）:
    //   %f字体名;   — 切换字体，如 %f源ノ角ゴシックB;
    //   %fuser;     — 恢复默认字体
    //   #RRGGBBAA;  — 切换颜色，如 #00ff80c0;
    //   #;          — 恢复默认颜色
    //   %数字;      — 字号百分比，如 %150;（喊叫场景）
    //   $f.name;    — 运行时替换为玩家姓名
    //   \n          — 文本内换行
    //
    // 例: "はっけぇ～ん%f源ノ角ゴシックB;#00ff80c0;♥%fuser;#;"
    //   → 正文 → 切换字体 → 设颜色 → 显示♥ → 恢复字体 → 恢复颜色
    private static ExtractionResult ExtractTextFromJson(string jsonPath)
    {
        var lines = new List<DialogueLine>();
        var titles = new List<SceneTitle>();
        var totalTexts = 0;
        var scnName = "";

        try
        {
            var jsonText = File.ReadAllText(jsonPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var nameProp))
                scnName = nameProp.GetString() ?? "";

            if (!root.TryGetProperty("scenes", out var scenes))
                return new ExtractionResult(scnName, lines, titles, 0, false);

            var sceneIndex = 0;
            foreach (var scene in scenes.EnumerateArray())
            {
                if (scene.TryGetProperty("title", out var titleProp)
                    && titleProp.ValueKind == JsonValueKind.String)
                {
                    var title = titleProp.GetString();
                    if (!string.IsNullOrEmpty(title))
                        titles.Add(new SceneTitle(sceneIndex, title));
                }

                if (!scene.TryGetProperty("texts", out var texts))
                {
                    sceneIndex++;
                    continue;
                }

                var index = 0;
                foreach (var entry in texts.EnumerateArray())
                {
                    totalTexts++;

                    // texts[i] = [name, textFragments, null, displayWidth, envData]
                    if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
                    {
                        index++;
                        continue;
                    }

                    // entry[0]: 角色名 (string) 或 null (旁白)
                    string? name = null;
                    var nameElem = entry[0];
                    if (nameElem.ValueKind == JsonValueKind.String)
                        name = nameElem.GetString();

                    // entry[1]: 文本片段数组 [[null, "文本", charCount, ...], ...]
                    var textArray = entry[1];
                    if (textArray.ValueKind != JsonValueKind.Array)
                    {
                        index++;
                        continue;
                    }

                    foreach (var sub in textArray.EnumerateArray())
                    {
                        // sub = [null, "对话文本", charCount] 或 5元素扩展形式
                        if (sub.ValueKind == JsonValueKind.Array && sub.GetArrayLength() >= 2)
                        {
                            var textElem = sub[1]; // 原始文本（保留 \n、$f.name; 等控制符）
                            if (textElem.ValueKind == JsonValueKind.String)
                            {
                                var text = textElem.GetString();
                                if (!string.IsNullOrEmpty(text))
                                    lines.Add(new DialogueLine(index, name, text));
                            }
                        }
                    }

                    index++;
                }
                sceneIndex++;
            }

            return new ExtractionResult(scnName, lines, titles, totalTexts, true);
        }
        catch
        {
            return new ExtractionResult(scnName, lines, titles, totalTexts, false);
        }
    }

    // ========== 双行文本输出 ==========

    private static void WriteDualLineText(string path, List<DialogueLine> lines, List<SceneTitle> titles)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));

        foreach (var t in titles)
        {
            var si = t.SceneIndex.ToString("D4");
            writer.WriteLine($"◇{si}◇title◇{t.Title}");
            writer.WriteLine($"◆{si}◆title◆{t.Title}");
            writer.WriteLine();
        }

        foreach (var line in lines)
        {
            var idx = line.Index.ToString("D4");

            if (line.Name != null)
            {
                writer.WriteLine($"◇{idx}◇name◇【{line.Name}】");
                writer.WriteLine($"◆{idx}◆name◆【{line.Name}】");
                writer.WriteLine();
            }

            writer.WriteLine($"◇{idx}◇msg◇{line.Text}");
            writer.WriteLine($"◆{idx}◆msg◆{line.Text}");
            writer.WriteLine();
        }
    }

    // ========== filedict 生成 ==========

    private static readonly Regex ExtensionPattern = new(@"\.[a-zA-Z0-9]{1,6}$", RegexOptions.Compiled);
    // private static readonly Regex VersionPattern = new(@"^(.+?)(_[a-zA-Z0-9]{1,2})?(\.[^.]+)$", RegexOptions.Compiled);
    private static readonly Regex DefaultVersionPattern = new(@"^(.+?)(_[a-zA-Z0-9]{1})?(\.[^.]+)$", RegexOptions.Compiled);
    private static readonly Regex SteamVersionPattern = new(@"^(.+?)(_[a-zA-Z0-9]{1,2})?(\.[^.]+)$", RegexOptions.Compiled);
    private static readonly Regex EvPrefixPattern = new(@"^ev(cg|cgx|ed)\d+", RegexOptions.Compiled);
    private static readonly Regex CgEdPattern = new(@"^(cg|cgx|ed)\d+", RegexOptions.Compiled);
    private static readonly Regex FileRefRegex = new(@"""([^""\\]*\.[a-zA-Z0-9]{1,6})""", RegexOptions.Compiled);

    private static Task GenerateFileDictAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "生成 filedict（撞库字典）");

        if (!Directory.Exists(ctx.ExtractOutputDir))
        {
            ConsoleHelper.PrintError($"Extractor_Output 不存在: {ctx.ExtractOutputDir}");
            ConsoleHelper.PrintHint("请先执行 Function 1 解包");
            return Task.CompletedTask;
        }

        // Step 1: 加载 normaldict.json
        if (!File.Exists(ctx.NormalDictPath))
        {
            ConsoleHelper.PrintError($"normaldict.json 不存在: {ctx.NormalDictPath}");
            ConsoleHelper.PrintHint("请将 normaldict.json 放入 scr/dict/ 目录");
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(ctx.DictDir);
        ConsoleHelper.PrintInfo("加载 normaldict.json...");
        var (normalFiles, normalDirs) = LoadNormalDict(ctx.NormalDictPath);
        ConsoleHelper.PrintInfo($"  normaldict: {normalFiles.Count} 文件, {normalDirs.Count} 目录");

        // Step 2: SCN JSON 结构化提取 + 正则提取
        var scnRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(ctx.ScnPureWorkDir))
        {
            ConsoleHelper.PrintInfo("正在从 SCN JSON 提取文件引用...");
            var jsonFiles = Directory.GetFiles(ctx.ScnPureWorkDir, "*.json", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".resx.json"))
                .ToArray();
            foreach (var jf in jsonFiles)
            {
                ExtractStructuredReferences(jf, scnRefs);
                ExtractRegexReferences(jf, scnRefs);
            }
            ConsoleHelper.PrintInfo($"  SCN 引用: {scnRefs.Count} 个");
        }
        else
        {
            ConsoleHelper.PrintWarning("SCN_PURE_WORK/ 不存在，跳过 SCN 引用提取");
        }

        // Step 3: 衍生文件
        var derivatives = GenerateDerivatives(scnRefs);
        scnRefs.UnionWith(derivatives);

        // Step 4: 版本变体
        var enableSteamVersionMatch = ConsoleHelper.AskYesNo("是否开启 Steam 版本匹配（这将会耗时特别长）？", defaultYes: false);
        // var versionExpanded = ExpandVersionedNames(scnRefs);
        var versionExpanded = ExpandVersionedNames(scnRefs, enableSteamVersionMatch);
        scnRefs.UnionWith(versionExpanded);
        ConsoleHelper.PrintInfo($"  + 衍生 & 版本变体后: {scnRefs.Count} 个");

        // Step 5: 枚举模式
        var enumerated = GenerateEnumerationPatterns();
        ConsoleHelper.PrintInfo($"  枚举模式: {enumerated.Count} 个");

        // Step 6: 从 Fixname_Output 中已还原的 .dref 提取 dpak 引用（第二轮撞库时生效）
        var drefRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(ctx.FixnameOutputDir))
        {
            drefRefs = ExtractDrefReferences(ctx.FixnameOutputDir);
            if (drefRefs.Count > 0)
                ConsoleHelper.PrintInfo($"  .dref dpak 引用（来自 Fixname_Output）: {drefRefs.Count} 个");
            else
                ConsoleHelper.PrintInfo("  Fixname_Output 中未找到 .dref 文件");
        }
        else
        {
            ConsoleHelper.PrintHint("Fixname_Output 不存在，跳过 .dref 扫描（第一轮撞库后再生成字典可提取 dpak 引用）");
        }

        // Step 7: 合并输出 newdict.json
        var newNames = new HashSet<string>(scnRefs, StringComparer.OrdinalIgnoreCase);
        newNames.UnionWith(enumerated);
        newNames.UnionWith(drefRefs);
        newNames.ExceptWith(normalFiles);

        //var newDict = new { files = newNames.Order().ToList() };
        //File.WriteAllText(ctx.NewDictPath,
        //    JsonSerializer.Serialize(newDict, new JsonSerializerOptions { WriteIndented = true }),
        //    new UTF8Encoding(false));

        WriteNewDictJson(ctx.NewDictPath, newNames);
        ConsoleHelper.PrintSuccess($"newdict.json 已生成（{newNames.Count} 候选）");

        // Step 7: files.txt / dirs.txt（UTF-16LE BOM，供 krkr_hxv4_hash.py 使用）
        var allFiles = new HashSet<string>(normalFiles, StringComparer.OrdinalIgnoreCase);
        allFiles.UnionWith(newNames);

        //WriteUtf16LeList(ctx.FilesListPath, allFiles.Order());
        //WriteUtf16LeList(ctx.DirsListPath, normalDirs.Order());

        WriteUtf16LeList(ctx.FilesListPath, allFiles);
        WriteUtf16LeList(ctx.DirsListPath, normalDirs);

        ConsoleHelper.PrintSuccess($"files.txt ({allFiles.Count} 条) 和 dirs.txt ({normalDirs.Count} 条) 已生成");
        ConsoleHelper.PrintInfo($"输出目录: {ctx.DictDir}");

        return Task.CompletedTask;
    }

    private static (HashSet<string> files, HashSet<string> dirs) LoadNormalDict(string path)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var text = File.ReadAllText(path, Encoding.UTF8);
        using var doc = JsonDocument.Parse(text);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            if (prop.Name == "dirs")
            {
                foreach (var item in prop.Value.EnumerateArray())
                {
                    var v = item.GetString();
                    if (!string.IsNullOrEmpty(v)) dirs.Add(v);
                }
            }
            else
            {
                foreach (var item in prop.Value.EnumerateArray())
                {
                    var v = item.GetString();
                    if (!string.IsNullOrEmpty(v)) files.Add(v);
                }
            }
        }

        return (files, dirs);
    }

    // ========== SCN JSON 结构化引用提取 ==========

    private static void ExtractStructuredReferences(string jsonPath, HashSet<string> refs)
    {
        try
        {
            var text = File.ReadAllText(jsonPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(text);
            WalkJsonForRefs(doc.RootElement, refs, new Dictionary<string, string>());
        }
        catch { }
    }

    private static void WalkJsonForRefs(JsonElement element, HashSet<string> refs, Dictionary<string, string> context)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var localCtx = new Dictionary<string, string>(context);
            if (element.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                localCtx["name"] = nameProp.GetString() ?? "";
            if (element.TryGetProperty("class", out var classProp) && classProp.ValueKind == JsonValueKind.String)
                localCtx["class"] = classProp.GetString() ?? "";

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name == "filename" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var v = prop.Value.GetString();
                    if (!string.IsNullOrEmpty(v))
                        HandleFilenameRef(v, refs);
                }
                else if (prop.Name is "file" or "imageFile" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var v = prop.Value.GetString();
                    if (!string.IsNullOrEmpty(v))
                        HandleFileRef(v, localCtx, refs);
                }
                else
                {
                    WalkJsonForRefs(prop.Value, refs, localCtx);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                WalkJsonForRefs(item, refs, context);
        }
    }

    private static void HandleFilenameRef(string value, HashSet<string> refs)
    {
        if (!ExtensionPattern.IsMatch(value))
        {
            refs.Add($"{value}.ogg");
            refs.Add($"{value}.ogg.sli");
        }
        else
        {
            refs.Add(value);
        }
    }

    private static void HandleFileRef(string value, Dictionary<string, string> context, HashSet<string> refs)
    {
        if (value.StartsWith('&')) return;

        if (ExtensionPattern.IsMatch(value))
        {
            refs.Add(value);
            if (value.EndsWith(".stand", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = value[..^6];
                refs.Add($"{baseName}.pimg");
                refs.Add($"{baseName}.sinfo");
            }
            return;
        }

        if (value.StartsWith("ev", StringComparison.Ordinal) && EvPrefixPattern.IsMatch(value))
        {
            var stripped = value[2..];
            refs.Add($"{stripped}.dref");
            refs.Add($"{stripped}.dpak");
            refs.Add($"{stripped}.tlg");
            return;
        }

        if (CgEdPattern.IsMatch(value))
        {
            refs.Add($"{value}.dref");
            refs.Add($"{value}.dpak");
            refs.Add($"{value}.tlg");
            return;
        }

        if (value.StartsWith('@'))
        {
            refs.Add($"{value}.png");
            return;
        }

        var cls = context.GetValueOrDefault("class", "");
        switch (cls)
        {
            case "stage":
                refs.Add($"{value}.png");
                refs.Add($"{value}.jpg");
                break;
            case "event":
            case "evcutin":
                refs.Add($"{value}.dref");
                refs.Add($"{value}.dpak");
                refs.Add($"{value}.tlg");
                refs.Add($"{value}.mpg");
                break;
            case "character":
                refs.Add($"{value}.stand");
                refs.Add($"{value}.pimg");
                refs.Add($"{value}.sinfo");
                break;
            case "emotion":
            case "layer":
                refs.Add($"{value}.png");
                break;
            case "msgwin":
            case "udmask":
            case "cdowncls":
                refs.Add($"{value}.png");
                refs.Add($"{value}.tlg");
                break;
            case "movie":
                refs.Add($"{value}.mpg");
                break;
            default:
                refs.Add($"{value}.png");
                refs.Add($"{value}.tlg");
                refs.Add($"{value}.dref");
                refs.Add($"{value}.dpak");
                break;
        }
    }

    private static void ExtractRegexReferences(string jsonPath, HashSet<string> refs)
    {
        try
        {
            var text = File.ReadAllText(jsonPath, Encoding.UTF8);
            foreach (Match m in FileRefRegex.Matches(text))
            {
                var name = m.Groups[1].Value;
                if (!name.Contains('/') && !name.Contains('\\') && name.Length <= 120 && !name.StartsWith('.'))
                    refs.Add(name);
            }
        }
        catch { }
    }

    // ========== 衍生 / 版本变体 / 枚举 ==========

    private static HashSet<string> GenerateDerivatives(HashSet<string> names)
    {
        var d = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                d.Add($"{name}.sli");
            if (name.EndsWith(".stand", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = name[..^6];
                d.Add($"{baseName}.pimg");
                d.Add($"{baseName}.sinfo");
            }
        }
        return d;
    }

    // private static readonly string[] VersionSuffixes = GenerateVersionSuffixes();

    private static string[] GenerateVersionSuffixes(bool includeSteamVersionSuffixes)
    {
        var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chars = "abcdefghijklmnopqrstuvwxyz";

        // 1位: _1~_99, _a~_z
        // for (var i = 1; i <= 99; i++)
        //     suffixes.Add($"_{i}");
        for (var i = 1; i <= 9; i++)
            suffixes.Add($"_{i}");
        foreach (var c in chars)
            suffixes.Add($"_{c}");

        // 2位字母: _tw, _ab, ...
        // foreach (var c1 in chars)
        //     foreach (var c2 in chars)
        //         suffixes.Add($"_{c1}{c2}");

        // 2位混合: _a1~_a9, _1a~_9z
        // foreach (var c in chars)
        //     for (var i = 1; i <= 9; i++)
        //     {
        //         suffixes.Add($"_{c}{i}");
        //         suffixes.Add($"_{i}{c}");
        //     }

        if (includeSteamVersionSuffixes)
        {
            for (var i = 10; i <= 99; i++)
                suffixes.Add($"_{i}");

            foreach (var c1 in chars)
                foreach (var c2 in chars)
                    suffixes.Add($"_{c1}{c2}");

            foreach (var c in chars)
                for (var i = 1; i <= 9; i++)
                {
                    suffixes.Add($"_{c}{i}");
                    suffixes.Add($"_{i}{c}");
                }
        }

        return suffixes.ToArray();
    }

    // private static HashSet<string> ExpandVersionedNames(HashSet<string> names)
    private static HashSet<string> ExpandVersionedNames(HashSet<string> names, bool includeSteamVersionSuffixes)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versionPattern = includeSteamVersionSuffixes ? SteamVersionPattern : DefaultVersionPattern;
        var versionSuffixes = GenerateVersionSuffixes(includeSteamVersionSuffixes);
        foreach (var name in names)
        {
            // var match = VersionPattern.Match(name);
            var match = versionPattern.Match(name);
            if (!match.Success) continue;

            var basePart = match.Groups[1].Value;
            var ext = match.Groups[3].Value;

            // foreach (var suffix in VersionSuffixes)
            //     expanded.Add($"{basePart}{suffix}{ext}");
            foreach (var suffix in versionSuffixes)
                expanded.Add($"{basePart}{suffix}{ext}");
        }
        return expanded;
    }

    private static void WriteNewDictJson(string path, IEnumerable<string> files)
    {

        //using var stream = File.Create(path);
        //using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        //writer.WriteStartObject();
        //writer.WritePropertyName("files");
        //writer.WriteStartArray();
        //foreach (var file in files)
        //    writer.WriteStringValue(file);
        //writer.WriteEndArray();
        //writer.WriteEndObject();

        using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1024 * 1024);
        writer.WriteLine("{");
        writer.WriteLine("  \"files\": [");

        var first = true;
        foreach (var file in files)
        {
            if (!first)
                writer.WriteLine(",");

            writer.Write("    ");
            writer.Write(JsonSerializer.Serialize(file));
            first = false;
        }

        if (!first)
            writer.WriteLine();

        writer.WriteLine("  ]");
        writer.WriteLine("}");
    }

    private static HashSet<string> GenerateEnumerationPatterns()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 角色语音
        var charIds = new List<string>();
        foreach (var (prefix, start, end) in new[] { ('a', 1, 10), ('b', 21, 30), ('c', 41, 43) })
            for (var i = start; i <= end; i++)
                charIds.Add($"{prefix}{i:D3}");

        foreach (var cid in charIds)
        {
            foreach (var bgvType in new[] { "aegi", "fera" })
                foreach (var sub in new[] { "chu", "jyaku", "kyou" })
                    for (var v = 1; v <= 3; v++)
                    {
                        names.Add($"{cid}_bgv_{bgvType}_{sub}{v}.ogg");
                        names.Add($"{cid}_bgv_{bgvType}_{sub}{v}.ogg.sli");
                    }

            for (var v = 1; v <= 2; v++)
            {
                names.Add($"{cid}_bgv_jigo_{v}.ogg");
                names.Add($"{cid}_bgv_jigo_{v}.ogg.sli");
            }
            names.Add($"{cid}_dokofera01.ogg");
            names.Add($"{cid}_dokofera01.ogg.sli");

            for (var num = 1; num <= 3000; num++)
            {
                names.Add($"{cid}_{num:D5}.ogg");
                names.Add($"{cid}_{num:D5}.ogg.sli");
            }
        }

        // bgm
        for (var i = 1; i < 100; i++)
        {
            names.Add($"bgm{i:D2}.ogg");
            names.Add($"bgm{i:D2}.ogg.sli");
        }

        // sys 音效
        foreach (var n in new[] { "sys_back", "sys_info", "sys_over", "sys_push" })
        {
            names.Add($"{n}.ogg");
            names.Add($"{n}.ogg.sli");
            for (var i = 1; i <= 10; i++)
            {
                names.Add($"{n}_{i}.ogg");
                names.Add($"{n}_{i}.ogg.sli");
            }
        }

        // brand / title
        foreach (var n in new[] { "brand", "title" })
        {
            names.Add($"{n}.ogg");
            names.Add($"{n}.ogg.sli");
            for (var i = 1; i <= 10; i++)
            {
                names.Add($"{n}_{i}.ogg");
                names.Add($"{n}_{i}.ogg.sli");
            }
        }
        foreach (var pfx in new[] { "title_a", "title_b", "title_c" })
            for (var i = 0; i < 100; i++)
            {
                names.Add($"{pfx}{i:D3}.ogg");
                names.Add($"{pfx}{i:D3}.ogg.sli");
            }

        // cha*_up.pimg
        for (var i = 1; i < 200; i++)
            names.Add($"cha{i:D3}_up.pimg");

        // thum_cg*.png
        for (var i = 1; i < 200; i++)
            for (var j = 0; j < 100; j++)
                names.Add($"thum_cg{i:D3}_{j:D2}.png");

        // ev_cg*.l2d（含后缀字母变体）
        for (var i = 1; i < 200; i++)
            for (var j = 0; j < 100; j++)
            {
                names.Add($"ev_cg{i:D3}_{j:D2}.l2d");
                foreach (var s in "sabcdefgh")
                    names.Add($"ev_cg{i:D3}_{j:D2}{s}.l2d");
            }

        // 硬编码
        names.Add("skill_main.mpg");
        names.Add("skill_mam.mpg");
        names.Add("stuff.mpg");

        return names;
    }

    // ========== .dref → dpak 引用提取 ==========

    internal static HashSet<string> ExtractDrefReferences(string dumpDir)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] drefFiles;
        try
        {
            drefFiles = Directory.GetFiles(dumpDir, "*.dref", SearchOption.AllDirectories);
        }
        catch
        {
            return refs;
        }

        foreach (var drefPath in drefFiles)
        {
            try
            {
                var text = ReadDrefFile(drefPath);
                if (string.IsNullOrEmpty(text)) continue;

                foreach (var line in text.Split('\n'))
                {
                    var trimmed = line.Trim('\r', ' ');
                    if (!trimmed.StartsWith("psb://", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var path = trimmed[6..];
                    var slashIdx = path.IndexOf('/');
                    if (slashIdx < 0)
                    {
                        if (!string.IsNullOrEmpty(path))
                            refs.Add(path);
                        continue;
                    }

                    var dpakName = path[..slashIdx];
                    var contentName = path[(slashIdx + 1)..];

                    if (!string.IsNullOrEmpty(dpakName))
                    {
                        refs.Add(dpakName);
                        refs.Add($"{dpakName}.dpak");
                    }
                    if (!string.IsNullOrEmpty(contentName))
                        refs.Add(contentName);
                }
            }
            catch { }
        }

        return refs;
    }

    private static string ReadDrefFile(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length < 3) return "";

        if (bytes[0] == 0xFE && bytes[1] == 0xFE)
            return DecryptFeFe(bytes);

        return File.ReadAllText(filePath, Encoding.UTF8);
    }

    private static string DecryptFeFe(byte[] bytes)
    {
        if (bytes.Length < 5) return "";
        var mode = bytes[2];

        switch (mode)
        {
            case 0:
            {
                var charCount = (bytes.Length - 5) / 2;
                var sb = new StringBuilder(charCount);
                for (var i = 0; i < charCount; i++)
                {
                    var c = (ushort)(bytes[5 + i * 2] | (bytes[6 + i * 2] << 8));
                    if (c >= 0x20)
                        c = (ushort)(c ^ ((c & 0xFFFE) << 8) ^ 1);
                    sb.Append((char)c);
                }
                return sb.ToString();
            }
            case 1:
            {
                var charCount = (bytes.Length - 5) / 2;
                var sb = new StringBuilder(charCount);
                for (var i = 0; i < charCount; i++)
                {
                    var c = (ushort)(bytes[5 + i * 2] | (bytes[6 + i * 2] << 8));
                    var a = (ushort)(c << 1);
                    var b = (ushort)(c >> 1);
                    sb.Append((char)(ushort)(a ^ ((a ^ b) & 0x5555)));
                }
                return sb.ToString();
            }
            case 2:
            {
                if (bytes.Length < 21) return "";
                var compressedSize = BitConverter.ToInt32(bytes, 5);
                if (compressedSize <= 0 || 21 + compressedSize > bytes.Length)
                    return "";
                try
                {
                    using var ms = new MemoryStream(bytes, 21, compressedSize);
                    using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
                    using var reader = new StreamReader(zlib, Encoding.Unicode);
                    return reader.ReadToEnd();
                }
                catch
                {
                    return "";
                }
            }
            default:
                return "";
        }
    }

    internal static void WriteUtf16LeList(string path, IEnumerable<string> items)
    {
        using var writer = new StreamWriter(path, false, new UnicodeEncoding(false, true));
        foreach (var item in items)
            writer.WriteLine(item);
    }
}
