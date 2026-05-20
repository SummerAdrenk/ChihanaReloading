// ============================================================================
// KaguyaRuntimeContext.cs
// 运行时上下文: 保存一次会话所需的全部全局状态
//
// 职责:
//   - 解析并保存 game root / work directory / params.dat 路径
//   - 在 Create() 中自动探测 params.dat 并读取 LINK 加密密钥
//   - 为 CLI 模式和交互模式提供统一的上下文入口
//
// 初始化流程 (Create 方法):
//   1. 确定 params.dat 路径 (命令行指定 > game root 下 > CWD 下 > 工具目录下)
//   2. 从 params.dat 读取文档并提取 RawBlob 中的 Base64 加密密钥
//   3. 推导 game root (命令行 > params.dat 所在目录 > CWD)
//   4. 确定并创建工作目录
//
// 依赖: Formats.Params.ParamsDatCodec
// 被依赖: KaguyaApp, InteractiveSession, 各 Commands 类
// ============================================================================

using Kaguya_YaneKit.Formats.Params;

namespace Kaguya_YaneKit.App;

public sealed class KaguyaRuntimeContext
{
    public string ToolDirectory { get; init; } = "";
    public string GameRoot { get; init; } = "";
    public string WorkDirectory { get; init; } = "";
    public string? ParamsPath { get; init; }
    public ParamsDatDocument? Params { get; init; }
    public byte[]? LinkEncryptionKey { get; init; }
    public string? ParamsVersion => Params is null ? null : ParamsDatCodec.DescribeVersion(Params.Header);

    public static KaguyaRuntimeContext Create(string? gameRoot = null, string? workDirectory = null, string? paramsPath = null)
    {
        var toolDirectory = AppContext.BaseDirectory;
        var resolvedParamsPath = ResolveParamsPath(gameRoot, paramsPath);
        var resolvedGameRoot = ResolveGameRoot(gameRoot, resolvedParamsPath);
        var resolvedWorkDirectory = Path.GetFullPath(workDirectory ?? Path.Combine(toolDirectory, "workplace"));

        ParamsDatDocument? document = null;
        byte[]? linkKey = null;
        if (resolvedParamsPath is not null)
        {
            document = new ParamsDatCodec().Read(File.ReadAllBytes(resolvedParamsPath));
            linkKey = Convert.FromBase64String(document.GameSystem.RawBlob.DataBase64);
        }

        return new KaguyaRuntimeContext
        {
            ToolDirectory = toolDirectory,
            GameRoot = resolvedGameRoot,
            WorkDirectory = resolvedWorkDirectory,
            ParamsPath = resolvedParamsPath,
            Params = document,
            LinkEncryptionKey = linkKey
        };
    }

    // 优先级: 命令行 --game-root > params.dat 所在目录 > CWD
    private static string ResolveGameRoot(string? gameRoot, string? paramsPath)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            return Path.GetFullPath(gameRoot);
        }

        if (!string.IsNullOrWhiteSpace(paramsPath))
        {
            return Path.GetDirectoryName(Path.GetFullPath(paramsPath)) ?? Environment.CurrentDirectory;
        }

        return Environment.CurrentDirectory;
    }

    // 按优先级搜索 params.dat: 命令行指定 > game root > CWD > 工具目录
    private static string? ResolveParamsPath(string? gameRoot, string? paramsPath)
    {
        if (!string.IsNullOrWhiteSpace(paramsPath))
        {
            var fullParamsPath = Path.GetFullPath(paramsPath);
            if (!File.Exists(fullParamsPath))
            {
                throw new FileNotFoundException($"params.dat was specified but does not exist: {fullParamsPath}", fullParamsPath);
            }

            return fullParamsPath;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            candidates.Add(Path.Combine(Path.GetFullPath(gameRoot), "params.dat"));
        }

        candidates.Add(Path.Combine(Environment.CurrentDirectory, "params.dat"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "params.dat"));

        return candidates.FirstOrDefault(File.Exists);
    }
}
