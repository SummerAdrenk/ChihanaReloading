// ============================================================================
// InteractiveSession.cs
// 交互式菜单主循环: 无命令行参数时进入此模式
//
// 启动流程:
//   1. 提示用户输入游戏根目录和工作目录
//   2. 创建 KaguyaRuntimeContext 和 WorkspacePaths
//   3. 执行启动分析 (Params系导出 params/HLS; TBLSTR系只解包 SCR.ARC)
//   4. 进入主菜单循环
//
// 菜单树:
//   Main Menu
//   ├── 1. Archive Unpack    -- 解包 .arc 档案到 archive_unpack/
//   ├── 2. Archive Pack      -- 从 archive_pack/ 子目录打包为 .arc
//   ├── 3. Params          -- params.dat JSON 导出/导入, RawBlob 操作
//   ├── 4. SCR             -- .scr HLS 高级解析/回编，低级 SCRASM 调试
//   ├── 5. Text            -- message.dat / TBLSTR 文本资源处理
//   ├── 6. Picture         -- 图片分拣/转换/重打包/还原
//   ├── 7. Character       -- CG/立绘合成
//   ├── 8. PE              -- EXE 字符串 dump/import
//   └── 0. Exit
//
// 依赖: KaguyaRuntimeContext, WorkspacePaths,
//          LinkArchiveCodec, ScrContainerCodec, ScrHighLevelDecompiler, ScrHighLevelTextCodec, ScrTextCodec,
//          MessageDatCodec, MessageTextCodec, MessageScriptLinker,
//          ParamsDatCodec, FileSorter, FileConverter, Restorer,
//          CharacterComposer, PictureProcessing
// ============================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kaguya_YaneKit.Core;
using Kaguya_YaneKit.Formats.Archive;
using Kaguya_YaneKit.Formats.Character;
using Kaguya_YaneKit.Formats.Params;
using Kaguya_YaneKit.Formats.Pe;
using Kaguya_YaneKit.Formats.Picture;
using Kaguya_YaneKit.Script.Tblstr;
using Kaguya_YaneKit.Text.Tblstr;
using Kaguya_YaneKit.Gui;
using Kaguya_YaneKit.Text.MessageDat;
using Kaguya_YaneKit.Script.Params;

namespace Kaguya_YaneKit.App;

public sealed class InteractiveSession
{
    private KaguyaRuntimeContext _context = null!;
    private WorkspacePaths _paths = null!;

