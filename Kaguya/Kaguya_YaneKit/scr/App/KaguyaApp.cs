// ============================================================================
// KaguyaApp.cs
// 应用程序主调度器: 解析全局选项, 路由到各子命令或进入交互模式
//
// 调度流程:
//   1. 解析全局选项 (--game-root, --workdir, --params)
//   2. 无子命令时进入 InteractiveSession 交互模式
//   3. 有子命令时创建 KaguyaRuntimeContext 并路由到对应 Commands 类
//
// 子命令路由表:
//   scr       -> ScrCommands         (脚本 HLS 高级解析/回编，低级 SCRASM 调试/校验)
//   msg       -> MessageCommands     (message.dat 导入/导出/拆分/合并)
//   params    -> ParamsCommands      (params.dat 导入/导出/校验)
//   pic       -> PictureCommands     (图片分拣/转换/重打包/还原)
//   character -> CharacterCommands   (CG/立绘合成)
//   link      -> LinkCommands        (LINK 档案解包/打包/校验)
//
// 依赖: KaguyaRuntimeContext, InteractiveSession, 各 Commands 类
// ============================================================================

namespace Kaguya_YaneKit.App;

public static class KaguyaApp
{
    public static int Run(string[] args)
    {
        var startup = KaguyaStartupOptions.Parse(args);
        args = startup.CommandArgs;

        if (args.Length == 0)
        {
            return new InteractiveSession().Run();
        }

        KaguyaRuntimeContext? context = null;
        if (!IsHelp(args))
        {
            try
            {
                context = KaguyaRuntimeContext.Create(startup.GameRoot, startup.WorkDirectory, startup.ParamsPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        switch (args[0].Trim().ToLowerInvariant())
        {
            case "--help":
            case "-h":
                PrintHelp();
                return 0;
            case "--self-test":
                Console.WriteLine("Self-test stub: core scaffolding is present.");
                return 0;
            case "scr":
                return ScrCommands.Run(args.Skip(1).ToArray(), context);
            case "msg":
                return MessageCommands.Run(args.Skip(1).ToArray());
            case "tblstr":
                return TblstrCommands.Run(args.Skip(1).ToArray());
            case "tbl":
                return TblCommands.Run(args.Skip(1).ToArray());
            case "params":
                return ParamsCommands.Run(args.Skip(1).ToArray());
            case "pic":
                return PictureCommands.Run(args.Skip(1).ToArray(), context);
            case "character":
                return CharacterCommands.Run(args.Skip(1).ToArray(), context);
            case "link":
                return LinkCommands.Run(args.Skip(1).ToArray(), context);
            case "pe":
                return PeCommands.Run(args.Skip(1).ToArray());
            case "archive_unpack":
                return ArchiveCommands.Unpack(args.Skip(1).ToArray(), context);
            case "archive_pack":
                return ArchiveCommands.Pack(args.Skip(1).ToArray(), context);
            default:
                Console.WriteLine($"Unknown command: {args[0]}");
                return 1;
        }
    }

    private static bool IsHelp(string[] args) =>
        args.Length == 0 ||
        string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(args[0], "-h", StringComparison.OrdinalIgnoreCase) ||
        args.Any(arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase));

    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  Kaguya_YaneKit - Yane engine resource toolkit");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("  Global options:");
        Console.WriteLine("    --game-root <dir>      Game root directory (auto-detects params.dat)");
        Console.WriteLine("    --workdir <dir>        Work directory (default: <tool>/workplace)");
        Console.WriteLine("    --params <params.dat>  Override params.dat path");
        Console.WriteLine("    --help                 Show this help");
        Console.WriteLine("    --self-test            Run a tiny format scaffold check");
        Console.WriteLine();
        Console.WriteLine("  Script commands:");
        Console.WriteLine("    scr decompile <in> <out>");
        Console.WriteLine("                           Default: emit conservative HLS high-level IR");
        Console.WriteLine("    scr hls-asm <in> <out>");
        Console.WriteLine("                           Default: assemble HLS high-level IR to .scr");
        Console.WriteLine("    scr verify-hls <in>    Roundtrip-check .scr through HLS");
        Console.WriteLine("    scr disasm <in> <out>  Low-level SCRASM disassembly for debugging");
        Console.WriteLine("    scr asm <in> <out>     Low-level SCRASM assembly for debugging");
        Console.WriteLine("    scr opcodes [out.md]   Export the centralized SCR opcode schema");
        Console.WriteLine("    scr scan-opcodes <in|dir> [out.txt]");
        Console.WriteLine("                           Scan for unknown opcodes/schema conflicts");
        Console.WriteLine("    scr verify <in>        Roundtrip-check .scr binary");
        Console.WriteLine("    scr dump <in>          Print a readable opcode listing");
        Console.WriteLine();
        Console.WriteLine("  Message commands:");
        Console.WriteLine("    msg export <in> <out>  Export message.dat to text");
        Console.WriteLine("    msg import <in> <txt> <out>");
        Console.WriteLine("                           Import text and rebuild message.dat");
        Console.WriteLine("    msg verify <in>        Roundtrip-check message.dat");
        Console.WriteLine("    msg map <msg> <scrdir> <json>");
        Console.WriteLine("                           Build .scr to message mapping");
        Console.WriteLine("    msg split <msg> <scrdir> <outdir>");
        Console.WriteLine("                           Split message text by .scr usage");
        Console.WriteLine();
        Console.WriteLine("  TBLSTR commands:");
        Console.WriteLine("    tblstr export <in.arc> <outdir> [--ini tblstr_config.ini]");
        Console.WriteLine("                           Export TBLSTR text resource to text");
        Console.WriteLine("    tblstr import <in.arc> <txt> <out.arc> [--ini tblstr_config.ini]");
        Console.WriteLine("                           Import edited TBLSTR text and rebuild ARC");
        Console.WriteLine("    tblstr split <in.arc> <scrdir> <outdir> [--ini tblstr_config.ini]");
        Console.WriteLine("                           Split TBLSTR text by .scr usage");
        Console.WriteLine("    tblstr merge <base.txt> <split-dir> <out.txt>");
        Console.WriteLine("                           Merge split TBLSTR text back to flat text");
        Console.WriteLine("    tblstr verify-text <in.arc> [--ini tblstr_config.ini]");
        Console.WriteLine("                           Roundtrip-check TBLSTR text workflow");
        Console.WriteLine("    tbl export <tbl-file|tbl-dir> <outdir> [--json]");
        Console.WriteLine("                           Export TBLSTR-family .tbl support tables");
        Console.WriteLine("    tbl verify <tbl-file|tbl-dir>");
        Console.WriteLine("                           Roundtrip-check supported .tbl tables");
        Console.WriteLine();
        Console.WriteLine("  Params commands:");
        Console.WriteLine("    params dump <in>       Print params.dat summary");
        Console.WriteLine("    params export-json <in> <out.json>");
        Console.WriteLine("    params import-json <in.json> <out.dat>");
        Console.WriteLine("    params verify <in>     Roundtrip-check params.dat");
        Console.WriteLine();
        Console.WriteLine("  Picture commands:");
        Console.WriteLine("    pic sort <src> <dst>   Sort extracted files by format");
        Console.WriteLine("    pic convert <dir>      Convert originals to PNG");
        Console.WriteLine("    pic repack <dir>       Repack fixed PNGs to originals");
        Console.WriteLine("    pic repack-png <dir>   Test repack PNG outputs directly to new");
        Console.WriteLine("    pic export-game <game> <work>");
        Console.WriteLine("                           Extract + sort + convert all picture arcs");
        Console.WriteLine();
        Console.WriteLine("  Character commands:");
        Console.WriteLine("    character compose <pic-dir> [output-dir]");
        Console.WriteLine("                           Compose CG/SP from pattern data");
        Console.WriteLine();
        Console.WriteLine("  Link archive commands:");
        Console.WriteLine("    link list <in.arc>     List archive entries");
        Console.WriteLine("    link extract <in.arc> <outdir>");
        Console.WriteLine("    link pack6 <indir> <out.arc>");
        Console.WriteLine("    link repack6 <indir> <manifest.json> <out.arc>");
        Console.WriteLine("    link verify <in.arc>   Validate chunk layout");
        Console.WriteLine();
        Console.WriteLine("  PE commands:");
        Console.WriteLine("    pe string-dump <in.exe> <out.json>");
        Console.WriteLine("                           Dump TBLSTR EXE strings and pointer refs");
        Console.WriteLine("    pe string-import <in.exe> <strings.json> <out.exe>");
        Console.WriteLine("                           Add translated string section and patch refs");
        Console.WriteLine();
        Console.WriteLine("  Unified archive commands:");
        Console.WriteLine("    archive_unpack <in.arc> <outdir>");
        Console.WriteLine("                           Auto-detect AF01 or LINK archive");
        Console.WriteLine("    archive_pack <indir> <manifest.json> <out.arc>");
        Console.WriteLine("                           Repack from _archive_manifest.json or _link_manifest.json");
        Console.WriteLine();
    }

    // 从命令行参数中提取全局选项 (--game-root, --workdir, --params)
    // 剩余参数作为子命令和子命令参数
    private sealed class KaguyaStartupOptions
    {
        public string? GameRoot { get; init; }
        public string? WorkDirectory { get; init; }
        public string? ParamsPath { get; init; }
        public string[] CommandArgs { get; init; } = [];

        public static KaguyaStartupOptions Parse(string[] args)
        {
            string? gameRoot = null;
            string? workDirectory = null;
            string? paramsPath = null;
            var index = 0;
            while (index < args.Length)
            {
                var arg = args[index];
                if (!IsGlobalOption(arg))
                {
                    break;
                }

                if (index + 1 >= args.Length)
                {
                    break;
                }

                var value = args[index + 1];
                switch (arg.ToLowerInvariant())
                {
                    case "--game-root":
                        gameRoot = value;
                        break;
                    case "--workdir":
                        workDirectory = value;
                        break;
                    case "--params":
                        paramsPath = value;
                        break;
                }

                index += 2;
            }

            return new KaguyaStartupOptions
            {
                GameRoot = gameRoot,
                WorkDirectory = workDirectory,
                ParamsPath = paramsPath,
                CommandArgs = args.Skip(index).ToArray()
            };
        }

        private static bool IsGlobalOption(string arg) =>
            string.Equals(arg, "--game-root", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--workdir", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--params", StringComparison.OrdinalIgnoreCase);
    }
}
