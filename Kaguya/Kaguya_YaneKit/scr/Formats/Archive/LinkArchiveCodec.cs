// ============================================================================
// LinkArchiveCodec.cs
// LINK 档案编解码器: 读取/解包/打包 LINK3~6 档案
//
// 读取 (ReadManifest / ReadHeader / ReadEntry):
//   - 支持 LINK3/4/5/6 四种版本
//   - LINK6: u16 标志 + u8 名称长 + ASCII 名 + 条目名用 UTF-16LE
//   - LINK3~5: 3 字节 ASCII 名 + 条目名用 Shift-JIS + 2 字节 LegacyExtra
//   - 条目结构: u32 ChunkSize + u16 Flags + 时间戳 + 名称 + 数据
//
// 解包 (Extract):
//   - 按条目并行提取 (Parallel.ForEach, 默认并行度为 CPU 数 * 2，可用 KAGUYA_ARCHIVE_PARALLELISM 覆盖)
//   - 压缩条目: 检测 BMR 魔数 -> BmrDecoder.Unpack()
//   - 加密条目 (EntryFlags bit2): 从 params.dat RawBlob 提取 XOR 密钥
//     跳过文件格式头 (BMP=0x36, AP-2/AP-3=0x18, AP=0x0C) 后对数据区 XOR
//   - 还原文件时间戳, 写出 _link_manifest.json
//
// 打包 (PackLink6 / PackLink6FromManifest):
//   - 仅支持 LINK6 格式输出
//   - PackLink6: 从目录扫描文件, 按字典序写入
//   - PackLink6FromManifest: 按已有 manifest 恢复原始条目顺序和标志
//   - 尾部写 u32(0) 终止符
//
// 安全: GetSafeOutputPath 防止路径穿越攻击
//
// 依赖: BmrDecoder (BMR 解压), ParamsDatCodec (加密密钥),
//        LinkArchiveManifestWriter (manifest JSON),
//        System.Buffers.Binary, System.Text (Shift-JIS/UTF-16LE)
// ============================================================================
using System.Buffers.Binary;
using System.Text;
using Kaguya_YaneKit.Formats.Params;
using Microsoft.Win32.SafeHandles;

namespace Kaguya_YaneKit.Formats.Archive;

public sealed class LinkArchivePackOptions
{
    public bool CompressPackedEntries { get; init; }
    public bool EncryptEncryptedEntries { get; init; }
    public byte[]? EncryptionKey { get; init; }
}

public sealed class LinkArchiveCodec
{
    private static readonly Encoding ShiftJis = CreateShiftJisEncoding();
    private static readonly Encoding Utf16Le = Encoding.Unicode;
    private static readonly int ExtractParallelism = ReadMaxDegreeOfParallelism(
        "KAGUYA_ARCHIVE_PARALLELISM",
        Environment.ProcessorCount * 2);
    private static readonly int PackParallelism = ReadMaxDegreeOfParallelism(
        "KAGUYA_LINK_PACK_PARALLELISM",
        Math.Max(1, Environment.ProcessorCount / 2));

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

    public LinkArchiveManifest ReadManifest(Stream stream)
    {
        var header = ReadHeader(stream);
        var entries = new List<LinkArchiveEntry>();
        while (stream.Position < stream.Length)
        {
            var entry = ReadEntry(stream, header);
            if (entry is null)
            {
                break;
            }

            entries.Add(entry);
            stream.Position = entry.EntryOffset + entry.ChunkSize;
        }

        return new LinkArchiveManifest
        {
            Header = header,
            Entries = entries
        };
    }

    public LinkArchiveHeader ReadHeader(Stream stream)
    {
        stream.Position = 0;
        var magic = ReadAscii(stream, 5);
        if (magic is not ("LINK3" or "LINK4" or "LINK5" or "LINK6"))
        {
            throw new InvalidDataException($"Unsupported LINK archive magic: {magic}");
        }

        if (magic == "LINK6")
        {
            var flags = ReadU16(stream);
            var nameLength = ReadU8(stream);
            return new LinkArchiveHeader
            {
                Magic = magic,
                Version = 6,
                Flags = flags,
                ArchiveName = ReadAscii(stream, nameLength),
                HeaderSize = 8 + nameLength
            };
        }

        var archiveName = ReadAscii(stream, 3);
        var legacyFlags = magic is "LINK4" or "LINK5" ? ReadU16(stream) : (ushort)0;
        return new LinkArchiveHeader
        {
            Magic = magic,
            Version = magic[^1] - '0',
            Flags = legacyFlags,
            ArchiveName = archiveName,
            HeaderSize = magic is "LINK4" or "LINK5" ? 10 : 8
        };
    }

