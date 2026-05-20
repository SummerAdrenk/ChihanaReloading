// ============================================================================
// PicturePathHelper.cs
// 图片路径解析工具: 在元数据和实际文件路径之间转换
//
// 职责:
//   GetMetadataPathForSource  -- 从 fix/orig 中的源文件路径推导 metadata JSON 路径
//   GetArchiveRelativePath    -- 从元数据中恢复档案内相对路径 (用于 Repack/Restore)
//
// 路径推导算法:
//   向上搜索祖先目录找到 "fix" 或 "orig", 然后定位同级的 "metadata" 目录
//
// 依赖: System.Text.Json
// ============================================================================
using System.Text.Json;

namespace Kaguya_YaneKit.Formats.Picture;

internal static class PicturePathHelper
{
    public static string GetMetadataPathForSource(string sourcePath)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var sourceRoot = FindAncestorDirectory(sourceFullPath, "fix") ?? FindAncestorDirectory(sourceFullPath, "orig")
            ?? throw new DirectoryNotFoundException($"Cannot find fix/orig directory for {sourcePath}");
        var formatDir = Directory.GetParent(sourceRoot)?.FullName
            ?? throw new DirectoryNotFoundException($"Cannot find format directory for {sourcePath}");
        var relativePath = Path.GetRelativePath(sourceRoot, sourceFullPath);
        return Path.Combine(formatDir, "metadata", relativePath + ".json");
    }

    public static string GetArchiveRelativePath(Dictionary<string, JsonElement> metadata, string originalRelativePath, string formatDir)
    {
        if (metadata.TryGetValue("SourceArchive", out var archiveNode) &&
            archiveNode.ValueKind == JsonValueKind.String)
        {
            var sourceArchive = archiveNode.GetString();
            if (!string.IsNullOrWhiteSpace(sourceArchive))
            {
                var prefix = sourceArchive.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (originalRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return originalRelativePath.Substring(prefix.Length);
                }
            }
        }

        return originalRelativePath;
    }

    private static string? FindAncestorDirectory(string path, string directoryName)
    {
        var current = File.Exists(path)
            ? Directory.GetParent(path)
            : new DirectoryInfo(path);

        while (current is not null)
        {
            if (current.Name.Equals(directoryName, StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

}
