using System.Buffers.Binary;
using System.Text;
using Kaguya_YaneKit.Text.MessageDat;

namespace Kaguya_YaneKit.Text.Tblstr;

public sealed class TblstrCodec
{
    private readonly Encoding _readEncoding;
    private readonly Encoding _writeEncoding;
    private readonly MessagePlaceholderConfig _placeholders;

    public TblstrCodec(
        Encoding? readEncoding = null,
        Encoding? writeEncoding = null,
        MessagePlaceholderConfig? placeholders = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _readEncoding = readEncoding ?? Encoding.GetEncoding(932);
        _writeEncoding = writeEncoding ?? Encoding.GetEncoding(932);
        _placeholders = placeholders ?? MessagePlaceholderConfig.Empty;
    }

    public TblstrDocument Read(byte[] data)
    {
        if (data.Length < 16)
        {
            throw new InvalidDataException("TBLSTR file is too small.");
        }

        var magic = Encoding.ASCII.GetString(data, 0, 4);
        return magic switch
        {
            "UF01" => ReadUf01(data, magic),
            _ => throw new InvalidDataException($"Unsupported TBLSTR magic: {magic}")
        };
    }

    public byte[] Write(TblstrDocument document)
    {
        return document.Version switch
        {
            "UF01" => WriteUf01(document),
            _ => throw new InvalidDataException($"Unsupported TBLSTR version: {document.Version}")
        };
    }

    private TblstrDocument ReadUf01(byte[] data, string magic)
    {
        var recordTableOffset = ReadU32(data, 0x04);
        var headerReserved08 = ReadU32(data, 0x08);
        var headerField0C = ReadU32(data, 0x0C);
        if (recordTableOffset < 0x10 || recordTableOffset > data.Length)
        {
            throw new InvalidDataException($"Invalid UF01 record table offset: 0x{recordTableOffset:X8}");
        }

        var tableBytes = data.Length - checked((int)recordTableOffset);
        if (tableBytes % 4 != 0)
        {
            throw new InvalidDataException($"UF01 record table size is not aligned: {tableBytes}");
        }

        var entryCount = tableBytes / 4;
        var relativeOffsets = new uint[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            relativeOffsets[i] = ReadU32(data, checked((int)recordTableOffset + i * 4));
        }

        var document = new TblstrDocument
        {
            Magic = magic,
            Version = "UF01",
            Header = new TblstrHeader
            {
                RecordTableOffset = recordTableOffset,
                HeaderReserved08 = headerReserved08,
                HeaderField0C = headerField0C,
                EntryCount = entryCount
            }
        };

        for (var i = 0; i < entryCount; i++)
        {
            var absoluteOffset = checked(8L + relativeOffsets[i]);
            var nextAbsoluteOffset = i + 1 < entryCount
                ? checked(8L + relativeOffsets[i + 1])
                : recordTableOffset;
            if (absoluteOffset < 0x10 || nextAbsoluteOffset < absoluteOffset || nextAbsoluteOffset > recordTableOffset)
            {
                throw new InvalidDataException($"Invalid UF01 entry offset at index {i}: 0x{absoluteOffset:X}");
            }

            var byteLength = checked((int)(nextAbsoluteOffset - absoluteOffset));
            if (byteLength < 8)
            {
                throw new InvalidDataException($"UF01 entry {i} is too small: {byteLength}");
            }

            var entryStart = checked((int)absoluteOffset);
            var textByteLength = byteLength - 8;
            var textBytes = new byte[textByteLength];
            for (var j = 0; j < textByteLength; j++)
            {
                textBytes[j] = (byte)(data[entryStart + j] ^ 0xFF);
            }

            var text = _placeholders.Decode(textBytes, _readEncoding);
            document.Entries.Add(new TblstrEntry
            {
                Index = i,
                RelativeOffset = relativeOffsets[i],
                AbsoluteOffset = absoluteOffset,
                ByteLength = byteLength,
                Meta0 = ReadU32(data, entryStart + textByteLength),
                Meta1 = ReadU32(data, entryStart + textByteLength + 4),
                Text = text,
                OriginalText = text,
                OriginalTextBytes = textBytes
            });
        }

        return document;
    }

    private byte[] WriteUf01(TblstrDocument document)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[16];
        Encoding.ASCII.GetBytes("UF01", header[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), document.Header.HeaderReserved08);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), document.Header.HeaderField0C);
        stream.Write(header);

        var relativeOffsets = new uint[document.Entries.Count];
        var meta = new byte[8];
        for (var i = 0; i < document.Entries.Count; i++)
        {
            var entry = document.Entries[i];
            var absoluteOffset = checked((uint)stream.Position);
            relativeOffsets[i] = checked(absoluteOffset - 8);

            var encodedText = entry.OriginalTextBytes.Length > 0 && string.Equals(entry.Text, entry.OriginalText, StringComparison.Ordinal)
                ? entry.OriginalTextBytes.ToArray()
                : _placeholders.Encode(entry.Text, _writeEncoding);
            for (var j = 0; j < encodedText.Length; j++)
            {
                encodedText[j] ^= 0xFF;
            }

            stream.Write(encodedText);
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(0, 4), entry.Meta0);
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(4, 4), entry.Meta1);
            stream.Write(meta);
        }

        var recordTableOffset = checked((uint)stream.Position);
        var tableItem = new byte[4];
        foreach (var offset in relativeOffsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tableItem, offset);
            stream.Write(tableItem);
        }

        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), recordTableOffset);
        return result;
    }

    private static uint ReadU32(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}