    public int Run()
    {
        Console.OutputEncoding = Encoding.UTF8;

        PrintBanner();
	Console.WriteLine("[PreSetting]");
        var gameRoot = PromptPath("Game root directory", true);
        var workDir = PromptPath("Work directory", true);

        try
        {
            _context = KaguyaRuntimeContext.Create(gameRoot, workDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to create runtime context: {ex.Message}");
            return 1;
        }

        _paths = new WorkspacePaths(_context.WorkDirectory);
        _paths.EnsureDirectories();

        Console.WriteLine();
        PrintKeyValue(">>Game root", _context.GameRoot);
        PrintKeyValue(">>Work dir ", _context.WorkDirectory);
        PrintKeyValue(">>Engine   ", _context.EngineProfile);
        PrintKeyValue(">>Params   ", _context.ParamsPath ?? "(not found)");
        PrintKeyValue(">>Params v ", _context.ParamsVersion ?? "(unknown)");
        PrintKeyValue(">>TBLSTR   ", _context.TblstrArchivePath ?? "(not found)");
        Console.WriteLine();

        RunStartupAnalysis();

        return MainMenuLoop();
    }

    // 启动分析

    // 自动执行三步启动分析:
    //   [1/3] 导出 params.dat 为 JSON；若工作区已有 params.json 则跳过，避免覆盖编辑
    //   [2/3] 解包 scr.arc；若工作区已有 .scr 则跳过
    //   [3/3] 批量高级解析 .scr 文件为 HLS；若工作区已有 HLS 则跳过
    private void RunStartupAnalysis()
    {
        PrintSection("Startup Analysis");

        if (_context.IsTblstrFamily)
        {
            RunTblstrStartupAnalysis();
            PrintSectionEnd();
            return;
        }

        if (!_context.IsParamsFamily)
        {
            Console.WriteLine("  Engine family not recognized.");
            Console.WriteLine("  No params.dat or TBLSTR.ARC was found under the selected game root.");
            PrintSectionEnd();
            return;
        }

        var paramsJsonPath = Path.Combine(_paths.AnalysisParams, "params.json");
        if (_context.Params is not null && _context.ParamsPath is not null)
        {
            if (File.Exists(paramsJsonPath))
            {
                Console.WriteLine($"  [1/3] params.json already exists, skipped to avoid overwriting edits.");
                Console.WriteLine($"        -> {_paths.Relative(paramsJsonPath)}");
            }
            else
            {
                Console.WriteLine($"  [1/3] Exporting params.dat ({_context.Params.Header}, version {_context.ParamsVersion}) ...");
                try
                {
                    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    Directory.CreateDirectory(_paths.AnalysisParams);
                    ReadableUnicodeJson.WriteAllText(paramsJsonPath, JsonSerializer.Serialize(_context.Params, options));
                    Console.WriteLine($"        -> {_paths.Relative(paramsJsonPath)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"        Failed: {ex.Message}");
                }
            }
        }
        else
        {
            Console.WriteLine("  [1/3] params.dat not found, skipped.");
        }

        var paramsForHls = LoadStartupParamsForHls(paramsJsonPath);

        var scrArcPath = FindScriptArchivePath();
        var existingScrCount = CountFiles(_paths.AnalysisScr, "*.scr");

        if (existingScrCount > 0)
        {
            Console.WriteLine($"  [2/3] .scr files already exist, skipped extraction to avoid overwriting edits.");
            Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScr)} ({existingScrCount} files)");
        }
        else if (scrArcPath is not null)
        {
            Console.WriteLine($"  [2/3] Extracting {Path.GetFileName(scrArcPath)} ...");
            try
            {
                ExtractArchiveAuto(scrArcPath, _paths.AnalysisScr);
                Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScr)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"        Failed: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("  [2/3] scr.arc not found, skipped.");
        }

        var existingHlsCount = CountFiles(_paths.AnalysisScrHls, "*.hls.txt");
        var scrFiles = Directory.Exists(_paths.AnalysisScr)
            ? Directory.GetFiles(_paths.AnalysisScr, "*.scr", SearchOption.AllDirectories)
            : [];
        if (scrFiles.Length == 0)
        {
            Console.WriteLine("  [3/3] HLS decompile skipped (no .scr files).");
            PrintSectionEnd();
            return;
        }

        var missingHlsFiles = scrFiles
            .Where(scr => !File.Exists(Path.Combine(_paths.AnalysisScrHls, Path.GetFileNameWithoutExtension(scr) + ".hls.txt")))
            .ToArray();
        if (missingHlsFiles.Length == 0)
        {
            Console.WriteLine($"  [3/3] HLS files already complete, skipped decompile to avoid overwriting edits.");
            Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScrHls)} ({existingHlsCount}/{scrFiles.Length} files)");
        }
        else
        {
            if (existingHlsCount > 0)
            {
                Console.WriteLine($"  [3/3] HLS files partially exist; decompiling missing files only.");
                Console.WriteLine($"        existing={existingHlsCount}, missing={missingHlsFiles.Length}, total={scrFiles.Length}");
            }

            Console.WriteLine("  [3/3] Decompiling .scr files to HLS ...");
            try
            {
                var containerCodec = new ScrContainerCodec();
                var hlsDecompiler = new ScrHighLevelDecompiler(paramsDocument: paramsForHls);
                Directory.CreateDirectory(_paths.AnalysisScrHls);
                int ok = 0, fail = 0;
                using var progress = PictureProcessing.StartProgress("HLS scr", missingHlsFiles.Length);
                foreach (var scr in missingHlsFiles)
                {
                    try
                    {
                        var document = containerCodec.Read(File.ReadAllBytes(scr), Path.GetFileName(scr));
                        var outputPath = Path.Combine(_paths.AnalysisScrHls, Path.GetFileNameWithoutExtension(scr) + ".hls.txt");
                        File.WriteAllText(outputPath, hlsDecompiler.Write(document), Encoding.UTF8);
                        ok++;
                    }
                    catch
                    {
                        fail++;
                    }
                    finally
                    {
                        progress.Increment();
                    }
                }
                Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScrHls)} ({ok} ok, {fail} failed)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"        Failed: {ex.Message}");
            }
        }

        PrintSectionEnd();
    }

    private void RunTblstrStartupAnalysis()
    {
        Console.WriteLine("  [1/3] params.dat not found; using TBLSTR系 startup.");
        Console.WriteLine($"        TBLSTR archive: {_context.TblstrArchivePath ?? "(not found yet)"}");

        if (_context.TblstrArchivePath is not null)
        {
            Console.WriteLine("  [2/3] TBLSTR text resource detected; parsing is handled by Text Processing.");
        }
        else
        {
            Console.WriteLine("  [2/3] TBLSTR text resource not found under game root.");
        }

        var existingScrCount = CountFiles(_paths.AnalysisScr, "*.scr");
        if (existingScrCount > 0)
        {
            Console.WriteLine($"  [3/3] .scr files already exist, skipped extraction to avoid overwriting edits.");
            Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScr)} ({existingScrCount} files)");
            DecompileMissingTblstrScrHls();
            return;
        }

        var scrArchivePath = FindScriptArchivePath();
        if (scrArchivePath is null)
        {
            Console.WriteLine("  [3/3] SCR archive not found, skipped.");
            return;
        }

        Console.WriteLine($"  [3/3] Extracting {Path.GetFileName(scrArchivePath)} for TBLSTR系 reverse ...");
        try
        {
            ExtractArchiveAuto(scrArchivePath, _paths.AnalysisScr);
            Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScr)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        Failed: {ex.Message}");
            return;
        }

        DecompileMissingTblstrScrHls();
    }

    private void DecompileMissingTblstrScrHls()
    {
        var scrFiles = Directory.Exists(_paths.AnalysisScr)
            ? Directory.GetFiles(_paths.AnalysisScr, "*.scr", SearchOption.AllDirectories)
            : [];
        if (scrFiles.Length == 0)
        {
            Console.WriteLine("        TBLSTR系 HLS skipped: no .scr files.");
            ExportScrSupportTablesToHls(overwrite: false, indent: "        ");
            return;
        }

        var missing = scrFiles
            .Where(scr => !File.Exists(Path.Combine(_paths.AnalysisScrHls, Path.GetFileNameWithoutExtension(scr) + ".hls.txt")))
            .ToArray();
        if (missing.Length == 0)
        {
            Console.WriteLine($"        TBLSTR系 HLS already complete, skipped to avoid overwriting edits.");
            Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScrHls)} ({scrFiles.Length}/{scrFiles.Length} files)");
            ExportScrSupportTablesToHls(overwrite: false, indent: "        ");
            return;
        }

        Console.WriteLine($"        Decompiling missing TBLSTR系 .scr to conservative HLS/IR ({missing.Length}/{scrFiles.Length}) ...");
        Directory.CreateDirectory(_paths.AnalysisScrHls);
        var codec = new TblstrScrCodec();
        var formatter = new TblstrScrTextFormatter();
        int ok = 0, fail = 0;
        using var progress = PictureProcessing.StartProgress("TBLSTR HLS scr", missing.Length);
        foreach (var scr in missing)
        {
            try
            {
                var input = File.ReadAllBytes(scr);
                if (!TblstrScrCodec.IsTblstrScr(input))
                {
                    throw new InvalidDataException("not a TBLSTR SCR file");
                }

                var document = codec.Read(input, Path.GetFileName(scr), TblstrScrCodec.TryReadSiblingLabels(scr));
                var outputPath = Path.Combine(_paths.AnalysisScrHls, Path.GetFileNameWithoutExtension(scr) + ".hls.txt");
                File.WriteAllText(outputPath, formatter.WriteHls(document), Encoding.UTF8);
                ok++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"        Failed: {Path.GetFileName(scr)}: {ex.Message}");
                fail++;
            }
            finally
            {
                progress.Increment();
            }
        }

        Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScrHls)} ({ok} ok, {fail} failed)");
        ExportScrSupportTablesToHls(overwrite: false, indent: "        ");
    }

    private static int CountFiles(string directory, string pattern)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Count()
            : 0;
    }

    private string? FindScriptArchivePath()
    {
        var candidates = new[]
        {
            Path.Combine(_context.GameRoot, "scr.arc"),
            Path.Combine(_context.GameRoot, "SCR.ARC"),
            Path.Combine(_context.GameRoot, "arc", "scr.arc"),
            Path.Combine(_context.GameRoot, "ARC", "SCR.ARC")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private void ExtractArchiveAuto(string archivePath, string outputDirectory)
    {
        ExtractArchiveAuto(archivePath, outputDirectory, progress: null);
    }

    private void ExtractArchiveAuto(string archivePath, string outputDirectory, Action<int, int>? progress)
    {
        var magic = ReadArchiveMagic(archivePath);
        if (magic.StartsWith("AF01", StringComparison.Ordinal))
        {
            new Af01ArchiveCodec().Extract(archivePath, outputDirectory, progress);
            return;
        }

        if (magic.StartsWith("LINK", StringComparison.Ordinal))
        {
            new LinkArchiveCodec().Extract(archivePath, outputDirectory, _context.ParamsPath, _context.LinkEncryptionKey, decrypt: true, progress);
            return;
        }

        if (magic.StartsWith("UF01", StringComparison.Ordinal))
        {
            throw new NotSupportedException("UF01 is a TBLSTR text resource package; use Text Processing -> TBLSTR.");
        }

        throw new NotSupportedException($"Unsupported archive magic: {magic}");
    }

    private static string ReadArchiveMagic(string archivePath)
    {
        Span<byte> buffer = stackalloc byte[4];
        using var input = File.OpenRead(archivePath);
        var read = input.Read(buffer);
        return Encoding.ASCII.GetString(buffer[..read]);
    }

    private ParamsDatDocument? LoadStartupParamsForHls(string paramsJsonPath)
    {
        if (!File.Exists(paramsJsonPath))
        {
            return _context.Params;
        }

        try
        {
            return JsonSerializer.Deserialize<ParamsDatDocument>(
                    File.ReadAllText(paramsJsonPath, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? _context.Params;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        Warning: failed to read existing params.json for HLS context: {ex.Message}");
            return _context.Params;
        }
    }

    // 主菜单

    private int MainMenuLoop()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("  ╔══════════════════════════════╗");
            Console.WriteLine("  ║        Main  Menu            ║");
            Console.WriteLine("  ╠══════════════════════════════╣");
            Console.WriteLine("  ║  1. Archive Unpack           ║");
            Console.WriteLine("  ║  2. Archive Pack             ║");
            Console.WriteLine("  ║  3. Params Processing        ║");
            Console.WriteLine("  ║  4. SCR Processing           ║");
            Console.WriteLine("  ║  5. Text Processing          ║");
            Console.WriteLine("  ║  6. Picture Processing       ║");
            Console.WriteLine("  ║  7. Character CG/SP Compose  ║");
            Console.WriteLine("  ║  8. PE Processing            ║");
            Console.WriteLine("  ║  0. Exit                     ║");
            Console.WriteLine("  ╚══════════════════════════════╝");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": LinkUnpackMenu(); break;
                case "2": LinkPackMenu(); break;
                case "3": ParamsMenu(); break;
                case "4": ScrMenu(); break;
                case "5": TextMenu(); break;
                case "6": PictureMenu(); break;
                case "7": CharacterMenu(); break;
                case "8": PeMenu(); break;
                case "0": Console.WriteLine("  See you again~~~"); return 0;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }
        }
    }

    #region PE

    private void PeMenu()
    {
        PrintSubMenu("PE Processing");
        Console.WriteLine("  1. Dump EXE strings to JSON");
        Console.WriteLine("  2. Import edited string JSON to EXE");
        Console.WriteLine("  0. Back");
        var choice = Prompt("Select").Trim();
        Console.WriteLine();

        try
        {
            switch (choice)
            {
                case "1":
                    PeStringDumpMenu();
                    break;
                case "2":
                    PeStringImportMenu();
                    break;
                case "0":
                    break;
                default:
                    Console.WriteLine($"  Unknown option: {choice}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  PE error: {ex.Message}");
        }
    }

    private void PeStringDumpMenu()
    {
        var exePath = PromptExePath();
        var encodingName = Prompt("Read encoding [cp932]").Trim();
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            encodingName = "cp932";
        }

        Directory.CreateDirectory(_paths.AnalysisPe);
        var defaultOutput = Path.Combine(_paths.AnalysisPe, $"{Path.GetFileNameWithoutExtension(exePath)}.strings.json");
        var outputPath = Prompt($"Output JSON [{defaultOutput}]").Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = defaultOutput;
        }

        var tool = new PeStringTableTool();
        var document = tool.Dump(exePath, new PeStringDumpOptions { EncodingName = encodingName });
        tool.WriteDocument(outputPath, document);
        Console.WriteLine($"  Wrote {outputPath}");
        Console.WriteLine($"  Entries: {document.Entries.Count}");
        Console.WriteLine($"  Referenced entries: {document.Entries.Count(entry => entry.Refs.Count > 0)}");
        Console.WriteLine($"  Length-patch entries: {document.Entries.Count(entry => entry.NeedsLengthPatch)}");
    }

    private void PeStringImportMenu()
    {
        var exePath = PromptExePath();
        var defaultJson = Path.Combine(_paths.AnalysisPe, $"{Path.GetFileNameWithoutExtension(exePath)}.strings.json");
        var jsonPath = Prompt($"Edited JSON [{defaultJson}]").Trim();
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            jsonPath = defaultJson;
        }

        var encodingName = Prompt("Write encoding [cp932]").Trim();
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            encodingName = "cp932";
        }

        Directory.CreateDirectory(_paths.AnalysisPe);
        var defaultOutput = Path.Combine(_paths.AnalysisPe, $"{Path.GetFileNameWithoutExtension(exePath)}.pe_patched.exe");
        var outputPath = Prompt($"Output EXE [{defaultOutput}]").Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = defaultOutput;
        }

        var result = new PeStringTableTool().Import(exePath, jsonPath, outputPath, new PeStringImportOptions { EncodingName = encodingName });
        Console.WriteLine($"  Wrote {outputPath}");
        Console.WriteLine($"  Changed entries: {result.ChangedEntries}");
        Console.WriteLine($"  In-place entries: {result.InPlaceEntries}");
        Console.WriteLine($"  Moved entries: {result.MovedEntries}");
        Console.WriteLine($"  Patched references: {result.PatchedReferences}");
        Console.WriteLine($"  Patched length immediates: {result.PatchedLengths}");
    }

    private string PromptExePath()
    {
        var exeFiles = Directory.Exists(_context.GameRoot)
            ? Directory.EnumerateFiles(_context.GameRoot, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        if (exeFiles.Count > 0)
        {
            Console.WriteLine("  Available EXE files:");
            for (var i = 0; i < exeFiles.Count; i++)
            {
                Console.WriteLine($"    {i + 1}. {Path.GetFileName(exeFiles[i])}");
            }
        }

        var defaultExe = exeFiles.FirstOrDefault(path =>
                Path.GetFileName(path).Equals("GAME_SYS_Crack.exe", StringComparison.OrdinalIgnoreCase)) ??
            exeFiles.FirstOrDefault() ??
            Path.Combine(_context.GameRoot, "GAME_SYS_Crack.exe");

        var input = Prompt($"EXE path or number [{defaultExe}]").Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return defaultExe;
        }

        if (int.TryParse(input, out var index) && index >= 1 && index <= exeFiles.Count)
        {
            return exeFiles[index - 1];
        }

        return input;
    }

    #endregion

    #region Link Unpack

    // 列出游戏目录下所有普通 .arc，过滤 TBLSTR 文本包，用户选择后解包到 archive_unpack/
    private void LinkUnpackMenu()
    {
        _paths.EnsureArchiveDirectories();

        var archives = EnumerateGameArchives()
            .ToList();

        if (archives.Count == 0)
        {
            Console.WriteLine("  No .arc files found in game directory or ARC/.");
            return;
        }

        PrintSubMenu("Archive Unpack");
        Console.WriteLine("  Available archives:");
        foreach (var arc in archives)
        {
            Console.WriteLine($"    {arc.DisplayName} [{arc.Magic}]");
        }

        Console.WriteLine();
        Console.WriteLine("  Enter '-all' to extract all, or comma-separated archive names:");
        var input = Prompt("Archives").Trim();

        List<string> selected;
        if (input.Equals("-all", StringComparison.OrdinalIgnoreCase))
        {
            selected = archives.Select(a => a.DisplayName).ToList();
            selected = FilterSlowOptionalArchivesForAll(selected);
        }
        else
        {
            selected = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.EndsWith(".arc", StringComparison.OrdinalIgnoreCase) ? name : name + ".arc")
                .ToList();
        }

        var jobs = new List<(GameArchiveInfo Archive, string OutputDirectory)>();
        foreach (var requestedName in selected)
        {
            var archive = archives.FirstOrDefault(a =>
                string.Equals(a.DisplayName, requestedName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Name, requestedName, StringComparison.OrdinalIgnoreCase));
            if (archive is null)
            {
                Console.WriteLine($"  Warning: {requestedName} not found, skipped.");
                continue;
            }

            var outDir = Path.Combine(_paths.Link6Unpack, GetArchiveWorkDirectoryName(archive.Name));
            jobs.Add((archive, outDir));
        }

        RunParallelBatch(
            "EXTRACT",
            jobs,
            job => $"{job.Archive.DisplayName} [{job.Archive.Magic}]",
            (job, report) =>
            {
                ExtractArchiveAuto(job.Archive.Path, job.OutputDirectory, report);
                return $"-> {_paths.Relative(job.OutputDirectory)}";
            });
    }

    private List<string> FilterSlowOptionalArchivesForAll(List<string> selected)
    {
        var optional = selected
            .Where(IsSlowOptionalArchive)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (optional.Count == 0)
        {
            return selected;
        }

        Console.WriteLine();
        Console.WriteLine($"  Slow optional archives detected: {string.Join(", ", optional)}");
        Console.WriteLine("  Exclude bgm/sed/voice archives from this -all extract? (Y/n, default Y)");
        var answer = Prompt("Exclude audio").Trim();
        if (!string.IsNullOrEmpty(answer) &&
            !answer.Equals("y", StringComparison.OrdinalIgnoreCase) &&
            !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return selected;
        }

        var filtered = selected
            .Where(name => !IsSlowOptionalArchive(name))
            .ToList();
        Console.WriteLine($"  Excluded {optional.Count} archive(s): {string.Join(", ", optional)}");
        return filtered;
    }

    private static bool IsSlowOptionalArchive(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return name == "bgm" ||
               name == "sed" ||
               name.StartsWith("voice", StringComparison.OrdinalIgnoreCase) ||
               IsVoiceArchiveName(name);
    }

    private List<GameArchiveInfo> EnumerateGameArchives()
    {
        var roots = new[]
        {
            _context.GameRoot,
            Path.Combine(_context.GameRoot, "ARC")
        };

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetFiles(root, "*.arc")
                .Concat(Directory.GetFiles(root, "*.ARC"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new GameArchiveInfo(
                    path,
                    Path.GetFileName(path),
                    Path.GetRelativePath(_context.GameRoot, path),
                    ReadArchiveMagic(path))))
            .Where(a => !IsTextResourceArchive(a))
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsTextResourceArchive(GameArchiveInfo archive)
        => archive.Magic.StartsWith("UF01", StringComparison.Ordinal) ||
           archive.Name.Equals("TBLSTR.ARC", StringComparison.OrdinalIgnoreCase) ||
           archive.Name.Equals("tblstr.arc", StringComparison.OrdinalIgnoreCase);

    private sealed record GameArchiveInfo(string Path, string Name, string DisplayName, string Magic);

    #endregion

    #region Link Pack

    // 将 archive_pack/ 下的子目录打包为 .arc 档案
    private void LinkPackMenu()
    {
        _paths.EnsureArchiveDirectories();

        if (!Directory.Exists(_paths.Link6Pack))
        {
            Console.WriteLine($"  Pack directory not found: {_paths.Relative(_paths.Link6Pack)}");
            Console.WriteLine("  Create subdirectories in archive_pack/ with files to pack.");
            return;
        }

        var subDirs = Directory.GetDirectories(_paths.Link6Pack)
            .Select(d => new DirectoryInfo(d).Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subDirs.Count == 0)
        {
            Console.WriteLine("  No subdirectories found in archive_pack/.");
            return;
        }

        PrintSubMenu("Archive Pack");
        Console.WriteLine("  Available pack directories:");
        foreach (var d in subDirs)
        {
            Console.WriteLine($"    {d}/");
        }

        Console.WriteLine();
        Console.WriteLine("  Enter '-all' to pack all, or comma-separated directory names:");
        var input = Prompt("Directories").Trim();

        Console.WriteLine("  Output extension (press Enter for default .arc):");
        var extInput = Prompt("Extension").Trim();
        var ext = string.IsNullOrEmpty(extInput) ? ".arc" : (extInput.StartsWith('.') ? extInput : "." + extInput);

        Console.WriteLine("  Recompress LINK entries that were compressed in the manifest? (Y/n, default Y)");
        var compressInput = Prompt("LINK recompress").Trim();
        var recompressLinkEntries = string.IsNullOrEmpty(compressInput) ||
                                    compressInput.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                                    compressInput.Equals("yes", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine("  Re-encrypt LINK entries that were encrypted in the manifest? (Y/n, default Y)");
        var encryptInput = Prompt("LINK re-encrypt").Trim();
        var reencryptLinkEntries = string.IsNullOrEmpty(encryptInput) ||
                                   encryptInput.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                                   encryptInput.Equals("yes", StringComparison.OrdinalIgnoreCase);

        var linkOptions = new LinkArchivePackOptions
        {
            CompressPackedEntries = recompressLinkEntries,
            EncryptEncryptedEntries = reencryptLinkEntries,
            EncryptionKey = _context.LinkEncryptionKey
        };

        List<string> selected;
        if (input.Equals("-all", StringComparison.OrdinalIgnoreCase))
        {
            selected = subDirs;
        }
        else
        {
            selected = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        var jobs = new List<(string Name, string InputDirectory, string ManifestPath, string OutputPath)>();
        foreach (var dirName in selected)
        {
            var dirPath = Path.Combine(_paths.Link6Pack, dirName);
            if (!Directory.Exists(dirPath))
            {
                Console.WriteLine($"  Warning: {dirName}/ not found, skipped.");
                continue;
            }

            var manifestPath = Path.Combine(dirPath, Af01ArchiveCodec.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                manifestPath = Path.Combine(dirPath, "_link_manifest.json");
            }
            if (!File.Exists(manifestPath))
            {
                var unpackManifest = Path.Combine(_paths.Link6Unpack, dirName, Af01ArchiveCodec.ManifestFileName);
                if (File.Exists(unpackManifest)) manifestPath = unpackManifest;
            }
            if (!File.Exists(manifestPath))
            {
                var unpackManifest = Path.Combine(_paths.Link6Unpack, dirName, "_link_manifest.json");
                if (File.Exists(unpackManifest)) manifestPath = unpackManifest;
            }
            var outputPath = Path.Combine(_paths.Link6Pack, dirName + ext);
            jobs.Add((dirName, dirPath, manifestPath, outputPath));
        }

        RunParallelBatch(
            "PACK",
            jobs,
            job => job.Name,
            (job, report) =>
            {
                if (File.Exists(job.ManifestPath))
                {
                    PackArchiveAuto(job.InputDirectory, job.ManifestPath, job.OutputPath, linkOptions, report);
                }
                else
                {
                    new LinkArchiveCodec().PackLink6(job.InputDirectory, job.OutputPath, job.Name, 0, false, report);
                }

                return $"-> {_paths.Relative(job.OutputPath)}";
            });
    }

    private static void PackArchiveAuto(string inputDirectory, string manifestPath, string outputPath, LinkArchivePackOptions linkOptions, Action<int, int>? progress = null)
    {
        var format = ReadArchiveManifestFormat(manifestPath);
        if (string.Equals(format, "AF01", StringComparison.OrdinalIgnoreCase))
        {
            var manifest = Af01ArchiveManifestWriter.Read(manifestPath);
            var shouldCompress = manifest.Entries.Any(entry => entry.IsPacked);
            new Af01ArchiveCodec().PackFromManifest(inputDirectory, manifestPath, outputPath, shouldCompress, progress);
            return;
        }

        if (string.Equals(format, "LINK", StringComparison.OrdinalIgnoreCase))
        {
            new LinkArchiveCodec().PackLink6FromManifest(inputDirectory, manifestPath, outputPath, linkOptions, progress);
            return;
        }

        throw new NotSupportedException($"Unsupported archive manifest format: {format}");
    }

    private static string ReadArchiveManifestFormat(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.TryGetProperty("Format", out var format))
        {
            return format.GetString() ?? "";
        }

        if (root.TryGetProperty("Header", out var header) &&
            header.TryGetProperty("Magic", out var magic) &&
            (magic.GetString() ?? "").StartsWith("LINK", StringComparison.OrdinalIgnoreCase))
        {
            return "LINK";
        }

        return "";
    }

    #endregion

    #region Text

    private void TextMenu()
    {
        while (true)
        {
            Console.WriteLine("  ┌─────────────────────────────┐");
            Console.WriteLine("  │       Text Menu             │");
            Console.WriteLine("  ├─────────────────────────────┤");
            Console.WriteLine("  │  1. TBLSTR                  │");
            Console.WriteLine("  │  2. message.dat             │");
            Console.WriteLine("  │  0. Back                    │");
            Console.WriteLine("  └─────────────────────────────┘");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": TblstrMenu(); break;
                case "2": MessageMenu(); break;
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    #endregion

    #region TBL support tables

    private void TblSupportMenu()
    {
        while (true)
        {
            Console.WriteLine("  ┌─────────────────────────────┐");
            Console.WriteLine("  │   TBL Support Tables        │");
            Console.WriteLine("  ├─────────────────────────────┤");
            Console.WriteLine("  │  1. Export TBL -> text/json │");
            Console.WriteLine("  │  2. Rebuild JSON -> TBL     │");
            Console.WriteLine("  │  3. Verify TBL roundtrip    │");
            Console.WriteLine("  │  0. Back                    │");
            Console.WriteLine("  └─────────────────────────────┘");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": TblSupportExport(); break;
                case "2": TblSupportImport(); break;
                case "3": TblSupportVerify(); break;
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    private void TblSupportExport()
    {
        if (!Directory.Exists(_paths.AnalysisScr))
        {
            Console.WriteLine($"  SCR analysis directory not found: {_paths.Relative(_paths.AnalysisScr)}");
            Console.WriteLine("  Run startup analysis or archive unpack first.");
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.AnalysisScrHls);
            var result = TblCommands.ExportTables(_paths.AnalysisScr, _paths.AnalysisScrHls, writeJson: true);
            var verify = TblCommands.VerifyTables(_paths.AnalysisScr);
            Console.WriteLine($"  Exported TBL tables -> {_paths.Relative(_paths.AnalysisScrHls)}");
            Console.WriteLine("    Wrote editable .txt and rebuildable .json");
            Console.WriteLine($"    Export={result.Success} ok, {result.Skipped} skipped");
            Console.WriteLine($"    Verify={verify.Success} ok, {verify.Skipped} skipped, {verify.Failure} failed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void TblSupportImport()
    {
        var inputDirectory = _paths.AnalysisScrHls;
        var outputDirectory = _paths.AnalysisScrAsm;
        if (!Directory.Exists(inputDirectory) || !Directory.EnumerateFiles(inputDirectory, "tbl_*.json").Any())
        {
            Console.WriteLine($"  No tbl_*.json files found in {_paths.Relative(inputDirectory)}");
            Console.WriteLine("  Decompile SCR HLS or export TBL support tables first.");
            return;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            var result = TblCommands.ImportTables(inputDirectory, outputDirectory, Console.WriteLine);
            Console.WriteLine($"  Rebuilt TBL tables -> {_paths.Relative(outputDirectory)}");
            Console.WriteLine($"    Import={result.Success} ok, {result.Skipped} skipped, {result.Failure} failed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void TblSupportVerify()
    {
        if (!Directory.Exists(_paths.AnalysisScr))
        {
            Console.WriteLine($"  SCR analysis directory not found: {_paths.Relative(_paths.AnalysisScr)}");
            return;
        }

        try
        {
            var verify = TblCommands.VerifyTables(_paths.AnalysisScr, Console.WriteLine);
            Console.WriteLine($"  Verify={verify.Success} ok, {verify.Skipped} skipped, {verify.Failure} failed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void ExportScrSupportTablesToHls(bool overwrite, string indent)
    {
        if (!Directory.Exists(_paths.AnalysisScr) ||
            !Directory.EnumerateFiles(_paths.AnalysisScr, "*.tbl", SearchOption.TopDirectoryOnly).Any())
        {
            return;
        }

        if (!overwrite &&
            Directory.Exists(_paths.AnalysisScrHls) &&
            Directory.EnumerateFiles(_paths.AnalysisScrHls, "tbl_*.json", SearchOption.TopDirectoryOnly).Any())
        {
            Console.WriteLine($"{indent}TBL support tables already exported, skipped to avoid overwriting edits.");
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.AnalysisScrHls);
            var result = TblCommands.ExportTables(_paths.AnalysisScr, _paths.AnalysisScrHls, writeJson: true);
            if (result.Success > 0 || result.Skipped > 0 || result.Failure > 0)
            {
                Console.WriteLine($"{indent}TBL support tables -> {_paths.Relative(_paths.AnalysisScrHls)} ({result.Success} ok, {result.Skipped} skipped, {result.Failure} failed)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{indent}TBL support export failed: {ex.Message}");
        }
    }

    private void ImportScrSupportTablesToAsm(string indent)
    {
        if (!Directory.Exists(_paths.AnalysisScrHls) ||
            !Directory.EnumerateFiles(_paths.AnalysisScrHls, "tbl_*.json", SearchOption.TopDirectoryOnly).Any())
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.AnalysisScrAsm);
            var result = TblCommands.ImportTables(_paths.AnalysisScrHls, _paths.AnalysisScrAsm);
            if (result.Success > 0 || result.Skipped > 0 || result.Failure > 0)
            {
                Console.WriteLine($"{indent}TBL support rebuild -> {_paths.Relative(_paths.AnalysisScrAsm)} ({result.Success} ok, {result.Skipped} skipped, {result.Failure} failed)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{indent}TBL support rebuild failed: {ex.Message}");
        }
    }

    #endregion

    #region TBLSTR

    private void TblstrMenu()
    {
        var iniPath = FindDefaultTblstrIni();
        PrintKeyValue("TBLSTR INI", iniPath ?? "(default)");
        Console.WriteLine("  Enter custom TBLSTR INI path or press Enter to use default:");
        var input = Prompt("INI").Trim();
        if (!string.IsNullOrEmpty(input))
        {
            iniPath = input;
        }

        var tblstrConfig = MessagePlaceholderConfig.Load(iniPath);
        var tblstrCodec = CreateTblstrCodec(tblstrConfig);
        Console.WriteLine();

        while (true)
        {
            Console.WriteLine("  ┌─────────────────────────────┐");
            Console.WriteLine("  │       TBLSTR Menu           │");
            Console.WriteLine("  ├─────────────────────────────┤");
            Console.WriteLine("  │  1. Export TBLSTR           │");
            Console.WriteLine("  │  2. Import text -> ARC      │");
            Console.WriteLine("  │  3. Split by SCR            │");
            Console.WriteLine("  │  4. Merge split -> text     │");
            Console.WriteLine("  │  0. Back                    │");
            Console.WriteLine("  └─────────────────────────────┘");
            Console.WriteLine($"  Detected: {_context.TblstrArchivePath ?? "(not found)"}");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": TblstrExport(tblstrCodec); break;
                case "2": TblstrImport(tblstrCodec, tblstrConfig); break;
                case "3": TblstrSplitByScr(tblstrCodec); break;
                case "4": TblstrMerge(); break;
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    private void TblstrExport(TblstrCodec codec)
    {
        if (_context.TblstrArchivePath is null || !File.Exists(_context.TblstrArchivePath))
        {
            Console.WriteLine("  TBLSTR archive not found.");
            return;
        }

        try
        {
            var document = codec.Read(File.ReadAllBytes(_context.TblstrArchivePath));
            var map = Directory.Exists(_paths.AnalysisScr)
                ? new TblstrScriptLinker().BuildMap(document, _paths.AnalysisScr)
                : null;
            Directory.CreateDirectory(_paths.Msg);
            var textPath = Path.Combine(_paths.Msg, "tblstr.txt");
            File.WriteAllText(textPath, TblstrTextWriter.Write(document, map), Encoding.UTF8);
            Console.WriteLine($"  Exported -> {_paths.Relative(textPath)}");
            Console.WriteLine($"    Version={document.Version} Entries={document.Entries.Count}");
            if (map is not null)
            {
                Console.WriteLine($"    SCR map: name={map.NameIndices.Count}, msg={map.MessageIndices.Count}, choice={map.ChoiceIndices.Count}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void TblstrSplitByScr(TblstrCodec codec)
    {
        if (_context.TblstrArchivePath is null || !File.Exists(_context.TblstrArchivePath))
        {
            Console.WriteLine("  TBLSTR archive not found.");
            return;
        }

        if (!Directory.Exists(_paths.AnalysisScr) || !Directory.EnumerateFiles(_paths.AnalysisScr, "*.scr").Any())
        {
            Console.WriteLine($"  No .scr files found in {_paths.Relative(_paths.AnalysisScr)}");
            Console.WriteLine("  Run startup analysis or extract SCR.ARC first.");
            return;
        }

        try
        {
            var document = codec.Read(File.ReadAllBytes(_context.TblstrArchivePath));
            var linker = new TblstrScriptLinker();
            var map = linker.BuildMap(document, _paths.AnalysisScr);
            var outputDirectory = Path.Combine(_paths.Msg, "tblstr_split");
            Directory.CreateDirectory(outputDirectory);
            linker.Split(document, map, outputDirectory);
            linker.WriteMapJson(map, Path.Combine(outputDirectory, "_map.json"));
            Console.WriteLine($"  Split -> {_paths.Relative(outputDirectory)}");
            Console.WriteLine($"    name={map.NameIndices.Count}, msg={map.MessageIndices.Count}, choice={map.ChoiceIndices.Count}, unreferenced={map.UnreferencedIndices.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void TblstrImport(TblstrCodec codec, MessagePlaceholderConfig config)
    {
        if (_context.TblstrArchivePath is null || !File.Exists(_context.TblstrArchivePath))
        {
            Console.WriteLine("  TBLSTR archive not found.");
            return;
        }

        var textPath = Path.Combine(_paths.Msg, "tblstr.txt");
        if (!File.Exists(textPath))
        {
            Console.WriteLine($"  Text file not found: {_paths.Relative(textPath)}");
            Console.WriteLine("  Export TBLSTR first.");
            return;
        }

        try
        {
            var document = codec.Read(File.ReadAllBytes(_context.TblstrArchivePath));
            var writeEncoding = MessageDatCodec.ResolveEncoding(config.WriteEncodingName);
            var text = File.ReadAllText(textPath, Encoding.UTF8);
            text = new TblstrTextWorkflowProcessor(config).ApplyPreImportTransforms(text, writeEncoding);
            var applied = new TblstrTextCodec().Apply(document, text);
            var outputPath = Path.Combine(_paths.Msg, "tblstr_new.arc");
            Directory.CreateDirectory(_paths.Msg);
            File.WriteAllBytes(outputPath, codec.Write(document));
            Console.WriteLine($"  Imported -> {_paths.Relative(outputPath)}");
            Console.WriteLine($"    Applied={applied} Entries={document.Entries.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void TblstrMerge()
    {
        var baseTextPath = Path.Combine(_paths.Msg, "tblstr.txt");
        var splitDirectory = Path.Combine(_paths.Msg, "tblstr_split");
        if (!File.Exists(baseTextPath))
        {
            Console.WriteLine($"  Base text not found: {_paths.Relative(baseTextPath)}");
            Console.WriteLine("  Export TBLSTR first.");
            return;
        }

        if (!Directory.Exists(splitDirectory))
        {
            Console.WriteLine($"  Split directory not found: {_paths.Relative(splitDirectory)}");
            Console.WriteLine("  Split TBLSTR first.");
            return;
        }

        try
        {
            var outputPath = Path.Combine(_paths.Msg, "tblstr_merged.txt");
            Directory.CreateDirectory(_paths.Msg);
            var result = new TblstrTextCodec().Merge(baseTextPath, splitDirectory, outputPath);
            Console.WriteLine($"  Merged -> {_paths.Relative(outputPath)}");
            Console.WriteLine($"    Collected={result.Collected}  Replaced={result.Replaced}  Missing={result.MissingInBase}  Conflicts={result.Conflicts}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    #endregion

    #region Message

    // Message 子菜单: 导出/导入/拆分/合并 message.dat
    private void MessageMenu()
    {
        var iniPath = FindDefaultIni();
        PrintKeyValue("INI", iniPath ?? "(default)");
        Console.WriteLine("  Enter custom INI path or press Enter to use default:");
        var input = Prompt("INI").Trim();
        if (!string.IsNullOrEmpty(input))
        {
            iniPath = input;
        }

        var config = MessagePlaceholderConfig.Load(iniPath);
        var readEncoding = MessageDatCodec.ResolveEncoding(config.ReadEncodingName);
        var writeEncoding = MessageDatCodec.ResolveEncoding(config.WriteEncodingName);
        var msgCodec = new MessageDatCodec(readEncoding, writeEncoding, config);
        Console.WriteLine();

        while (true)
        {
            Console.WriteLine("  ┌─────────────────────────────┐");
            Console.WriteLine("  │      Message Menu           │");
            Console.WriteLine("  ├─────────────────────────────┤");
            Console.WriteLine("  │  1. Export message.dat      │");
            Console.WriteLine("  │  2. Import text -> dat      │");
            Console.WriteLine("  │  3. Split message by .scr   │");
            Console.WriteLine("  │  4. Merge split -> text     │");
            Console.WriteLine("  │  0. Back                    │");
            Console.WriteLine("  └─────────────────────────────┘");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": MsgExport(msgCodec); break;
                case "2": MsgImport(msgCodec); break;
                case "3": MsgSplit(msgCodec); break;
                case "4": MsgMerge(); break;
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    private void MsgExport(MessageDatCodec codec)
    {
        var msgDatPath = Path.Combine(_context.GameRoot, "message.dat");
        if (!File.Exists(msgDatPath))
        {
            Console.WriteLine($"  message.dat not found: {msgDatPath}");
            return;
        }

        try
        {
            var document = codec.Read(File.ReadAllBytes(msgDatPath));
            var textPath = Path.Combine(_paths.Msg, "message.txt");
            Directory.CreateDirectory(_paths.Msg);
            File.WriteAllText(textPath, new MessageTextCodec().Write(document), Encoding.UTF8);
            Console.WriteLine($"  Exported -> {_paths.Relative(textPath)}");
            Console.WriteLine($"    Names={document.Names.Count}  Choices={document.Choices.Count}  Messages={document.Messages.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void MsgImport(MessageDatCodec codec)
    {
        var msgDatPath = Path.Combine(_context.GameRoot, "message.dat");
        if (!File.Exists(msgDatPath))
        {
            Console.WriteLine($"  message.dat not found: {msgDatPath}");
            return;
        }

        var txtPath = Path.Combine(_paths.Msg, "message.txt");
        if (!File.Exists(txtPath))
        {
            Console.WriteLine($"  Text file not found: {_paths.Relative(txtPath)}");
            return;
        }

        try
        {
            var document = codec.Read(File.ReadAllBytes(msgDatPath));
            new MessageTextCodec().Apply(document, File.ReadAllText(txtPath, Encoding.UTF8));
            var outputPath = Path.Combine(_paths.Msg, "message_new.dat");
            Directory.CreateDirectory(_paths.Msg);
            File.WriteAllBytes(outputPath, codec.Write(document, document.Encrypted, document.XorKey));
            Console.WriteLine($"  Imported -> {_paths.Relative(outputPath)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    // 按 .scr 使用情况拆分 message.dat 到独立文件
    private void MsgSplit(MessageDatCodec codec)
    {
        var msgDatPath = Path.Combine(_context.GameRoot, "message.dat");
        if (!File.Exists(msgDatPath))
        {
            Console.WriteLine($"  message.dat not found: {msgDatPath}");
            return;
        }

        var scrDirectory = ResolveMessageSplitScrDirectory();
        if (scrDirectory is null)
        {
            return;
        }

        try
        {
            var document = codec.Read(File.ReadAllBytes(msgDatPath));
            var linker = new MessageScriptLinker();
            var map = linker.BuildMap(document, scrDirectory);
            Directory.CreateDirectory(_paths.MsgSplitOut);
            linker.Split(document, map, _paths.MsgSplitOut);
            linker.WriteMapJson(map, Path.Combine(_paths.MsgSplitOut, "_map.json"));
            File.WriteAllText(Path.Combine(_paths.MsgSplitOut, "_base_message.txt"), new MessageTextCodec().Write(document), Encoding.UTF8);
            Console.WriteLine($"  Split -> {_paths.Relative(_paths.MsgSplitOut)}");
            Console.WriteLine($"    Scripts={map.Scripts.Count}  Shared={map.SharedMessageIndices.Count}  Orphan={map.OrphanMessageIndices.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    // 将按 .scr 拆分的消息文件合并回完整的 message.txt
    private string? ResolveMessageSplitScrDirectory()
    {
        if (HasScrFiles(_paths.AnalysisScrAsm))
        {
            Console.WriteLine($"  Using assembled .scr files: {_paths.Relative(_paths.AnalysisScrAsm)}");
            return _paths.AnalysisScrAsm;
        }

        Console.WriteLine($"  No assembled .scr files found in {_paths.Relative(_paths.AnalysisScrAsm)}.");

        if (HasScrFiles(_paths.AnalysisScr))
        {
            Console.WriteLine($"  Falling back to extracted .scr files: {_paths.Relative(_paths.AnalysisScr)}");
            return _paths.AnalysisScr;
        }

        Console.WriteLine($"  No .scr files found in {_paths.Relative(_paths.AnalysisScrAsm)} or {_paths.Relative(_paths.AnalysisScr)}.");
        Console.WriteLine("  Run startup analysis first, or assemble HLS to .scr.");
        return null;
    }

    private static bool HasScrFiles(string directory)
        => Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.scr").Any();

    private void MsgMerge()
    {
        var baseTxtPath = Path.Combine(_paths.MsgSplitOut, "_base_message.txt");
        if (!File.Exists(baseTxtPath))
        {
            Console.WriteLine($"  Base text not found: {_paths.Relative(baseTxtPath)}");
            Console.WriteLine("  Run split first.");
            return;
        }

        try
        {
            var outputPath = Path.Combine(_paths.Msg, "message_merged.txt");
            Directory.CreateDirectory(_paths.Msg);
            var result = new MessageScriptLinker().Merge(baseTxtPath, _paths.MsgSplitOut, outputPath);
            Console.WriteLine($"  Merged -> {_paths.Relative(outputPath)}");
            Console.WriteLine($"    Collected={result.Collected}  Replaced={result.Replaced}  Missing={result.MissingInBase}  Conflicts={result.Conflicts}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    #endregion

    #region Params

    // Params 子菜单: JSON 导出/导入, RawBlob 提取/替换
    private void ParamsMenu()
    {
        while (true)
        {
            Console.WriteLine("  ┌─────────────────────────────┐");
            Console.WriteLine("  │      Params Menu            │");
            Console.WriteLine("  ├─────────────────────────────┤");
            Console.WriteLine("  │  1. Export params -> JSON    │");
            Console.WriteLine("  │  2. Import JSON -> params    │");
            Console.WriteLine("  │  3. Extract RawBlob         │");
            Console.WriteLine("  │  4. Replace RawBlob         │");
            Console.WriteLine("  │  0. Back                    │");
            Console.WriteLine("  └─────────────────────────────┘");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": ParamsExportJson(); break;
                case "2": ParamsImportJson(); break;
                case "3": ParamsExtractRaw(); break;
                case "4": ParamsReplaceRaw(); break;
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    private void ParamsExportJson()
    {
        if (_context.ParamsPath is null)
        {
            Console.WriteLine("  params.dat not found.");
            return;
        }

        try
        {
            var codec = new ParamsDatCodec();
            var document = codec.Read(File.ReadAllBytes(_context.ParamsPath));
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var outputPath = Path.Combine(_paths.AnalysisParams, "params.json");
            Directory.CreateDirectory(_paths.AnalysisParams);
            ReadableUnicodeJson.WriteAllText(outputPath, JsonSerializer.Serialize(document, options));
            Console.WriteLine($"  Exported -> {_paths.Relative(outputPath)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void ParamsImportJson()
    {
        var jsonPath = Path.Combine(_paths.AnalysisParams, "params.json");
        if (!File.Exists(jsonPath))
        {
            Console.WriteLine($"  JSON file not found: {_paths.Relative(jsonPath)}");
            Console.WriteLine("  Run export first.");
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ParamsDatDocument>(
                File.ReadAllText(jsonPath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (document is null)
            {
                Console.WriteLine("  Failed: deserialized document is null.");
                return;
            }

            var codec = new ParamsDatCodec();
            var outputPath = Path.Combine(_paths.AnalysisParams, "params_new.dat");
            Directory.CreateDirectory(_paths.AnalysisParams);
            File.WriteAllBytes(outputPath, codec.Write(document));
            Console.WriteLine($"  Imported -> {_paths.Relative(outputPath)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    // 将 params.dat 中的 RawBlob (加密密钥等) 提取为二进制文件
    private void ParamsExtractRaw()
    {
        if (_context.ParamsPath is null)
        {
            Console.WriteLine("  params.dat not found.");
            return;
        }

        try
        {
            var codec = new ParamsDatCodec();
            var document = codec.Read(File.ReadAllBytes(_context.ParamsPath));
            if (document.GameSystem.RawBlob is null || string.IsNullOrEmpty(document.GameSystem.RawBlob.LinkXorKeyBase64))
            {
                Console.WriteLine("  No RawBlob found in params.dat.");
                return;
            }

            var outputPath = Path.Combine(_paths.AnalysisParams, "rawblob.bin");
            Directory.CreateDirectory(_paths.AnalysisParams);
            File.WriteAllBytes(outputPath, Convert.FromBase64String(document.GameSystem.RawBlob.LinkXorKeyBase64));
            Console.WriteLine($"  Extracted -> {_paths.Relative(outputPath)} ({document.GameSystem.RawBlob.ExpectedWidth}x{document.GameSystem.RawBlob.ExpectedHeight}, {document.GameSystem.RawBlob.ExpectedBytesPerPixel}bpp)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    // 用外部二进制文件替换 params.dat 中的 RawBlob
    private void ParamsReplaceRaw()
    {
        if (_context.ParamsPath is null)
        {
            Console.WriteLine("  params.dat not found.");
            return;
        }

        var rawPath = Path.Combine(_paths.AnalysisParams, "rawblob.bin");
        if (!File.Exists(rawPath))
        {
            Console.WriteLine($"  RawBlob file not found: {_paths.Relative(rawPath)}");
            Console.WriteLine("  Run extract first.");
            return;
        }

        try
        {
            var codec = new ParamsDatCodec();
            var document = codec.Read(File.ReadAllBytes(_context.ParamsPath));
            if (document.GameSystem.RawBlob is null)
            {
                Console.WriteLine("  No RawBlob section in params.dat to replace.");
                return;
            }

            var raw = File.ReadAllBytes(rawPath);
            document.GameSystem.RawBlob.KeyByteLength = raw.Length;
            document.GameSystem.RawBlob.LinkXorKeyBase64 = Convert.ToBase64String(raw);
            var outputPath = Path.Combine(_paths.AnalysisParams, "params_new.dat");
            Directory.CreateDirectory(_paths.AnalysisParams);
            File.WriteAllBytes(outputPath, codec.Write(document));
            Console.WriteLine($"  Replaced -> {_paths.Relative(outputPath)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    #endregion

    #region SCR

    // SCR 子菜单: 默认使用 HLS 高级解析/回编；低级 SCRASM 保留为调试入口
    private void ScrMenu()
    {
        Console.WriteLine("  SCR encoding settings (press Enter for default cp932):");
        var readEnc = Prompt("Read encoding").Trim();
        var writeEnc = Prompt("Write encoding").Trim();
        if (string.IsNullOrEmpty(readEnc)) readEnc = null;
        if (string.IsNullOrEmpty(writeEnc)) writeEnc = null;
        Console.WriteLine($"    Read:  {readEnc ?? "cp932 (default)"}");
        Console.WriteLine($"    Write: {writeEnc ?? "cp932 (default)"}");
        Console.WriteLine();

        while (true)
        {
            Console.WriteLine("  ┌─────────────────────────────┐");
            Console.WriteLine("  │       SCR Menu              │");
            Console.WriteLine("  ├─────────────────────────────┤");
            Console.WriteLine("  │  1. Decompile .scr -> HLS   │");
            Console.WriteLine("  │  2. Assemble HLS -> .scr    │");
            Console.WriteLine("  │  3. Low-level disasm        │");
            Console.WriteLine("  │  4. Low-level asm           │");
            Console.WriteLine("  │  5. TBL support tables      │");
            Console.WriteLine("  │  0. Back                    │");
            Console.WriteLine("  └─────────────────────────────┘");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": ScrDecompileHighLevel(readEnc); break;
                case "2": ScrAssembleHighLevel(writeEnc); break;
                case "3": ScrDisassembleLowLevel(readEnc, writeEnc); break;
                case "4": ScrAssembleLowLevel(readEnc, writeEnc); break;
                case "5": TblSupportMenu(); break;
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    // 批量高级解析 analysis/scr/ 下的 .scr 文件到 analysis/scr_hls/
    private void ScrDecompileHighLevel(string? readEncoding)
    {
        if (!Directory.Exists(_paths.AnalysisScr) || !Directory.EnumerateFiles(_paths.AnalysisScr, "*.scr").Any())
        {
            Console.WriteLine($"  No .scr files found in {_paths.Relative(_paths.AnalysisScr)}");
            Console.WriteLine("  Extract scr.arc first (startup analysis or link unpack).");
            return;
        }

        try
        {
            var containerCodec = new ScrContainerCodec();
            var hlsDecompiler = new ScrHighLevelDecompiler(readEncoding, _context.Params);
            var scrFiles = Directory.GetFiles(_paths.AnalysisScr, "*.scr", SearchOption.AllDirectories);
            Directory.CreateDirectory(_paths.AnalysisScrHls);
            int ok = 0, fail = 0;
            using var progress = PictureProcessing.StartProgress("HLS scr", scrFiles.Length);
            foreach (var scr in scrFiles)
            {
                try
                {
                    var input = File.ReadAllBytes(scr);
                    var outputPath = Path.Combine(_paths.AnalysisScrHls, Path.GetFileNameWithoutExtension(scr) + ".hls.txt");
                    if (TblstrScrCodec.IsTblstrScr(input))
                    {
                        var tblstrCodec = new TblstrScrCodec(readEncoding);
                        var document = tblstrCodec.Read(input, Path.GetFileName(scr), TblstrScrCodec.TryReadSiblingLabels(scr));
                        File.WriteAllText(outputPath, new TblstrScrTextFormatter().WriteHls(document), Encoding.UTF8);
                    }
                    else
                    {
                        var document = containerCodec.Read(input, Path.GetFileName(scr));
                        File.WriteAllText(outputPath, hlsDecompiler.Write(document), Encoding.UTF8);
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Failed: {Path.GetFileName(scr)}: {ex.Message}");
                    fail++;
                }
                finally
                {
                    progress.Increment();
                }
            }
            Console.WriteLine($"  HLS decompile done -> {_paths.Relative(_paths.AnalysisScrHls)} ({ok} ok, {fail} failed)");
            ExportScrSupportTablesToHls(overwrite: true, indent: "  ");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    // 批量回编 analysis/scr_hls/ 下的 .hls.txt
    private void ScrAssembleHighLevel(string? writeEncoding)
    {
        if (!Directory.Exists(_paths.AnalysisScrHls) || !Directory.EnumerateFiles(_paths.AnalysisScrHls, "*.hls.txt").Any())
        {
            Console.WriteLine($"  No .hls.txt files found in {_paths.Relative(_paths.AnalysisScrHls)}");
            Console.WriteLine("  Run HLS decompile first.");
            return;
        }

        try
        {
            var containerCodec = new ScrContainerCodec();
            var hlsCodec = new ScrHighLevelTextCodec(writeEncoding);
            var hlsFiles = Directory.GetFiles(_paths.AnalysisScrHls, "*.hls.txt", SearchOption.AllDirectories);
            Directory.CreateDirectory(_paths.AnalysisScrAsm);
            int ok = 0, fail = 0;
            using var progress = PictureProcessing.StartProgress("HLS ASM scr", hlsFiles.Length);
            foreach (var hls in hlsFiles)
            {
                try
                {
                    var hlsText = File.ReadAllText(hls, Encoding.UTF8);
                    var outputPath = Path.Combine(_paths.AnalysisScrAsm, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(hls)) + ".scr");
                    if (hlsText.StartsWith(".file kind=tblstr_scr_hls", StringComparison.Ordinal))
                    {
                        var tblstrDocument = new TblstrScrHlsTextCodec(writeEncoding).Read(hlsText, Path.GetFileName(outputPath));
                        File.WriteAllBytes(outputPath, TblstrScrCodec.WriteRaw(tblstrDocument));
                        ok++;
                        continue;
                    }

                    if (hlsText.StartsWith("# TBLSTR-SCR-", StringComparison.Ordinal))
                    {
                        throw new NotSupportedException("TBLSTR系 HLS/IR 当前只支持解析输出，暂未实现可编辑回编。");
                    }

                    var document = hlsCodec.Read(hlsText);
                    File.WriteAllBytes(outputPath, containerCodec.Write(document));
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Failed: {Path.GetFileName(hls)}: {ex.Message}");
                    fail++;
                }
                finally
                {
                    progress.Increment();
                }
            }
            ImportScrSupportTablesToAsm(indent: "  ");
            Console.WriteLine($"  HLS assemble done -> {_paths.Relative(_paths.AnalysisScrAsm)} ({ok} ok, {fail} failed)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    // 批量低级反汇编 analysis/scr/ 下的 .scr 文件到 analysis/scr_disasm/
    private void ScrDisassembleLowLevel(string? readEncoding, string? writeEncoding)
    {
        if (!Directory.Exists(_paths.AnalysisScr) || !Directory.EnumerateFiles(_paths.AnalysisScr, "*.scr").Any())
        {
            Console.WriteLine($"  No .scr files found in {_paths.Relative(_paths.AnalysisScr)}");
            Console.WriteLine("  Extract scr.arc first (startup analysis or link unpack).");
            return;
        }

        try
        {
            var containerCodec = new ScrContainerCodec();
            var textCodec = new ScrTextCodec(readEncoding, writeEncoding);
            var scrFiles = Directory.GetFiles(_paths.AnalysisScr, "*.scr", SearchOption.AllDirectories);
            Directory.CreateDirectory(_paths.AnalysisScrDisasm);
            int ok = 0, fail = 0;
            using var progress = PictureProcessing.StartProgress("SCRASM disasm", scrFiles.Length);
            foreach (var scr in scrFiles)
            {
                try
                {
                    var input = File.ReadAllBytes(scr);
                    var outputPath = Path.Combine(_paths.AnalysisScrDisasm, Path.GetFileNameWithoutExtension(scr) + ".disasm.txt");
                    if (TblstrScrCodec.IsTblstrScr(input))
                    {
                        var tblstrCodec = new TblstrScrCodec(readEncoding);
                        var document = tblstrCodec.Read(input, Path.GetFileName(scr), TblstrScrCodec.TryReadSiblingLabels(scr));
                        File.WriteAllText(outputPath, new TblstrScrTextFormatter().WriteDisasm(document), Encoding.UTF8);
                    }
                    else
                    {
                        var document = containerCodec.Read(input, Path.GetFileName(scr));
                        File.WriteAllText(outputPath, textCodec.Write(document), Encoding.UTF8);
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Failed: {Path.GetFileName(scr)}: {ex.Message}");
                    fail++;
                }
                finally
                {
                    progress.Increment();
                }
            }
            Console.WriteLine($"  Low-level disasm done -> {_paths.Relative(_paths.AnalysisScrDisasm)} ({ok} ok, {fail} failed)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    // 批量低级汇编 analysis/scr_disasm/ 下的 .disasm.txt
    private void ScrAssembleLowLevel(string? readEncoding, string? writeEncoding)
    {
        if (!Directory.Exists(_paths.AnalysisScrDisasm) || !Directory.EnumerateFiles(_paths.AnalysisScrDisasm, "*.disasm.txt").Any())
        {
            Console.WriteLine($"  No .disasm.txt files found in {_paths.Relative(_paths.AnalysisScrDisasm)}");
            Console.WriteLine("  Run low-level disasm first.");
            return;
        }

        try
        {
            var containerCodec = new ScrContainerCodec();
            var textCodec = new ScrTextCodec(readEncoding, writeEncoding);
            var txtFiles = Directory.GetFiles(_paths.AnalysisScrDisasm, "*.disasm.txt", SearchOption.AllDirectories);
            Directory.CreateDirectory(_paths.AnalysisScrAsm);
            int ok = 0, fail = 0;
            using var progress = PictureProcessing.StartProgress("SCRASM asm", txtFiles.Length);
            foreach (var txt in txtFiles)
            {
                try
                {
                    var document = textCodec.Read(File.ReadAllText(txt, Encoding.UTF8));
                    var outputPath = Path.Combine(_paths.AnalysisScrAsm, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(txt)) + ".scr");
                    File.WriteAllBytes(outputPath, containerCodec.Write(document));
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Failed: {Path.GetFileName(txt)}: {ex.Message}");
                    fail++;
                }
                finally
                {
                    progress.Increment();
                }
            }
            Console.WriteLine($"  Low-level assemble done -> {_paths.Relative(_paths.AnalysisScrAsm)} ({ok} ok, {fail} failed)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    #endregion

    #region Picture

    // Picture 子菜单: 分拣/转换/重打包/还原,
    private void PictureMenu()
    {
        Console.WriteLine("  Detected picture archives (from params):");
        var pictureArcs = new List<string>();
        if (_context.Params is not null)
        {
            pictureArcs = _context.Params.GameSystem.InstallTable
                .Select(e => e.File)
                .Where(f => !IsNonPictureArchive(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var arc in pictureArcs)
            {
                var unpackDir = Path.Combine(_paths.Link6Unpack, GetArchiveWorkDirectoryName(arc));
                var exists = Directory.Exists(unpackDir);
                Console.WriteLine($"    {arc} {(exists ? "[unpacked]" : "[not unpacked]")}");
            }
        }
        else
        {
            Console.WriteLine("    (params.dat not loaded)");
        }

        Console.WriteLine();

        while (true)
        {
            Console.WriteLine("  ┌────────────────────────────────────────────┐");
            Console.WriteLine("  │              Picture Menu                  │");
            Console.WriteLine("  ├────────────────────────────────────────────┤");
            Console.WriteLine("  │  1. Sort (unpack -> pic)                   │");
            Console.WriteLine("  │  2. Convert (orig -> png)                  │");
            Console.WriteLine("  │  3. Repack (fix -> new)                    │");
            Console.WriteLine("  │  4. Repack-fix (single fix dir)            │");
            Console.WriteLine("  │  5. Restore (new -> pack)                  │");
            Console.WriteLine("  │  6. Restore with replenish                 │");
            Console.WriteLine("  │  7. Test repack (png -> new)               │");
            Console.WriteLine("  │  0. Back                                   │");
            Console.WriteLine("  └────────────────────────────────────────────┘");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": PicSort(); break;
                case "2": PicConvert(); break;
                case "3": PicRepack(); break;
                case "4": PicRepackFix(); break;
                case "5": PicRestore(); break;
                case "6": PicRestoreWithReplenish(); break;
                case "7": PicRepackPng(); break;
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    // 从 archive_unpack/ 的子目录按文件格式分拣到 pic/
    private void PicSort()
    {
        var unpackedDirs = new List<string>();
        if (Directory.Exists(_paths.Link6Unpack))
        {
            unpackedDirs = Directory.GetDirectories(_paths.Link6Unpack)
                .Select(d => new DirectoryInfo(d).Name)
                .Where(n => !IsNonPictureArchive(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (unpackedDirs.Count == 0)
        {
            Console.WriteLine("  No unpacked archive directories in archive_unpack/. Unpack first.");
            return;
        }

        Console.WriteLine("  Available unpacked directories:");
        foreach (var d in unpackedDirs)
        {
            Console.WriteLine($"    {d}/");
        }

        Console.WriteLine();
        Console.WriteLine("  Enter '-all' for all, or comma-separated names:");
        var input = Prompt("Source dirs").Trim();

        List<string> selected;
        if (input.Equals("-all", StringComparison.OrdinalIgnoreCase))
        {
            selected = unpackedDirs;
        }
        else
        {
            selected = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        var jobs = new List<(string Name, string SourceDirectory, string OutputDirectory)>();
        foreach (var dirName in selected)
        {
            var srcDir = Path.Combine(_paths.Link6Unpack, dirName);
            if (!Directory.Exists(srcDir))
            {
                Console.WriteLine($"  Warning: {dirName}/ not found, skipped.");
                continue;
            }

            var dstDir = Path.Combine(_paths.Pic, dirName);
            jobs.Add((dirName, srcDir, dstDir));
        }

        RunParallelBatch(
            "SORT",
            jobs,
            job => job.Name,
            (job, report) =>
            {
                var summary = FileSorter.Sort(job.SourceDirectory, job.OutputDirectory, job.Name, job.Name, quiet: true, progress: report);
                return $"{summary.Success}/{summary.Total} ok, {summary.Failure} failed, {summary.Unrecognized} unknown";
            });
    }

    private void PicConvert()
    {
        var archiveDirs = Directory.Exists(_paths.Pic)
            ? Directory.GetDirectories(_paths.Pic)
                .Where(IsPictureWorkDirectory)
                .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        if (archiveDirs.Count == 0)
        {
            Console.WriteLine($"  Converting in {_paths.Relative(_paths.Pic)} ...");
            try
            {
                FileConverter.ConvertAll(_paths.Pic);
                Console.WriteLine("  Convert done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Failed: {ex.Message}");
            }
            return;
        }

        RunParallelBatch(
            "CONVERT",
            archiveDirs,
            dir => new DirectoryInfo(dir).Name,
            (dir, report) =>
            {
                var summary = FileConverter.ConvertAll(dir, quiet: true, progress: report);
                return $"{summary.Success}/{summary.Total} ok, {summary.Failure} failed";
            });
    }

    private static bool IsPictureWorkDirectory(string directory)
    {
        var formatNames = new[] { "ap0", "ap2", "ap3", "anm", "plt", "bmp", "ap" };
        return formatNames.Any(format => Directory.Exists(Path.Combine(directory, format)));
    }

    private static void RunParallelBatch<T>(
        string operation,
        IReadOnlyList<T> jobs,
        Func<T, string> labelSelector,
        Func<T, Action<int, int>, string> action)
    {
        if (jobs.Count == 0)
        {
            Console.WriteLine($"  [{operation}] no jobs.");
            return;
        }

        var parallelism = ResolveBatchParallelism();
        using var progress = new BatchProgress(operation, jobs.Select(labelSelector).ToList(), parallelism);
        var totalStopwatch = Stopwatch.StartNew();
        var success = 0;
        var failure = 0;

        Parallel.ForEach(
            Enumerable.Range(0, jobs.Count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            index =>
            {
                var stopwatch = Stopwatch.StartNew();
                progress.Update(index, "running", 0, null, "");
                try
                {
                    var detail = action(jobs[index], (done, total) =>
                    {
                        var percent = total <= 0 ? 0 : Math.Clamp((int)Math.Round(done * 100.0 / total), 0, 99);
                        progress.Update(index, "running", percent, stopwatch.Elapsed, "");
                    });
                    Interlocked.Increment(ref success);
                    progress.Update(index, "done", 100, stopwatch.Elapsed, detail);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failure);
                    progress.Update(index, "failed", 100, stopwatch.Elapsed, ex.Message);
                }
            });

        progress.Finish($"  [{operation} SUMMARY] {jobs.Count} total, {success} success, {failure} failure, elapsed={totalStopwatch.Elapsed.TotalSeconds:F2}s.");
    }

    private static void RunSingleProgress(
        string operation,
        string label,
        Func<Action<int, int>, string> action)
    {
        using var progress = new BatchProgress(operation, new[] { label }, 1);
        var stopwatch = Stopwatch.StartNew();
        progress.Update(0, "running", 0, null, "");
        try
        {
            var detail = action((done, total) =>
            {
                var percent = total <= 0 ? 0 : Math.Clamp((int)Math.Round(done * 100.0 / total), 0, 99);
                progress.Update(0, "running", percent, stopwatch.Elapsed, "");
            });
            progress.Update(0, "done", 100, stopwatch.Elapsed, "");
            progress.Finish($"  [{operation} SUMMARY] {detail}, elapsed={stopwatch.Elapsed.TotalSeconds:F2}s.");
        }
        catch (Exception ex)
        {
            progress.Update(0, "failed", 100, stopwatch.Elapsed, ex.Message);
            progress.Finish($"  [{operation} SUMMARY] failed, elapsed={stopwatch.Elapsed.TotalSeconds:F2}s.");
        }
    }

    private static string FormatRestoreSummary(Restorer.RestoreSummary summary)
    {
        return $"{summary.Copied}/{summary.Total} copied, {summary.Restored} restored, {summary.Replenished} replenished, {summary.Skipped} skipped, {summary.Failed} failed";
    }

    private static string FormatConversionSummary(FileConverter.ConversionSummary summary)
    {
        return $"{summary.Batches} batches, {summary.Total} total, {summary.Success} success, {summary.Failure} failure";
    }

    private static int ResolveBatchParallelism()
    {
        var value = Environment.GetEnvironmentVariable("KAGUYA_BATCH_PARALLELISM");
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return Math.Clamp(parsed, 1, 16);
        }

        return Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    }

    private sealed class BatchProgress : IDisposable
    {
        private const int BarWidth = 18;

        private readonly object _lock = new();
        private readonly IReadOnlyList<string> _labels;
        private readonly int _labelWidth;
        private readonly int _indexDigits;
        private bool _interactive;
        private readonly int _top;
        private readonly int _width;
        private bool _finished;

        public BatchProgress(string operation, IReadOnlyList<string> labels, int parallelism)
        {
            _labels = labels;
            _labelWidth = Math.Clamp(labels.Max(label => label.Length), 16, 32);
            _indexDigits = Math.Max(2, labels.Count.ToString().Length);

            Console.WriteLine($"  [{operation}] {labels.Count} job(s), parallel={parallelism}");
            _interactive = TryReserveRenderArea(labels.Count, out _top);
            _width = _interactive ? Math.Max(80, Console.WindowWidth - 1) : 120;
            for (var i = 0; i < labels.Count; i++)
            {
                Render(i, "pending", 0, null, "");
            }
        }

        public void Update(int index, string status, int percent, TimeSpan? elapsed, string detail)
        {
            lock (_lock)
            {
                Render(index, status, percent, elapsed, detail);
            }
        }

        public void Finish(string summary)
        {
            lock (_lock)
            {
                if (_interactive)
                {
                    TrySetCursorPosition(0, _top + _labels.Count);
                }

                Console.WriteLine(summary);
                _finished = true;
            }
        }

        private void Render(int index, string status, int percent, TimeSpan? elapsed, string detail)
        {
            if (_interactive)
            {
                TrySetCursorPosition(0, _top + index);
            }

            var filled = Math.Clamp((int)Math.Round(percent / 100.0 * BarWidth), 0, BarWidth);
            var bar = new string('>', filled) + new string('-', BarWidth - filled);
            var elapsedText = elapsed is null ? "" : $"{elapsed.Value.TotalSeconds:F2}s";
            var label = Truncate(_labels[index], _labelWidth).PadRight(_labelWidth);
            var indexFormat = $"D{_indexDigits}";
            var indexText = $"{(index + 1).ToString(indexFormat)}/{_labels.Count.ToString(indexFormat)}";
            var line = $"  [{indexText}] {label}: [{bar}] {percent,3}% {status,-7}";
            if (!string.IsNullOrEmpty(elapsedText))
            {
                line += $" {elapsedText}";
            }

            if (!string.IsNullOrWhiteSpace(detail))
            {
                line += $" {detail}";
            }
            if (line.Length > _width)
            {
                line = line[.._width];
            }

            Console.Write(line.PadRight(_width));
            if (!_interactive)
            {
                Console.WriteLine();
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 1)] + "~";
        }

        private static bool TryReserveRenderArea(int rowCount, out int top)
        {
            top = 0;
            if (Console.IsOutputRedirected)
            {
                return false;
            }

            try
            {
                var requiredRows = rowCount + 1;
                if (requiredRows >= Console.BufferHeight)
                {
                    return false;
                }

                for (var i = 0; i < rowCount; i++)
                {
                    Console.WriteLine();
                }

                top = Console.CursorTop - rowCount;
                if (top < 0 || top + rowCount >= Console.BufferHeight)
                {
                    return false;
                }

                Console.SetCursorPosition(0, top);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private void TrySetCursorPosition(int left, int top)
        {
            try
            {
                if (top < 0 || top >= Console.BufferHeight)
                {
                    _interactive = false;
                    return;
                }

                Console.SetCursorPosition(left, top);
            }
            catch (IOException)
            {
                _interactive = false;
            }
            catch (ArgumentOutOfRangeException)
            {
                _interactive = false;
            }
        }

        public void Dispose()
        {
            if (!_finished && _interactive)
            {
                TrySetCursorPosition(0, _top + _labels.Count);
            }
        }
    }

    private void PicRepack()
    {
        Console.WriteLine($"  Repacking in {_paths.Relative(_paths.Pic)} ...");
        RunSingleProgress(
            "REPACK FIX",
            "pic -> new",
            report => FormatConversionSummary(FileConverter.RepackAll(_paths.Pic, quiet: true, progress: report)));
    }

    private void PicRepackPng()
    {
        Console.WriteLine($"  Test repacking PNG in {_paths.Relative(_paths.Pic)} ...");
        RunSingleProgress(
            "REPACK PNG",
            "pic -> new",
            report => FormatConversionSummary(FileConverter.RepackPngAll(_paths.Pic, quiet: true, progress: report)));
    }

    private void PicRepackFix()
    {
        Console.WriteLine("  Enter the fix directory path (e.g. pic/cg00/ap2/fix):");
        var fixDir = Prompt("Fix dir").Trim();
        if (string.IsNullOrEmpty(fixDir)) return;

        if (!Path.IsPathRooted(fixDir))
        {
            fixDir = Path.Combine(_paths.Pic, fixDir);
        }

        RunSingleProgress(
            "REPACK FIX DIR",
            _paths.Relative(fixDir),
            report => FormatConversionSummary(FileConverter.RepackFix(fixDir, quiet: true, progress: report)));
    }

    private void PicRestore()
    {
        Console.WriteLine($"  Restoring from {_paths.Relative(_paths.Pic)} -> {_paths.Relative(_paths.Link6Pack)} ...");
        RunSingleProgress(
            "RESTORE",
            "pic -> archive_pack",
            report => FormatRestoreSummary(Restorer.Restore(_paths.Pic, _paths.Link6Pack, report)));
    }

    private void PicRestoreWithReplenish()
    {
        Console.WriteLine("  Enter formats to exclude (e.g. bmp,ap2) or press Enter for none:");
        var excludeInput = Prompt("Exclude").Trim();
        HashSet<string>? exclude = null;
        if (!string.IsNullOrEmpty(excludeInput))
        {
            exclude = excludeInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();
        }

        Console.WriteLine($"  Restoring with replenish from {_paths.Relative(_paths.Pic)} -> {_paths.Relative(_paths.Link6Pack)} ...");
        RunSingleProgress(
            "RESTORE REPLENISH",
            "pic -> archive_pack",
            report => FormatRestoreSummary(Restorer.RestoreWithReplenish(_paths.Pic, _paths.Link6Pack, exclude, report)));
    }

    private static bool IsNonPictureArchive(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return name == "scr" ||
               name == "bgm" ||
               name == "sed" ||
               name == "se" ||
               name == "wav" ||
               name.StartsWith("voice") ||
               IsVoiceArchiveName(name);
    }

    private static bool IsVoiceArchiveName(string name) =>
        name.Length > 2 &&
        name.StartsWith("vo", StringComparison.OrdinalIgnoreCase) &&
        name.Skip(2).All(char.IsDigit);

    #endregion

    #region Character

    // Character 子菜单: 从 pic/ 合成 CG/立绘到 character/
    private void CharacterMenu()
    {
        while (true)
        {
            Console.WriteLine("  ┌──────────────────────────────────┐");
            Console.WriteLine("  │      Character Menu              │");
            Console.WriteLine("  ├──────────────────────────────────┤");
            Console.WriteLine("  │  1. Compose CG/SP (pic -> char)  │");
            Console.WriteLine("  │  2. SP Viewer (GUI)              │");
            Console.WriteLine("  │  0. Back                         │");
            Console.WriteLine("  └──────────────────────────────────┘");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": CharacterCompose(); break;
                case "2": CharacterSpViewer(); break;
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    private void CharacterCompose()
    {
        if (!EnsureCharacterInputs("Character compose"))
        {
            return;
        }

        Console.WriteLine($"  Composing from {_paths.Relative(_paths.Pic)} -> {_paths.Relative(_paths.Character)} ...");
        try
        {
            var width = (int)(_context.Params?.GameSystem.Width ?? 1280);
            var height = (int)(_context.Params?.GameSystem.Height ?? 720);
            var result = CharacterComposer.ComposeAll(_paths.Pic, _paths.Character, _context.Params, width, height);
            CharacterCommands.PrintResult(_paths.Relative(_paths.Character), result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void CharacterSpViewer()
    {
        if (!EnsureCharacterInputs("SP Viewer", allowTblstrScr: true))
        {
            return;
        }

        Console.WriteLine("  Launching SP Viewer GUI ...");
        try
        {
            var (width, height) = ResolveSpViewerCanvasSize();
            var source = _context.IsTblstrFamily
                ? SpViewerSource.FromTblstrScr(_paths.AnalysisScr)
                : SpViewerSource.FromParams(_context.Params);
            SpViewerApp.Launch(_paths.Pic, source, width, height);
            Console.WriteLine("  SP Viewer closed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private bool EnsureCharacterInputs(string featureName, bool allowTblstrScr = false)
    {
        if (_context.Params?.Pattern is null && !(allowTblstrScr && HasTblstrScrInputs()))
        {
            Console.WriteLine(_context.IsTblstrFamily
                ? $"  {featureName} requires TBLSTR .scr files in {_paths.Relative(_paths.AnalysisScr)}."
                : $"  {featureName} requires params.dat Pattern data.");
            return false;
        }

        if (!Directory.Exists(_paths.Pic))
        {
            Console.WriteLine($"  Picture workspace not found: {_paths.Relative(_paths.Pic)}");
            Console.WriteLine("  Run picture sort/convert first.");
            return false;
        }

        var hasCharacterPictureDir = Directory.EnumerateDirectories(_paths.Pic)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Any(name =>
                name!.StartsWith("cg", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("sp", StringComparison.OrdinalIgnoreCase));

        if (!hasCharacterPictureDir)
        {
            Console.WriteLine($"  No cg*/sp* picture folders found in {_paths.Relative(_paths.Pic)}.");
            Console.WriteLine("  Run picture sort/convert first.");
            return false;
        }

        return true;
    }

    private (int Width, int Height) ResolveSpViewerCanvasSize()
    {
        if (_context.Params is not null)
        {
            return ((int)_context.Params.GameSystem.Width, (int)_context.Params.GameSystem.Height);
        }

        if (TryInferCanvasSizeFromBackgrounds(_paths.Pic, out var width, out var height))
        {
            Console.WriteLine($"  TBLSTR canvas inferred from BG resources: {width}x{height}");
            return (width, height);
        }

        Console.WriteLine("  TBLSTR canvas size not found; using fallback 1280x720.");
        return (1280, 720);
    }

    private static bool TryInferCanvasSizeFromBackgrounds(string picDir, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!Directory.Exists(picDir))
        {
            return false;
        }

        var backgroundDirs = Directory.GetDirectories(picDir)
            .Where(dir => Path.GetFileName(dir).StartsWith("bg", StringComparison.OrdinalIgnoreCase))
            .Concat(new[] { Path.Combine(picDir, "bgd"), Path.Combine(picDir, "BG") }.Where(Directory.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase);

        var sizes = new Dictionary<(int Width, int Height), int>();
        foreach (var backgroundDir in backgroundDirs)
        {
            foreach (var formatDir in Directory.GetDirectories(backgroundDir).OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase))
            {
                var pngDir = Path.Combine(formatDir, "png");
                if (!Directory.Exists(pngDir))
                {
                    continue;
                }

                foreach (var png in Directory.GetFiles(pngDir, "*.png", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        using var image = System.Drawing.Image.FromFile(png);
                        if (image.Width <= 0 || image.Height <= 0)
                        {
                            continue;
                        }

                        var key = (image.Width, image.Height);
                        sizes[key] = sizes.GetValueOrDefault(key) + 1;
                    }
                    catch
                    {
                        // skip unreadable background candidates
                    }
                }
            }
        }

        if (sizes.Count == 0)
        {
            return false;
        }

        var best = sizes
            .OrderByDescending(pair => pair.Value)
            .ThenByDescending(pair => pair.Key.Width * pair.Key.Height)
            .First()
            .Key;
        width = best.Width;
        height = best.Height;
        return true;
    }

    private bool HasTblstrScrInputs() =>
        _context.IsTblstrFamily &&
        Directory.Exists(_paths.AnalysisScr) &&
        Directory.EnumerateFiles(_paths.AnalysisScr, "*.scr", SearchOption.AllDirectories).Any();

    #endregion

    #region Helpers

    private static string Prompt(string label)
    {
        Console.Write($"  {label}> ");
        return Console.ReadLine() ?? "";
    }

    private static string PromptPath(string label, bool mustExist)
    {
        while (true)
        {
            Console.Write($"  {label}: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(input))
            {
                Console.WriteLine("  >> Path cannot be empty.");
                continue;
            }

            var fullPath = Path.GetFullPath(input);
            if (mustExist && !Directory.Exists(fullPath))
            {
                Console.WriteLine($"  >> Directory not found: {fullPath}");
                Console.WriteLine("  >> Create it? (y/n)");
                Console.Write("  >> ");
                var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (confirm == "y")
                {
                    Directory.CreateDirectory(fullPath);
                    return fullPath;
                }
                continue;
            }

            return fullPath;
        }
    }

    private static string? FindDefaultIni()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ini", "message_config.ini"),
            Path.Combine(Environment.CurrentDirectory, "ini", "message_config.ini")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindDefaultTblstrIni()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ini", "tblstr_config.ini"),
            Path.Combine(Environment.CurrentDirectory, "ini", "tblstr_config.ini")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static TblstrCodec CreateTblstrCodec(MessagePlaceholderConfig config)
    {
        var readEncoding = MessageDatCodec.ResolveEncoding(config.ReadEncodingName);
        var writeEncoding = MessageDatCodec.ResolveEncoding(config.WriteEncodingName);
        return new TblstrCodec(readEncoding, writeEncoding, config);
    }

    private static string GetArchiveWorkDirectoryName(string archiveFileName)
    {
        var name = Path.GetFileNameWithoutExtension(archiveFileName);
        return name.Length > 0 && char.IsWhiteSpace(name[^1])
            ? Path.GetFileName(archiveFileName)
            : name;
    }

    // ─── UI 辅助方法 ─────────────────────────────────────────────────

    private static void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════╗");
        Console.WriteLine("  ║   Kaguya_YaneKit  Interactive Mode  ║");
        Console.WriteLine("  ╚══════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static void PrintSection(string title)
    {
        Console.WriteLine($"  ── {title} ──────────────────────────────");
    }

    private static void PrintSectionEnd()
    {
        Console.WriteLine("  ──────────────────────────────────────────");
        Console.WriteLine();
    }

    private static void PrintSubMenu(string title)
    {
        Console.WriteLine($"  ── {title} ──");
    }

    private static void PrintKeyValue(string key, string value)
    {
        Console.WriteLine($"  {key} : {value}");
    }

    #endregion
}
