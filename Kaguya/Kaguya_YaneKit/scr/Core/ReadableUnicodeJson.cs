// ============================================================================
// ReadableUnicodeJson.cs
// JSON Unicode 转义还原工具
//
// 功能说明:
//   System.Text.Json 默认将非 ASCII 字符序列化为 \uXXXX 转义,
//   导致中日文等字符不可直接阅读.
//   本工具将 \uXXXX 转义还原为原始 Unicode 字符, 保留控制字符和 " \ 的转义.
//
// 核心算法:
//   RestoreReadableUnicodeEscapes() - 正则匹配 \\u[0-9a-fA-F]{4},
//   将非控制/非特殊字符还原为实际字符, 控制字符和引号/反斜杠保持转义
//   WriteAllText() - 还原后以 UTF-8 写入文件
//
// 依赖: 无外部依赖
// 被依赖: MessageScriptLinker (写出映射 JSON)
// ============================================================================
using System.Text;
using System.Text.RegularExpressions;

namespace Kaguya_YaneKit.Core;

internal static class ReadableUnicodeJson
{
    private static readonly Regex ReadableUnicodeEscapeRegex = new(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

    public static void WriteAllText(string path, string json)
    {
        File.WriteAllText(path, RestoreReadableUnicodeEscapes(json), Encoding.UTF8);
    }

    public static string RestoreReadableUnicodeEscapes(string json) =>
        ReadableUnicodeEscapeRegex.Replace(json, match =>
        {
            var value = Convert.ToUInt16(match.Groups[1].Value, 16);
            var ch = (char)value;
            return char.IsControl(ch) || ch is '"' or '\\'
                ? match.Value
                : ch.ToString();
        });
}
