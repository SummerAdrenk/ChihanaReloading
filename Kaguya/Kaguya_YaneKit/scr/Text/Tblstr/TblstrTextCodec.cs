using System.Text.RegularExpressions;

namespace Kaguya_YaneKit.Text.Tblstr;

public sealed class TblstrTextCodec
{
    private static readonly Regex ImportLineRegex = new(
        "^◆T([a-fA-F0-9]{8})◆(?:(name|msg|choice|alt-msg|unknown)◆)?(.*)$",
        RegexOptions.Compiled);

    public int Apply(TblstrDocument document, string text)
    {
        var applied = 0;
        foreach (var line in ReadLogicalLines(text))
        {
            var match = ImportLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var index = Convert.ToInt32(match.Groups[1].Value, 16);
            if (index < 0 || index >= document.Entries.Count)
            {
                continue;
            }

            document.Entries[index].Text = Unescape(match.Groups[3].Value);
            applied++;
        }

        return applied;
    }

    public TblstrMergeResult Merge(string baseTextPath, string splitDirectory, string outputTextPath)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = 0;
        foreach (var file in Directory.EnumerateFiles(splitDirectory, "*.txt", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(file).StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                var key = TranslationKey(line);
                if (key is null)
                {
                    continue;
                }

                if (replacements.TryGetValue(key, out var existing) && existing != line)
                {
                    conflicts++;
                }

                replacements[key] = line;
            }
        }

        var replaced = 0;
        var missing = replacements.Keys.ToHashSet(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var line in File.ReadLines(baseTextPath))
        {
            var key = TranslationKey(line);
            if (key is not null && replacements.TryGetValue(key, out var replacement))
            {
                output.Add(replacement);
                replaced++;
                missing.Remove(key);
            }
            else
            {
                output.Add(line);
            }
        }

        File.WriteAllLines(outputTextPath, output);
        return new TblstrMergeResult(replacements.Count, replaced, missing.Count, conflicts);
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

    private static string? TranslationKey(string line)
    {
        if (!line.StartsWith('◆'))
        {
            return null;
        }

        var second = line.IndexOf('◆', 1);
        if (second < 0)
        {
            return null;
        }

        var key = line[..(second + 1)];
        return key.Length == 11 && key.StartsWith("◆T", StringComparison.Ordinal) ? key : null;
    }

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n", StringComparison.Ordinal);
}

public sealed record TblstrMergeResult(int Collected, int Replaced, int MissingInBase, int Conflicts);
