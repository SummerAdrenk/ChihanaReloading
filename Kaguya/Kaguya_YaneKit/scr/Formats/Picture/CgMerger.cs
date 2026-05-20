// ============================================================================
// CgMerger.cs
// CG 差分合成: 将 BMP 基底图与 AP2/AP 差分图叠加合成
//
// 合成规则 (按优先级):
//   1. 文件名匹配 "cg{id}_{name}{id2}" -> 查找 AP2 差分 "cg{id}_{name}モ{id2}甲"
//   2. 文件名匹配 "cg{id}_{name}_{id2}" -> 查找同名规则的 AP2 差分
//   3. 文件名匹配 "cg{id}_{id2}" -> 查找 "cg{id}_モ{id2}甲"
//   4. AP2 无匹配时回退到 AP 目录查找同名差分
//   5. 均无匹配时直接复制基底图
//
// 合成算法: 基底图转为 32bpp ARGB, 将差分图按 AP2 元数据偏移叠加绘制
//
// 依赖: System.Drawing (Bitmap/Graphics), System.Text.Json
// ============================================================================
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kaguya_YaneKit.Formats.Picture;

public static class CgMerger
{
    private sealed class Ap2Metadata
    {
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
    }

    public static void MergeCgFiles(string ap2Dir, string bmpDir)
    {
        ap2Dir = Path.GetFullPath(ap2Dir);
        bmpDir = Path.GetFullPath(bmpDir);

        var workDir = new DirectoryInfo(bmpDir).Parent!.FullName;
        var ap2PngDir = Path.Combine(ap2Dir, "png");
        var ap2MetaDir = Path.Combine(ap2Dir, "metadata");
        var apDir = Path.Combine(workDir, "ap");
        var apPngDir = Path.Combine(apDir, "png");
        var bmpPngDir = Path.Combine(bmpDir, "png");
        var outputDir = Path.Combine(workDir, "cg");

        if (!Directory.Exists(ap2PngDir) || !Directory.Exists(ap2MetaDir) || !Directory.Exists(bmpPngDir))
        {
            throw new DirectoryNotFoundException("缺少 ap2 或 bmp 目录下的 png/metadata 文件夹。");
        }

        var hasApDir = Directory.Exists(apPngDir);
        Directory.CreateDirectory(outputDir);

        foreach (var baseFile in Directory.GetFiles(bmpPngDir, "*.png", SearchOption.TopDirectoryOnly))
        {
            var baseFileName = Path.GetFileName(baseFile);
            var baseNameWithoutExt = Path.GetFileNameWithoutExtension(baseFile);
            string? diffPngPath = null;
            Ap2Metadata? mergeMeta = null;

            var matchBaku = Regex.Match(baseNameWithoutExt, @"^cg(\d+)_([a-zA-Z]+)(\d+)$");
            if (matchBaku.Success)
            {
                var id1 = matchBaku.Groups[1].Value;
                var charName = matchBaku.Groups[2].Value;
                var id2 = matchBaku.Groups[3].Value;
                var diffFileName = $"cg{id1}_{charName}モ{id2}甲";
                var potentialAp2Png = Path.Combine(ap2PngDir, diffFileName + ".png");
                var potentialAp2Json = Path.Combine(ap2MetaDir, diffFileName + ".json");
                if (File.Exists(potentialAp2Png) && File.Exists(potentialAp2Json))
                {
                    diffPngPath = potentialAp2Png;
                    mergeMeta = JsonSerializer.Deserialize<Ap2Metadata>(File.ReadAllText(potentialAp2Json), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                else if (hasApDir)
                {
                    var potentialApPng = Path.Combine(apPngDir, diffFileName + ".png");
                    if (File.Exists(potentialApPng))
                    {
                        diffPngPath = potentialApPng;
                        mergeMeta = new Ap2Metadata { OffsetX = 0, OffsetY = 0 };
                    }
                }
            }

            if (diffPngPath == null)
            {
                var matchRuleA = Regex.Match(baseNameWithoutExt, @"^(cg\d+_[a-zA-Z_]+_)(\d+)$");
                if (matchRuleA.Success)
                {
                    var part1 = matchRuleA.Groups[1].Value;
                    var id2 = matchRuleA.Groups[2].Value;
                    var diffName = $"{part1}モ{id2}甲";
                    var pPng = Path.Combine(ap2PngDir, diffName + ".png");
                    var pJson = Path.Combine(ap2MetaDir, diffName + ".json");
                    if (File.Exists(pPng) && File.Exists(pJson))
                    {
                        diffPngPath = pPng;
                        mergeMeta = JsonSerializer.Deserialize<Ap2Metadata>(File.ReadAllText(pJson), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                }
                else
                {
                    var matchRuleC = Regex.Match(baseNameWithoutExt, @"^(cg\d+)_(\d+)$");
                    if (matchRuleC.Success)
                    {
                        var id1Part = matchRuleC.Groups[1].Value;
                        var id2 = matchRuleC.Groups[2].Value;
                        var diffName = $"{id1Part}_モ{id2}甲";
                        var pPng = Path.Combine(ap2PngDir, diffName + ".png");
                        var pJson = Path.Combine(ap2MetaDir, diffName + ".json");
                        if (File.Exists(pPng) && File.Exists(pJson))
                        {
                            diffPngPath = pPng;
                            mergeMeta = JsonSerializer.Deserialize<Ap2Metadata>(File.ReadAllText(pJson), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        }
                    }
                }
            }

            var finalOutputPath = Path.Combine(outputDir, baseFileName);
            if (diffPngPath != null && mergeMeta != null)
            {
                using var baseImageOriginal = new Bitmap(baseFile);
                using var baseImageCanvas = baseImageOriginal.Clone(new Rectangle(0, 0, baseImageOriginal.Width, baseImageOriginal.Height), PixelFormat.Format32bppArgb);
                using var diffImage = new Bitmap(diffPngPath);
                using (var g = Graphics.FromImage(baseImageCanvas))
                {
                    g.DrawImage(diffImage, mergeMeta.OffsetX, mergeMeta.OffsetY, diffImage.Width, diffImage.Height);
                }
                baseImageCanvas.Save(finalOutputPath, ImageFormat.Png);
            }
            else
            {
                File.Copy(baseFile, finalOutputPath, true);
            }
        }
    }
}
