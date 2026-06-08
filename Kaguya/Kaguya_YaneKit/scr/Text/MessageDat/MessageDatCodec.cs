// ============================================================================
// MessageDatCodec.cs
// message.dat 二进制编解码器
//
// 文件格式 (ver4.0):
//   [Magic]    "[SCR-MESSAGE]ver4.0" + 加密标志(bool) + XOR 密钥(byte)
//   [Names]    角色名列表 (u32 count + u16 length + bytes per entry)
//   [Choices]  选项文本列表 (同上格式)
//   [Messages] 消息条目列表 (i32 blockLen + 加密块: i32 textLen + text + u8 voiceCount + null-terminated UTF-16 voices)
//   [Commands] 命令列表 (i32 id + u8 paramCount + i32 per param)
//   [RawTail]  尾部原始数据 (原样保留)
//
// 核心算法:
//   XOR 加密 - 当 Encrypted=true 时, 字符串字节和消息块与 XorKey 逐字节异或
//   编码转换 - 读取时用 _readEncoding, 写入时用 _writeEncoding (支持 cp932/GBK/UTF-8 等)
//   往返保真 - TryUseRawBytes() 在文本未变时优先使用原始字节, 避免编码差异
//   占位符处理 - 通过 MessagePlaceholderConfig 在字节与占位符文本之间双向转换
//
// 依赖: MessagePlaceholderConfig, MessageDatDocument, MessageEntry, MessageCommand
// 被依赖: InteractiveSession (CLI 主流程)
// ============================================================================
using System.Text;
using Kaguya_YaneKit.Text.MessageDat.Model;

namespace Kaguya_YaneKit.Text.MessageDat;

public sealed class MessageDatCodec
{
    private readonly Encoding _readEncoding;
    private readonly Encoding _writeEncoding;
    private readonly MessagePlaceholderConfig _placeholders;

    public MessageDatCodec(
        Encoding readEncoding,
        Encoding writeEncoding,
        MessagePlaceholderConfig? placeholders = null)
    {
        _readEncoding = readEncoding ?? throw new ArgumentNullException(nameof(readEncoding));
        _writeEncoding = writeEncoding ?? throw new ArgumentNullException(nameof(writeEncoding));
        _placeholders = placeholders ?? MessagePlaceholderConfig.Empty;
    }

    public MessageDatDocument Read(ReadOnlySpan<byte> source)
    {
        using var stream = new MemoryStream(source.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var magicBytes = reader.ReadBytes(MessageDatDocument.Magic.Length);
        var magic = Encoding.ASCII.GetString(magicBytes);
        if (magic != MessageDatDocument.Magic)
        {
            throw new InvalidDataException($"Invalid message.dat magic: {magic}");
        }

        var document = new MessageDatDocument
        {
            Encrypted = reader.ReadByte() != 0,
            XorKey = reader.ReadByte()
        };

        ReadStringList(reader, document.Names, document.RawNameBytes, document);
        ReadStringList(reader, document.Choices, document.RawChoiceBytes, document);

        var messageCount = reader.ReadInt32();
        for (var i = 0; i < messageCount; i++)
        {
            document.Messages.Add(ReadMessage(reader, document));
        }

        var commandCount = reader.ReadInt32();
        for (var i = 0; i < commandCount; i++)
        {
            document.Commands.Add(ReadCommand(reader));
        }

        var remaining = checked((int)(stream.Length - stream.Position));
        document.RawTail = remaining > 0 ? reader.ReadBytes(remaining) : [];
        return document;
    }

    public byte[] Write(MessageDatDocument document, bool? encrypted = null, byte? xorKey = null)
    {
        document.Encrypted = encrypted ?? document.Encrypted;
        document.XorKey = xorKey ?? document.XorKey;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(MessageDatDocument.Magic));
        writer.Write(document.Encrypted);
        writer.Write(document.XorKey);
        WriteStringList(writer, document.Names, document.RawNameBytes, document);
        WriteStringList(writer, document.Choices, document.RawChoiceBytes, document);

        writer.Write(document.Messages.Count);
        foreach (var message in document.Messages)
        {
            WriteMessage(writer, message, document);
        }

        writer.Write(document.Commands.Count);
        foreach (var command in document.Commands)
        {
            WriteCommand(writer, command);
        }

        if (document.RawTail.Length > 0)
        {
            writer.Write(document.RawTail);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static Encoding ResolveEncoding(string? value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (string.IsNullOrWhiteSpace(value))
        {
            return Encoding.GetEncoding(932);
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "cp932" or "sjis" or "shift-jis" or "shift_jis" => Encoding.GetEncoding(932),
            "cp936" or "gbk" => Encoding.GetEncoding(936),
            "utf8" or "utf-8" => Encoding.UTF8,
            _ => int.TryParse(value, out var codePage)
                ? Encoding.GetEncoding(codePage)
                : Encoding.GetEncoding(value)
        };
    }

    private void ReadStringList(BinaryReader reader, List<string> target, List<byte[]> rawTarget, MessageDatDocument document)
    {
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var length = reader.ReadUInt16();
            var bytes = reader.ReadBytes(length);
            XorIfNeeded(bytes, document);
            rawTarget.Add(bytes.ToArray());
            target.Add(_placeholders.Decode(bytes, _readEncoding));
        }
    }

    private void WriteStringList(BinaryWriter writer, IReadOnlyList<string> values, IReadOnlyList<byte[]> rawValues, MessageDatDocument document)
    {
        writer.Write(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            var bytes = TryUseRawBytes(rawValues, i, value) ?? _placeholders.Encode(value, _writeEncoding);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new InvalidDataException("message.dat string entry is too large.");
            }

            XorIfNeeded(bytes, document);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }
    }

