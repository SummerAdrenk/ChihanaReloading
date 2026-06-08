using System.Text;
using System.Text.RegularExpressions;
using Kaguya_YaneKit.Text.MessageDat;

namespace Kaguya_YaneKit.Text.Tblstr;

public sealed class TblstrTextWorkflowProcessor
{
    private static readonly Regex TranslationLineRegex = new(
        "^◆T([a-fA-F0-9]{8})◆(?:(name|msg|choice|alt-msg|unknown)◆)?(.*)$",
        RegexOptions.Compiled);

    private readonly MessagePlaceholderConfig _config;

    public TblstrTextWorkflowProcessor(MessagePlaceholderConfig config)
    {
        _config = config;
    }

    public string ApplyPreImportTransforms(string text, Encoding writeEncoding)
    {
        var lines = new List<string>();
        var fixedCount = 0;
        var lengthWarnings = 0;
        var invalidChars = 0;
        var pendingSpeakerName = false;
        foreach (var line in ReadLogicalLines(text))
        {
            var match = TranslationLineRegex.Match(line);
            if (!match.Success)
            {
                lines.Add(line);
                continue;
            }

            var id = $"T{match.Groups[1].Value.ToUpperInvariant()}";
            var tag = match.Groups[2].Value;
            var content = Unescape(match.Groups[3].Value);
            if (tag == "msg")
            {
                var isDialogue = pendingSpeakerName && IsDialogueText(content);
                if (_config.MsgLengthFix && CountLengthIssues(content, silent: true) > 0)
                {
                    content = Wrap(content, isDialogue);
                    fixedCount++;
                }

                if (_config.MsgLengthCheck)
                {
                    lengthWarnings += CountLengthIssues(content, silent: false, id);
                }

                pendingSpeakerName = false;
            }
            else if (tag == "name")
            {
                pendingSpeakerName = !string.IsNullOrWhiteSpace(content);
            }
            else if (tag is "choice" or "unknown")
            {
                pendingSpeakerName = false;
            }

            if (_config.GbkCheck && writeEncoding.CodePage is 936 or 54936)
            {
                invalidChars += ReportInvalidChars(id, content, writeEncoding);
            }

            lines.Add(line[..match.Groups[3].Index] + Escape(content));
        }

        if (_config.MsgLengthFix)
        {
            Console.WriteLine($"TBLSTR MsgLengthFix: {fixedCount} message(s) adjusted.");
        }

        if (_config.MsgLengthCheck)
        {
            Console.WriteLine($"TBLSTR MsgLengthCheck: {lengthWarnings} warning(s).");
        }

        if (_config.GbkCheck && writeEncoding.CodePage is 936 or 54936)
        {
            Console.WriteLine($"TBLSTR GBKCheck: {invalidChars} incompatible character(s).");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private int CountLengthIssues(string text, bool silent, string id = "")
    {
        var warnings = 0;
        if (!string.IsNullOrWhiteSpace(text) && !text.EndsWith('\n'))
        {
            warnings++;
            if (!silent) Console.WriteLine($"[TBLSTR STRUCTURE] {id} does not end with \\n.");
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
            if (!silent) Console.WriteLine($"[TBLSTR STRUCTURE] {id} has {contentLines} lines.");
        }

        for (var i = 0; i < contentLines; i++)
        {
            var length = DisplayLength(lines[i]);
            if (length > _config.MsgLengthSet)
            {
                warnings++;
                if (!silent) Console.WriteLine($"[TBLSTR LENGTH] {id} line {i + 1}: {length}>{_config.MsgLengthSet}");
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

    private static bool IsDialogueText(string text)
    {
        var trimmed = text.Replace("\n", "", StringComparison.Ordinal).Trim();
        return IsBracketed(trimmed, '（', '）') ||
               IsBracketed(trimmed, '『', '』') ||
               IsBracketed(trimmed, '「', '」');
    }

    private static bool IsBracketed(string text, char open, char close) =>
        text.Length >= 2 && text[0] == open && text[^1] == close;

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
                Console.WriteLine($"[TBLSTR GBK] {label} line {line}, col {column}: U+{value:X4} \"{EscapeForLog(token)}\" context=\"{BuildContext(text, i, length)}\"");
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

    private static IEnumerable<string> ReadLogicalLines(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
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

    private static string Escape(string value) =>
        value.Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n", StringComparison.Ordinal);

    private sealed record DisplaySegment(string Text, int DisplayLength);
}
