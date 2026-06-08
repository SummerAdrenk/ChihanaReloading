using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kaguya_YaneKit.Formats.Pe;

public sealed class PeStringTableTool
{
    private const uint ReadOnlyDataSection = 0x40000040;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PeStringDumpDocument Dump(string exePath, PeStringDumpOptions options)
    {
        var data = File.ReadAllBytes(exePath);
        var image = PeImage.Read(data);
        var encoding = PeFormatModule.ResolveEncoding(options.EncodingName);
        var entries = ScanStrings(data, image, encoding, options).ToList();
        AttachReferences(data, image, entries);
        entries = entries
            .Where(entry => options.IncludeUnreferenced || entry.Refs.Count > 0)
            .Where(entry => options.IncludeDiagnostics || !IsDiagnosticString(entry.Original))
            .ToList();

        return new PeStringDumpDocument
        {
            SourcePath = exePath,
            ImageBase = Hex(image.ImageBase),
            EncodingName = options.EncodingName ?? "cp932",
            MinBytes = options.MinBytes,
            Sections = image.Sections
                .Select(section => new PeSectionInfo
                {
                    Name = section.Name,
                    Rva = Hex(section.Rva),
                    VirtualSize = Hex(section.VirtualSize),
                    RawOffset = Hex(section.RawOffset),
                    RawSize = Hex(section.RawSize),
                    Characteristics = Hex(section.Characteristics)
                })
                .ToList(),
            Entries = entries
        };
    }

    public PeStringImportResult Import(string exePath, string jsonPath, string outputPath, PeStringImportOptions options)
    {
        if (string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Output EXE must be different from input EXE.");
        }

        var document = ReadDocument(jsonPath);
        var encoding = PeFormatModule.ResolveEncoding(options.EncodingName ?? document.EncodingName);
        var data = File.ReadAllBytes(exePath);
        var image = PeImage.Read(data);
        var changed = document.Entries
            .Where(entry => options.AllowEmptyTranslation
                ? entry.Translated is not null
                : !string.IsNullOrEmpty(entry.Translated))
            .Where(entry => entry.Translated != entry.Original)
            .ToList();

        if (changed.Count == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            File.WriteAllBytes(outputPath, data);
            return new PeStringImportResult(0, 0, 0, 0, 0, 0);
        }

        var blob = new List<byte>();
        var newVaById = new Dictionary<string, uint>(StringComparer.Ordinal);
        var inPlace = 0;
        var moved = 0;
        foreach (var entry in changed)
        {
            var translatedBytes = encoding.GetBytes(entry.Translated);
            if (translatedBytes.Length <= entry.ByteLength)
            {
                PatchOriginalString(image.GetData(), entry, translatedBytes);
                inPlace++;
                continue;
            }

            if (entry.Refs.Count == 0)
            {
                throw new InvalidDataException($"Edited entry is longer than original and has no recorded references: {entry.Id}");
            }

            var blobOffset = blob.Count;
            blob.AddRange(translatedBytes);
            blob.Add(0);
            newVaById[entry.Id] = checked((uint)blobOffset);
            PatchOriginalString(image.GetData(), entry, []);
            moved++;
        }

        var rawOffset = 0;
        if (blob.Count > 0)
        {
            rawOffset = image.AddSection(blob.ToArray(), options.SectionName, ReadOnlyDataSection, out var sectionRva);
            foreach (var id in newVaById.Keys.ToList())
            {
                newVaById[id] = image.RvaToVa(sectionRva + newVaById[id]);
            }
        }

        var patchedRefs = 0;
        var patchedLengths = 0;
        foreach (var entry in changed)
        {
            var newBytes = encoding.GetBytes(entry.Translated);
            var hasMoved = newVaById.TryGetValue(entry.Id, out var newVa);
            foreach (var reference in entry.Refs)
            {
                if (hasMoved)
                {
                    var refOffset = ParseInt(reference.FileOffset);
                    if (refOffset < 0 || refOffset + 4 > image.GetData().Length)
                    {
                        throw new InvalidDataException($"Reference offset out of range: {entry.Id} {reference.FileOffset}");
                    }

                    PeImage.WriteU32(image.GetData(), refOffset, newVa);
                    patchedRefs++;
                }

                if (reference.LengthPatch is null)
                {
                    continue;
                }

                if (newBytes.Length > byte.MaxValue)
                {
                    throw new InvalidDataException($"Translated string is too long for push imm8 length patch: {entry.Id}");
                }

                var lengthOffset = ParseInt(reference.LengthPatch.FileOffset);
                if (lengthOffset < 0 || lengthOffset >= image.GetData().Length)
                {
                    throw new InvalidDataException($"Length patch offset out of range: {entry.Id} {reference.LengthPatch.FileOffset}");
                }

                image.GetData()[lengthOffset] = (byte)newBytes.Length;
                patchedLengths++;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        File.WriteAllBytes(outputPath, image.GetData());
        return new PeStringImportResult(changed.Count, patchedRefs, patchedLengths, rawOffset, inPlace, moved);
    }

    public void WriteDocument(string path, PeStringDumpDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions), Encoding.UTF8);
    }