    public LinkArchiveEntry? ReadEntry(Stream stream, LinkArchiveHeader header)
    {
        var entryOffset = stream.Position;
        if (stream.Length - stream.Position < 4)
        {
            throw new EndOfStreamException($"LINK chunk size is truncated at 0x{entryOffset:X}.");
        }

        var chunkSize = ReadU32(stream);
        if (chunkSize == 0)
        {
            return null;
        }

        if (chunkSize < 4)
        {
            throw new InvalidDataException($"Invalid LINK chunk size at 0x{entryOffset:X}: {chunkSize}");
        }

        var chunkEnd = checked(entryOffset + chunkSize);
        if (chunkEnd > stream.Length)
        {
            throw new InvalidDataException($"LINK chunk at 0x{entryOffset:X} extends past EOF: size={chunkSize}");
        }

        var entry = new LinkArchiveEntry
        {
            EntryOffset = entryOffset,
            ChunkSize = chunkSize,
            EntryFlags = ReadU16(stream),
            Year = ReadU16(stream),
            Month = ReadU8(stream),
            Day = ReadU8(stream),
            Hour = ReadU8(stream),
            Minute = ReadU8(stream),
            Second = ReadU8(stream)
        };

        if (header.Version == 6)
        {
            var nameByteLength = ReadU16(stream);
            entry.Name = Utf16Le.GetString(ReadExact(stream, nameByteLength));
        }
        else
        {
            var nameByteLength = ReadU8(stream);
            entry.LegacyExtra = ReadExact(stream, 2);
            entry.Name = ShiftJis.GetString(ReadExact(stream, nameByteLength));
        }

        entry.DataOffset = stream.Position;
        var dataSize = chunkEnd - entry.DataOffset;
        if (dataSize is < 0 or > uint.MaxValue)
        {
            throw new InvalidDataException($"Invalid LINK data size at 0x{entryOffset:X}: {dataSize}");
        }

        entry.DataSize = (uint)dataSize;
        entry.IsCompressed = (entry.EntryFlags & 3) != 0;
        return entry;
    }

    public void Extract(
        string archivePath,
        string outputDirectory,
        string? paramsPath = null,
        byte[]? encryptionKey = null,
        bool decrypt = true,
        Action<int, int>? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);
        using var input = File.OpenRead(archivePath);
        var manifest = ReadManifest(input);
        var hasEncryptedEntries = manifest.Entries.Any(entry => IsEncrypted(entry));
        var key = decrypt && hasEncryptedEntries
            ? encryptionKey ?? TryReadEncryptionKey(paramsPath ?? FindParamsPath(archivePath))
            : null;

        if (decrypt && hasEncryptedEntries && key is null)
        {
            throw new InvalidDataException("LINK archive contains encrypted entries, but params.dat was not found. Set the game root/current directory to the game folder, pass --params <params.dat>, or use --no-decrypt for raw extraction.");
        }

        using var archiveHandle = File.OpenHandle(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);

        var completed = 0;
        var total = manifest.Entries.Count;
        Parallel.ForEach(manifest.Entries, new ParallelOptions { MaxDegreeOfParallelism = ExtractParallelism }, entry =>
        {
            var outputPath = GetSafeOutputPath(outputDirectory, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            ThrowIfUnsupportedCombinedFlags(entry);

            if ((entry.EntryFlags & 1) != 0)
            {
                var rawData = ReadExact(archiveHandle, entry.DataOffset, checked((int)entry.DataSize));
                File.WriteAllBytes(outputPath, UnpackLinkLzssPayload(rawData, entry.Name));
            }
            else if ((entry.EntryFlags & 2) != 0)
            {
                var rawData = ReadExact(archiveHandle, entry.DataOffset, checked((int)entry.DataSize));
                if (!BmrDecoder.IsBmr(rawData))
                {
                    throw new InvalidDataException($"LINK entry is marked as BMR-compressed but payload does not start with BMR: {entry.Name}");
                }

                var decoder = new BmrDecoder(rawData);
                var decompressed = decoder.Unpack();
                File.WriteAllBytes(outputPath, decompressed);
            }
            else
            {
                if (key is not null && IsEncrypted(entry))
                {
                    var rawData = ReadExact(archiveHandle, entry.DataOffset, checked((int)entry.DataSize));
                    File.WriteAllBytes(outputPath, TransformEncryptedPayload(rawData, key, entry.Name, requireSupported: false));
                }
                else
                {
                    using var output = File.Create(outputPath);
                    CopyBytes(archiveHandle, output, entry.DataOffset, entry.DataSize);
                }
            }

            TrySetTimestamp(outputPath, entry);
            progress?.Invoke(Interlocked.Increment(ref completed), total);
        });

        LinkArchiveManifestWriter.Write(Path.Combine(outputDirectory, "_link_manifest.json"), manifest);
    }

