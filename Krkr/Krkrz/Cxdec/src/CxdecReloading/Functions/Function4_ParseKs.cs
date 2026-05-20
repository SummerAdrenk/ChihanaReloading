using System.Text;
using System.Text.RegularExpressions;

namespace CxdecReloading.Functions;

/// <summary>
/// Function 4: 解析 KS 脚本
/// 子功能：导出可翻译文本 / 回注翻译文本
///
/// 支持两种 KS 文件模式：
///   1. quest_flags 类：提取 @info text="..." 和 @text default="..."
///   2. ona_history 类：提取 ;■标题、■子标题、■后至 @exit 之间的正文
/// </summary>
public static class Function4_ParseKs
{
    private static readonly Regex InfoTextRegex =
        new(@"@info\s+.*?text=""([^""]*)""", RegexOptions.Compiled);

    private static readonly Regex TextDefaultRegex =
        new(@"@text\s+.*?default=""([^""]*)""", RegexOptions.Compiled);

    private const string BodyLineSeparator = @"\n";

    private record KsEntry(int Index, string Type, string Content, int StartLine, int EndLine);

    public static Task RunAsync(PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "Function 4: 解析 KS 脚本");

        var sourceDir = ConsoleHelper.AskInput(
            "请输入 KS 文件所在目录", ctx.FixnameOutputDir);

        if (!Directory.Exists(sourceDir))
        {
            ConsoleHelper.PrintError($"目录不存在: {sourceDir}");
            return Task.CompletedTask;
        }

        var ksFiles = Directory.GetFiles(sourceDir, "*.ks", SearchOption.AllDirectories);
        if (ksFiles.Length == 0)
        {
            ConsoleHelper.PrintWarning($"在 {sourceDir} 中未找到 KS 文件");
            return Task.CompletedTask;
        }

        ConsoleHelper.PrintInfo($"共 {ksFiles.Length} 个 KS 文件");

