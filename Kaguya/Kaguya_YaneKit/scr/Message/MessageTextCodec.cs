// ============================================================================
// MessageTextCodec.cs
// MsgTool 文本格式编解码器
//
// 文本格式 (菱形分隔):
//   Section 1: Names    - "◇A{hex}◇{text}" (原文) / "◆A{hex}◆{text}" (译文)
//   Section 2: Choices  - "◇B{hex}◇{text}" / "◆B{hex}◆{text}"
//   Section 3: Dialogue - "◇C{hex}◇name◇{speaker}" + "◇C{hex}◇msg◇{text}"
//                         ◆ 行为可编辑译文行, ◇ 行为只读原文行
//   Section 4: Commands - 只读注释 "// Command[nnnn]: Id=x, Params=[...]"
//
// 核心算法:
//   Write()  - 从 MessageDatDocument 生成文本, 通过 Commands 建立消息->角色名映射
//              和消息->分支映射 (branch01, branch02...)
//   Apply()  - 解析 ◆ 行正则, 按 A/B/C 类型和索引回写到 document 的 Names/Choices/Messages
//   换行转义 - \n <-> 实际换行
//
// 依赖: MessageDatDocument, MessageEntry, MessageCommand
// 被依赖: InteractiveSession (导出/导入文本)
// ============================================================================
using System.Text;
using System.Text.RegularExpressions;
using Kaguya_YaneKit.Message.Model;

namespace Kaguya_YaneKit.Message;

public sealed class MessageTextCodec
{
    private static readonly Regex ImportLineRegex = new(
        "^◆([ABC][a-fA-F0-9]{8})◆(?:(name|msg)◆)?(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex BranchRegex = new(
        "^branch\\d+◆(.*)$",
        RegexOptions.Compiled);

    public string Write(MessageDatDocument document)
    {
        var msgToName = BuildMessageToNameMap(document);
        var msgToBranch = BuildMessageToBranchMap(document);
        var builder = new StringBuilder();

        WriteSectionHeader(builder, "Section 1: Names");
        for (var i = 0; i < document.Names.Count; i++)
        {
            var text = Escape(document.Names[i]);
            builder.AppendLine($"◇A{i:X8}◇{text}");
            builder.AppendLine($"◆A{i:X8}◆{text}");
            builder.AppendLine();
        }

        WriteSectionHeader(builder, "Section 2: Choices");
        for (var i = 0; i < document.Choices.Count; i++)
        {
            var text = Escape(document.Choices[i]);
            builder.AppendLine($"◇B{i:X8}◇{text}");
            builder.AppendLine($"◆B{i:X8}◆{text}");
            builder.AppendLine();
        }

        WriteSectionHeader(builder, "Section 3: Dialogue Flow");
        for (var i = 0; i < document.Messages.Count; i++)
        {
            if (msgToName.TryGetValue(i, out var nameId))
            {
                var speaker = Escape(document.Names[nameId]);
                builder.AppendLine($"◇C{i:X8}◇name◇{speaker}");
                builder.AppendLine($"◆C{i:X8}◆name◆{speaker}");
                builder.AppendLine();
            }

            var messageText = Escape(document.Messages[i].Text);
            if (msgToBranch.TryGetValue(i, out var branch))
            {
                builder.AppendLine($"◇C{i:X8}◇msg◇{branch}◇{messageText}");
                builder.AppendLine($"◆C{i:X8}◆msg◆{branch}◆{messageText}");
            }
            else
            {
                builder.AppendLine($"◇C{i:X8}◇msg◇{messageText}");
                builder.AppendLine($"◆C{i:X8}◆msg◆{messageText}");
            }

            builder.AppendLine();
        }

        WriteSectionHeader(builder, "Section 4: Commands (For analysis only)");
        for (var i = 0; i < document.Commands.Count; i++)
        {
            var command = document.Commands[i];
            builder.AppendLine($"// Command[{i:D4}]: Id={command.Id}, Params=[{string.Join(", ", command.Params)}]");
        }

        return builder.ToString();
    }

    public void Apply(MessageDatDocument document, string text)
    {
        foreach (var line in ReadLogicalLines(text))
        {
            var match = ImportLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var type = match.Groups[1].Value[0];
            var index = Convert.ToInt32(match.Groups[1].Value[1..], 16);
            var textType = match.Groups[2].Value;
            var rawContent = match.Groups[3].Value;
            var branchMatch = BranchRegex.Match(rawContent);
            var content = Unescape(branchMatch.Success ? branchMatch.Groups[1].Value : rawContent);

            switch (type)
            {
                case 'A' when index < document.Names.Count:
                    document.Names[index] = content;
                    break;
                case 'B' when index < document.Choices.Count:
                    document.Choices[index] = content;
                    break;
                case 'C' when textType == "msg" && index < document.Messages.Count:
                    document.Messages[index].Text = content;
                    break;
            }
        }
    }

    private static IReadOnlyDictionary<int, int> BuildMessageToNameMap(MessageDatDocument document)
    {
        var result = new Dictionary<int, int>();
        foreach (var command in document.Commands)
        {
            if (command.Id < 0 || command.Id >= document.Names.Count)
            {
                continue;
            }

            foreach (var messageId in command.Params)
            {
                if (messageId >= 0 && messageId < document.Messages.Count)
                {
                    result[messageId] = command.Id;
                }
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<int, string> BuildMessageToBranchMap(MessageDatDocument document)
    {
        var result = new Dictionary<int, string>();
        foreach (var command in document.Commands)
        {
            if (command.Params.Count < 2)
            {
                continue;
            }

            for (var i = 0; i < command.Params.Count; i++)
            {
                var messageId = command.Params[i];
                if (messageId >= 0 && messageId < document.Messages.Count)
                {
                    result[messageId] = $"branch{i + 1:D2}";
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> ReadLogicalLines(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static void WriteSectionHeader(StringBuilder builder, string title)
    {
        builder.AppendLine("//==================================================");
        builder.AppendLine($"; {title}");
        builder.AppendLine("//==================================================");
    }

    private static string Escape(string value) =>
        value.Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n", StringComparison.Ordinal);
}
