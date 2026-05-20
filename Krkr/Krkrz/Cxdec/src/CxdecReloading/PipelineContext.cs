namespace CxdecReloading;

/// <summary>
/// 流水线共享上下文，贯穿所有步骤
///
/// 目录结构：
///   根目录/
///     run.bat                          启动入口
///     Extractor_Output/                Step1 产物 (从游戏目录移过来)
///     SCN/                             Step2 产物 (PSB 文件副本)
///     SCN_PURE/                        Step3 产物 (成功转换的 JSON)
///     scr/
///       CxdecReloading/                主控程序
///       KrkrExtractForCxdecV2/         解包三件套
///       LE/                            Locale Emulator
///       FreeMote/                      FreeMote 编译产物
/// </summary>
public class PipelineContext
{
    // ===== 根目录 =====
    public string RootDir { get; set; } = "";

    // ===== 用户输入 =====
    public string GameExePath { get; set; } = "";
    public string GameDirectory => Path.GetDirectoryName(GameExePath) ?? "";
    public string GameExeName => Path.GetFileName(GameExePath);

    // ===== scr 工具路径 =====
    public string ScrDir => Path.Combine(RootDir, "scr");

    public string CxdecLoaderPath =>
        Path.Combine(ScrDir, "KrkrExtractForCxdecV2", "CxdecExtractorLoader.exe");

    public string VersionDllSourcePath =>
        Path.Combine(ScrDir, "krkr_hxv4_dumphash", "version.dll");

    public string LeDir => Path.Combine(ScrDir, "LE");
    public string LoaderDllPath => Path.Combine(LeDir, "LoaderDll.dll");

    public string PsbDecompilePath =>
        Path.Combine(ScrDir, "FreeMote", "PsbDecompile.exe");

    public string PsBuildPath =>
        Path.Combine(ScrDir, "FreeMote", "PsBuild.exe");

    // ===== Step 1 输出：解包结果 =====
    public string ExtractOutputDir => Path.Combine(RootDir, "Extractor_Output");

    public string ExtractorLogPath =>
        Path.Combine(ScrDir, "KrkrExtractForCxdecV2", "Extractor.log");

    // ===== Step 2 输出：PSB 文件副本 =====
    public string ScnDir => Path.Combine(RootDir, "SCN");

    // ===== Step 3 输出：JSON 文件 =====
    public string ScnTempDir => Path.Combine(RootDir, "SCN_Temp");
    public string ScnPureDir => Path.Combine(RootDir, "SCN_PURE");

    // ===== Function 2 工作目录 =====
    public string ScnPureWorkDir => Path.Combine(RootDir, "SCN_PURE_WORK");
    public string ScnPureTxtDir => Path.Combine(RootDir, "SCN_PURE_TXT");
    public string ScnPureTxtTransDir => Path.Combine(RootDir, "SCN_PURE_TXT_TRANS");
    public string ScnPureWorkTransDir => Path.Combine(RootDir, "SCN_PURE_WORK_TRANS");
    public string ScnPureNewDir => Path.Combine(RootDir, "SCN_PURE_NEW");

    // ===== Step 2 统计 =====
    public int TotalFilesScanned { get; set; }
    public int PsbFilesFound { get; set; }
    public Dictionary<FileType, int> FileTypeCounts { get; set; } = [];

    // ===== Step 4+ 输出 =====
    public List<string> ExtractedFileNames { get; set; } = [];
    public List<string> ExtractedDirNames { get; set; } = [];

    // ===== 字典生成 =====
    public string DictDir => Path.Combine(ScrDir, "dict");
    public string NormalDictPath => Path.Combine(DictDir, "normaldict.json");
    public string NewDictPath => Path.Combine(DictDir, "newdict.json");
    public string FilesListPath => Path.Combine(DictDir, "files.txt");
    public string DirsListPath => Path.Combine(DictDir, "dirs.txt");

    // ===== 并发设置 =====
    public int MaxParallelism { get; set; }

    // ===== Function 3: 撞库 =====
    public string FixnameOutputDir => Path.Combine(RootDir, "Fixname_Output");
    public string FixnameFailedDir => Path.Combine(RootDir, "Fixname_Failed");

    // ===== Function 4: KS 脚本处理 =====
    public string KsTxtDir => Path.Combine(RootDir, "KS_TXT");
    public string KsTxtTransDir => Path.Combine(RootDir, "KS_TXT_TRANS");
    public string KsNewDir => Path.Combine(RootDir, "KS_NEW");
}

public enum FileType
{
    Unknown,
    PSB,
    MDF,
    TLG5,
    TLG6,
    OGG,
    PNG,
    JPEG,
    BMP,
    WAV,
    RIFF,
    TJS2,
    OTF,
    TTF,
    UnicodeText,
}