    public PeStringDumpDocument ReadDocument(string path) =>
        JsonSerializer.Deserialize<PeStringDumpDocument>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
        ?? throw new InvalidDataException("Invalid PE string JSON.");

    private static IEnumerable<PeStringEntry> ScanStrings(byte[] data, PeImage image, Encoding encoding, PeStringDumpOptions options)
    {
        var selectedSections = options.Sections.Count == 0
            ? null
            : new HashSet<string>(options.Sections, StringComparer.OrdinalIgnoreCase);

        foreach (var section in image.Sections)
        {
            if (!ShouldScanSection(section, selectedSections, options.IncludeText))
            {
                continue;
            }

            var start = checked((int)section.RawOffset);
            var end = checked((int)Math.Min(data.Length, section.RawOffset + section.RawSize));
            var offset = start;
            while (offset < end)
            {
                if (data[offset] == 0)
                {
                    offset++;
                    continue;
                }

                var terminator = offset;
                while (terminator < end && data[terminator] != 0)
                {
                    terminator++;
                }

                var length = terminator - offset;
                if (length >= options.MinBytes &&
                    TryDecode(data, offset, length, encoding, out var text) &&
                    IsUsefulString(text, options.IncludeAsciiOnly))
                {
                    var rva = section.Rva + checked((uint)(offset - section.RawOffset));
                    var va = image.RvaToVa(rva);
                    var raw = new byte[length];
                    Buffer.BlockCopy(data, offset, raw, 0, length);
                    yield return new PeStringEntry
                    {
                        Id = $"S{rva:X8}",
                        Section = section.Name,
                        Rva = Hex(rva),
                        Va = Hex(va),
                        FileOffset = Hex(offset),
                        ByteLength = length,
                        RawHex = Convert.ToHexString(raw),
                        Original = text,
                        Translated = "",
                        Status = "confirmed"
                    };
                }

                offset = terminator + 1;
            }
        }
    }

    private static void AttachReferences(byte[] data, PeImage image, List<PeStringEntry> entries)
    {
        var byVa = entries.ToDictionary(entry => ParseUInt(entry.Va), entry => entry);
        for (var offset = 0; offset <= data.Length - 4; offset++)
        {
            var value = PeImage.ReadU32(data, offset);
            if (!byVa.TryGetValue(value, out var entry))
            {
                continue;
            }

            if (!image.TryFileOffsetToRva(offset, out var rva, out var section))
            {
                continue;
            }

            var reference = new PeStringReference
            {
                Section = section.Name,
                Rva = Hex(rva),
                Va = Hex(image.RvaToVa(rva)),
                FileOffset = Hex(offset),
                Kind = "absolute_va",
                LengthPatch = TryFindPushImm8LengthPatch(data, image, offset, entry.ByteLength)
            };
            entry.Refs.Add(reference);
        }

        foreach (var entry in entries)
        {
            entry.NeedsLengthPatch = entry.Refs.Any(reference => reference.LengthPatch is not null);
            if (entry.Refs.Count == 0)
            {
                entry.Status = "unreferenced";
            }
        }
    }

    private static PeLengthPatch? TryFindPushImm8LengthPatch(byte[] data, PeImage image, int refOffset, int originalLength)
    {
        // x86 common shape: 6A len ; 68 string_va
        if (refOffset < 3 ||
            data[refOffset - 1] != 0x68 ||
            data[refOffset - 3] != 0x6A ||
            data[refOffset - 2] != (byte)originalLength)
        {
            return null;
        }

        var lengthOffset = refOffset - 2;
        if (!image.TryFileOffsetToRva(lengthOffset, out var rva, out _))
        {
            return null;
        }

        return new PeLengthPatch
        {
            FileOffset = Hex(lengthOffset),
            Rva = Hex(rva),
            Va = Hex(image.RvaToVa(rva)),
            OriginalLength = originalLength
        };
    }

    private static bool ShouldScanSection(PeSection section, HashSet<string>? selectedSections, bool includeText)
    {
        if (selectedSections is not null)
        {
            return selectedSections.Contains(section.Name);
        }

        if (section.RawSize == 0 || !section.IsReadable)
        {
            return false;
        }

        return includeText || !section.IsExecutable;
    }

