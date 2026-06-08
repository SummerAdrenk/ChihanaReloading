using System.Buffers.Binary;
using System.Text;
using Kaguya_YaneKit.Text.Tblstr;

namespace Kaguya_YaneKit.Script.Tblstr;

public sealed class TblstrScrCodec
{
    public const uint Magic0 = 0x0A0D0C01;
    public const uint Magic1 = 0x05033B0C;

    private readonly Encoding _encoding;

    public TblstrScrCodec(string? encodingName = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _encoding = string.IsNullOrWhiteSpace(encodingName)
            ? Encoding.GetEncoding(932)
            : Encoding.GetEncoding(encodingName);
    }

    public static bool IsTblstrScr(ReadOnlySpan<byte> source)
    {
        return source.Length >= 12
            && BinaryPrimitives.ReadUInt32LittleEndian(source[..4]) == Magic0
            && BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4)) == Magic1;
    }

    public TblstrScrDocument Read(ReadOnlySpan<byte> source, string? sourceName = null, IEnumerable<TblstrLabelEntry>? labels = null)
    {
        if (source.Length < 12)
        {
            throw new InvalidDataException("TBLSTR SCR file is too small.");
        }

        var magic0 = BinaryPrimitives.ReadUInt32LittleEndian(source[..4]);
        var magic1 = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4));
        if (magic0 != Magic0 || magic1 != Magic1)
        {
            throw new InvalidDataException($"Unsupported TBLSTR SCR magic: 0x{magic0:X8} 0x{magic1:X8}");
        }

        var payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, 4)));
        EnsureAvailable(source, 12, payloadSize, "script payload");
        var payload = source.Slice(12, payloadSize).ToArray();

        var document = new TblstrScrDocument
        {
            SourceName = sourceName ?? "",
            Magic0 = magic0,
            Magic1 = magic1,
            PayloadSize = payloadSize,
            Payload = payload,
            LabelsByOffset = BuildLabelMap(sourceName, labels)
        };

        DecodeInstructions(document);
        return document;
    }

    public static byte[] WriteRaw(TblstrScrDocument document)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], document.Magic0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), document.Magic1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), checked((uint)document.Payload.Length));
        stream.Write(header);
        stream.Write(document.Payload);
        return stream.ToArray();
    }

    public static IReadOnlyList<TblstrLabelEntry> ReadLabelTable(string labelTblPath)
    {
        var codec = new TblSupportCodec();
        var document = codec.Read(Path.GetFileName(labelTblPath), File.ReadAllBytes(labelTblPath));
        if (document.LabelTable is null)
        {
            return [];
        }

        return document.LabelTable.Entries
            .Select(entry => new TblstrLabelEntry(entry.ScriptFile, entry.Label, entry.TargetOffset))
            .ToArray();
    }

    public static IReadOnlyList<TblstrLabelEntry> TryReadSiblingLabels(string scrPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(scrPath));
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [];
        }

        var candidates = new[]
        {
            Path.Combine(directory, "label.tbl"),
            Path.Combine(directory, "Label.tbl")
        };

        var path = candidates.FirstOrDefault(File.Exists);
        return path is null ? [] : ReadLabelTable(path);
    }

    public TblstrScrScanSummary ScanPath(string path)
    {
        var summary = new TblstrScrScanSummary();
        foreach (var file in EnumerateScrFiles(path))
        {
            summary.FileCount++;
            try
            {
                var labels = TryReadSiblingLabels(file);
                var document = Read(File.ReadAllBytes(file), Path.GetFileName(file), labels);
                foreach (var instruction in document.Instructions)
                {
                    summary.AddInstruction(instruction);
                }
            }
            catch (Exception ex)
            {
                summary.Issues.Add($"{file}: {ex.Message}");
            }
        }

        return summary;
    }

    private void DecodeInstructions(TblstrScrDocument document)
    {
        var payload = document.Payload.AsSpan();
        var pc = 0;
        while (pc < payload.Length)
        {
            EnsureAvailable(payload, pc, 2, "instruction header");
            var opcode = payload[pc];
            var baseLength = payload[pc + 1];
            if (baseLength < 2)
            {
                throw new InvalidDataException($"Invalid base length {baseLength} at 0x{pc:X8}.");
            }

            EnsureAvailable(payload, pc, baseLength, $"instruction 0x{pc:X8}");
            var baseSpan = payload.Slice(pc, baseLength);
            var descriptor = TblstrScrOpcodeTable.Get(opcode);
            if (descriptor == TblstrScrOpcodeTable.Unknown)
            {
                throw new InvalidDataException($"Unknown opcode {opcode} at 0x{pc:X8}.");
            }

            var strings = DecodeInlineStrings(payload, pc, baseSpan, opcode, baseLength, out var extraLength);
            var totalLength = checked(baseLength + extraLength);
            EnsureAvailable(payload, pc, totalLength, $"instruction extra data 0x{pc:X8}");

            document.Instructions.Add(new TblstrScrInstruction
            {
                Offset = pc,
                Opcode = opcode,
                BaseLength = baseLength,
                ExtraLength = extraLength,
                RawBytes = payload.Slice(pc, totalLength).ToArray(),
                BaseOperandBytes = baseLength > 2 ? payload.Slice(pc + 2, baseLength - 2).ToArray() : [],
                Strings = strings,
                Descriptor = descriptor
            });

            pc += totalLength;
        }
    }

    private List<TblstrScrStringImmediate> DecodeInlineStrings(
        ReadOnlySpan<byte> payload,
        int instructionOffset,
        ReadOnlySpan<byte> baseInstruction,
        int opcode,
        int baseLength,
        out int extraLength)
    {
        var strings = new List<TblstrScrStringImmediate>();
        var cursor = instructionOffset + baseLength;
        foreach (var lengthOffset in TblstrScrOpcodeTable.GetInlineStringLengthOffsets(opcode, baseInstruction))
        {
            if (lengthOffset < 0 || lengthOffset >= baseInstruction.Length)
            {
                throw new InvalidDataException($"String length slot inst[{lengthOffset}] is outside opcode {opcode} at 0x{instructionOffset:X8}.");
            }

            var declaredLength = baseInstruction[lengthOffset];
            if (TblstrScrOpcodeTable.ShouldSkipString(opcode, lengthOffset, declaredLength, baseInstruction))
            {
                continue;
            }

            EnsureAvailable(payload, cursor, declaredLength, $"inline string at 0x{instructionOffset:X8}");
            var encoded = payload.Slice(cursor, declaredLength).ToArray();
            strings.Add(new TblstrScrStringImmediate
            {
                Name = TblstrScrOpcodeTable.GetStringName(opcode, lengthOffset),
                LengthOffset = lengthOffset,
                DeclaredLength = declaredLength,
                DataOffset = cursor - instructionOffset,
                EncodedBytes = encoded,
                Text = TblstrScrText.DecodeInlineString(encoded, _encoding)
            });
            cursor += declaredLength;
        }

        extraLength = cursor - instructionOffset - baseLength;
        return strings;
    }

    private static Dictionary<int, List<string>> BuildLabelMap(string? sourceName, IEnumerable<TblstrLabelEntry>? labels)
    {
        var result = new Dictionary<int, List<string>>();
        if (string.IsNullOrWhiteSpace(sourceName) || labels is null)
        {
            return result;
        }

        var sourceBaseName = Path.GetFileNameWithoutExtension(sourceName);
        foreach (var label in labels)
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(label.ScriptName), sourceBaseName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!result.TryGetValue(label.TargetOffset, out var bucket))
            {
                bucket = [];
                result[label.TargetOffset] = bucket;
            }

            bucket.Add(label.Label);
        }

        return result;
    }

    private static IEnumerable<string> EnumerateScrFiles(string path)
    {
        if (File.Exists(path))
        {
            yield return path;
            yield break;
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        foreach (var file in Directory.EnumerateFiles(path, "*.scr", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            yield return file;
        }
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> source, int offset, int count, string name)
    {
        if (count < 0 || offset < 0 || source.Length - offset < count)
        {
            throw new EndOfStreamException($"Unexpected EOF while reading {name}.");
        }
    }
}
