using System.Buffers.Binary;
using System.Text;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Script.Params;

public sealed class ScrOpcodeScanner
{
    private readonly ScrContainerCodec _containerCodec = new();

    public ScrOpcodeScanSummary ScanPath(string path)
    {
        var files = File.Exists(path)
            ? [new FileInfo(path)]
            : Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFiles("*.scr", SearchOption.AllDirectories).OrderBy(x => x.FullName).ToArray()
                : throw new FileNotFoundException($"SCR scan target was not found: {path}");

        var summary = new ScrOpcodeScanSummary();
        foreach (var file in files)
        {
            summary.FilesScanned++;
            try
            {
                ScanFile(file.FullName, summary);
            }
            catch (Exception ex)
            {
                summary.Issues.Add(new ScrOpcodeScanIssue(file.FullName, null, "read_error", ex.Message));
            }
        }

        return summary;
    }

    public string Format(ScrOpcodeScanSummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"SCR opcode scan: files={summary.FilesScanned}, issues={summary.Issues.Count}");
        builder.AppendLine("Opcode usage:");
        foreach (var (opcode, count) in summary.OpcodeCounts)
        {
            var descriptor = ScrOpcodeInfo.Get(opcode);
            builder.AppendLine($"  {opcode}: {descriptor.Name} count={count}");
        }

        if (summary.ProgramSubOpcodeCounts.Count > 0)
        {
            var programDescriptor = ScrOpcodeInfo.Get(20);
            builder.AppendLine("Program sub-opcode usage:");
            foreach (var (programId, count) in summary.ProgramSubOpcodeCounts)
            {
                var subOpcode = programDescriptor.SubOpcodes.FirstOrDefault(x => x.Value == programId);
                var name = subOpcode is null ? "unknown" : subOpcode.Name;
                builder.AppendLine($"  {programId}: program.{name} count={count}");
            }
        }

        if (summary.Issues.Count > 0)
        {
            builder.AppendLine("Issues:");
        }

        foreach (var issue in summary.Issues)
        {
            builder.Append(issue.FilePath);
            if (issue.Offset is { } offset)
            {
                builder.Append($" @0x{offset:X8}");
            }

            builder.Append($" [{issue.Kind}] ");
            builder.AppendLine(issue.Message);
        }

        return builder.ToString();
    }

    private void ScanFile(string filePath, ScrOpcodeScanSummary summary)
    {
        var bytes = File.ReadAllBytes(filePath);
        var document = _containerCodec.Read(bytes, Path.GetFileName(filePath));
        var codeStart = Encoding.ASCII.GetByteCount(document.Header) + 4;

        var bytecodeOffset = 0;
        foreach (var element in document.Script.Elements)
        {
            if (element is ScriptTail tail)
            {
                summary.Issues.Add(new ScrOpcodeScanIssue(
                    filePath,
                    bytecodeOffset,
                    "bytecode_tail",
                    $"Unparsed bytecode tail length={tail.Data.Length}."));
                bytecodeOffset += tail.Data.Length;
                continue;
            }

            if (element is not ScriptInstruction instruction)
            {
                continue;
            }

            ScanInstruction(filePath, instruction, bytecodeOffset, codeStart, summary);
            bytecodeOffset += instruction.DeclaredLength;
        }
    }

    private static void ScanInstruction(string filePath, ScriptInstruction instruction, int fallbackOffset, int codeStart, ScrOpcodeScanSummary summary)
    {
        var offset = OffsetOf(instruction, fallbackOffset);
        var descriptor = ScrOpcodeInfo.Get(instruction.Opcode);
        summary.OpcodeCounts[instruction.Opcode] = summary.OpcodeCounts.GetValueOrDefault(instruction.Opcode) + 1;
        if (!descriptor.IsKnown)
        {
            summary.Issues.Add(new ScrOpcodeScanIssue(
                filePath,
                offset,
                "unknown_opcode",
                $"opcode={instruction.Opcode}, bodyLength={instruction.Body.Length}."));
            return;
        }

        if (!IsBodyLengthValid(descriptor, instruction.Body))
        {
            summary.Issues.Add(new ScrOpcodeScanIssue(
                filePath,
                offset,
                "invalid_length",
                $"opcode={instruction.Opcode} ({descriptor.Name}), bodyLength={instruction.Body.Length}, expected={descriptor.LengthRule}."));
        }

        if (instruction.Opcode == 20 && instruction.Body.Length == 3)
        {
            var programId = ReadU16(instruction.Body, 1);
            summary.ProgramSubOpcodeCounts[programId] = summary.ProgramSubOpcodeCounts.GetValueOrDefault(programId) + 1;
            if (descriptor.SubOpcodes.All(x => x.Value != programId))
            {
                summary.Issues.Add(new ScrOpcodeScanIssue(
                    filePath,
                    offset,
                    "unknown_sub_opcode",
                    $"opcode=20 programId={programId}."));
            }
        }

        if (ScrOpcodeInfo.TryGetPcTargetOffset(instruction.Opcode, instruction.Body.Length, out var operandOffset))
        {
            var fileTarget = BinaryPrimitives.ReadUInt32LittleEndian(instruction.Body.AsSpan(operandOffset, 4));
            if (instruction.TargetLabel is null)
            {
                var relative = fileTarget >= codeStart && fileTarget <= int.MaxValue
                    ? ((int)fileTarget - codeStart).ToString("X8")
                    : "out-of-code";
                summary.Issues.Add(new ScrOpcodeScanIssue(
                    filePath,
                    offset,
                    "unresolved_pc_target",
                    $"opcode={instruction.Opcode} rawFileOffset=0x{fileTarget:X8}, relative={relative}."));
            }
        }
    }

    private static bool IsBodyLengthValid(ScrOpcodeDescriptor descriptor, byte[] body)
    {
        return descriptor.LengthKind switch
        {
            ScrLengthKind.Fixed => body.Length == (descriptor.ExpectedBodyLength ?? 0),
            ScrLengthKind.CountedString => body.Length >= 1 && body.Length == 1 + body[0],
            ScrLengthKind.CountedI32Array => body.Length >= 1 && body.Length == 1 + body[0] * 4,
            ScrLengthKind.Variable => HasMinimumOperands(descriptor, body),
            _ => false
        };
    }

    private static bool HasMinimumOperands(ScrOpcodeDescriptor descriptor, byte[] body)
    {
        var minimumLength = 0;
        foreach (var operand in descriptor.Operands)
        {
            var operandEnd = operand.Size is { } size
                ? operand.Offset + size
                : operand.Offset;
            minimumLength = Math.Max(minimumLength, operandEnd);
        }

        return body.Length >= minimumLength;
    }

    private static int OffsetOf(ScriptInstruction instruction, int fallbackOffset) =>
        instruction.Metadata.TryGetValue("offset", out var value) && value is int offset ? offset : fallbackOffset;

    private static ushort ReadU16(byte[] body, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2));
}

public sealed class ScrOpcodeScanSummary
{
    public int FilesScanned { get; set; }
    public SortedDictionary<ushort, int> OpcodeCounts { get; } = [];
    public SortedDictionary<ushort, int> ProgramSubOpcodeCounts { get; } = [];
    public List<ScrOpcodeScanIssue> Issues { get; } = [];
}

public sealed record ScrOpcodeScanIssue(string FilePath, int? Offset, string Kind, string Message);
