using System.Runtime.InteropServices;
using System.Text;

namespace CxdecReloading;

public static class ConsoleHelper
{
    // Win32 ReadConsoleW — 直接读 Unicode，不经过 codepage 转换
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleW(
        IntPtr hConsoleInput, StringBuilder lpBuffer,
        uint nNumberOfCharsToRead, out uint lpNumberOfCharsRead, IntPtr pInputControl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private const int STD_INPUT_HANDLE = -10;

    /// <summary>
    /// 用 Win32 ReadConsoleW 读取一行输入，支持日文等非 ASCII 字符的拖拽/粘贴
    /// </summary>
    public static string ReadLineUnicode()
    {
        var handle = GetStdHandle(STD_INPUT_HANDLE);
        var sb = new StringBuilder(4096);
        if (ReadConsoleW(handle, sb, 4096, out var charsRead, IntPtr.Zero))
        {
            var result = sb.ToString(0, (int)charsRead);
            return result.TrimEnd('\r', '\n');
        }
        return Console.ReadLine() ?? "";
    }

    public static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║    CxdecReloading - KiriKiriZ CxDec 拆包工具    ║");
        Console.WriteLine("║                     by ChihanaSonnetia Ver1.0.0 ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void PrintStepHeader(int step, string description)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine($"  Step {step}: {description}");
        Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void PrintInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  {message}");
        Console.ResetColor();
    }

    public static void PrintSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  [OK] {message}");
        Console.ResetColor();
    }

    public static void PrintWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  [!] {message}");
        Console.ResetColor();
    }

    public static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  [ERROR] {message}");
        Console.ResetColor();
    }

    public static void PrintHint(string message)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  > {message}");
        Console.ResetColor();
    }

    public static string AskInput(string prompt, string? defaultValue = null)
    {
        Console.ForegroundColor = ConsoleColor.White;
        if (defaultValue != null)
            Console.Write($"  {prompt} [{defaultValue}]: ");
        else
            Console.Write($"  {prompt}: ");
        Console.ResetColor();

        var input = ReadLineUnicode().Trim().Trim('"');
        return string.IsNullOrEmpty(input) && defaultValue != null ? defaultValue : input;
    }

    public static string AskPath(string prompt, string? defaultValue = null)
    {
        while (true)
        {
            var path = AskInput(prompt, defaultValue);

            if (File.Exists(path) || Directory.Exists(path))
                return path;

            if (!string.IsNullOrEmpty(path))
            {
                PrintWarning($"路径不存在: {path}");

                if (defaultValue != null && path != defaultValue)
                    continue;

                return path;
            }

            PrintWarning("路径不能为空");
        }
    }

    public static bool AskYesNo(string prompt, bool defaultYes = true)
    {
        var suffix = defaultYes ? "[Y/n]" : "[y/N]";
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  {prompt} {suffix}: ");
        Console.ResetColor();

        var input = ReadLineUnicode().Trim().ToLower();
        if (string.IsNullOrEmpty(input)) return defaultYes;
        return input is "y" or "yes";
    }

    public static void PrintProgress(int current, int total, string suffix = "")
    {
        var pct = total > 0 ? (int)((long)current * 100 / total) : 100;
        var extra = string.IsNullOrEmpty(suffix) ? "" : $" | {suffix}";
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write($"\r  [{pct,3}%] {current}/{total}{extra}    ");
        Console.ResetColor();
    }

    public static int AskMenu(string prompt, params string[] options)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  {prompt}");
        Console.ResetColor();

        for (var i = 0; i < options.Length; i++)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"    {i + 1}. ");
            Console.ResetColor();
            Console.WriteLine(options[i]);
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    0. 退出");
        Console.ResetColor();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  请输入编号: ");
            Console.ResetColor();

            var input = ReadLineUnicode().Trim();
            if (int.TryParse(input, out var choice) && choice >= 0 && choice <= options.Length)
                return choice;

            PrintWarning("无效输入，请重新选择");
        }
    }

    public static int AskInt(string prompt, int defaultValue, int min = 1)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  {prompt} [{defaultValue}]: ");
        Console.ResetColor();

        var input = ReadLineUnicode().Trim();
        if (string.IsNullOrEmpty(input)) return defaultValue;
        if (int.TryParse(input, out var value) && value >= min) return value;

        PrintWarning($"无效输入，使用默认值: {defaultValue}");
        return defaultValue;
    }

    public static int EnsureParallelism(PipelineContext ctx)
    {
        if (ctx.MaxParallelism > 0)
        {
            PrintInfo($"并发进程数: {ctx.MaxParallelism}");
            return ctx.MaxParallelism;
        }

        var defaultVal = Math.Max(Environment.ProcessorCount * 4, 32);
        ctx.MaxParallelism = AskInt("并发进程数（建议 SSD: 128~256, HDD: 32~64）", defaultVal);
        return ctx.MaxParallelism;
    }

    public static void WaitForEnter(string message = "按回车继续...")
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write($"  {message}");
        Console.ResetColor();
        ReadLineUnicode();
    }
}