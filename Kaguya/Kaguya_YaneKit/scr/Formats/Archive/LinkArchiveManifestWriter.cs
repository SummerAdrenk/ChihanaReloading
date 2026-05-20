// ============================================================================
// LinkArchiveManifestWriter.cs
// LINK 档案清单 JSON 序列化/反序列化工具
//
// 功能:
//   Write() -- 将 LinkArchiveManifest 序列化为带缩进的 JSON 文件
//              使用 UnsafeRelaxedJsonEscaping 保留非 ASCII 字符原样输出
//              通过 ReadableUnicodeJson.WriteAllText 确保 Unicode 可读性
//   Read()  -- 从 JSON 文件反序列化为 LinkArchiveManifest
//
// 文件名约定: _link_manifest.json (由 LinkArchiveCodec.Extract 写出)
//
// 依赖: Kaguya_YaneKit.Core.ReadableUnicodeJson, System.Text.Json
// 被依赖: LinkArchiveCodec (Extract/PackLink6FromManifest)
// ============================================================================
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kaguya_YaneKit.Core;

namespace Kaguya_YaneKit.Formats.Archive;

public static class LinkArchiveManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Write(string outputPath, LinkArchiveManifest manifest)
    {
        ReadableUnicodeJson.WriteAllText(outputPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public static LinkArchiveManifest Read(string inputPath)
    {
        return JsonSerializer.Deserialize<LinkArchiveManifest>(File.ReadAllText(inputPath, Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("Manifest JSON did not contain a LINK archive manifest.");
    }

}
