// ============================================================================
// MessageVer3DatCodec.cs
// 旧式 [SCR-MESSAGE]ver + u8 version=3 的独立二进制 codec
//
// block 明文结构:
//   cstring formatName
//   u8 itemCount
//   repeat itemCount:
//       u8 voiceCount
//       voiceCount * cstring voice
//       cstring message
// ============================================================================
using System.Text;
using Kaguya_YaneKit.Text.MessageDat.Model;

namespace Kaguya_YaneKit.Text.MessageDat;

public sealed class MessageVer3DatCodec
{
    private readonly Encoding _readEncoding;
    private readonly Encoding _writeEncoding;
    private readonly MessagePlaceholderConfig _placeholders;

    public MessageVer3DatCodec(
        Encoding readEncoding,
        Encoding writeEncoding,
        MessagePlaceholderConfig? placeholders = null)
    {
        _readEncoding = readEncoding ?? throw new ArgumentNullException(nameof(readEncoding));
        _writeEncoding = writeEncoding ?? throw new ArgumentNullException(nameof(writeEncoding));
        _placeholders = placeholders ?? MessagePlaceholderConfig.Empty;
    }

    public static bool IsVersion3(ReadOnlySpan<byte> source)
    {
        return TryReadLegacyVersion(source, out var version) &&
            version == MessageVer3Document.Version3;
    }

    public static bool IsLegacyVersion(ReadOnlySpan<byte> source)
    {
        return TryReadLegacyVersion(source, out _);
    }

    private static bool TryReadLegacyVersion(ReadOnlySpan<byte> source, out byte version)
    {
        var magic = Encoding.ASCII.GetBytes(MessageVer3Document.MagicPrefix);
        version = 0;
        if (source.Length < magic.Length + 1 || !source[..magic.Length].SequenceEqual(magic))
        {
            return false;
        }

        version = source[magic.Length];
        return version is MessageVer3Document.Version2 or MessageVer3Document.Version3;
    }

