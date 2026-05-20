// ============================================================================
// InteractiveSession.cs
// 交互式菜单主循环: 无命令行参数时进入此模式
//
// 启动流程:
//   1. 提示用户输入游戏根目录和工作目录
//   2. 创建 KaguyaRuntimeContext 和 WorkspacePaths
//   3. 执行启动分析 (导出 params / 解包 scr.arc / 反汇编 .scr)
//   4. 进入主菜单循环
//
// 菜单树:
//   Main Menu
//   ├── 1. Archive Unpack    -- 解包 .arc 档案到 link6_unpack/
//   ├── 2. Archive Pack         -- 从 link6_pack/ 子目录打包为 .arc
//   ├── 3. Message        		-- message.dat 导出/导入/拆分/合并
//   ├── 4. Params         		-- params.dat JSON 导出/导入, RawBlob 操作
//   ├── 5. SCR            		-- .scr 反汇编/汇编
//   ├── 6. Picture        		-- 图片分拣/转换/重打包/还原
//   ├── 7. Character      		-- CG/立绘合成
//   ├── 8. PE             -- (TBD)
//   └── 0. Exit
//
// 依赖: KaguyaRuntimeContext, WorkspacePaths,
//          LinkArchiveCodec, ScrContainerCodec, ScrTextCodec,
//          MessageDatCodec, MessageTextCodec, MessageScriptLinker,
//          ParamsDatCodec, FileSorter, FileConverter, Restorer,
//          CharacterComposer, PictureProcessing
// ============================================================================

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kaguya_YaneKit.Core;
using Kaguya_YaneKit.Formats.Archive;
using Kaguya_YaneKit.Formats.Character;
using Kaguya_YaneKit.Formats.Params;
using Kaguya_YaneKit.Formats.Picture;
using Kaguya_YaneKit.Gui;
using Kaguya_YaneKit.Message;
using Kaguya_YaneKit.Scr;

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
        PrintKeyValue(">>Params   ", _context.ParamsPath ?? "(not found)");
        PrintKeyValue(">>Params v ", _context.ParamsVersion ?? "(unknown)");
        Console.WriteLine();

        RunStartupAnalysis();

        return MainMenuLoop();
    }

    // 启动分析

    // 自动执行三步启动分析:
    //   [1/3] 导出 params.dat 为 JSON
    //   [2/3] 解包 scr.arc
    //   [3/3] 批量反汇编 .scr 文件
    private void RunStartupAnalysis()
    {
        PrintSection("Startup Analysis");

        if (_context.Params is not null && _context.ParamsPath is not null)
        {
            Console.WriteLine($"  [1/3] Exporting params.dat ({_context.Params.Header}, version {_context.ParamsVersion}) ...");
            try
            {
                var jsonPath = Path.Combine(_paths.AnalysisParams, "params.json");
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                ReadableUnicodeJson.WriteAllText(jsonPath, JsonSerializer.Serialize(_context.Params, options));
                Console.WriteLine($"        -> {_paths.Relative(jsonPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"        Failed: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("  [1/3] params.dat not found, skipped.");
        }

        var scrArcPath = Path.Combine(_context.GameRoot, "scr.arc");

        if (File.Exists(scrArcPath))
        {
            Console.WriteLine("  [2/3] Extracting scr.arc ...");
            try
            {
                var codec = new LinkArchiveCodec();
                codec.Extract(scrArcPath, _paths.AnalysisScr, _context.ParamsPath, _context.LinkEncryptionKey, decrypt: true);
                Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScr)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"        Failed: {ex.Message}");
            }

            Console.WriteLine("  [3/3] Disassembling .scr files ...");
            try
            {
                var containerCodec = new ScrContainerCodec();
                var textCodec = new ScrTextCodec();
                var scrFiles = Directory.GetFiles(_paths.AnalysisScr, "*.scr", SearchOption.AllDirectories);
                Directory.CreateDirectory(_paths.AnalysisScrDisasm);
                int ok = 0, fail = 0;
                using var progress = PictureProcessing.StartProgress("DISASM scr", scrFiles.Length);
                foreach (var scr in scrFiles)
                {
                    try
                    {
                        var document = containerCodec.Read(File.ReadAllBytes(scr), Path.GetFileName(scr));
                        var outputPath = Path.Combine(_paths.AnalysisScrDisasm, Path.GetFileNameWithoutExtension(scr) + ".disasm.txt");
                        File.WriteAllText(outputPath, textCodec.Write(document), Encoding.UTF8);
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
                Console.WriteLine($"        -> {_paths.Relative(_paths.AnalysisScrDisasm)} ({ok} ok, {fail} failed)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"        Failed: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("  [2/3] scr.arc not found, skipped.");
            Console.WriteLine("  [3/3] Disassembly skipped (no scr.arc).");
        }

        PrintSectionEnd();
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
            Console.WriteLine("  ║  3. Message Processing       ║");
            Console.WriteLine("  ║  4. Params Processing        ║");
            Console.WriteLine("  ║  5. SCR Processing           ║");
            Console.WriteLine("  ║  6. Picture Processing       ║");
            Console.WriteLine("  ║  7. Character CG/SP Compose  ║");
            Console.WriteLine("  ║  8. PE Processing     (TBD)  ║");
            Console.WriteLine("  ║  0. Exit                     ║");
            Console.WriteLine("  ╚══════════════════════════════╝");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": LinkUnpackMenu(); break;
                case "2": LinkPackMenu(); break;
                case "3": MessageMenu(); break;
                case "4": ParamsMenu(); break;
                case "5": ScrMenu(); break;
                case "6": PictureMenu(); break;
                case "7": CharacterMenu(); break;
                case "8": Console.WriteLine("  PE processing is not yet implemented."); break;
                case "0": Console.WriteLine("  See you again~~~"); return 0;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }
        }
    }

    #region Link Unpack

    // 列出游戏目录下所有 .arc, 用户选择后逐个解包到 link6_unpack/
    private void LinkUnpackMenu()
    {
        var arcDir = _context.GameRoot;

        var archives = Directory.GetFiles(arcDir, "*.arc")
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (archives.Count == 0)
        {
            Console.WriteLine("  No .arc files found in game directory.");
            return;
        }

        PrintSubMenu("Link Unpack");
        Console.WriteLine("  Available archives:");
        foreach (var arc in archives)
        {
            Console.WriteLine($"    {arc}");
        }

        Console.WriteLine();
        Console.WriteLine("  Enter '-all' to extract all, or comma-separated archive names:");
        var input = Prompt("Archives").Trim();

        List<string> selected;
        if (input.Equals("-all", StringComparison.OrdinalIgnoreCase))
        {
            selected = archives;
        }
        else
        {
            selected = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.EndsWith(".arc", StringComparison.OrdinalIgnoreCase) ? name : name + ".arc")
                .ToList();
        }

        var codec = new LinkArchiveCodec();
        int index = 0;
        foreach (var arcName in selected)
        {
            index++;
            var arcPath = Path.Combine(arcDir, arcName);
            if (!File.Exists(arcPath))
            {
                Console.WriteLine($"  Warning: {arcName} not found, skipped.");
                continue;
            }

            var outDir = Path.Combine(_paths.Link6Unpack, Path.GetFileNameWithoutExtension(arcName));
            Console.Write($"  [EXTRACT {arcName}] {index}/{selected.Count} ... ");
            try
            {
                codec.Extract(arcPath, outDir, _context.ParamsPath, _context.LinkEncryptionKey, decrypt: true);
                using var stream = File.OpenRead(arcPath);
                var manifest = codec.ReadManifest(stream);
                Console.WriteLine($"{manifest.Entries.Count} entries -> {_paths.Relative(outDir)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
            }
        }
    }

    #endregion

    #region Link Pack

    // 将 link6_pack/ 下的子目录打包为 .arc 档案
    private void LinkPackMenu()
    {
        if (!Directory.Exists(_paths.Link6Pack))
        {
            Console.WriteLine($"  Pack directory not found: {_paths.Relative(_paths.Link6Pack)}");
            Console.WriteLine("  Create subdirectories in link6_pack/ with files to pack.");
            return;
        }

        var subDirs = Directory.GetDirectories(_paths.Link6Pack)
            .Select(d => new DirectoryInfo(d).Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subDirs.Count == 0)
        {
            Console.WriteLine("  No subdirectories found in link6_pack/.");
            return;
        }

        PrintSubMenu("Link Pack");
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

        List<string> selected;
        if (input.Equals("-all", StringComparison.OrdinalIgnoreCase))
        {
            selected = subDirs;
        }
        else
        {
            selected = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        var codec = new LinkArchiveCodec();
        int index = 0;
        foreach (var dirName in selected)
        {
            index++;
            var dirPath = Path.Combine(_paths.Link6Pack, dirName);
            if (!Directory.Exists(dirPath))
            {
                Console.WriteLine($"  Warning: {dirName}/ not found, skipped.");
                continue;
            }

            var manifestPath = Path.Combine(dirPath, "_link_manifest.json");
            if (!File.Exists(manifestPath))
            {
                var unpackManifest = Path.Combine(_paths.Link6Unpack, dirName, "_link_manifest.json");
                if (File.Exists(unpackManifest)) manifestPath = unpackManifest;
            }
            var outputPath = Path.Combine(_paths.Link6Pack, dirName + ext);

            Console.Write($"  [PACK {dirName}] {index}/{selected.Count} ... ");
            try
            {
                if (File.Exists(manifestPath))
                {
                    codec.PackLink6FromManifest(dirPath, manifestPath, outputPath);
                }
                else
                {
                    codec.PackLink6(dirPath, outputPath, dirName, 0, false);
                }
                Console.WriteLine($"-> {_paths.Relative(outputPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
            }
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

        if (!Directory.Exists(_paths.AnalysisScrDisasm) || !Directory.EnumerateFiles(_paths.AnalysisScrDisasm).Any())
        {
            Console.WriteLine("  Disassembled .scr files not found. Run startup analysis first.");
            return;
        }

        try
        {
            var document = codec.Read(File.ReadAllBytes(msgDatPath));
            var linker = new MessageScriptLinker();
            var map = linker.BuildMap(document, _paths.AnalysisScr);
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
            if (document.GameSystem.RawBlob is null || string.IsNullOrEmpty(document.GameSystem.RawBlob.DataBase64))
            {
                Console.WriteLine("  No RawBlob found in params.dat.");
                return;
            }

            var outputPath = Path.Combine(_paths.AnalysisParams, "rawblob.bin");
            File.WriteAllBytes(outputPath, Convert.FromBase64String(document.GameSystem.RawBlob.DataBase64));
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

            document.GameSystem.RawBlob.DataBase64 = Convert.ToBase64String(File.ReadAllBytes(rawPath));
            var outputPath = Path.Combine(_paths.AnalysisParams, "params_new.dat");
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

    // SCR 子菜单: 反汇编/汇编 .scr 脚本, 支持自定义编码
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
            Console.WriteLine("  │  1. Disassemble .scr -> txt │");
            Console.WriteLine("  │  2. Assemble txt -> .scr    │");
            Console.WriteLine("  │  0. Back                    │");
            Console.WriteLine("  └─────────────────────────────┘");

            var choice = Prompt("Select").Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1": ScrDisassemble(readEnc, writeEnc); break;
                case "2": ScrAssemble(readEnc, writeEnc); break;
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    // 批量反汇编 analysis/scr/ 下的 .scr 文件
    private void ScrDisassemble(string? readEncoding, string? writeEncoding)
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
            using var progress = PictureProcessing.StartProgress("DISASM scr", scrFiles.Length);
            foreach (var scr in scrFiles)
            {
                try
                {
                    var document = containerCodec.Read(File.ReadAllBytes(scr), Path.GetFileName(scr));
                    var outputPath = Path.Combine(_paths.AnalysisScrDisasm, Path.GetFileNameWithoutExtension(scr) + ".disasm.txt");
                    File.WriteAllText(outputPath, textCodec.Write(document), Encoding.UTF8);
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
            Console.WriteLine($"  Disassemble done -> {_paths.Relative(_paths.AnalysisScrDisasm)} ({ok} ok, {fail} failed)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    // 批量汇编 analysis/scr_disasm/ 下的 .disasm.txt
    private void ScrAssemble(string? readEncoding, string? writeEncoding)
    {
        if (!Directory.Exists(_paths.AnalysisScrDisasm) || !Directory.EnumerateFiles(_paths.AnalysisScrDisasm, "*.disasm.txt").Any())
        {
            Console.WriteLine($"  No .disasm.txt files found in {_paths.Relative(_paths.AnalysisScrDisasm)}");
            Console.WriteLine("  Run disassemble first.");
            return;
        }

        try
        {
            var containerCodec = new ScrContainerCodec();
            var textCodec = new ScrTextCodec(readEncoding, writeEncoding);
            var txtFiles = Directory.GetFiles(_paths.AnalysisScrDisasm, "*.disasm.txt", SearchOption.AllDirectories);
            Directory.CreateDirectory(_paths.AnalysisScrAsm);
            int ok = 0, fail = 0;
            using var progress = PictureProcessing.StartProgress("ASM scr", txtFiles.Length);
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
            Console.WriteLine($"  Assemble done -> {_paths.Relative(_paths.AnalysisScrAsm)} ({ok} ok, {fail} failed)");
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
                var unpackDir = Path.Combine(_paths.Link6Unpack, Path.GetFileNameWithoutExtension(arc));
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
            Console.WriteLine("  ┌──────────────────────────────────┐");
            Console.WriteLine("  │       Picture Menu               │");
            Console.WriteLine("  ├──────────────────────────────────┤");
            Console.WriteLine("  │  1. Sort (unpack -> pic)         │");
            Console.WriteLine("  │  2. Convert (orig -> png)        │");
            Console.WriteLine("  │  3. Repack (fix -> new)          │");
            Console.WriteLine("  │  4. Repack-fix (single fix dir)  │");
            Console.WriteLine("  │  5. Restore (new -> pack)       │");
            Console.WriteLine("  │  6. Restore with replenish      │");
            Console.WriteLine("  │  0. Back                         │");
            Console.WriteLine("  └──────────────────────────────────┘");

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
                case "0": return;
                default: Console.WriteLine($"  Unknown option: {choice}"); break;
            }

            Console.WriteLine();
        }
    }

    // 从 link6_unpack/ 的子目录按文件格式分拣到 pic/
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
            Console.WriteLine("  No unpacked archive directories in link6_unpack/. Unpack first.");
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

        int index = 0;
        foreach (var dirName in selected)
        {
            index++;
            var srcDir = Path.Combine(_paths.Link6Unpack, dirName);
            if (!Directory.Exists(srcDir))
            {
                Console.WriteLine($"  Warning: {dirName}/ not found, skipped.");
                continue;
            }

            var dstDir = Path.Combine(_paths.Pic, dirName);
            Console.WriteLine($"  [SORT {dirName}] {index}/{selected.Count} ...");
            try
            {
                FileSorter.Sort(srcDir, dstDir, dirName, dirName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Failed: {ex.Message}");
            }
        }
    }

    private void PicConvert()
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
    }

    private void PicRepack()
    {
        Console.WriteLine($"  Repacking in {_paths.Relative(_paths.Pic)} ...");
        try
        {
            FileConverter.RepackAll(_paths.Pic);
            Console.WriteLine("  Repack done.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
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

        try
        {
            FileConverter.RepackFix(fixDir);
            Console.WriteLine("  Repack-fix done.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private void PicRestore()
    {
        Console.WriteLine($"  Restoring from {_paths.Relative(_paths.Pic)} -> {_paths.Relative(_paths.Link6Pack)} ...");
        try
        {
            Restorer.Restore(_paths.Pic, _paths.Link6Pack);
            Console.WriteLine("  Restore done.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
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
        try
        {
            Restorer.RestoreWithReplenish(_paths.Pic, _paths.Link6Pack, exclude);
            Console.WriteLine("  Restore with replenish done.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

    private static bool IsNonPictureArchive(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return name == "scr" ||
               name == "bgm" ||
               name == "sed" ||
               name.StartsWith("voice");
    }

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
        Console.WriteLine("  Launching SP Viewer GUI ...");
        try
        {
            var width = (int)(_context.Params?.GameSystem.Width ?? 1280);
            var height = (int)(_context.Params?.GameSystem.Height ?? 720);
            SpViewerApp.Launch(_paths.Pic, _context.Params, width, height);
            Console.WriteLine("  SP Viewer closed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed: {ex.Message}");
        }
    }

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