    private MessageEntry ReadMessage(BinaryReader reader, MessageDatDocument document)
    {
        var blockLength = reader.ReadInt32();
        var block = reader.ReadBytes(blockLength);
        XorIfNeeded(block, document);

        using var stream = new MemoryStream(block, writable: false);
        using var blockReader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false);
        var message = new MessageEntry();

        var textLength = blockReader.ReadInt32();
        var textBytes = blockReader.ReadBytes(textLength);
        message.Text = _placeholders.Decode(textBytes, _readEncoding);
        message.RawTextBytes = textBytes.ToArray();

        var voiceCount = blockReader.ReadByte();
        for (var i = 0; i < voiceCount; i++)
        {
            message.Voices.Add(ReadNullTerminatedUtf16(blockReader));
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("message block has unread trailing bytes.");
        }

        return message;
    }

    private void WriteMessage(BinaryWriter writer, MessageEntry message, MessageDatDocument document)
    {
        using var stream = new MemoryStream();
        using (var blockWriter = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            var textBytes = TryUseRawBytes(message.RawTextBytes, message.Text) ?? _placeholders.Encode(message.Text, _writeEncoding);
            blockWriter.Write(textBytes.Length);
            blockWriter.Write(textBytes);

            if (message.Voices.Count > byte.MaxValue)
            {
                throw new InvalidDataException("message entry has too many voices.");
            }

            blockWriter.Write((byte)message.Voices.Count);
            foreach (var voice in message.Voices)
            {
                blockWriter.Write(Encoding.Unicode.GetBytes(voice));
                blockWriter.Write((ushort)0);
            }
        }

        var block = stream.ToArray();
        XorIfNeeded(block, document);
        writer.Write(block.Length);
        writer.Write(block);
    }

    private static MessageCommand ReadCommand(BinaryReader reader)
    {
        var command = new MessageCommand
        {
            Id = reader.ReadInt32()
        };

        var count = reader.ReadByte();
        for (var i = 0; i < count; i++)
        {
            command.Params.Add(reader.ReadInt32());
        }

        return command;
    }

    private byte[]? TryUseRawBytes(IReadOnlyList<byte[]> rawValues, int index, string currentText)
    {
        if (index >= rawValues.Count)
        {
            return null;
        }

        return TryUseRawBytes(rawValues[index], currentText);
    }

    private byte[]? TryUseRawBytes(byte[] rawBytes, string currentText)
    {
        if (rawBytes.Length == 0 && currentText.Length != 0)
        {
            return null;
        }

        return _placeholders.Decode(rawBytes, _readEncoding) == currentText
            ? rawBytes.ToArray()
            : null;
    }

    private static void WriteCommand(BinaryWriter writer, MessageCommand command)
    {
        if (command.Params.Count > byte.MaxValue)
        {
            throw new InvalidDataException("message command has too many params.");
        }

        writer.Write(command.Id);
        writer.Write((byte)command.Params.Count);
        foreach (var param in command.Params)
        {
            writer.Write(param);
        }
    }

    private static string ReadNullTerminatedUtf16(BinaryReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var value = reader.ReadUInt16();
            if (value == 0)
            {
                break;
            }

            bytes.Add((byte)(value & 0xff));
            bytes.Add((byte)(value >> 8));
        }

        return Encoding.Unicode.GetString(bytes.ToArray());
    }

    private static void XorIfNeeded(byte[] bytes, MessageDatDocument document)
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