    private static string? FindParamsPath(string archivePath)
    {
        var archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        var candidates = new[]
        {
            archiveDirectory is null ? null : Path.Combine(archiveDirectory, "params.dat"),
            archiveDirectory is null ? null : Path.Combine(Directory.GetParent(archiveDirectory)?.FullName ?? "", "params.dat"),
            Path.Combine(Environment.CurrentDirectory, "params.dat")
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static byte[]? TryReadEncryptionKey(string? paramsPath)
    {
        if (string.IsNullOrWhiteSpace(paramsPath) || !File.Exists(paramsPath))
        {
            return null;
        }

        var document = new ParamsDatCodec().Read(File.ReadAllBytes(paramsPath));
        return Convert.FromBase64String(document.GameSystem.RawBlob.LinkXorKeyBase64);
    }

    private static bool IsEncrypted(LinkArchiveEntry entry) => (entry.EntryFlags & 4) != 0;

    private static byte[] ReadExact(SafeFileHandle handle, long offset, int length)
    {
        var data = new byte[length];
        var readTotal = 0;
        while (readTotal < data.Length)
        {
            var read = RandomAccess.Read(handle, data.AsSpan(readTotal), offset + readTotal);
            if (read == 0)
            {
                throw new EndOfStreamException("LINK entry data ended unexpectedly.");
            }

            readTotal += read;
        }

        return data;
    }

    private static void CopyBytes(SafeFileHandle handle, Stream output, long offset, uint count)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = count;
        var currentOffset = offset;
        while (remaining > 0)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)), currentOffset);
            if (read == 0)
            {
                throw new EndOfStreamException("LINK entry data ended unexpectedly.");
            }

            output.Write(buffer, 0, read);
            currentOffset += read;
            remaining -= (uint)read;
        }
    }

    private static void CopyEntryMaybeDecrypt(SafeFileHandle handle, Stream output, long offset, uint dataSize, byte[] key)
    {
        var prefix = ReadProbePrefix(handle, offset, dataSize);
        var prefixLength = GetEncryptedPrefixLength(prefix);
        if (prefixLength <= 0 || prefixLength >= dataSize)
        {
            output.Write(prefix);
            CopyBytes(handle, output, offset + prefix.Length, dataSize - (uint)prefix.Length);
            return;
        }

        output.Write(prefix.AsSpan(0, prefixLength));
        var alreadyReadEncryptedBytes = prefix.Length - prefixLength;
        if (alreadyReadEncryptedBytes > 0)
        {
            XorWrite(output, prefix.AsSpan(prefixLength, alreadyReadEncryptedBytes), key, 0);
        }

        XorCopyBytes(handle, output, offset + prefix.Length, dataSize - (uint)prefix.Length, key, alreadyReadEncryptedBytes);
    }

    private static byte[] ReadProbePrefix(SafeFileHandle handle, long offset, uint dataSize)
    {
        var length = (int)Math.Min(dataSize, 0x40);
        return ReadExact(handle, offset, length);
    }

    private static void CopyEntryMaybeDecrypt(Stream input, Stream output, uint dataSize, byte[] key)
    {
        var prefix = ReadProbePrefix(input, dataSize);
        var prefixLength = GetEncryptedPrefixLength(prefix);
        if (prefixLength <= 0 || prefixLength >= dataSize)
        {
            output.Write(prefix);
            CopyBytes(input, output, dataSize - (uint)prefix.Length);
            return;
        }

        output.Write(prefix.AsSpan(0, prefixLength));
        var alreadyReadEncryptedBytes = prefix.Length - prefixLength;
        if (alreadyReadEncryptedBytes > 0)
        {
            XorWrite(output, prefix.AsSpan(prefixLength, alreadyReadEncryptedBytes), key, 0);
        }

        XorCopyBytes(input, output, dataSize - (uint)prefix.Length, key, alreadyReadEncryptedBytes);
    }

    private static byte[] ReadProbePrefix(Stream input, uint dataSize)
    {
        var length = (int)Math.Min(dataSize, 0x40);
        return ReadExact(input, length);
    }

    private static int GetEncryptedPrefixLength(byte[] prefix)
    {
        if (StartsWithAscii(prefix, "BMR"))
        {
            return 0;
        }

        if (StartsWithAscii(prefix, "BM"))
        {
            return 0x36;
        }

        if (StartsWithAscii(prefix, "AP-2") || StartsWithAscii(prefix, "AP-3"))
        {
            return 0x18;
        }

        if (StartsWithAscii(prefix, "AP"))
        {
            return 0x0C;
        }

        return 0;
    }

    private static bool StartsWithAscii(byte[] data, string value)
    {
        if (data.Length < value.Length)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (data[i] != (byte)value[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void XorCopyBytes(Stream input, Stream output, uint count, byte[] key, int keyOffset)
    {
        if (key.Length == 0)
        {
            throw new InvalidDataException("LINK encryption key is empty.");
        }

        var keyIndex = keyOffset % key.Length;
        var buffer = new byte[1024 * 64];
        var remaining = count;
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = input.Read(buffer, 0, want);
            if (read == 0)
            {
                throw new EndOfStreamException("LINK encrypted entry data ended unexpectedly.");
            }

            for (var i = 0; i < read; i++)
            {
                buffer[i] ^= key[keyIndex++];
                if (keyIndex == key.Length)
                {
                    keyIndex = 0;
                }
            }

            output.Write(buffer, 0, read);
            remaining -= (uint)read;
        }
    }

    private static void XorCopyBytes(SafeFileHandle handle, Stream output, long offset, uint count, byte[] key, int keyOffset)
    {
        if (key.Length == 0)
        {
            throw new InvalidDataException("LINK encryption key is empty.");
        }

        var keyIndex = keyOffset % key.Length;
        var buffer = new byte[1024 * 64];
        var remaining = count;
        var currentOffset = offset;
        while (remaining > 0)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)), currentOffset);
            if (read == 0)
            {
                throw new EndOfStreamException("LINK encrypted entry data ended unexpectedly.");
            }

            for (var i = 0; i < read; i++)
            {
                buffer[i] ^= key[keyIndex++];
                if (keyIndex == key.Length)
                {
                    keyIndex = 0;
                }
            }

            output.Write(buffer, 0, read);
            currentOffset += read;
            remaining -= (uint)read;
        }
    }

    private static void XorWrite(Stream output, ReadOnlySpan<byte> data, byte[] key, int keyOffset)
    {
        if (key.Length == 0)
        {
            throw new InvalidDataException("LINK encryption key is empty.");
        }

        var keyIndex = keyOffset % key.Length;
        var buffer = data.ToArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] ^= key[keyIndex++];
            if (keyIndex == key.Length)
            {
                keyIndex = 0;
            }
        }

        output.Write(buffer);
    }

    public void PackLink6(string inputDirectory, string outputPath, string archiveName, ushort archiveFlags = 0, bool recursive = false, Action<int, int>? progress = null)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(inputDirectory, "*", searchOption)
            .Where(path => !string.Equals(Path.GetFileName(path), "_link_manifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var completed = 0;
        var total = files.Count;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var output = File.Create(outputPath);
        WriteAscii(output, "LINK6");
        WriteU16(output, archiveFlags);
        WriteShortAscii(output, archiveName);

        foreach (var file in files)
        {
            var relativeName = Path.GetRelativePath(inputDirectory, file).Replace(Path.DirectorySeparatorChar, '\\');
            WriteLink6Entry(output, relativeName, file);
            progress?.Invoke(++completed, total);
        }

        WriteU32(output, 0);
    }

    public void PackLink6FromManifest(string inputDirectory, string manifestPath, string outputPath, bool clearEncryptionFlags = true)
    {
        PackLink6FromManifest(inputDirectory, manifestPath, outputPath, new LinkArchivePackOptions
        {
            CompressPackedEntries = false,
            EncryptEncryptedEntries = !clearEncryptionFlags
        });
    }

    public void PackLink6FromManifest(string inputDirectory, string manifestPath, string outputPath, LinkArchivePackOptions options, Action<int, int>? progress = null)
    {
        var manifest = LinkArchiveManifestWriter.Read(manifestPath);
        if (manifest.Header.Version != 6)
        {
            throw new InvalidDataException($"Manifest repack currently supports LINK6 only, got {manifest.Header.Magic}.");
        }

        var completed = 0;
        var prepackCandidates = GetParallelPrepackCandidateIndexes(manifest.Entries, options);
        var total = manifest.Entries.Count + prepackCandidates.Count;
        var prepackedPayloads = new PrepackedLinkPayload?[manifest.Entries.Count];
        if (prepackCandidates.Count > 0)
        {
            Parallel.ForEach(
                prepackCandidates,
                new ParallelOptions { MaxDegreeOfParallelism = PackParallelism },
                index =>
                {
                    var entry = manifest.Entries[index];
                    var inputPath = GetSafeOutputPath(inputDirectory, entry.Name);
                    if (!File.Exists(inputPath))
                    {
                        throw new FileNotFoundException($"Extracted file is missing for LINK entry: {entry.Name}", inputPath);
                    }

                    prepackedPayloads[index] = BuildPrepackedPayload(inputPath, entry, options);
                    progress?.Invoke(Interlocked.Increment(ref completed), total);
                });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var output = File.Create(outputPath);
        WriteAscii(output, "LINK6");
        WriteU16(output, manifest.Header.Flags);
        WriteShortAscii(output, manifest.Header.ArchiveName);

        for (var i = 0; i < manifest.Entries.Count; i++)
        {
            var entry = manifest.Entries[i];
            var inputPath = GetSafeOutputPath(inputDirectory, entry.Name);
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException($"Extracted file is missing for LINK entry: {entry.Name}", inputPath);
            }

            WriteLink6Entry(output, entry.Name, inputPath, entry, options, prepackedPayloads[i]);
            progress?.Invoke(Interlocked.Increment(ref completed), total);
        }

        WriteU32(output, 0);
    }

    private static void WriteLink6Entry(Stream output, string name, string filePath)
    {
        var entryStart = output.Position;
        WriteU32(output, 0);

        var timestamp = File.GetLastWriteTime(filePath);
        WriteU16(output, 0);
        WriteU16(output, (ushort)Math.Clamp(timestamp.Year, 0, ushort.MaxValue));
        WriteU8(output, (byte)Math.Clamp(timestamp.Month, 0, byte.MaxValue));
        WriteU8(output, (byte)Math.Clamp(timestamp.Day, 0, byte.MaxValue));
        WriteU8(output, (byte)Math.Clamp(timestamp.Hour, 0, byte.MaxValue));
        WriteU8(output, (byte)Math.Clamp(timestamp.Minute, 0, byte.MaxValue));
        WriteU8(output, (byte)Math.Clamp(timestamp.Second, 0, byte.MaxValue));

        var nameBytes = Utf16Le.GetBytes(name);
        if (nameBytes.Length > ushort.MaxValue)
        {
            throw new InvalidDataException($"LINK6 entry name is too long: {name}");
        }

        WriteU16(output, (ushort)nameBytes.Length);
        output.Write(nameBytes);
        using (var input = File.OpenRead(filePath))
        {
            input.CopyTo(output);
        }

        var entryEnd = output.Position;
        var chunkSize = entryEnd - entryStart;
        if (chunkSize > uint.MaxValue)
        {
            throw new InvalidDataException($"LINK6 entry is too large: {name}");
        }

        output.Position = entryStart;
        WriteU32(output, (uint)chunkSize);
        output.Position = entryEnd;
    }

    private static void WriteLink6Entry(Stream output, string name, string filePath, LinkArchiveEntry sourceEntry, LinkArchivePackOptions options, PrepackedLinkPayload? prepackedPayload = null)
    {
        var entryStart = output.Position;
        WriteU32(output, 0);

        byte[] payload;
        ushort entryFlags;
        if (prepackedPayload is not null)
        {
            payload = prepackedPayload.Payload;
            entryFlags = prepackedPayload.EntryFlags;
        }
        else
        {
            payload = BuildPackedPayload(filePath, sourceEntry, options, out entryFlags);
        }
        WriteU16(output, entryFlags);
        WriteU16(output, sourceEntry.Year);
        WriteU8(output, sourceEntry.Month);
        WriteU8(output, sourceEntry.Day);
        WriteU8(output, sourceEntry.Hour);
        WriteU8(output, sourceEntry.Minute);
        WriteU8(output, sourceEntry.Second);

        var nameBytes = Utf16Le.GetBytes(name);
        if (nameBytes.Length > ushort.MaxValue)
        {
            throw new InvalidDataException($"LINK6 entry name is too long: {name}");
        }

        WriteU16(output, (ushort)nameBytes.Length);
        output.Write(nameBytes);
        output.Write(payload);

        var entryEnd = output.Position;
        var chunkSize = entryEnd - entryStart;
        if (chunkSize > uint.MaxValue)
        {
            throw new InvalidDataException($"LINK6 entry is too large: {name}");
        }

        output.Position = entryStart;
        WriteU32(output, (uint)chunkSize);
        output.Position = entryEnd;
    }

    private static List<int> GetParallelPrepackCandidateIndexes(IReadOnlyList<LinkArchiveEntry> entries, LinkArchivePackOptions options)
    {
        if (!options.CompressPackedEntries)
        {
            return [];
        }

        var indexes = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            if ((entries[i].EntryFlags & 3) != 0)
            {
                indexes.Add(i);
            }
        }

        return indexes;
    }

    private sealed record PrepackedLinkPayload(byte[] Payload, ushort EntryFlags);

    private static PrepackedLinkPayload BuildPrepackedPayload(string filePath, LinkArchiveEntry sourceEntry, LinkArchivePackOptions options)
    {
        var payload = BuildPackedPayload(filePath, sourceEntry, options, out var entryFlags);
        return new PrepackedLinkPayload(payload, entryFlags);
    }

    private static byte[] BuildPackedPayload(string filePath, LinkArchiveEntry sourceEntry, LinkArchivePackOptions options, out ushort entryFlags)
    {
        entryFlags = sourceEntry.EntryFlags;
        ThrowIfUnsupportedCombinedFlags(sourceEntry);
        var payload = File.ReadAllBytes(filePath);

        if ((sourceEntry.EntryFlags & 3) != 0)
        {
            if (options.CompressPackedEntries)
            {
                if ((sourceEntry.EntryFlags & 1) != 0)
                {
                    payload = BuildLinkLzssPayload(payload);
                }
                else if ((sourceEntry.EntryFlags & 2) != 0 && !BmrDecoder.IsBmr(payload))
                {
                    payload = BmrEncoder.Pack(payload);
                }
            }
            else
            {
                entryFlags = (ushort)(entryFlags & ~3);
            }
        }

        if ((sourceEntry.EntryFlags & 4) != 0)
        {
            if (options.EncryptEncryptedEntries)
            {
                if (options.EncryptionKey is null)
                {
                    throw new InvalidDataException($"LINK entry requires re-encryption but no params key was supplied: {sourceEntry.Name}");
                }

                payload = EncryptPayload(payload, options.EncryptionKey, sourceEntry.Name);
            }
            else
            {
                entryFlags = (ushort)(entryFlags & ~4);
            }
        }

        return payload;
    }

    private static void ThrowIfUnsupportedCombinedFlags(LinkArchiveEntry entry)
    {
        if ((entry.EntryFlags & 4) != 0 && (entry.EntryFlags & 3) != 0)
        {
            throw new NotSupportedException($"LINK entry has unsupported combined compression/encryption flags 0x{entry.EntryFlags:X}: {entry.Name}");
        }
    }

    private static byte[] UnpackLinkLzssPayload(byte[] payload, string entryName)
    {
        if (payload.Length < 4)
        {
            throw new InvalidDataException($"LINK LZSS entry is too short: {entryName}");
        }

        var unpackedSize = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (unpackedSize < 0)
        {
            throw new InvalidDataException($"LINK LZSS entry has a negative unpacked size: {entryName}");
        }

        return DecompressLinkLzss(payload.AsSpan(4), unpackedSize, entryName);
    }

    private static byte[] BuildLinkLzssPayload(byte[] unpackedPayload)
    {
        var stream = CompressLinkLzss(unpackedPayload);
        var output = new byte[4 + stream.Length];
        BinaryPrimitives.WriteInt32LittleEndian(output, unpackedPayload.Length);
        stream.CopyTo(output.AsSpan(4));
        return output;
    }

    private static byte[] DecompressLinkLzss(ReadOnlySpan<byte> input, int expectedSize, string entryName)
    {
        var output = new byte[expectedSize];
        var window = new byte[0x1000];
        var reader = new LinkLzssBitReader(input);
        var dst = 0;
        var windowPos = 1;

        while (dst < output.Length)
        {
            if (reader.ReadBit() != 0)
            {
                var value = (byte)reader.ReadBits(8);
                output[dst++] = value;
                window[windowPos] = value;
                windowPos = (windowPos + 1) & 0xFFF;
                continue;
            }

            var offset = reader.ReadBits(12);
            if (offset == 0)
            {
                break;
            }

            var length = reader.ReadBits(4) + 2;
            for (var i = 0; i < length && dst < output.Length; i++)
            {
                var value = window[(offset + i) & 0xFFF];
                output[dst++] = value;
                window[windowPos] = value;
                windowPos = (windowPos + 1) & 0xFFF;
            }
        }

        if (dst != output.Length)
        {
            throw new InvalidDataException($"LINK LZSS unpacked size mismatch for {entryName}: decoded {dst}, expected {output.Length}.");
        }

        return output;
    }

    private static byte[] CompressLinkLzss(byte[] input)
    {
        var writer = new LinkLzssBitWriter();
        var positionsByHash = new Dictionary<int, List<int>>();
        var src = 0;

        while (src < input.Length)
        {
            var bestPosition = -1;
            var bestLength = 0;
            if (src + 2 < input.Length)
            {
                var hash = Hash3(input, src);
                if (positionsByHash.TryGetValue(hash, out var candidates))
                {
                    for (var i = candidates.Count - 1; i >= 0; i--)
                    {
                        var candidate = candidates[i];
                        var distance = src - candidate;
                        if (distance > 0x1000)
                        {
                            candidates.RemoveRange(0, i + 1);
                            break;
                        }

                        var offset = (candidate + 1) & 0xFFF;
                        if (offset == 0)
                        {
                            continue;
                        }

                        var length = CountMatch(input, candidate, src, Math.Min(17, input.Length - src));
                        if (length > bestLength)
                        {
                            bestLength = length;
                            bestPosition = candidate;
                            if (length == 17)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            if (bestLength >= 3)
            {
                writer.WriteBit(0);
                writer.WriteBits((bestPosition + 1) & 0xFFF, 12);
                writer.WriteBits(bestLength - 2, 4);
                for (var i = 0; i < bestLength; i++)
                {
                    AddLzssPosition(positionsByHash, input, src + i);
                }

                src += bestLength;
                continue;
            }

            writer.WriteBit(1);
            writer.WriteBits(input[src], 8);
            AddLzssPosition(positionsByHash, input, src);
            src++;
        }

        writer.WriteBit(0);
        writer.WriteBits(0, 12);
        return writer.ToArray();
    }

    private static int Hash3(byte[] input, int offset) =>
        input[offset] | (input[offset + 1] << 8) | (input[offset + 2] << 16);

    private static int CountMatch(byte[] input, int candidate, int current, int maxLength)
    {
        var length = 0;
        while (length < maxLength && input[candidate + length] == input[current + length])
        {
            length++;
        }

        return length;
    }

    private static void AddLzssPosition(Dictionary<int, List<int>> positionsByHash, byte[] input, int offset)
    {
        if (offset + 2 >= input.Length)
        {
            return;
        }

        var hash = Hash3(input, offset);
        if (!positionsByHash.TryGetValue(hash, out var positions))
        {
            positions = [];
            positionsByHash[hash] = positions;
        }

        positions.Add(offset);
    }

    private static byte[] EncryptPayload(byte[] payload, byte[] key, string entryName)
    {
        return TransformEncryptedPayload(payload, key, entryName, requireSupported: true);
    }

    private static byte[] TransformEncryptedPayload(byte[] payload, byte[] key, string entryName, bool requireSupported)
    {
        if (key.Length == 0)
        {
            throw new InvalidDataException("LINK encryption key is empty.");
        }

        var output = (byte[])payload.Clone();
        if (TryTransformKnownEncryptedPayload(output, key))
        {
            return output;
        }

        if (requireSupported)
        {
            throw new NotSupportedException($"LINK entry cannot be re-encrypted because its payload header is not a supported encrypted resource type: {entryName}");
        }

        return payload;
    }

    private static bool TryTransformKnownEncryptedPayload(byte[] payload, byte[] key)
    {
        if (StartsWithAscii(payload, "BMR"))
        {
            return false;
        }

        if (StartsWithAscii(payload, "BM"))
        {
            XorPayloadRange(payload, 0x36, payload.Length - 0x36, key);
            return true;
        }

        if (StartsWithAscii(payload, "AP-2") || StartsWithAscii(payload, "AP-3"))
        {
            XorPayloadRange(payload, 0x18, payload.Length - 0x18, key);
            return true;
        }

        if (StartsWithAscii(payload, "AP"))
        {
            XorPayloadRange(payload, 0x0C, payload.Length - 0x0C, key);
            return true;
        }

        if (StartsWithAscii(payload, "AN00"))
        {
            return TryTransformAn00(payload, key, channels: 4);
        }

        if (StartsWithAscii(payload, "AN10"))
        {
            return TryTransformAn00(payload, key, channels: null);
        }

        if (StartsWithAscii(payload, "AN20"))
        {
            return TryTransformAn20(payload, key);
        }

        if (StartsWithAscii(payload, "AN21"))
        {
            return TryTransformAn21(payload, key);
        }

        if (StartsWithAscii(payload, "PL00"))
        {
            return TryTransformPl00(payload, key);
        }

        if (StartsWithAscii(payload, "PL10"))
        {
            return TryTransformPl10(payload, key);
        }

        return false;
    }

    private static bool TryTransformAn00(byte[] payload, byte[] key, int? channels)
    {
        if (!TryReadInt16(payload, 0x14, out var frameCount) || frameCount < 0)
        {
            return false;
        }

        var offset = 0x18 + frameCount * 4;
        if (!TryReadInt16(payload, offset, out var imageCount) || imageCount < 0)
        {
            return false;
        }

        offset += 2;
        for (var i = 0; i < imageCount; i++)
        {
            if (!TryReadUInt32(payload, offset + 8, out var width) ||
                !TryReadUInt32(payload, offset + 12, out var height))
            {
                return false;
            }

            var frameChannels = channels;
            var headerSize = 0x10;
            if (frameChannels is null)
            {
                if (!TryReadUInt32(payload, offset + 16, out var readChannels))
                {
                    return false;
                }

                frameChannels = checked((int)readChannels);
                headerSize = 0x14;
            }

            var size = CheckedImagePayloadSize(width, height, frameChannels.Value);
            XorPayloadRange(payload, offset + headerSize, size, key);
            offset = checked(offset + headerSize + size);
        }

        return true;
    }

    private static bool TryTransformAn20(byte[] payload, byte[] key)
    {
        if (!TrySkipAn20FrameTable(payload, out var offset) ||
            !TryReadInt16(payload, offset, out var imageCount) ||
            imageCount < 0)
        {
            return false;
        }

        offset += 2 + 0x10;
        for (var i = 0; i < imageCount; i++)
        {
            if (!TryReadUInt32(payload, offset + 8, out var width) ||
                !TryReadUInt32(payload, offset + 12, out var height) ||
                !TryReadUInt32(payload, offset + 16, out var channels))
            {
                return false;
            }

            var size = CheckedImagePayloadSize(width, height, checked((int)channels));
            XorPayloadRange(payload, offset + 0x14, size, key);
            offset = checked(offset + 0x14 + size);
        }

        return true;
    }

    private static bool TryTransformAn21(byte[] payload, byte[] key)
    {
        if (!TrySkipAn20FrameTable(payload, out var offset) ||
            !TryReadUInt16(payload, offset, out var branchCount))
        {
            return false;
        }

        offset += 2 + branchCount * 8 + 0x21;
        if (!TryReadInt32(payload, offset, out var width) ||
            !TryReadInt32(payload, offset + 4, out var height) ||
            !TryReadInt32(payload, offset + 8, out var channels))
        {
            return false;
        }

        offset += 12;
        var size = CheckedImagePayloadSize(width, height, channels);
        XorPayloadRange(payload, offset, size, key);
        return true;
    }

    private static bool TrySkipAn20FrameTable(byte[] payload, out int offset)
    {
        offset = 4;
        if (!TryReadInt16(payload, offset, out var tableCount) || tableCount < 0)
        {
            return false;
        }

        offset = 8;
        for (var i = 0; i < tableCount; i++)
        {
            if (offset >= payload.Length)
            {
                return false;
            }

            switch (payload[offset++])
            {
                case 0:
                    break;
                case 1:
                    offset += 8;
                    break;
                case 2:
                case 3:
                case 4:
                case 5:
                    offset += 4;
                    break;
                default:
                    return false;
            }
        }

        if (!TryReadUInt16(payload, offset, out var branchCount))
        {
            return false;
        }

        offset += 2 + branchCount * 8;
        return offset <= payload.Length;
    }

    private static bool TryTransformPl00(byte[] payload, byte[] key)
    {
        if (!TryReadUInt16(payload, 4, out var frameCount))
        {
            return false;
        }

        var offset = 4 + 2 + 16;
        for (var i = 0; i < frameCount; i++)
        {
            if (!TryReadUInt32(payload, offset + 8, out var width) ||
                !TryReadUInt32(payload, offset + 12, out var height) ||
                !TryReadInt32(payload, offset + 16, out var channels))
            {
                return false;
            }

            offset += 20;
            var size = CheckedImagePayloadSize(width, height, channels);
            XorPayloadRange(payload, offset, size, key);
            offset = checked(offset + size);
        }

        return true;
    }

    private static bool TryTransformPl10(byte[] payload, byte[] key)
    {
        var offset = 30;
        if (!TryReadInt32(payload, offset, out var width) ||
            !TryReadInt32(payload, offset + 4, out var height) ||
            !TryReadInt32(payload, offset + 8, out var channels))
        {
            return false;
        }

        offset += 12;
        var size = CheckedImagePayloadSize(width, height, channels);
        XorPayloadRange(payload, offset, size, key);
        return true;
    }

    private static int CheckedImagePayloadSize(uint width, uint height, int channels)
    {
        if (channels <= 0)
        {
            throw new InvalidDataException($"Invalid encrypted image channel count: {channels}");
        }

        return checked((int)(width * height * (uint)channels));
    }

    private static int CheckedImagePayloadSize(int width, int height, int channels)
    {
        if (width < 0 || height < 0 || channels <= 0)
        {
            throw new InvalidDataException($"Invalid encrypted image dimensions: {width}x{height}x{channels}");
        }

        return checked(width * height * channels);
    }

    private static void XorPayloadRange(byte[] data, int offset, int length, byte[] key)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
        {
            throw new InvalidDataException("Encrypted LINK payload range points outside entry data.");
        }

        if (key.Length == 0 || (key.Length & 31) != 0)
        {
            throw new InvalidDataException("LINK encryption key size must be a non-zero multiple of 32 bytes.");
        }

        var cursor = offset;
        var remaining = length;
        var keyOffset = 0;

        while (remaining >= 32)
        {
            for (var i = 0; i < 32; i++)
            {
                data[cursor + i] ^= key[(keyOffset + i) % key.Length];
            }

            cursor += 32;
            remaining -= 32;
            keyOffset = (keyOffset + 32) % key.Length;
        }

        while (remaining >= 4)
        {
            data[cursor] ^= key[keyOffset % key.Length];
            data[cursor + 1] ^= key[(keyOffset + 1) % key.Length];
            data[cursor + 2] ^= key[(keyOffset + 2) % key.Length];
            data[cursor + 3] ^= key[(keyOffset + 3) % key.Length];
            cursor += 4;
            remaining -= 4;
            keyOffset = (keyOffset + 4) % key.Length;
        }

        if (remaining == 1)
        {
            data[cursor] ^= key[keyOffset % key.Length];
        }
        else if (remaining == 2)
        {
            data[cursor] ^= key[(keyOffset + 1) % key.Length];
            data[cursor + 1] ^= key[keyOffset % key.Length];
        }
        else if (remaining == 3)
        {
            data[cursor] ^= key[(keyOffset + 2) % key.Length];
            data[cursor + 1] ^= key[(keyOffset + 1) % key.Length];
            data[cursor + 2] ^= key[keyOffset % key.Length];
        }
    }

    private static bool TryReadUInt16(byte[] data, int offset, out ushort value)
    {
        value = 0;
        if (offset < 0 || offset + 2 > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        return true;
    }

    private static bool TryReadInt16(byte[] data, int offset, out short value)
    {
        value = 0;
        if (offset < 0 || offset + 2 > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));
        return true;
    }

    private static bool TryReadUInt32(byte[] data, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset + 4 > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        return true;
    }

    private static bool TryReadInt32(byte[] data, int offset, out int value)
    {
        value = 0;
        if (offset < 0 || offset + 4 > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        return true;
    }

    private static string GetSafeOutputPath(string rootDirectory, string entryName)
    {
        var normalized = entryName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException($"Unsafe LINK entry path: {entryName}");
        }

        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(parts.Prepend(fullRoot).ToArray()));
        if (!fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"LINK entry escapes output directory: {entryName}");
        }

        return fullPath;
    }

    private static void TrySetTimestamp(string path, LinkArchiveEntry entry)
    {
        try
        {
            if (entry.Year is >= 1601 and <= 9999 &&
                entry.Month is >= 1 and <= 12 &&
                entry.Day is >= 1 and <= 31 &&
                entry.Hour <= 23 &&
                entry.Minute <= 59 &&
                entry.Second <= 59)
            {
                File.SetLastWriteTime(path, new DateTime(entry.Year, entry.Month, entry.Day, entry.Hour, entry.Minute, entry.Second));
            }
        }
        catch
        {
            // Metadata restoration is best-effort; extraction data is already complete.
        }
    }

    private static void CopyBytes(Stream input, Stream output, uint count)
    {
        Span<byte> buffer = stackalloc byte[1024 * 64];
        var remaining = count;
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = input.Read(buffer[..want]);
            if (read == 0)
            {
                throw new EndOfStreamException("LINK entry data ended unexpectedly.");
            }

            output.Write(buffer[..read]);
            remaining -= (uint)read;
        }
    }

    private static string ReadAscii(Stream stream, int count) => Encoding.ASCII.GetString(ReadExact(stream, count));

    private static byte[] ReadExact(Stream stream, int count)
    {
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte ReadU8(Stream stream)
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException("Unexpected EOF while reading u8.");
        }

        return (byte)value;
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

    private static void WriteShortAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length > byte.MaxValue)
        {
            throw new InvalidDataException($"LINK6 archive name is too long: {value}");
        }

        WriteU8(stream, (byte)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteAscii(Stream stream, string value) => stream.Write(Encoding.ASCII.GetBytes(value));

    private static void WriteU8(Stream stream, byte value) => stream.WriteByte(value);

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

    private ref struct LinkLzssBitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;
        private int _mask;
        private int _current;

        public LinkLzssBitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
            _mask = 0x80;
            _current = 0;
        }

        public int ReadBit()
        {
            if (_mask == 0x80)
            {
                if (_offset >= _data.Length)
                {
                    throw new EndOfStreamException("LINK LZSS bit stream ended unexpectedly.");
                }

                _current = _data[_offset++];
            }

            var bit = (_current & _mask) != 0 ? 1 : 0;
            _mask >>= 1;
            if (_mask == 0)
            {
                _mask = 0x80;
            }

            return bit;
        }

        public int ReadBits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 1) | ReadBit();
            }

            return value;
        }
    }

    private sealed class LinkLzssBitWriter
    {
        private readonly List<byte> _data = [];
        private int _current;
        private int _mask = 0x80;

        public void WriteBit(int bit)
        {
            if (bit != 0)
            {
                _current |= _mask;
            }

            _mask >>= 1;
            if (_mask != 0)
            {
                return;
            }

            _data.Add((byte)_current);
            _current = 0;
            _mask = 0x80;
        }

        public void WriteBits(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                WriteBit((value >> i) & 1);
            }
        }

        public byte[] ToArray()
        {
            if (_mask != 0x80)
            {
                _data.Add((byte)_current);
                _current = 0;
                _mask = 0x80;
            }

            return [.. _data];
        }
    }
}
