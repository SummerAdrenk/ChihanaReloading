using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kaguya_YaneKit.Formats.Archive;

public sealed class Af01ArchiveCodec
{
    public const string ManifestFileName = "_archive_manifest.json";
    public static int DefaultExtractParallelism { get; } = ReadMaxDegreeOfParallelism(
        "KAGUYA_ARCHIVE_PARALLELISM",
        Environment.ProcessorCount * 2);

    private const int MaxNameBytes = 0x400;
    private static readonly Encoding ShiftJis = CreateShiftJisEncoding();

    private static Encoding CreateShiftJisEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    private static int ReadMaxDegreeOfParallelism(string variableName, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return Math.Clamp(parsed, 1, 64);
        }

        return Math.Clamp(defaultValue, 1, 32);
    }

    public Af01ArchiveManifest ReadManifest(Stream stream)
    {
        stream.Position = 0;
        var magic = ReadAscii(stream, 4);
        if (magic != "AF01")
        {
            throw new InvalidDataException($"Unsupported AF archive magic: {magic}");
        }

        var version = ReadU32(stream);
        var indexBaseOffset = ReadU32(stream);
        var indexOffset = checked((long)indexBaseOffset + 8);
        if (indexOffset < 12 || indexOffset > stream.Length)
        {
            throw new InvalidDataException($"Invalid AF01 index offset: 0x{indexBaseOffset:X8}");
        }

        var manifest = new Af01ArchiveManifest
        {
            Header = new Af01ArchiveHeader
            {
                Magic = magic,
                Version = version,
                IndexBaseOffset = indexBaseOffset,
                IndexOffset = indexOffset
            }
        };

        stream.Position = indexOffset;
        long dataOffset = 12;
        while (stream.Position < stream.Length)
        {
            var indexEntryOffset = stream.Position;
            var remaining = stream.Length - stream.Position;
            if (remaining == 0)
            {
                break;
            }

            if (remaining < 4)
            {
                throw new EndOfStreamException($"AF01 index entry is truncated at 0x{indexEntryOffset:X}.");
            }

            var nameLength = ReadI32(stream);
            if (nameLength <= 0 || nameLength > MaxNameBytes)
            {
                throw new InvalidDataException($"Invalid AF01 name length at 0x{indexEntryOffset:X}: {nameLength}");
            }

            var storedName = DecodeName(ReadExact(stream, nameLength));
            var safeName = storedName.TrimStart('\\', '/');
            var flags = ReadU16(stream);
            var packedSize = ReadU32(stream);
            var unpackedSize = ReadU32(stream);
            var isPacked = (flags & 1) != 0;

            var entryHeaderOffset = dataOffset;
            dataOffset = checked(dataOffset + 4 + nameLength + 6);
            if (isPacked)
            {
                dataOffset = checked(dataOffset + 4);
            }

            var storedSize = isPacked ? packedSize : unpackedSize;
            var entry = new Af01ArchiveEntry
            {
                Name = safeName,
                StoredName = storedName,
                Flags = flags,
                IsPacked = isPacked,
                EntryHeaderOffset = entryHeaderOffset,
                DataOffset = dataOffset,
                PackedSize = packedSize,
                UnpackedSize = unpackedSize,
                StoredSize = storedSize
            };

            if (entry.DataOffset < 0 || entry.DataOffset + storedSize > indexOffset)
            {
                throw new InvalidDataException($"AF01 entry {entry.Name} points outside the data area.");
            }

            manifest.Entries.Add(entry);
            dataOffset = checked(dataOffset + storedSize);
        }

        if (dataOffset != indexOffset)
        {
            throw new InvalidDataException($"AF01 data/index boundary mismatch: computed=0x{dataOffset:X}, index=0x{indexOffset:X}.");
        }

        return manifest;
    }

    public void Extract(string archivePath, string outputDirectory, Action<int, int>? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);
        using var input = File.OpenRead(archivePath);
        var manifest = ReadManifest(input);
        using var archiveHandle = File.OpenHandle(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);

        var completed = 0;
        var total = manifest.Entries.Count;
        Parallel.ForEach(manifest.Entries, new ParallelOptions { MaxDegreeOfParallelism = DefaultExtractParallelism }, entry =>
        {
            var outputPath = GetSafeOutputPath(outputDirectory, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            if (entry.IsPacked)
            {
                var packed = ReadExact(archiveHandle, entry.DataOffset, checked((int)entry.PackedSize));
                var unpacked = LzUnpack(packed, checked((int)entry.UnpackedSize));
                File.WriteAllBytes(outputPath, unpacked);
            }
            else
            {
                using var output = File.Create(outputPath);
                CopyBytes(archiveHandle, output, entry.DataOffset, entry.UnpackedSize);
            }

            progress?.Invoke(Interlocked.Increment(ref completed), total);
        });

        Af01ArchiveManifestWriter.Write(Path.Combine(outputDirectory, ManifestFileName), manifest);
    }

    public void PackFromManifest(
        string inputDirectory,
        string manifestPath,
        string outputPath,
        bool compressPackedEntries = true,
        Action<int, int>? progress = null)
    {
        var manifest = Af01ArchiveManifestWriter.Read(manifestPath);
        if (!string.Equals(manifest.Format, "AF01", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Manifest format is not AF01: {manifest.Format}");
        }

        var packedEntries = new List<PackEntryInfo>();
        foreach (var entry in manifest.Entries)
        {
            var inputPath = GetSafeOutputPath(inputDirectory, entry.Name);
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException($"Missing archive entry file: {inputPath}", inputPath);
            }

            var fileSize = new FileInfo(inputPath).Length;
            if (fileSize > uint.MaxValue)
            {
                throw new InvalidDataException($"AF01 entry is too large: {entry.Name}");
            }

            var storedName = string.IsNullOrEmpty(entry.StoredName) ? entry.Name : entry.StoredName;
            var nameBytes = EncodeName(storedName);
            if (nameBytes.Length is <= 0 or > MaxNameBytes)
            {
                throw new InvalidDataException($"AF01 entry name is too long: {entry.Name}");
            }

            packedEntries.Add(PreparePackEntry(entry, nameBytes, inputPath, (uint)fileSize, compressPackedEntries));
        }

        var compressionEntries = packedEntries.Where(entry => entry.NeedsCompression).ToList();
        var completed = 0;
        var total = compressionEntries.Count + packedEntries.Count;

        Parallel.ForEach(
            compressionEntries,
            new ParallelOptions { MaxDegreeOfParallelism = DefaultExtractParallelism },
            entry =>
            {
                var raw = File.ReadAllBytes(entry.InputPath);
                var packed = LzPack(raw);
                entry.PackedData = packed;
                entry.PackedSize = (uint)packed.Length;
                progress?.Invoke(Interlocked.Increment(ref completed), total);
            });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var output = File.Create(outputPath);
        WriteAscii(output, "AF01");
        WriteU32(output, manifest.Header.Version == 0 ? 1 : manifest.Header.Version);
        WriteU32(output, 0);

        foreach (var entry in packedEntries)
        {
            WriteDataEntry(output, entry);
            progress?.Invoke(Interlocked.Increment(ref completed), total);
        }

        var indexOffset = output.Position;
        if (indexOffset < 8 || indexOffset - 8 > uint.MaxValue)
        {
            throw new InvalidDataException($"AF01 index offset is out of range: 0x{indexOffset:X}");
        }

        output.Position = 8;
        WriteU32(output, (uint)(indexOffset - 8));
        output.Position = indexOffset;

        foreach (var item in packedEntries)
        {
            WriteIndexEntry(output, item);
        }
    }

    private sealed class PackEntryInfo
    {
        public required byte[] NameBytes { get; init; }
        public required string InputPath { get; init; }
        public required ushort Flags { get; init; }
        public uint PackedSize { get; set; }
        public required uint UnpackedSize { get; init; }
        public required bool NeedsCompression { get; init; }
        public byte[]? PackedData { get; set; }
    }

    private static PackEntryInfo PreparePackEntry(
        Af01ArchiveEntry sourceEntry,
        byte[] nameBytes,
        string inputPath,
        uint fileSize,
        bool compressPackedEntries)
    {
        if (sourceEntry.IsPacked && compressPackedEntries)
        {
            return new PackEntryInfo
            {
                NameBytes = nameBytes,
                InputPath = inputPath,
                Flags = (ushort)(sourceEntry.Flags | 1),
                PackedSize = 0,
                UnpackedSize = fileSize,
                NeedsCompression = true
            };
        }

        var unpackedFlags = (ushort)(sourceEntry.Flags & ~1);
        return new PackEntryInfo
        {
            NameBytes = nameBytes,
            InputPath = inputPath,
            Flags = unpackedFlags,
            PackedSize = 0,
            UnpackedSize = fileSize,
            NeedsCompression = false
        };
    }

    private static void WriteDataEntry(Stream output, PackEntryInfo entry)
    {
        WriteU32(output, (uint)entry.NameBytes.Length);
        output.Write(entry.NameBytes);
        WriteU16(output, entry.Flags);
        if (entry.NeedsCompression)
        {
            var packed = entry.PackedData ?? throw new InvalidDataException($"AF01 entry was not pre-compressed: {entry.InputPath}");
            WriteU32(output, entry.PackedSize);
            WriteU32(output, entry.UnpackedSize);
            output.Write(packed);
            return;
        }

        WriteU32(output, entry.UnpackedSize);
        using var input = File.OpenRead(entry.InputPath);
        input.CopyTo(output);
    }

    private static void WriteIndexEntry(Stream output, PackEntryInfo entry)
    {
        WriteU32(output, (uint)entry.NameBytes.Length);
        output.Write(entry.NameBytes);
        WriteU16(output, entry.Flags);
        WriteU32(output, entry.PackedSize);
        WriteU32(output, entry.UnpackedSize);
    }

    private static byte[] LzUnpack(byte[] input, int unpackedSize)
    {
        var output = new byte[unpackedSize];
        var frame = new byte[0x1000];
        var framePos = 1;
        var dst = 0;
        var bits = new MsbBitReader(input);
        while (dst < output.Length)
        {
            if (bits.GetBits(1) != 0)
            {
                var b = (byte)bits.GetBits(8);
                output[dst++] = b;
                frame[framePos++ & 0xFFF] = b;
            }
            else
            {
                var offset = bits.GetBits(12);
                var count = bits.GetBits(4) + 2;
                for (var i = 0; i < count && dst < output.Length; i++)
                {
                    var b = frame[(offset + i) & 0xFFF];
                    output[dst++] = b;
                    frame[framePos++ & 0xFFF] = b;
                }
            }
        }

        return output;
    }

    private sealed class MsbBitReader(byte[] data)
    {
        private int _byteOffset;
        private int _bitMask;

        public int GetBits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                if (_bitMask == 0)
                {
                    if (_byteOffset >= data.Length)
                    {
                        throw new EndOfStreamException("AF01 LZ bitstream ended before the output buffer was filled.");
                    }

                    _bitMask = 0x80;
                }

                value <<= 1;
                if ((data[_byteOffset] & _bitMask) != 0)
                {
                    value |= 1;
                }

                _bitMask >>= 1;
                if (_bitMask == 0)
                {
                    _byteOffset++;
                }
            }

            return value;
        }
    }

    private static byte[] LzPack(byte[] input)
    {
        var matches = new LzMatchFinder();
        var pos = 0;
        using var output = new MemoryStream();
        var bits = new MsbBitWriter(output);

        while (pos < input.Length)
        {
            var (matchOffset, matchLength) = matches.FindBestMatch(input, pos);
            if (matchLength >= 2)
            {
                bits.WriteBits(0, 1);
                bits.WriteBits(matchOffset, 12);
                bits.WriteBits(matchLength - 2, 4);
                for (var i = 0; i < matchLength; i++)
                {
                    matches.WriteByte(input[pos + i]);
                }

                pos += matchLength;
            }
            else
            {
                bits.WriteBits(1, 1);
                bits.WriteBits(input[pos], 8);
                matches.WriteByte(input[pos++]);
            }
        }

        bits.Flush();
        return output.ToArray();
    }

    private sealed class LzMatchFinder
    {
        private const int FrameSize = 0x1000;
        private const int FrameMask = FrameSize - 1;
        private const int MaxCandidates = 256;

        private readonly byte[] _frame = new byte[FrameSize];
        private readonly List<int>?[] _buckets = new List<int>?[0x10000];
        private int _framePos = 1;

        public LzMatchFinder()
        {
            var zeroes = new List<int>(FrameSize);
            for (var i = 0; i < FrameSize; i++)
            {
                zeroes.Add(i);
            }

            _buckets[0] = zeroes;
        }

        public (int Offset, int Length) FindBestMatch(byte[] input, int pos)
        {
            var maxLength = Math.Min(17, input.Length - pos);
            if (maxLength < 2)
            {
                return (0, 0);
            }

            var key = (input[pos] << 8) | input[pos + 1];
            var candidates = _buckets[key];
            if (candidates is null || candidates.Count == 0)
            {
                return (0, 0);
            }

            var bestOffset = 0;
            var bestLength = 0;
            var checkedCandidates = 0;
            for (var index = candidates.Count - 1; index >= 0 && checkedCandidates < MaxCandidates; index--)
            {
                var offset = candidates[index];
                var length = 0;
                while (length < maxLength && ReadMatchByte(input, pos, offset, length) == input[pos + length])
                {
                    length++;
                }

                if (length > bestLength)
                {
                    bestLength = length;
                    bestOffset = offset;
                    if (bestLength == maxLength)
                    {
                        break;
                    }
                }

                checkedCandidates++;
            }

            return (bestOffset, bestLength);
        }

        public void WriteByte(byte value)
        {
            var index = _framePos & FrameMask;
            RemoveOffset((index - 1) & FrameMask);
            RemoveOffset(index);
            _frame[index] = value;
            AddOffset((index - 1) & FrameMask);
            AddOffset(index);
            _framePos++;
        }

        private byte ReadMatchByte(byte[] input, int inputPos, int offset, int length)
        {
            var source = (offset + length) & FrameMask;
            for (var previous = 0; previous < length; previous++)
            {
                if (((_framePos + previous) & FrameMask) == source)
                {
                    return input[inputPos + previous];
                }
            }

            return _frame[source];
        }

        private void RemoveOffset(int offset)
        {
            var key = KeyAt(offset);
            _buckets[key]?.Remove(offset);
        }

        private void AddOffset(int offset)
        {
            var key = KeyAt(offset);
            var bucket = _buckets[key];
            if (bucket is null)
            {
                bucket = [];
                _buckets[key] = bucket;
            }

            bucket.Add(offset);
        }

        private int KeyAt(int offset) => (_frame[offset] << 8) | _frame[(offset + 1) & FrameMask];
    }

    private sealed class MsbBitWriter(Stream output)
    {
        private int _current;
        private int _bitCount;

        public void WriteBits(int value, int count)
        {
            for (var bit = count - 1; bit >= 0; bit--)
            {
                _current = (_current << 1) | ((value >> bit) & 1);
                _bitCount++;
                if (_bitCount == 8)
                {
                    output.WriteByte((byte)_current);
                    _current = 0;
                    _bitCount = 0;
                }
            }
        }

        public void Flush()
        {
            if (_bitCount == 0)
            {
                return;
            }

            output.WriteByte((byte)(_current << (8 - _bitCount)));
            _current = 0;
            _bitCount = 0;
        }
    }

    private static string DecodeName(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 0xFF;
        }

        return ShiftJis.GetString(bytes);
    }

    private static byte[] EncodeName(string name)
    {
        var bytes = ShiftJis.GetBytes(name);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 0xFF;
        }

        return bytes;
    }

    private static string GetSafeOutputPath(string rootDirectory, string entryName)
    {
        var normalized = entryName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Archive entry escapes output directory: {entryName}");
        }

        return fullPath;
    }

    private static void CopyBytes(Stream input, Stream output, uint length)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading AF01 entry data.");
            }

            output.Write(buffer, 0, read);
            remaining -= (uint)read;
        }
    }

    private static void CopyBytes(SafeFileHandle handle, Stream output, long offset, uint length)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        var currentOffset = offset;
        while (remaining > 0)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)), currentOffset);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading AF01 entry data.");
            }

            output.Write(buffer, 0, read);
            currentOffset += read;
            remaining -= (uint)read;
        }
    }

    private static string ReadAscii(Stream stream, int length) => Encoding.ASCII.GetString(ReadExact(stream, length));

    private static byte[] ReadExact(Stream stream, int length)
    {
        var data = new byte[length];
        stream.ReadExactly(data);
        return data;
    }

    private static byte[] ReadExact(SafeFileHandle handle, long offset, int length)
    {
        var data = new byte[length];
        var readTotal = 0;
        while (readTotal < length)
        {
            var read = RandomAccess.Read(handle, data.AsSpan(readTotal), offset + readTotal);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading AF01 entry data.");
            }

            readTotal += read;
        }

        return data;
    }

    private static int ReadI32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static ushort ReadU16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    private static uint ReadU32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static void WriteAscii(Stream stream, string value) => stream.Write(Encoding.ASCII.GetBytes(value));

    private static void WriteU16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
