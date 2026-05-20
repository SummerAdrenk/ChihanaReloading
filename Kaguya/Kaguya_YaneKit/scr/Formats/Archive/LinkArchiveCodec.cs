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
//   - 按条目并行提取 (Parallel.ForEach, MaxDegreeOfParallelism=128)
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

namespace Kaguya_YaneKit.Formats.Archive;

public sealed class LinkArchiveCodec
{
    private static readonly Encoding ShiftJis = CreateShiftJisEncoding();
    private static readonly Encoding Utf16Le = Encoding.Unicode;

    private static Encoding CreateShiftJisEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
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

    public void Extract(string archivePath, string outputDirectory, string? paramsPath = null, byte[]? encryptionKey = null, bool decrypt = true)
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

        Parallel.ForEach(manifest.Entries, new ParallelOptions { MaxDegreeOfParallelism = 128 }, entry =>
        {
            using var entryStream = File.OpenRead(archivePath);
            entryStream.Position = entry.DataOffset;
            var outputPath = GetSafeOutputPath(outputDirectory, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            if (entry.IsCompressed)
            {
                var rawData = ReadExact(entryStream, (int)entry.DataSize);
                if (BmrDecoder.IsBmr(rawData))
                {
                    var decoder = new BmrDecoder(rawData);
                    var decompressed = decoder.Unpack();
                    File.WriteAllBytes(outputPath, decompressed);
                }
                else
                {
                    File.WriteAllBytes(outputPath, rawData);
                }
            }
            else
            {
                using var output = File.Create(outputPath);
                if (key is not null && IsEncrypted(entry))
                {
                    CopyEntryMaybeDecrypt(entryStream, output, entry.DataSize, key);
                }
                else
                {
                    CopyBytes(entryStream, output, entry.DataSize);
                }
            }

            TrySetTimestamp(outputPath, entry);
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
        return Convert.FromBase64String(document.GameSystem.RawBlob.DataBase64);
    }

    private static bool IsEncrypted(LinkArchiveEntry entry) => (entry.EntryFlags & 4) != 0;

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

    public void PackLink6(string inputDirectory, string outputPath, string archiveName, ushort archiveFlags = 0, bool recursive = false)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(inputDirectory, "*", searchOption)
            .Where(path => !string.Equals(Path.GetFileName(path), "_link_manifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var output = File.Create(outputPath);
        WriteAscii(output, "LINK6");
        WriteU16(output, archiveFlags);
        WriteShortAscii(output, archiveName);

        foreach (var file in files)
        {
            var relativeName = Path.GetRelativePath(inputDirectory, file).Replace(Path.DirectorySeparatorChar, '\\');
            WriteLink6Entry(output, relativeName, file);
        }

        WriteU32(output, 0);
    }

    public void PackLink6FromManifest(string inputDirectory, string manifestPath, string outputPath)
    {
        var manifest = LinkArchiveManifestWriter.Read(manifestPath);
        if (manifest.Header.Version != 6)
        {
            throw new InvalidDataException($"Manifest repack currently supports LINK6 only, got {manifest.Header.Magic}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var output = File.Create(outputPath);
        WriteAscii(output, "LINK6");
        WriteU16(output, manifest.Header.Flags);
        WriteShortAscii(output, manifest.Header.ArchiveName);

        foreach (var entry in manifest.Entries)
        {
            var inputPath = GetSafeOutputPath(inputDirectory, entry.Name);
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException($"Extracted file is missing for LINK entry: {entry.Name}", inputPath);
            }

            WriteLink6Entry(output, entry.Name, inputPath, entry);
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

    private static void WriteLink6Entry(Stream output, string name, string filePath, LinkArchiveEntry sourceEntry)
    {
        var entryStart = output.Position;
        WriteU32(output, 0);

        WriteU16(output, sourceEntry.EntryFlags);
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
}
