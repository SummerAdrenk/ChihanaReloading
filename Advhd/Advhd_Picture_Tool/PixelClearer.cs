using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Threading;

namespace AdvhdPictureTool
{
    public static class PixelClearer
    {
        public static void ClearPixels(string basePath)
        {
            basePath = Path.GetFullPath(basePath);

            if (!Directory.Exists(basePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"错误: 目标目录 '{basePath}' 不存在。");
                Console.ResetColor();
                return;
            }

            // 只查找所有名为 png 的目录
            var sourceDirs = Directory.EnumerateDirectories(basePath, "png", SearchOption.AllDirectories)
                                      .ToList();

            if (!sourceDirs.Any())
            {
                Console.WriteLine($"在 '{basePath}' 目录及其子目录中未找到任何 png 文件夹。");
                return;
            }

            Console.WriteLine($"\n==> 开始清除指定目录下png文件夹内的PNG像素:  '{basePath}'");

            int totalSuccess = 0;
            int totalFailure = 0;
            int totalFiles = 0;

            foreach (var sourceDir in sourceDirs)
            {
                var filesInDir = Directory.GetFiles(sourceDir, "*.png", SearchOption.AllDirectories);
                if (filesInDir.Length == 0)
                {
                    continue;
                }

                totalFiles += filesInDir.Length;

                string formatDir = new DirectoryInfo(sourceDir).Parent!.FullName;
                string clearDir = Path.Combine(formatDir, "clear");
                Directory.CreateDirectory(clearDir);

                Console.WriteLine($"\n==> 正在处理: {sourceDir}");
                Console.WriteLine($"\n==> 输出到: {clearDir}");

                int currentSuccess = 0;
                int currentFailure = 0;

                Parallel.ForEach(filesInDir, file =>
                {
                    try
                    {
                        using var originalImage = Image.Load(file);
                        using var blankImage = new Image<Rgba32>(originalImage.Width, originalImage.Height);

                        string relativePath = Path.GetRelativePath(sourceDir, file);
                        string destPath = Path.Combine(clearDir, relativePath);

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        blankImage.SaveAsPng(destPath);

                        Interlocked.Increment(ref currentSuccess);
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"处理文件 '{Path.GetFileName(file)}' 时出错: {ex.Message}");
                        Console.ResetColor();
                        Interlocked.Increment(ref currentFailure);
                    }
                });

                Console.WriteLine($"  -> 本目录处理完成: 成功 {currentSuccess}, 失败 {currentFailure}");
                totalSuccess += currentSuccess;
                totalFailure += currentFailure;
            }

            Console.WriteLine("\n--------------------------");
            Console.WriteLine($"所有 png 子目录清除完成！共处理: {totalFiles} 个文件, 成功: {totalSuccess}, 失败: {totalFailure}");
        }
    }
}