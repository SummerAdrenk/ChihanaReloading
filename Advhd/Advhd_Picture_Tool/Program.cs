using System.Text;
using System.Text.RegularExpressions;
namespace AdvhdPictureTool;

class Program
{
    static string[] SplitCommandLine(string commandLine)
    {
        var matches = Regex.Matches(commandLine, @"[^\s""]+|""([^""]*)""");
        return matches.Cast<Match>().Select(m => m.Value.Trim('"')).ToArray();
    }

    static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        bool isRunning = true;

        while (isRunning)
        {
            Console.Clear();
            PrintUsage();
            Console.Write("> ");

            string? input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            var inputArgs = SplitCommandLine(input);
            if (inputArgs.Length == 0) continue;
            string command = inputArgs[0].ToLower();

            if (command == "exit" || command == "quit")
            {
                isRunning = false;
                continue;
            }

            try
            {
                switch (command)
                {
                    case "-sort":
                        if (inputArgs.Length != 3) { Console.WriteLine("[ERROR] -sort 指令需要[源目录]和[分类目录]两个参数。"); break; }
                        FileSorter.Sort(inputArgs[1], inputArgs[2]);
                        break;

                    case "-convert":
                        if (inputArgs.Length != 2) { Console.WriteLine("[ERROR] -convert 指令需要一个[目录]参数。"); break; }
                        FileConverter.ConvertAll(inputArgs[1]);
                        break;

                    case "-repack":
                        if (inputArgs.Length != 2) { Console.WriteLine("[ERROR] -repack 指令需要一个[目录]参数。"); break; }
                        FileConverter.RepackAll(inputArgs[1]);
                        break;

                    case "-repack-fix":
                        if (inputArgs.Length != 2) { Console.WriteLine("[ERROR] -repack-fix 指令需要一个[fix目录]参数。"); break; }
                        FileConverter.RepackFix(inputArgs[1]);
                        break;

                    case "-restore":
                        if (inputArgs.Length != 3) { Console.WriteLine("[ERROR] -restore 指令需要[分类目录]和[输出目录]两个参数。"); break; }
                        Restorer.Restore(inputArgs[1], inputArgs[2]);
                        break;

                    case "-restore-replenish":
                        if (inputArgs.Length != 3) { Console.WriteLine("[ERROR] -restore-replenish 指令需要[分类目录]和[输出目录]两个参数。"); break; }
                        Restorer.RestoreWithReplenish(inputArgs[1], inputArgs[2]);
                        break;
                    case "-merge-cg":
                        if (inputArgs.Length != 3) { Console.WriteLine("[ERROR] -merge-cg 指令需要[ap-2目录]和[bmp目录]两个参数。"); break; }
                        //CgMerger.MergeCgFiles(inputArgs[1], inputArgs[2]);
                        break;

                    case "-repack-arc":
                        if (inputArgs.Length < 2) { Console.WriteLine("[ERROR] -repack-arc 指令至少需要一个[目标目录]参数。"); break; }
                        AdvhdArcPacker.Pack(inputArgs);
                        break;

                    case "-unpack-arc":
                        if (inputArgs.Length != 2) { Console.WriteLine("[ERROR] -unpack-arc 指令需要一个[目标目录]参数。"); break; }
                        AdvhdArcPacker.Unpack(inputArgs);
                        break;

                    case "-clear":
                        if (inputArgs.Length != 2) { Console.WriteLine("[ERROR] -clear 指令需要一个[目标目录]参数。"); break; }
                        PixelClearer.ClearPixels(inputArgs[1]);
                        break;

                    default:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[ERROR] 未知的指令 '{command}'");
                        Console.ResetColor();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n操作过程中发生严重错误:");
                Console.WriteLine(ex.Message);
                Console.WriteLine("-----------------------------------------------------------------------------------------------");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
            }

            Console.WriteLine("\n操作完成。按 Enter 键返回主菜单...");
            Console.ReadLine();
        }

        Console.WriteLine("程序已退出。");
    }
    static void PrintUsage()
    {
        Console.WriteLine("AdvhdPictureTool Ver1.0.0");
        Console.WriteLine("   ——by ChihanaSonnetia");
        Console.WriteLine("-----------------------------------------------------------------------------------------------");
        Console.WriteLine("Usage: AdvhdPictureTool.exe [指令] [参数] <可选参数>");
        Console.WriteLine();
        Console.WriteLine("Command:");
        Console.WriteLine("  -sort [源目录] [分类目录]");
        Console.WriteLine("    => 扫描[源目录]，将文件依据文件头分类到[分类目录]下对应的 orig 文件夹内。");
        Console.WriteLine();
        Console.WriteLine("  -convert [目录]");
        Console.WriteLine("    => [目录]可以是总分类目录，也可以是特定的子分类文件夹【也就是bmp、ap那些】。");
        Console.WriteLine("    => 将 orig 内的文件转为 .png 和 metadata 。");
        Console.WriteLine();
        Console.WriteLine("  -repack [目录]");
        Console.WriteLine("    => [目录]可以是总分类目录，也可以是特定的子分类文件夹【也就是bmp、ap那些】。");
        Console.WriteLine("    => 【批量转换】 遍历目录，仅使用每个子格式文件夹内的 fix 文件夹重新转回 .png 。");
        Console.WriteLine();
        Console.WriteLine("  -repack-fix [fix_target目录]");
        Console.WriteLine("    => [fix目录]为修图文件夹。");
        Console.WriteLine("    => 【单个转换】 仅将指定的[fix_target目录]内的文件重新打包到对应的 new 目录中。");
        Console.WriteLine();
        Console.WriteLine("  -restore [分类目录] [输出目录]");
        Console.WriteLine("    => 【基础还原】 仅收集所有 new 内的文件，还原到[输出目录]_finish文件夹。");
        Console.WriteLine();
        Console.WriteLine("  -restore-replenish [分类目录] [输出目录]");
        Console.WriteLine("    => 【补充还原】 还原 new 内文件后，用 orig 内的原始文件补全新建的[输出目录]_finish文件夹。");
        Console.WriteLine();
        Console.WriteLine("  -unpack-arc [目录路径]");
        Console.WriteLine("    => 【ARC解包】 解包指定的 .arc 文件，或解包指定目录下所有的 .arc 文件。");
        Console.WriteLine("       (单一模式): -unpack-arc [文件目录] -> 将该文件解包到同名文件夹");
        Console.WriteLine("       (批量模式): -unpack-arc [父目录] -> 解包该目录下所有arc到对应子文件夹");
        Console.WriteLine();
        Console.WriteLine("  -repack-arc [目标目录] <-all> <-ext .ext>");
        Console.WriteLine("    => 【ARC封包】 将指定目录内的文件打包为 AdvHD 格式的 .arc 文件。");
        Console.WriteLine("       (单一模式): -pack-arc [文件夹路径] -> 将该文件夹内容打包。");
        Console.WriteLine("       (批量模式): -pack-arc [父目录] -all -> 将父目录下每个子文件夹分别打包。");
        Console.WriteLine("       <-ext .ext> 为可选参数，指定输出文件的后缀，默认为 .arc");
        Console.WriteLine();
        Console.WriteLine("  -clear [目标目录]");
        Console.WriteLine("    => 【像素清除】 扫描[目标目录]下的所有 png 子文件夹，将PNG图片变为空白透明，并保存到各自分类下的 clear 目录中。");
        Console.WriteLine();
        Console.WriteLine("  exit");
        Console.WriteLine("    => 退出程序。");
        Console.WriteLine();
        Console.WriteLine("#Others: 目前的.MOS似乎就是.png");
        Console.WriteLine();
        Console.WriteLine("请输入指令...");
    }
}