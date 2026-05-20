using System.Text;
using CxdecReloading;
using CxdecReloading.Functions;

Console.OutputEncoding = Encoding.UTF8;

ConsoleHelper.PrintHeader();

var ctx = new PipelineContext();

// 自动检测根目录
var exeDir = AppDomain.CurrentDomain.BaseDirectory;
var cwd = Environment.CurrentDirectory;

string[] candidates =
[
    Path.GetFullPath(Path.Combine(exeDir, "..", "..")),
    cwd,
    Path.GetFullPath(Path.Combine(cwd, "..")),
];

ctx.RootDir = candidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "scr")))
              ?? cwd;

ConsoleHelper.PrintInfo($"根目录: {ctx.RootDir}");
ConsoleHelper.PrintInfo($"工具目录: {ctx.ScrDir}");

// 主菜单循环
while (true)
{
    var choice = ConsoleHelper.AskMenu("请选择功能:",
        "提取并恢复 SCN 文件（解包 → SCN文件名还原）",
        "解析 SCN 文件（导出双行文本 / 生成 filedict）",
        "撞库（哈希文件名匹配）",
        "解析 KS 脚本（炎孕特供）");

    try
    {
        switch (choice)
        {
            case 1:
                await Function1_ExtractScn.RunAsync(ctx);
                break;
            case 2:
                await Function2_ParseScn.RunAsync(ctx);
                break;
            case 3:
                await Function3_HashMatch.RunAsync(ctx);
                break;
            case 4:
                await Function4_ParseKs.RunAsync(ctx);
                break;
            case 0:
                ConsoleHelper.PrintInfo("See you again~~~");
                return;
        }
    }
    catch (Exception ex)
    {
        ConsoleHelper.PrintError($"执行出错: {ex.Message}");
        ConsoleHelper.PrintInfo(ex.StackTrace ?? "");
    }

    Console.WriteLine();
    ConsoleHelper.WaitForEnter("按回车返回主菜单...");
}