// ============================================================================
// CharacterComposer.cs
// CG/立绘合成引擎: 根据 params.dat 的 Pattern 数据合成完整角色图片
//
// 合成流程:
//   1. BuildStaticAssetIndex   -- 扫描 pic/ 下 bmp/ap/ap2 目录建立静态素材索引
//   2. BuildAnimatedAssetIndex -- 扫描 anm/ 目录建立动画素材索引
//   3. BuildCgPlans            -- 根据 Pattern.GroupTable1 生成 CG 合成计划
//   4. BuildSpPlans            -- 根据 Pattern.IntArrays 生成 SP 合成计划
//   5. ComposeCgPlans          -- 并行执行 CG 合成 (叠加图层或直接复制)
//   6. ComposeSpPlans          -- 并行执行 SP 合成 (支持动画帧序列)
//
// Pattern 资源解析:
//   Pattern.IntArrays 中的每个数组引用 Pattern.Items
//   每个 Item 按 Kind 解析: 0=单文件名, 1=文件名列表, 2/3=子名称
//   资源路径格式: "{archiveName}:{relativePath}" (如 "cg00:image.bmp")
//
// 图层合成算法:
//   CG: 按偏移叠加, 画布大小为所有图层边界的最小外接矩形
//   SP: 在固定画布 (canvasWidth x canvasHeight) 上叠加, 支持多帧动画
//
// 并行处理: Parallel.ForEach + PictureProcessing.ParallelOptions
// 进度汇报: [CHAR-CG:{archive}] N/M (X%), [CHAR-SP:{archive}] N/M (X%)
//
// 依赖: Formats.Params.ParamsDatDocument, Formats.Picture.PictureProcessing,
//        System.Drawing (Bitmap/Graphics)
// ============================================================================
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using Kaguya_YaneKit.Formats.Params;
using Kaguya_YaneKit.Formats.Picture;

namespace Kaguya_YaneKit.Formats.Character;

