// ============================================================================
// MessageVer3TextCodec.cs
// 旧式 message ver3 专用文本导出/导入
//
// ver3 的 block formatName 同时承担角色名、选项文字、标签文字等职责，
// 所以必须作为可编辑字段导出，不能只写在注释里。
// ============================================================================
using System.Text;
using System.Text.RegularExpressions;
using Kaguya_YaneKit.Text.MessageDat.Model;

namespace Kaguya_YaneKit.Text.MessageDat;

public sealed class MessageVer3TextCodec
{
    private static readonly Regex ImportLineRegex = new(
        "^◆V3B([a-fA-F0-9]{4})(?:I([a-fA-F0-9]{4}))?◆(format|name|voice\\d{2}|msg)◆(.*)$",
        RegexOptions.Compiled);

    public string Write(MessageVer3Document document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("//==================================================");
        builder.AppendLine($"; Section: Message ver{document.Version} blocks");
        builder.AppendLine("//==================================================");

        WriteBlocks(builder, document, Enumerable.Range(0, document.Blocks.Count));
        return builder.ToString();
    }

    public string WriteBlocks(MessageVer3Document document, IEnumerable<int> blockIndices)
    {
        var builder = new StringBuilder();
        WriteBlocks(builder, document, blockIndices);
        return builder.ToString();
    }

    public void Apply(MessageVer3Document document, string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var match = ImportLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var blockIndex = Convert.ToInt32(match.Groups[1].Value, 16);
            if (blockIndex >= document.Blocks.Count)
            {
                continue;
            }

            var block = document.Blocks[blockIndex];
            var field = match.Groups[3].Value;
            var value = Unescape(match.Groups[4].Value);
            if (field is "format" or "name")
            {
                block.FormatName = value;
                continue;
            }

            if (!match.Groups[2].Success)
            {
                if (field == "msg")
                {
                    // ver3 的选项/标签 block 没有 item，底层文字存放在 formatName。
                    // 新文本格式把它导出为 msg，便于翻译侧按普通文本处理。
                    block.FormatName = value;
                }
                continue;
            }

            var itemIndex = Convert.ToInt32(match.Groups[2].Value, 16);
            if (itemIndex >= block.Items.Count)
            {
                continue;
            }

            var item = block.Items[itemIndex];
            if (field == "msg")
            {
                item.Message.Text = value;
                continue;
            }

            var voiceIndex = int.Parse(field[5..]);
            if (voiceIndex < item.Voices.Count)
            {
                item.Voices[voiceIndex].Text = value;
            }
        }
    }

    private static void WriteBlocks(StringBuilder builder, MessageVer3Document document, IEnumerable<int> blockIndices)
    {
        foreach (var blockIndex in blockIndices.Distinct().OrderBy(x => x))
        {
            if (blockIndex < 0 || blockIndex >= document.Blocks.Count)
            {
                continue;
            }

            var block = document.Blocks[blockIndex];
            if (block.Items.Count == 0)
            {
                WritePair(builder, $"V3B{blockIndex:X4}", "msg", block.FormatName);
                builder.AppendLine();
                continue;
            }

            if (!string.IsNullOrEmpty(block.FormatName))
            {
                WritePair(builder, $"V3B{blockIndex:X4}", "name", block.FormatName);
                builder.AppendLine();
            }

            for (var itemIndex = 0; itemIndex < block.Items.Count; itemIndex++)
            {
                var item = block.Items[itemIndex];
                var itemKey = $"V3B{blockIndex:X4}I{itemIndex:X4}";
                for (var voiceIndex = 0; voiceIndex < item.Voices.Count; voiceIndex++)
                {
                    WritePair(builder, itemKey, $"voice{voiceIndex:D2}", item.Voices[voiceIndex].Text);
                }

                if (item.Voices.Count > 0)
                {
                    builder.AppendLine();
                }

                WritePair(builder, itemKey, "msg", item.Message.Text);
                builder.AppendLine();
            }
        }
    }

    private static void WritePair(StringBuilder builder, string key, string field, string value)
    {
        var text = Escape(value);
        builder.AppendLine($"◇{key}◇{field}◇{text}");
        builder.AppendLine($"◆{key}◆{field}◆{text}");
    }

    private static string Escape(string value) =>
        value.Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n", StringComparison.Ordinal);
}