    public MessageVer3Document Read(ReadOnlySpan<byte> source)
    {
        using var stream = new MemoryStream(source.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var magic = Encoding.ASCII.GetString(reader.ReadBytes(MessageVer3Document.MagicPrefix.Length));
        if (magic != MessageVer3Document.MagicPrefix)
        {
            throw new InvalidDataException($"Invalid message ver3 magic: {magic}");
        }

        var version = reader.ReadByte();
        if (version is not (MessageVer3Document.Version2 or MessageVer3Document.Version3))
        {
            throw new InvalidDataException($"Unsupported legacy message version byte: 0x{version:X2}");
        }

        var encryptionFlag = reader.ReadByte();
        var headerHasXorKey = version == MessageVer3Document.Version3;
        var document = new MessageVer3Document
        {
            Version = version,
            HeaderHasXorKey = headerHasXorKey,
            EncryptionFlag = encryptionFlag,
            Encrypted = encryptionFlag != 0,
            XorKey = headerHasXorKey ? reader.ReadByte() : encryptionFlag
        };

        while (stream.Position < stream.Length)
        {
            var blockLength = reader.ReadInt32();
            if (blockLength < 0 || blockLength > stream.Length - stream.Position)
            {
                throw new InvalidDataException($"Invalid message ver3 block length at 0x{stream.Position - 4:X}: {blockLength}");
            }

            var block = reader.ReadBytes(blockLength);
            XorIfNeeded(block, document);
            document.Blocks.Add(ReadBlock(block, document.Version));
        }

        return document;
    }

    public byte[] Write(MessageVer3Document document, bool? encrypted = null, byte? xorKey = null)
    {
        if (encrypted is not null)
        {
            document.Encrypted = encrypted.Value;
            document.EncryptionFlag = encrypted.Value
                ? (document.EncryptionFlag == 0 ? (byte)0xff : document.EncryptionFlag)
                : (byte)0;
        }
        if (!document.HeaderHasXorKey && encrypted == true && xorKey is null)
        {
            document.XorKey = document.EncryptionFlag == 0 ? (byte)0xff : document.EncryptionFlag;
        }
        document.XorKey = xorKey ?? document.XorKey;
        if (!document.HeaderHasXorKey)
        {
            document.EncryptionFlag = document.Encrypted ? document.XorKey : (byte)0;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(MessageVer3Document.MagicPrefix));
        writer.Write(document.Version);
        writer.Write(document.EncryptionFlag);
        if (document.HeaderHasXorKey)
        {
            writer.Write(document.XorKey);
        }

        foreach (var block in document.Blocks)
        {
            var bytes = WriteBlock(block, document.Version);
            XorIfNeeded(bytes, document);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private MessageVer3Block ReadBlock(byte[] blockBytes, byte version)
    {
        var offset = 0;
        var rawFormatName = ReadCString(blockBytes, ref offset);
        var block = new MessageVer3Block
        {
            RawFormatNameBytes = rawFormatName,
            FormatName = _placeholders.Decode(rawFormatName, _readEncoding)
        };

        if (offset >= blockBytes.Length)
        {
            throw new InvalidDataException("message ver3 block is missing item count.");
        }

        var itemCount = blockBytes[offset++];
        for (var i = 0; i < itemCount; i++)
        {
            if (offset >= blockBytes.Length)
            {
                throw new InvalidDataException($"message ver3 block item {i} is truncated.");
            }

            var item = new MessageVer3Item();
            if (version == MessageVer3Document.Version2)
            {
                var rawVoice = ReadCString(blockBytes, ref offset);
                item.Voices.Add(new MessageVer3String
                {
                    RawBytes = rawVoice,
                    Text = _placeholders.Decode(rawVoice, _readEncoding)
                });
            }
            else
            {
                var voiceCount = blockBytes[offset++];
                for (var j = 0; j < voiceCount; j++)
                {
                    var rawVoice = ReadCString(blockBytes, ref offset);
                    item.Voices.Add(new MessageVer3String
                    {
                        RawBytes = rawVoice,
                        Text = _placeholders.Decode(rawVoice, _readEncoding)
                    });
                }
            }

            var rawMessage = ReadCString(blockBytes, ref offset);
            item.Message = new MessageVer3String
            {
                RawBytes = rawMessage,
                Text = _placeholders.Decode(rawMessage, _readEncoding)
            };
            block.Items.Add(item);
        }

        if (offset != blockBytes.Length)
        {
            throw new InvalidDataException($"message ver3 block has trailing bytes: {blockBytes.Length - offset}");
        }

        return block;
    }

    private byte[] WriteBlock(MessageVer3Block block, byte version)
    {
        using var stream = new MemoryStream();
        WriteCString(stream, TryUseRawBytes(block.RawFormatNameBytes, block.FormatName));
        if (block.Items.Count > byte.MaxValue)
        {
            throw new InvalidDataException("message ver3 block has too many items.");
        }

        stream.WriteByte((byte)block.Items.Count);
        foreach (var item in block.Items)
        {
            if (version == MessageVer3Document.Version2)
            {
                var voice = item.Voices.Count > 0 ? item.Voices[0] : new MessageVer3String();
                WriteCString(stream, TryUseRawBytes(voice.RawBytes, voice.Text));
            }
            else
            {
                if (item.Voices.Count > byte.MaxValue)
                {
                    throw new InvalidDataException("message ver3 item has too many voices.");
                }

                stream.WriteByte((byte)item.Voices.Count);
                foreach (var voice in item.Voices)
                {
                    WriteCString(stream, TryUseRawBytes(voice.RawBytes, voice.Text));
                }
            }

            WriteCString(stream, TryUseRawBytes(item.Message.RawBytes, item.Message.Text));
        }

        return stream.ToArray();
    }

    private byte[] TryUseRawBytes(byte[] rawBytes, string currentText)
    {
        if (_placeholders.Decode(rawBytes, _readEncoding) == currentText)
        {
            return rawBytes.ToArray();
        }

        return _placeholders.Encode(currentText, _writeEncoding);
    }

    private static byte[] ReadCString(byte[] bytes, ref int offset)
    {
        var start = offset;
        while (offset < bytes.Length && bytes[offset] != 0)
        {
            offset++;
        }

        if (offset >= bytes.Length)
        {
            throw new InvalidDataException("message ver3 cstring is not terminated.");
        }

        var result = bytes[start..offset].ToArray();
        offset++;
        return result;
    }

    private static void WriteCString(Stream stream, byte[] bytes)
    {
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
    }

    private static void XorIfNeeded(byte[] bytes, MessageVer3Document document)
    {
        if (!document.Encrypted)
        {
            return;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= document.XorKey;
        }
    }
}