        while (true)
        {
            var choice = ConsoleHelper.AskMenu("请选择操作:",
                "导出 TXT（提取双行文本）",
                "回注翻译文本（TXT → KS）");

            switch (choice)
            {
                case 1:
                    ExportKsTxt(ksFiles, sourceDir, ctx);
                    break;
                case 2:
                    InjectKsTranslation(ksFiles, sourceDir, ctx);
                    break;
                case 0:
                    return Task.CompletedTask;
            }
        }
    }

    // ========== 导出 TXT ==========

    private static void ExportKsTxt(string[] ksFiles, string sourceDir, PipelineContext ctx)
    {
        ConsoleHelper.PrintInfo("正在提取 KS 脚本文本...");
        Directory.CreateDirectory(ctx.KsTxtDir);

        var txtCount = 0;
        var totalEntries = 0;

        foreach (var ksFile in ksFiles)
        {
            var (lines, _) = ReadKsFile(ksFile);
            var entries = ExtractFromKsLines(lines);
            if (entries.Count == 0) continue;

            var relativePath = Path.GetRelativePath(sourceDir, ksFile);
            var txtRelative = Path.ChangeExtension(relativePath, ".txt");
            var txtPath = Path.Combine(ctx.KsTxtDir, txtRelative);
            var txtDir = Path.GetDirectoryName(txtPath);
            if (txtDir != null)
                Directory.CreateDirectory(txtDir);

            WriteKsDualLineText(txtPath, entries);
            txtCount++;
            totalEntries += entries.Count;
        }

        ConsoleHelper.PrintSuccess($"KS 文本提取完成: {txtCount} 个文件, 共 {totalEntries} 条文本");
        ConsoleHelper.PrintInfo($"输出目录: {ctx.KsTxtDir}");
    }

    // ========== 回注翻译文本 ==========

    private static void InjectKsTranslation(string[] ksFiles, string sourceDir, PipelineContext ctx)
    {
        ConsoleHelper.PrintStepHeader(0, "回注 KS 翻译文本");

        if (!Directory.Exists(ctx.KsTxtTransDir))
        {
            ConsoleHelper.PrintError($"翻译文本目录不存在: {ctx.KsTxtTransDir}");
            ConsoleHelper.PrintHint("请将翻译后的 TXT 文件放入 KS_TXT_TRANS/，目录结构与 KS_TXT/ 一致");
            return;
        }

        var txtFiles = Directory.GetFiles(ctx.KsTxtTransDir, "*.txt", SearchOption.AllDirectories);
        if (txtFiles.Length == 0)
        {
            ConsoleHelper.PrintWarning("KS_TXT_TRANS/ 中没有 TXT 文件");
            return;
        }

        ConsoleHelper.PrintInfo($"共 {txtFiles.Length} 个翻译文件待回注");
        Directory.CreateDirectory(ctx.KsNewDir);

        var injectedCount = 0;
        var skipCount = 0;
        var failCount = 0;
        var failedFiles = new List<string>();

        foreach (var txtFile in txtFiles)
        {
            var relativePath = Path.GetRelativePath(ctx.KsTxtTransDir, txtFile);
            var ksRelative = Path.ChangeExtension(relativePath, ".ks");

            var originalKsPath = ksFiles.FirstOrDefault(f =>
                Path.GetRelativePath(sourceDir, f)
                    .Equals(ksRelative, StringComparison.OrdinalIgnoreCase));

            if (originalKsPath == null)
            {
                skipCount++;
                continue;
            }

            try
            {
                var translations = ParseTranslatedKsTxt(txtFile);
                if (translations.Count == 0)
                {
                    skipCount++;
                    continue;
                }

                var (originalLines, encoding) = ReadKsFile(originalKsPath);
                var modifiedLines = InjectIntoKsLines(originalLines, translations);

                var outKsPath = Path.Combine(ctx.KsNewDir, ksRelative);
                var outDir = Path.GetDirectoryName(outKsPath);
                if (outDir != null)
                    Directory.CreateDirectory(outDir);

                File.WriteAllLines(outKsPath, modifiedLines, encoding);
                injectedCount++;
            }
            catch (Exception ex)
            {
                failCount++;
                failedFiles.Add($"{relativePath}: {ex.Message}");
            }
        }

        ConsoleHelper.PrintSuccess($"回注完成: 成功 {injectedCount}, 跳过 {skipCount}, 失败 {failCount}");
        ConsoleHelper.PrintInfo($"输出目录: {ctx.KsNewDir}");

        if (failedFiles.Count > 0)
        {
            ConsoleHelper.PrintWarning("以下文件回注失败：");
            foreach (var f in failedFiles)
                ConsoleHelper.PrintHint($"  {f}");
        }
    }

    // ========== 文件读取 ==========

    private static (string[] Lines, Encoding Encoding) ReadKsFile(string path)
    {
        var bytes = File.ReadAllBytes(path);

        Encoding encoding;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            encoding = new UnicodeEncoding(false, true); // UTF-16LE with BOM
        else
            encoding = new UTF8Encoding(true);

        return (File.ReadAllLines(path, encoding), encoding);
    }

    // ========== KS 文本提取 ==========
    //
    // quest_flags 模式:
    //   @info tag=xxx text="可翻译文本"    → info_text
    //   @text ... default="可翻译文本"     → text_default
    //
    // ona_history 模式:
    //   ;■段落标题                          → section
    //   *label                              → (跳过)
    //   ■子标题                             → header
    //   正文第1行                           → body (多行用 \n 连接)
    //   正文第2行
    //   ...
    //   @exit

    private static List<KsEntry> ExtractFromKsLines(string[] lines)
    {
        var entries = new List<KsEntry>();
        var index = 0;
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // @info text="..."
            var m = InfoTextRegex.Match(line);
            if (m.Success)
            {
                entries.Add(new KsEntry(index++, "info_text", m.Groups[1].Value, i, i));
                i++;
                continue;
            }

            // @text default="..."
            m = TextDefaultRegex.Match(line);
            if (m.Success)
            {
                entries.Add(new KsEntry(index++, "text_default", m.Groups[1].Value, i, i));
                i++;
                continue;
            }

            // ;■section → ■header → body → @exit
            if (line.StartsWith(";■"))
            {
                entries.Add(new KsEntry(index++, "section", line[2..], i, i));
                i++;

                // 跳到 ■header（跳过 *label 和空行）
                while (i < lines.Length
                       && !lines[i].StartsWith('■')
                       && !lines[i].StartsWith(";■")
                       && lines[i] != "@exit")
                    i++;

                if (i < lines.Length && lines[i].StartsWith('■'))
                {
                    entries.Add(new KsEntry(index++, "header", lines[i][1..], i, i));
                    i++;

                    // 收集正文行直到 @exit 或下一个 ;■
                    var bodyStart = i;
                    while (i < lines.Length && lines[i] != "@exit" && !lines[i].StartsWith(";■"))
                        i++;

                    // 修剪尾部空行
                    var bodyEnd = i - 1;
                    while (bodyEnd >= bodyStart && string.IsNullOrWhiteSpace(lines[bodyEnd]))
                        bodyEnd--;

                    if (bodyEnd >= bodyStart)
                    {
                        var bodyLines = new List<string>();
                        for (var k = bodyStart; k <= bodyEnd; k++)
                            bodyLines.Add(lines[k]);
                        entries.Add(new KsEntry(
                            index++, "body",
                            string.Join(BodyLineSeparator, bodyLines),
                            bodyStart, bodyEnd));
                    }

                    // 跳过 @exit（若存在）
                    if (i < lines.Length && lines[i] == "@exit")
                        i++;
                }

                continue;
            }

            i++;
        }

        return entries;
    }

    // ========== 双行文本输出 ==========

    private static void WriteKsDualLineText(string path, List<KsEntry> entries)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));

        foreach (var entry in entries)
        {
            var idx = entry.Index.ToString("D4");
            writer.WriteLine($"◇{idx}◇{entry.Type}◇{entry.Content}");
            writer.WriteLine($"◆{idx}◆{entry.Type}◆{entry.Content}");
            writer.WriteLine();
        }
    }

    // ========== 翻译 TXT 解析 ==========

    private record TranslatedKsEntry(string Type, string Content);

    private static Dictionary<int, TranslatedKsEntry> ParseTranslatedKsTxt(string txtPath)
    {
        var entries = new Dictionary<int, TranslatedKsEntry>();
        var lines = File.ReadAllLines(txtPath, Encoding.UTF8);

        foreach (var line in lines)
        {
            if (!line.StartsWith('◆')) continue;

            var parts = line.Split('◆', 4);
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[1], out var index)) continue;

            entries[index] = new TranslatedKsEntry(parts[2], parts[3]);
        }

        return entries;
    }

    // ========== KS 回注 ==========

    private static string[] InjectIntoKsLines(
        string[] lines, Dictionary<int, TranslatedKsEntry> translations)
    {
        var entries = ExtractFromKsLines(lines);

        var lineReplacements = new Dictionary<int, string[]>();
        var skipLines = new HashSet<int>();

        foreach (var entry in entries)
        {
            if (!translations.TryGetValue(entry.Index, out var trans)) continue;
            if (trans.Type != entry.Type) continue;

            switch (entry.Type)
            {
                case "info_text":
                {
                    var origLine = lines[entry.StartLine];
                    var m = InfoTextRegex.Match(origLine);
                    if (m.Success)
                    {
                        var before = origLine[..m.Groups[1].Index];
                        var after = origLine[(m.Groups[1].Index + m.Groups[1].Length)..];
                        lineReplacements[entry.StartLine] = [before + trans.Content + after];
                    }
                    break;
                }
                case "text_default":
                {
                    var origLine = lines[entry.StartLine];
                    var m = TextDefaultRegex.Match(origLine);
                    if (m.Success)
                    {
                        var before = origLine[..m.Groups[1].Index];
                        var after = origLine[(m.Groups[1].Index + m.Groups[1].Length)..];
                        lineReplacements[entry.StartLine] = [before + trans.Content + after];
                    }
                    break;
                }
                case "section":
                    lineReplacements[entry.StartLine] = [$";■{trans.Content}"];
                    break;
                case "header":
                    lineReplacements[entry.StartLine] = [$"■{trans.Content}"];
                    break;
                case "body":
                {
                    for (var k = entry.StartLine; k <= entry.EndLine; k++)
                        skipLines.Add(k);
                    lineReplacements[entry.StartLine] =
                        trans.Content.Split(BodyLineSeparator);
                    break;
                }
            }
        }

        var result = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lineReplacements.TryGetValue(i, out var replacement))
                result.AddRange(replacement);
            else if (!skipLines.Contains(i))
                result.Add(lines[i]);
        }

        return result.ToArray();
    }
}
