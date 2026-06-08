// ============================================================================
// MessageDatWorkflowProcessor.cs
// 导出/导入工作流变换处理器
//
// 工作流阶段:
//   ApplyExportTransforms()     - 导出前变换: 分支消息调整
//   ApplyPreImportTransforms()  - 导入前变换: 分支消息调整 (与导出对称)
//   ApplyImportTransforms()     - 导入后变换: 行长修复, 行长检查, GBK 编码检查
//
// 核心算法:
//   AdjustBranchMessages - 交换分支命令中 Params[0] 与 Params[AdjustMsgId] 的消息文本
//   FixMessageLengths    - 按 MsgLengthSet 自动换行, 对话文本行尾补全角空格
//   CheckMessageLengths  - 检查行数(<=3)和行宽, 输出警告
//   CheckEncoding        - 逐 Rune 检查目标编码兼容性, 输出定位明细
//   DisplayLength        - 计算显示宽度, 占位符使用 PlaceholderDisplayLengths 配置值
//
// 依赖: MessagePlaceholderConfig, MessageDatDocument
// 被依赖: InteractiveSession / MessageCommands (message.dat export/import 命令)
// ============================================================================
using System.Text;
using Kaguya_YaneKit.Text.MessageDat.Model;

namespace Kaguya_YaneKit.Text.MessageDat;

public sealed class MessageDatWorkflowProcessor
{
    private readonly MessagePlaceholderConfig _config;

    public MessageDatWorkflowProcessor(MessagePlaceholderConfig config)
    {
        _config = config;
    }

    public void ApplyExportTransforms(MessageDatDocument document)
    {
        if (_config.AdjustBranchMessages)
        {
            AdjustBranchMessages(document);
        }
    }

    public void ApplyPreImportTransforms(MessageDatDocument document)
    {
        if (_config.AdjustBranchMessages)
        {
            AdjustBranchMessages(document);
        }
    }

    public void ApplyImportTransforms(MessageDatDocument document, Encoding writeEncoding)
    {
        if (_config.MsgLengthFix)
        {
            FixMessageLengths(document);
        }

        if (_config.MsgLengthCheck)
        {
            CheckMessageLengths(document);
        }

        if (_config.GbkCheck && writeEncoding.CodePage is 936 or 54936)
        {
            CheckEncoding(document, writeEncoding);
        }
    }

    private void AdjustBranchMessages(MessageDatDocument document)
    {
        var count = 0;
        for (var commandIndex = 0; commandIndex < document.Commands.Count; commandIndex++)
        {
            var command = document.Commands[commandIndex];
            if (command.Params.Count == 2)
            {
                count += SwapMessages(document, commandIndex, 0, 1);
            }
            else if (command.Params.Count > 2 && _config.AdjustMsgId < command.Params.Count)
            {
                count += SwapMessages(document, commandIndex, 0, _config.AdjustMsgId);
            }
        }

        Console.WriteLine($"AdjustBranchMessages: {count} swap(s).");
    }

    private int SwapMessages(MessageDatDocument document, int commandIndex, int leftParamIndex, int rightParamIndex)
    {
        var command = document.Commands[commandIndex];
        var left = command.Params[leftParamIndex];
        var right = command.Params[rightParamIndex];
        if (left < 0 || right < 0 || left >= document.Messages.Count || right >= document.Messages.Count)
        {
            return 0;
        }

        var beforeLeft = document.Messages[left].Text;
        var beforeRight = document.Messages[right].Text;
        (document.Messages[left].Text, document.Messages[right].Text) = (document.Messages[right].Text, document.Messages[left].Text);
        if (_config.AdjustMsgDetails)
        {
            Console.WriteLine($"AdjustBranchMessages detail: Command[{commandIndex:D4}] C{left:X8} <-> C{right:X8}");
            Console.WriteLine($"  before left : {EscapeForLog(beforeLeft)}");
            Console.WriteLine($"  before right: {EscapeForLog(beforeRight)}");
        }

        return 1;
    }

    private void FixMessageLengths(MessageDatDocument document)
    {
        var fixedCount = 0;
        var messageToName = BuildMessageToNameMap(document);
        for (var i = 0; i < document.Messages.Count; i++)
        {
            var message = document.Messages[i];
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            if (CountIssues(message.Text, silent: true) == 0)
            {
                continue;
            }

            message.Text = Wrap(message.Text, IsDialogueText(message.Text, messageToName.ContainsKey(i)));
            fixedCount++;
        }

        Console.WriteLine($"MsgLengthFix: {fixedCount} message(s) adjusted.");
    }

    private void CheckMessageLengths(MessageDatDocument document)
    {
        var warnings = 0;
        for (var i = 0; i < document.Messages.Count; i++)
        {
            warnings += CountIssues(document.Messages[i].Text, silent: false, $"C{i:X8}");
        }

        Console.WriteLine($"MsgLengthCheck: {warnings} warning(s).");
    }

    private void CheckEncoding(MessageDatDocument document, Encoding encoding)
    {
        var invalid = 0;
        for (var i = 0; i < document.Names.Count; i++)
        {
            invalid += ReportInvalidChars($"A{i:X8}", document.Names[i], encoding);
        }

        for (var i = 0; i < document.Choices.Count; i++)
        {
            invalid += ReportInvalidChars($"B{i:X8}", document.Choices[i], encoding);
        }

        for (var i = 0; i < document.Messages.Count; i++)
        {
            invalid += ReportInvalidChars($"C{i:X8}", document.Messages[i].Text, encoding);
        }

        Console.WriteLine($"GBKCheck: {invalid} incompatible character(s).");
    }