public static class CharacterComposer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static CharacterComposeResult ComposeAll(string picDir, string outputDir, int canvasWidth = 1280, int canvasHeight = 720)
        => ComposeAll(picDir, outputDir, null, canvasWidth, canvasHeight);

    public static CharacterComposeResult ComposeAll(string picDir, string outputDir, ParamsDatDocument? paramsDocument, int canvasWidth = 1280, int canvasHeight = 720)
    {
        picDir = Path.GetFullPath(picDir);
        outputDir = Path.GetFullPath(outputDir);

        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(Path.Combine(outputDir, "cg"));
        Directory.CreateDirectory(Path.Combine(outputDir, "sp"));

        var result = new CharacterComposeResult();
        if (paramsDocument?.Pattern is null)
        {
            PictureProcessing.WriteLine("Character compose requires params.dat Pattern data. No filename heuristic was used.");
            WriteUsageReport(outputDir, result);
            return result;
        }

        var staticAssets = BuildStaticAssetIndex(picDir);
        var animatedAssets = BuildAnimatedAssetIndex(picDir);
        result.StaticAssetCount = staticAssets.Count;
        result.AnimatedAssetCount = animatedAssets.Count;

        var usedStaticKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedAnimatedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cgPlans = BuildCgPlans(paramsDocument.Pattern, staticAssets, usedStaticKeys, result)
            .ToArray();
        var spPlans = BuildSpPlans(paramsDocument.Pattern, staticAssets, animatedAssets, usedStaticKeys, usedAnimatedKeys, result)
            .ToArray();

        ComposeCgPlans(Path.Combine(outputDir, "cg"), cgPlans, result);
        ComposeSpPlans(Path.Combine(outputDir, "sp"), spPlans, canvasWidth, canvasHeight, result);

        result.StaticUsedCount = usedStaticKeys.Count;
        result.AnimatedUsedCount = usedAnimatedKeys.Count;
        BuildResourceUsageReports(staticAssets, animatedAssets, usedStaticKeys, usedAnimatedKeys, result);
        WriteUsageReport(outputDir, result);
        return result;
    }

    private static void ComposeCgPlans(string outputDir, IReadOnlyList<CgCompositionPlan> plans, CharacterComposeResult result)
    {
        foreach (var group in plans.GroupBy(plan => plan.ArchiveName, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            int composed = 0, copied = 0, failed = 0;
            result.CgBaseCount += group.Count();
            using var progress = PictureProcessing.StartProgress($"CHAR-CG:{group.Key}", group.Count());

            Parallel.ForEach(group, PictureProcessing.ParallelOptions, plan =>
            {
                try
                {
                    var archiveDir = Path.Combine(outputDir, SanitizeFileName(plan.ArchiveName));
                    Directory.CreateDirectory(archiveDir);
                    var destFile = Path.Combine(archiveDir, $"{plan.Index:D4}_{BuildPlanLabel(plan.LabelParts)}.png");

                    if (CanCopySingleLayer(plan.Layers))
                    {
                        File.Copy(plan.Layers[0].PrimaryPath, destFile, true);
                        Interlocked.Increment(ref copied);
                    }
                    else
                    {
                        ComposeLayers(plan.Layers, destFile);
                        Interlocked.Increment(ref composed);
                    }
                }
                catch (Exception ex)
                {
                    PictureProcessing.WriteLine($"Failed to compose CG \"{plan.ArchiveName}:{plan.Index}\": {ex.Message}");
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    progress.Increment();
                }
            });

            result.CgComposedCount += composed;
            result.CgCopiedCount += copied;
            result.FailureCount += failed;
        }
    }

    private static void ComposeSpPlans(string outputDir, IReadOnlyList<SpCompositionPlan> plans, int canvasWidth, int canvasHeight, CharacterComposeResult result)
    {
        foreach (var group in plans.GroupBy(plan => plan.ArchiveName, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            int composed = 0, copied = 0, failed = 0;
            result.SpBaseCount += group.Count();
            using var progress = PictureProcessing.StartProgress($"CHAR-SP:{group.Key}", group.Count());

            Parallel.ForEach(group, PictureProcessing.ParallelOptions, plan =>
            {
                try
                {
                    var archiveDir = Path.Combine(outputDir, SanitizeFileName(plan.ArchiveName));
                    var comboDir = Path.Combine(archiveDir, $"{plan.Index:D4}_{BuildPlanLabel(plan.LabelParts)}");
                    Directory.CreateDirectory(comboDir);

                    if (plan.RequiresFrames)
                    {
                        var frameCount = plan.Layers.Where(layer => layer.RequiresFrames).Select(layer => layer.FramePaths.Count).DefaultIfEmpty(1).Max();
                        if (frameCount <= 1)
                        {
                            var compositePath = Path.Combine(comboDir, "composite.png");
                            ComposeOnCanvas(plan.Layers, 0, canvasWidth, canvasHeight, compositePath);
                        }
                        else
                        {
                            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                            {
                                var framePath = Path.Combine(comboDir, $"frame_{frameIndex:D4}.png");
                                ComposeOnCanvas(plan.Layers, frameIndex, canvasWidth, canvasHeight, framePath);
                            }
                        }

                        Interlocked.Increment(ref composed);
                        return;
                    }

                    var destFile = Path.Combine(comboDir, "composite.png");
                    if (CanCopySingleLayer(plan.Layers))
                    {
                        File.Copy(plan.Layers[0].PrimaryPath, destFile, true);
                        Interlocked.Increment(ref copied);
                    }
                    else
                    {
                        ComposeOnCanvas(plan.Layers, 0, canvasWidth, canvasHeight, destFile);
                        Interlocked.Increment(ref composed);
                    }
                }
                catch (Exception ex)
                {
                    PictureProcessing.WriteLine($"Failed to compose SP \"{plan.ArchiveName}:{plan.Index}\": {ex.Message}");
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    progress.Increment();
                }
            });

            result.SpComposedCount += composed;
            result.SpCopiedCount += copied;
            result.FailureCount += failed;
        }
    }

    private static IEnumerable<CgCompositionPlan> BuildCgPlans(
        ParamsPattern pattern,
        IReadOnlyDictionary<string, LayerAsset> staticAssets,
        ISet<string> usedStaticKeys,
        CharacterComposeResult result)
    {
        foreach (var group in pattern.GroupTable1.Groups)
        {
            if (!group.Name.StartsWith("cg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var rawArrayIndex in group.Indices)
            {
                if (rawArrayIndex >= pattern.IntArrays.Count)
                {
                    continue;
                }

                var resolved = ResolvePatternArrayResources(pattern, pattern.IntArrays[(int)rawArrayIndex])
                    .ToArray();
                if (resolved.Length == 0)
                {
                    continue;
                }

                var archiveSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var labelParts = new List<string>();
                var layers = new List<LayerAsset>();
                var valid = true;

                foreach (var resourcePath in resolved)
                {
                    if (!TryParsePatternResourcePath(resourcePath, out var archiveName, out _))
                    {
                        valid = false;
                        break;
                    }

                    if (!archiveName.StartsWith("cg", StringComparison.OrdinalIgnoreCase))
                    {
                        valid = false;
                        break;
                    }

                    if (!TryResolveStaticResource(resourcePath, staticAssets, out var asset))
                    {
                        RecordMissingReference(result, resourcePath);
                        valid = false;
                        break;
                    }

                    usedStaticKeys.Add(asset.ResourceKey);
                    archiveSet.Add(asset.ArchiveName);
                    layers.Add(asset);
                    labelParts.Add(Path.GetFileNameWithoutExtension(asset.RelativeName));
                }

                if (!valid || layers.Count == 0)
                {
                    continue;
                }

                yield return new CgCompositionPlan(BuildArchiveSummary(archiveSet), (int)rawArrayIndex, labelParts, layers);
            }
        }
    }

    internal static IEnumerable<SpCompositionPlan> BuildSpPlans(
        ParamsPattern pattern,
        IReadOnlyDictionary<string, LayerAsset> staticAssets,
        IReadOnlyDictionary<string, LayerAsset> animatedAssets,
        ISet<string> usedStaticKeys,
        ISet<string> usedAnimatedKeys,
        CharacterComposeResult result)
    {
        for (var index = 0; index < pattern.IntArrays.Count; index++)
        {
            var resolved = ResolvePatternArrayResources(pattern, pattern.IntArrays[index]).ToArray();
            if (resolved.Length == 0)
            {
                continue;
            }

            var archiveSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var labelParts = new List<string>();
            var layers = new List<LayerAsset>();
            var valid = true;

            foreach (var resourcePath in resolved)
            {
                if (!TryParsePatternResourcePath(resourcePath, out var archiveName, out _))
                {
                    valid = false;
                    break;
                }

                if (!archiveName.StartsWith("sp", StringComparison.OrdinalIgnoreCase))
                {
                    valid = false;
                    break;
                }

                if (!TryResolveResource(resourcePath, staticAssets, animatedAssets, out var asset))
                {
                    RecordMissingReference(result, resourcePath);
                    valid = false;
                    break;
                }

                if (asset.FormatTag.Equals("anm", StringComparison.OrdinalIgnoreCase))
                {
                    usedAnimatedKeys.Add(asset.ResourceKey);
                }
                else
                {
                    usedStaticKeys.Add(asset.ResourceKey);
                }

                archiveSet.Add(asset.ArchiveName);
                layers.Add(asset);
                labelParts.Add(Path.GetFileNameWithoutExtension(asset.RelativeName));
            }

            if (!valid || layers.Count == 0)
            {
                continue;
            }

            yield return new SpCompositionPlan(BuildArchiveSummary(archiveSet), index, labelParts, layers);
        }
    }

    internal static IEnumerable<SpCompositionPlan> BuildSpPlansFromLayerGroups(
        IEnumerable<SpLayerGroup> groups,
        ISet<string> usedStaticKeys,
        ISet<string> usedAnimatedKeys)
    {
        foreach (var group in groups)
        {
            if (group.Layers.Count == 0)
            {
                continue;
            }

            foreach (var layer in group.Layers)
            {
                if (layer.FormatTag.Equals("anm", StringComparison.OrdinalIgnoreCase))
                {
                    usedAnimatedKeys.Add(layer.ResourceKey);
                }
                else
                {
                    usedStaticKeys.Add(layer.ResourceKey);
                }
            }

            var archiveName = string.IsNullOrWhiteSpace(group.ArchiveName)
                ? BuildArchiveSummary(group.Layers.Select(layer => layer.ArchiveName))
                : group.ArchiveName;

            yield return new SpCompositionPlan(archiveName, group.Index, group.LabelParts, group.Layers)
            {
                CharacterHint = group.CharacterHint,
                SourceName = group.SourceName
            };
        }
    }

    private static IEnumerable<string> ResolvePatternArrayResources(ParamsPattern pattern, IReadOnlyList<uint> array)
    {
        foreach (var rawIndex in array)
        {
            if (rawIndex >= pattern.Items.Count)
            {
                continue;
            }

            var item = pattern.Items[(int)rawIndex];
            foreach (var resource in ResolvePatternItemResources(item))
            {
                if (!string.IsNullOrWhiteSpace(resource))
                {
                    yield return resource;
                }
            }
        }
    }

    private static List<string> ResolvePatternItemResources(ParamsPatternItem item)
    {
        var result = new List<string>();
        switch (item.Kind)
        {
            case 0:
                if (!string.IsNullOrWhiteSpace(item.Name))
                {
                    result.Add(item.Name);
                }
                break;
            case 1:
                result.AddRange(item.Strings.Where(text => !string.IsNullOrWhiteSpace(text)));
                break;
            case 2:
            case 3:
                if (!string.IsNullOrWhiteSpace(item.SubName))
                {
                    result.Add(item.SubName);
                }
                break;
        }

        return result;
    }

    internal static Dictionary<string, LayerAsset> BuildStaticAssetIndex(string picDir)
    {
        var index = new Dictionary<string, LayerAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var archiveDir in Directory.GetDirectories(picDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var archiveName = new DirectoryInfo(archiveDir).Name;
            if (!archiveName.StartsWith("cg", StringComparison.OrdinalIgnoreCase) &&
                !archiveName.StartsWith("sp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var formatName in new[] { "bmp", "ap", "ap2" })
            {
                var formatDir = Path.Combine(archiveDir, formatName);
                if (!Directory.Exists(formatDir))
                {
                    continue;
                }

                foreach (var asset in ReadStaticAssets(archiveName, formatName, formatDir))
                {
                    index[asset.ResourceKey] = asset;
                }
            }
        }

        return index;
    }

    internal static Dictionary<string, LayerAsset> BuildAnimatedAssetIndex(string picDir)
    {
        var index = new Dictionary<string, LayerAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var archiveDir in Directory.GetDirectories(picDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var archiveName = new DirectoryInfo(archiveDir).Name;
            if (!archiveName.StartsWith("cg", StringComparison.OrdinalIgnoreCase) &&
                !archiveName.StartsWith("sp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var anmDir = Path.Combine(archiveDir, "anm");
            if (!Directory.Exists(anmDir))
            {
                continue;
            }

            foreach (var asset in ReadAnimatedAssets(archiveName, anmDir))
            {
                index[asset.ResourceKey] = asset;
            }
        }

        return index;
    }

    private static IEnumerable<LayerAsset> ReadStaticAssets(string archiveName, string formatTag, string formatDir)
    {
        var pngDir = Path.Combine(formatDir, "png");
        var metaDir = Path.Combine(formatDir, "metadata");
        var origDir = Path.Combine(formatDir, "orig");

        if (!Directory.Exists(pngDir))
        {
            yield break;
        }

        var sourceFiles = Directory.Exists(origDir)
            ? Directory.GetFiles(origDir, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Directory.GetFiles(pngDir, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => Path.Combine(origDir, Path.GetFileNameWithoutExtension(f) + GetFallbackExtension(formatTag)))
                .ToArray();

        foreach (var sourceFile in sourceFiles)
        {
            var sourceName = Path.GetFileName(sourceFile);
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                continue;
            }

            var baseName = Path.GetFileNameWithoutExtension(sourceName);
            var ext = Path.GetExtension(sourceName);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = GetFallbackExtension(formatTag);
            }

            var pngPath = Path.Combine(pngDir, baseName + ".png");
            if (!File.Exists(pngPath))
            {
                continue;
            }

            var metadataPath = Path.Combine(metaDir, baseName + ".json");
            var metadata = File.Exists(metadataPath)
                ? JsonSerializer.Deserialize<StaticLayerMetadata>(File.ReadAllText(metadataPath), JsonOptions) ?? new StaticLayerMetadata()
                : new StaticLayerMetadata();

            yield return LayerAsset.CreateStatic(archiveName, formatTag, baseName + ext, pngPath, metadata.OffsetX, metadata.OffsetY);
        }
    }

    private static IEnumerable<LayerAsset> ReadAnimatedAssets(string archiveName, string formatDir)
    {
        var pngDir = Path.Combine(formatDir, "png");
        var metaDir = Path.Combine(formatDir, "metadata");
        if (!Directory.Exists(pngDir))
        {
            yield break;
        }

        foreach (var frameDir in Directory.GetDirectories(pngDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(frameDir);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var resourceName = name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? name : name + ".anm";
            var metadataPath = Path.Combine(metaDir, resourceName + ".json");
            var metadata = File.Exists(metadataPath)
                ? JsonSerializer.Deserialize<AnimatedLayerMetadata>(File.ReadAllText(metadataPath), JsonOptions) ?? new AnimatedLayerMetadata()
                : new AnimatedLayerMetadata();
            var frames = Directory.GetFiles(frameDir, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (frames.Length == 0)
            {
                continue;
            }

            yield return LayerAsset.CreateAnimated(archiveName, resourceName, frames, metadata.CanvasOffsetX, metadata.CanvasOffsetY);
        }
    }

    private static bool TryResolveStaticResource(string resourcePath, IReadOnlyDictionary<string, LayerAsset> staticAssets, out LayerAsset asset)
    {
        foreach (var key in BuildCandidateResourceKeys(resourcePath, includeAnimated: false))
        {
            if (staticAssets.TryGetValue(key, out var found) && found is not null)
            {
                asset = found;
                return true;
            }
        }

        asset = null!;
        return false;
    }

    internal static bool TryResolveResource(string resourcePath, IReadOnlyDictionary<string, LayerAsset> staticAssets, IReadOnlyDictionary<string, LayerAsset> animatedAssets, out LayerAsset asset)
    {
        foreach (var key in BuildCandidateResourceKeys(resourcePath, includeAnimated: true))
        {
            if (staticAssets.TryGetValue(key, out var staticAsset) && staticAsset is not null)
            {
                asset = staticAsset;
                return true;
            }

            if (animatedAssets.TryGetValue(key, out var animatedAsset) && animatedAsset is not null)
            {
                asset = animatedAsset;
                return true;
            }
        }

        asset = null!;
        return false;
    }

    private static IEnumerable<string> BuildCandidateResourceKeys(string resourcePath, bool includeAnimated)
    {
        if (!TryParsePatternResourcePath(resourcePath, out var archiveName, out var relativePath))
        {
            yield break;
        }

        yield return $"{archiveName}:{relativePath}";

        var extension = Path.GetExtension(relativePath);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            yield break;
        }

        if (archiveName.StartsWith("cg", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{archiveName}:{relativePath}.bmp";
            yield return $"{archiveName}:{relativePath}.alp";
            yield break;
        }

        if (archiveName.StartsWith("sp", StringComparison.OrdinalIgnoreCase) ||
            archiveName.Equals("spd", StringComparison.OrdinalIgnoreCase))
        {
            if (includeAnimated)
            {
                yield return $"{archiveName}:{relativePath}.anm";
            }

            yield return $"{archiveName}:{relativePath}.alp";
            yield return $"{archiveName}:{relativePath}.bmp";
        }
    }

    private static bool TryParsePatternResourcePath(string resourcePath, out string archiveName, out string relativePath)
    {
        archiveName = "";
        relativePath = "";

        var normalized = resourcePath.Replace('/', '\\');
        var colonIndex = normalized.IndexOf(':');
        if (colonIndex > 0 && colonIndex < normalized.Length - 1)
        {
            archiveName = normalized[..colonIndex];
            relativePath = normalized[(colonIndex + 1)..].TrimStart('\\');
            return archiveName.Length > 0 && relativePath.Length > 0;
        }

        var fileName = Path.GetFileName(normalized);
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var archive = extension.TrimStart('.');
        if (!archive.StartsWith("cg", StringComparison.OrdinalIgnoreCase) &&
            !archive.StartsWith("sp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        archiveName = archive;
        relativePath = Path.GetFileNameWithoutExtension(fileName);
        return archiveName.Length > 0 && relativePath.Length > 0;
    }

    private static void ComposeLayers(IReadOnlyList<LayerAsset> layers, string destFile)
    {
        var bounds = GetCompositionBounds(layers);
        using var canvas = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Transparent);
            foreach (var layer in layers)
            {
                using var image = new Bitmap(layer.PrimaryPath);
                graphics.DrawImage(image, layer.OffsetX - bounds.Left, layer.OffsetY - bounds.Top, image.Width, image.Height);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        canvas.Save(destFile, ImageFormat.Png);
    }

    private static Rectangle GetCompositionBounds(IReadOnlyList<LayerAsset> layers)
    {
        var left = 0;
        var top = 0;
        var right = 0;
        var bottom = 0;
        var initialized = false;

        foreach (var layer in layers)
        {
            using var image = new Bitmap(layer.PrimaryPath);
            var layerLeft = layer.OffsetX;
            var layerTop = layer.OffsetY;
            var layerRight = layer.OffsetX + image.Width;
            var layerBottom = layer.OffsetY + image.Height;
            if (!initialized)
            {
                left = layerLeft;
                top = layerTop;
                right = layerRight;
                bottom = layerBottom;
                initialized = true;
                continue;
            }

            left = Math.Min(left, layerLeft);
            top = Math.Min(top, layerTop);
            right = Math.Max(right, layerRight);
            bottom = Math.Max(bottom, layerBottom);
        }

        if (!initialized)
        {
            return new Rectangle(0, 0, 1, 1);
        }

        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        return new Rectangle(left, top, width, height);
    }

    internal static void ComposeOnCanvas(IReadOnlyList<LayerAsset> layers, int frameIndex, int canvasWidth, int canvasHeight, string destFile)
    {
        using var canvas = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Transparent);
            foreach (var layer in layers)
            {
                var imagePath = layer.GetFramePath(frameIndex);
                using var image = new Bitmap(imagePath);
                graphics.DrawImage(image, layer.OffsetX, layer.OffsetY, image.Width, image.Height);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        canvas.Save(destFile, ImageFormat.Png);
    }

    internal static Bitmap ComposeOnCanvasBitmap(IReadOnlyList<LayerAsset> layers, int frameIndex, int canvasWidth, int canvasHeight)
    {
        var canvas = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Transparent);
            foreach (var layer in layers)
            {
                var imagePath = layer.GetFramePath(frameIndex);
                using var image = new Bitmap(imagePath);
                graphics.DrawImage(image, layer.OffsetX, layer.OffsetY, image.Width, image.Height);
            }
        }

        return canvas;
    }

    private static bool CanCopySingleLayer(IReadOnlyList<LayerAsset> layers)
        => layers.Count == 1 && layers[0].OffsetX == 0 && layers[0].OffsetY == 0 && !layers[0].RequiresFrames;

    private static string BuildPlanLabel(IEnumerable<string> labelParts)
    {
        var parts = labelParts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parts.Length == 0)
        {
            return "unknown";
        }

        return SanitizeFileName(string.Join("+", parts));
    }

    private static string BuildArchiveSummary(IEnumerable<string> archives)
    {
        var parts = archives
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parts.Length == 0 ? "unknown" : SanitizeFileName(string.Join("+", parts));
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    private static string GetFallbackExtension(string formatTag)
        => formatTag.Equals("bmp", StringComparison.OrdinalIgnoreCase) ? ".bmp" : ".alp";

    private static void BuildResourceUsageReports(
        IReadOnlyDictionary<string, LayerAsset> staticAssets,
        IReadOnlyDictionary<string, LayerAsset> animatedAssets,
        ISet<string> usedStaticKeys,
        ISet<string> usedAnimatedKeys,
        CharacterComposeResult result)
    {
        foreach (var group in staticAssets.Values
                     .GroupBy(asset => (asset.ArchiveName, asset.FormatTag))
                     .OrderBy(group => group.Key.ArchiveName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(group => group.Key.FormatTag, StringComparer.OrdinalIgnoreCase))
        {
            var assets = group.ToArray();
            var usedCount = assets.Count(asset => usedStaticKeys.Contains(asset.ResourceKey));
            result.ResourceUsageReports.Add(new CharacterResourceUsageReport
            {
                ArchiveName = group.Key.ArchiveName,
                FormatTag = group.Key.FormatTag,
                TotalCount = assets.Length,
                UsedCount = usedCount,
                UnusedSamples = assets
                    .Where(asset => !usedStaticKeys.Contains(asset.ResourceKey))
                    .Select(asset => asset.RelativeName)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList()
            });
        }

        foreach (var group in animatedAssets.Values
                     .GroupBy(asset => (asset.ArchiveName, asset.FormatTag))
                     .OrderBy(group => group.Key.ArchiveName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(group => group.Key.FormatTag, StringComparer.OrdinalIgnoreCase))
        {
            var assets = group.ToArray();
            var usedCount = assets.Count(asset => usedAnimatedKeys.Contains(asset.ResourceKey));
            result.ResourceUsageReports.Add(new CharacterResourceUsageReport
            {
                ArchiveName = group.Key.ArchiveName,
                FormatTag = group.Key.FormatTag,
                TotalCount = assets.Length,
                UsedCount = usedCount,
                UnusedSamples = assets
                    .Where(asset => !usedAnimatedKeys.Contains(asset.ResourceKey))
                    .Select(asset => asset.RelativeName)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList()
            });
        }
    }

    internal static void RecordMissingReference(CharacterComposeResult result, string resourcePath)
    {
        result.MissingReferenceCount++;
        if (result.MissingReferenceSamples.Count < 20 && !result.MissingReferenceSamples.Any(sample => string.Equals(sample, resourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            result.MissingReferenceSamples.Add(resourcePath);
        }
    }

    private static void WriteUsageReport(string outputDir, CharacterComposeResult result)
    {
        var reportPath = Path.Combine(outputDir, "_resource_usage_report.txt");
        var builder = new StringBuilder();

        builder.AppendLine("Character composition usage report");
        builder.AppendLine($"Static assets : {result.StaticUsedCount}/{result.StaticAssetCount} used");
        builder.AppendLine($"Animated assets: {result.AnimatedUsedCount}/{result.AnimatedAssetCount} used");
        builder.AppendLine($"Missing refs  : {result.MissingReferenceCount}");
        builder.AppendLine();

        foreach (var report in result.ResourceUsageReports.OrderBy(r => r.ArchiveName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.FormatTag, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"{report.ArchiveName}/{report.FormatTag}: {report.UsedCount}/{report.TotalCount} used, {report.UnusedCount} unused");
            foreach (var sample in report.UnusedSamples)
            {
                builder.AppendLine($"  unused: {sample}");
            }
        }

        if (result.MissingReferenceSamples.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Missing reference samples:");
            foreach (var sample in result.MissingReferenceSamples)
            {
                builder.AppendLine($"  {sample}");
            }
        }

        File.WriteAllText(reportPath, builder.ToString(), Encoding.UTF8);
        PictureProcessing.WriteLine($"  Character usage report written: _resource_usage_report.txt");
    }

    internal sealed class LayerAsset
    {
        public string ArchiveName { get; }
        public string FormatTag { get; }
        public string RelativeName { get; }
        public IReadOnlyList<string> FramePaths { get; }
        public int OffsetX { get; }
        public int OffsetY { get; }
        public bool RequiresFrames => FramePaths.Count > 1;
        public string PrimaryPath => FramePaths.Count > 0 ? FramePaths[0] : throw new InvalidOperationException($"No image path was loaded for {ResourceKey}.");
        public string ResourceKey => $"{ArchiveName}:{RelativeName}";

        private LayerAsset(string archiveName, string formatTag, string relativeName, IReadOnlyList<string> framePaths, int offsetX, int offsetY)
        {
            ArchiveName = archiveName;
            FormatTag = formatTag;
            RelativeName = relativeName;
            FramePaths = framePaths;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        public static LayerAsset CreateStatic(string archiveName, string formatTag, string relativeName, string pngPath, int offsetX, int offsetY)
            => new(archiveName, formatTag, relativeName, [pngPath], offsetX, offsetY);

        public static LayerAsset CreateAnimated(string archiveName, string relativeName, IReadOnlyList<string> framePaths, int offsetX, int offsetY)
            => new(archiveName, "anm", relativeName, framePaths, offsetX, offsetY);

        public LayerAsset WithOffset(int offsetX, int offsetY)
            => new(ArchiveName, FormatTag, RelativeName, FramePaths, offsetX, offsetY);

        public string GetFramePath(int frameIndex)
        {
            if (FramePaths.Count == 0)
            {
                throw new InvalidOperationException($"No frame paths were loaded for {ResourceKey}.");
            }

            if (frameIndex < 0)
            {
                frameIndex = 0;
            }

            if (frameIndex >= FramePaths.Count)
            {
                frameIndex = FramePaths.Count - 1;
            }

            return FramePaths[frameIndex];
        }
    }

    private sealed record CgCompositionPlan(string ArchiveName, int Index, IReadOnlyList<string> LabelParts, IReadOnlyList<LayerAsset> Layers);

    internal sealed record SpLayerGroup(
        string ArchiveName,
        int Index,
        IReadOnlyList<string> LabelParts,
        IReadOnlyList<LayerAsset> Layers,
        string? CharacterHint = null,
        string? SourceName = null);

    internal sealed record SpCompositionPlan(string ArchiveName, int Index, IReadOnlyList<string> LabelParts, IReadOnlyList<LayerAsset> Layers)
    {
        public string? CharacterHint { get; init; }
        public string? SourceName { get; init; }
        public bool RequiresFrames => Layers.Any(layer => layer.RequiresFrames);
    }

    private sealed class StaticLayerMetadata
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
    }

    private sealed class AnimatedLayerMetadata
    {
        public uint CanvasWidth { get; set; }
        public uint CanvasHeight { get; set; }
        public int CanvasOffsetX { get; set; }
        public int CanvasOffsetY { get; set; }
    }
}