    private static bool TryDecode(byte[] data, int offset, int length, Encoding encoding, out string text)
    {
        try
        {
            text = encoding.GetString(data, offset, length);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    private static void PatchOriginalString(byte[] data, PeStringEntry entry, byte[] translatedBytes)
    {
        var offset = ParseInt(entry.FileOffset);
        if (offset < 0 || offset + entry.ByteLength > data.Length)
        {
            throw new InvalidDataException($"String offset out of range: {entry.Id} {entry.FileOffset}");
        }

        Buffer.BlockCopy(translatedBytes, 0, data, offset, translatedBytes.Length);
        Array.Clear(data, offset + translatedBytes.Length, entry.ByteLength - translatedBytes.Length);
    }

    private static bool IsUsefulString(string text, bool includeAsciiOnly)
    {
        if (text.Length == 0)
        {
            return false;
        }

        if (text.Any(IsPrivateUseOrInvalid))
        {
            return false;
        }

        if (LooksLikeBinaryMojibake(text))
        {
            return false;
        }

        if (text.Any(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t'))
        {
            return false;
        }

        var printableCount = text.Count(ch => !char.IsControl(ch) || ch is '\r' or '\n' or '\t');
        if (printableCount * 100 / text.Length < 95)
        {
            return false;
        }

        return includeAsciiOnly || text.Any(IsJapaneseLike);
    }

    private static bool IsJapaneseLike(char ch) =>
        ch is >= '\u3040' and <= '\u30FF' ||
        ch is >= '\u3400' and <= '\u9FFF' ||
        ch is >= '\uFF00' and <= '\uFFEF';

    private static bool HasKanaOrFullWidth(char ch) =>
        ch is >= '\u3040' and <= '\u30FF' ||
        ch is >= '\uFF00' and <= '\uFFEF';

    private static bool IsHalfWidthKana(char ch) =>
        ch is >= '\uFF61' and <= '\uFF9F';

    private static bool LooksLikeBinaryMojibake(string text)
    {
        if (text.Any(ch => ch is >= '\u0370' and <= '\u03FF'))
        {
            return true;
        }

        if (text.IndexOfAny(['?', '\'', '&', '<', '>']) >= 0)
        {
            return true;
        }

        if (text.Contains("Xiph.Org", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hasHalfWidthKana = text.Any(IsHalfWidthKana);
        var hasAsciiLetterOrDigit = text.Any(ch => ch < 0x80 && char.IsLetterOrDigit(ch));
        var hasPathOrReadableSeparator = text.IndexOfAny(['/', '\\', '.', ' ', '　']) >= 0;
        if (hasHalfWidthKana && hasAsciiLetterOrDigit && !hasPathOrReadableSeparator)
        {
            return true;
        }

        var asciiCount = text.Count(ch => ch < 0x80);
        var japaneseCount = text.Count(IsJapaneseLike);
        return asciiCount > japaneseCount * 2 && hasHalfWidthKana;
    }

    private static bool IsPrivateUseOrInvalid(char ch) =>
        ch is >= '\uE000' and <= '\uF8FF' ||
        ch == '\uFFFD';

    private static bool IsDiagnosticString(string text)
    {
        if (text.Contains("::", StringComparison.Ordinal) ||
            text.Contains("[%s]", StringComparison.Ordinal) ||
            text.Contains("%s", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Any(ch => ch < 0x80) && !text.Any(HasKanaOrFullWidth))
        {
            return true;
        }

        var markers = new[]
        {
            "エラー",
            "失敗",
            "無効",
            "間違",
            "見つかりません",
            "ありません",
            "できません",
            "できていません",
            "読み込めません",
            "未対応",
            "未実装",
            "不正",
            "大きすぎ",
            "原因になります",
            "読み込みに失敗",
            "作成に失敗",
            "保存に失敗",
            "ロードに失敗",
            "オープンできません",
            "使用不可",
            "指定しています",
            "指定している",
            "指定を読み",
            "存在しません",
            "番号が",
            "コマンド",
            "フォーマット",
            "アーカイブ",
            "ファイル",
            "レジストリー",
            "ビットストリーム",
            "バッファ",
            "ドライバー",
            "インデックス",
            "バージョン",
            "データではありません",
            "登録されています",
            "登録されていません",
            "登録外",
            "構築されていません",
            "作成されていません",
            "取得",
            "参照",
            "対応していません",
            "必要です",
            "文字以上",
            "一致しない",
            "キャンセルします",
            "より小さく",
            "してください",
            "既に",
            "起動中",
            "セットアップ",
            "ディスク",
            "ＤＶＤ",
            "挿入",
            "新規では作らない"
        };

        return markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    private static string Hex(int value) => $"0x{value:X8}";
    private static string Hex(uint value) => $"0x{value:X8}";

    private static uint ParseUInt(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(value[2..], 16)
            : Convert.ToUInt32(value);

    private static int ParseInt(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(value[2..], 16)
            : Convert.ToInt32(value);
}

public sealed class PeStringDumpOptions
{
    public string? EncodingName { get; init; }
    public int MinBytes { get; init; } = 4;
    public bool IncludeText { get; init; }
    public bool IncludeAsciiOnly { get; init; }
    public bool IncludeUnreferenced { get; init; }
    public bool IncludeDiagnostics { get; init; }
    public List<string> Sections { get; init; } = [];
}

public sealed class PeStringImportOptions
{
    public string? EncodingName { get; init; }
    public string SectionName { get; init; } = ".yktxt";
    public bool AllowEmptyTranslation { get; init; }
}

public sealed record PeStringImportResult(
    int ChangedEntries,
    int PatchedReferences,
    int PatchedLengths,
    int NewSectionRawOffset,
    int InPlaceEntries,
    int MovedEntries);