    private int CountIssues(string text, bool silent, string id = "")
    {
        var warnings = 0;
        if (!string.IsNullOrWhiteSpace(text) && !text.EndsWith('\n'))
        {
            warnings++;
            if (!silent) Console.WriteLine($"[STRUCTURE] {id} does not end with \\n.");
        }

        var lines = text.Split('\n');
        var contentLines = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        if (string.IsNullOrWhiteSpace(text))
        {
            contentLines = 0;
        }

        if (contentLines > 3)
        {
            warnings++;
            if (!silent) Console.WriteLine($"[STRUCTURE] {id} has {contentLines} lines.");
        }

        for (var i = 0; i < contentLines; i++)
        {
            var length = DisplayLength(lines[i]);
            if (length > _config.MsgLengthSet)
            {
                warnings++;
                if (!silent) Console.WriteLine($"[LENGTH] {id} line {i + 1}: {length}>{_config.MsgLengthSet}");
            }
        }

        return warnings;
    }

    private string Wrap(string text, bool padFullWidthSpaceBeforeNewline)
    {
        var continuous = text.Replace("\n", "", StringComparison.Ordinal);
        if (continuous.Length == 0)
        {
            return "\n";
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        var currentLength = 0;
        var lineLimit = padFullWidthSpaceBeforeNewline
            ? Math.Max(1, _config.MsgLengthSet - 1)
            : _config.MsgLengthSet;
        foreach (var segment in EnumerateDisplaySegments(continuous))
        {
            if (current.Length > 0 && currentLength + segment.DisplayLength > lineLimit)
            {
                lines.Add(FinishWrappedLine(current.ToString(), padFullWidthSpaceBeforeNewline));
                current.Clear();
                currentLength = 0;
            }

            current.Append(segment.Text);
            currentLength += segment.DisplayLength;
        }

        if (current.Length > 0)
        {
            lines.Add(FinishWrappedLine(current.ToString(), padFullWidthSpaceBeforeNewline));
        }

        return string.Join("\n", lines) + "\n";
    }

    private static string FinishWrappedLine(string line, bool padFullWidthSpaceBeforeNewline)
    {
        if (!padFullWidthSpaceBeforeNewline || line.EndsWith('　'))
        {
            return line;
        }

        return line + "　";
    }

    private static bool IsDialogueText(string text, bool hasSpeakerName)
    {
        if (!hasSpeakerName)
        {
            return false;
        }

        var trimmed = text.Replace("\n", "", StringComparison.Ordinal).Trim();
        return IsBracketed(trimmed, '（', '）') ||
               IsBracketed(trimmed, '『', '』') ||
               IsBracketed(trimmed, '「', '」');
    }

    private static bool IsBracketed(string text, char open, char close) =>
        text.Length >= 2 && text[0] == open && text[^1] == close;

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

    private int DisplayLength(string text) =>
        EnumerateDisplaySegments(text).Sum(segment => segment.DisplayLength);

    private IEnumerable<DisplaySegment> EnumerateDisplaySegments(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '[')
            {
                var matched = false;
                foreach (var pair in _config.PlaceholderDisplayLengths)
                {
                    if (i + pair.Key.Length <= text.Length && text.AsSpan(i, pair.Key.Length).SequenceEqual(pair.Key.AsSpan()))
                    {
                        yield return new DisplaySegment(pair.Key, pair.Value);
                        i += pair.Key.Length;
                        matched = true;
                        break;
                    }
                }

                if (matched)
                {
                    continue;
                }
            }

            if (char.IsSurrogatePair(text, i))
            {
                yield return new DisplaySegment(text.Substring(i, 2), 1);
                i += 2;
            }
            else
            {
                yield return new DisplaySegment(text[i].ToString(), 1);
                i++;
            }
        }
    }

    private static int ReportInvalidChars(string label, string text, Encoding encoding)
    {
        var clone = (Encoding)encoding.Clone();
        clone.EncoderFallback = EncoderFallback.ExceptionFallback;
        var count = 0;
        var line = 1;
        var column = 1;
        for (var i = 0; i < text.Length;)
        {
            var length = char.IsSurrogatePair(text, i) ? 2 : 1;
            var value = length == 2 ? char.ConvertToUtf32(text, i) : text[i];
            var token = text.Substring(i, length);
            try
            {
                clone.GetByteCount(token);
            }
            catch (EncoderFallbackException)
            {
                count++;
                Console.WriteLine($"[GBK] {label} line {line}, col {column}: U+{value:X4} \"{EscapeForLog(token)}\" context=\"{BuildContext(text, i, length)}\"");
            }

            if (token == "\n")
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }

            i += length;
        }

        return count;
    }

    private static string BuildContext(string text, int index, int length)
    {
        const int radius = 8;
        var start = Math.Max(0, index - radius);
        var end = Math.Min(text.Length, index + length + radius);
        return EscapeForLog(text[start..end]);
    }

    private static string EscapeForLog(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record DisplaySegment(string Text, int DisplayLength);
}
