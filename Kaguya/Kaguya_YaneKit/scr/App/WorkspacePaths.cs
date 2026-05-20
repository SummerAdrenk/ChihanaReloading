// ============================================================================
// WorkspacePaths.cs
// 工作目录的路径常量集合
//
// 树状结构 (以工作目录为根):
//   {Root}/
//   ├── analysis/
//   │   ├── params/          -- params.dat 导出的 JSON
//   │   ├── scr/             -- 从 scr.arc 解包的 .scr 二进制
//   │   ├── scr_disasm/      -- 反汇编后的 .disasm.txt
//   │   └── scr_asm/         -- 重新汇编后的 .scr 二进制
//   ├── link6_unpack/        -- LINK6 档案解包输出
//   ├── link6_pack/          -- LINK6 档案打包输入
//   ├── pic/                 -- 图片分拣/转换/重打包的工作区
//   ├── character/           -- CG/立绘合成输出
//   └── msg/                 -- message.dat 导出/拆分
//       └── _split_out/      -- 按脚本拆分后的消息文件
//
// 依赖: 无
// 被依赖: InteractiveSession (交互模式的所有子菜单)
// ============================================================================

namespace Kaguya_YaneKit.App;

public sealed class WorkspacePaths
{
    public string Root { get; }
    public string AnalysisParams { get; }
    public string AnalysisScr { get; }
    public string AnalysisScrDisasm { get; }
    public string AnalysisScrAsm { get; }
    public string Link6Unpack { get; }
    public string Link6Pack { get; }
    public string Pic { get; }
    public string Character { get; }
    public string Msg { get; }
    public string MsgSplitOut { get; }

    public WorkspacePaths(string workDirectory)
    {
        Root = Path.GetFullPath(workDirectory);
        AnalysisParams = Path.Combine(Root, "analysis", "params");
        AnalysisScr = Path.Combine(Root, "analysis", "scr");
        AnalysisScrDisasm = Path.Combine(Root, "analysis", "scr_disasm");
        AnalysisScrAsm = Path.Combine(Root, "analysis", "scr_asm");
        Link6Unpack = Path.Combine(Root, "link6_unpack");
        Link6Pack = Path.Combine(Root, "link6_pack");
        Pic = Path.Combine(Root, "pic");
        Character = Path.Combine(Root, "character");
        Msg = Path.Combine(Root, "msg");
        MsgSplitOut = Path.Combine(Root, "msg", "_split_out");
    }

    // 确保所有工作子目录存在
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(AnalysisParams);
        Directory.CreateDirectory(AnalysisScr);
        Directory.CreateDirectory(AnalysisScrDisasm);
        Directory.CreateDirectory(AnalysisScrAsm);
        Directory.CreateDirectory(Link6Unpack);
        Directory.CreateDirectory(Link6Pack);
        Directory.CreateDirectory(Pic);
        Directory.CreateDirectory(Character);
        Directory.CreateDirectory(Msg);
    }

    // 将绝对路径转为以工作目录为根的相对路径用于控制台显示
    public string Relative(string absolutePath)
    {
        try
        {
            var rel = Path.GetRelativePath(Root, absolutePath);
            return rel.StartsWith("..") ? absolutePath : rel;
        }
        catch
        {
            return absolutePath;
        }
    }
}
