using System.Buffers.Binary;
using System.Text;

namespace Kaguya_YaneKit.Text.Tblstr;

public sealed class TblSupportCodec
{
    private static readonly Encoding ShiftJis = CreateShiftJisEncoding();

    private static Encoding CreateShiftJisEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    public TblSupportDocument Read(string fileName, byte[] data)
    {
        var normalized = Path.GetFileName(fileName).ToLowerInvariant();
        return normalized switch
        {
            "value.tbl" => ReadLineTable(fileName, data, "value"),
            "globalvalue.tbl" => ReadLineTable(fileName, data, "globalvalue"),
            "label.tbl" => ReadLabelTable(fileName, data),
            "eventfg.tbl" => ReadEventFgTable(fileName, data),
            _ => throw new InvalidDataException($"Unsupported TBL file: {fileName}")
        };
    }

    public byte[] Write(TblSupportDocument document)
    {
        return document.Kind switch
        {
            "value" or "globalvalue" => WriteLineTable(document.LineTable ?? throw new InvalidDataException("Missing line table.")),
            "label" => WriteLabelTable(document.LabelTable ?? throw new InvalidDataException("Missing label table.")),
            "eventfg" => WriteEventFgTable(document.EventFgTable ?? throw new InvalidDataException("Missing EventFg table.")),
            _ => throw new InvalidDataException($"Unsupported TBL kind: {document.Kind}")
        };
    }

    private static TblSupportDocument ReadLineTable(string fileName, byte[] data, string kind)
    {
        var table = new TblLineTable();
        var start = 0;
        var index = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == 0x0D && i + 1 < data.Length && data[i + 1] == 0x0A)
            {
                AddLine(table, data.AsSpan(start, i - start), index++);
                i++;
                start = i + 1;
            }
        }

        if (start < data.Length)
        {
            AddLine(table, data.AsSpan(start), index);
        }

        return new TblSupportDocument
        {
            Kind = kind,
            FileName = Path.GetFileName(fileName),
            LineTable = table
        };
    }

    private static void AddLine(TblLineTable table, ReadOnlySpan<byte> encodedLine, int index)
    {
        var decoded = new byte[encodedLine.Length];
        for (var i = 0; i < encodedLine.Length; i++)
        {
            decoded[i] = (byte)~encodedLine[i];
        }

        table.Entries.Add(new TblLineEntry
        {
            Index = index,
            Name = ShiftJis.GetString(decoded)
        });
    }

    private static byte[] WriteLineTable(TblLineTable table)
    {
        using var output = new MemoryStream();
        foreach (var entry in table.Entries.OrderBy(e => e.Index))
        {
            var bytes = ShiftJis.GetBytes(entry.Name);
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)~bytes[i];
            }

            output.Write(bytes);
            output.WriteByte(0x0D);
            output.WriteByte(0x0A);
        }

        return output.ToArray();
    }

    private static TblSupportDocument ReadLabelTable(string fileName, byte[] data)
    {
        var offset = 0;
        var index = 0;
        var table = new TblLabelTable();
        while (offset < data.Length)
        {
            var scriptFile = ReadLengthString(data, ref offset);
            var label = ReadLengthString(data, ref offset);
            EnsureAvailable(data, offset, 4);
            var targetOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;
            table.Entries.Add(new TblLabelEntry
            {
                Index = index++,
                ScriptFile = scriptFile,
                Label = label,
                TargetOffset = targetOffset
            });
        }

        return new TblSupportDocument
        {
            Kind = "label",
            FileName = Path.GetFileName(fileName),
            LabelTable = table
        };
    }

    private static byte[] WriteLabelTable(TblLabelTable table)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4];
        foreach (var entry in table.Entries.OrderBy(e => e.Index))
        {
            WriteLengthString(output, entry.ScriptFile);
            WriteLengthString(output, entry.Label);
            BinaryPrimitives.WriteInt32LittleEndian(buffer, entry.TargetOffset);
            output.Write(buffer);
        }

        return output.ToArray();
    }

    private static TblSupportDocument ReadEventFgTable(string fileName, byte[] data)
    {
        if (data.Length < 5)
        {
            throw new InvalidDataException("EventFg.tbl is too small.");
        }

        var key = data[0];
        var payload = new byte[data.Length - 1];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(data[i + 1] ^ key);
        }

        var reader = new EventFgReader(payload);
        var table = new TblEventFgTable { XorKey = key };
        var characterCount = reader.ReadInt32();
        for (var charIndex = 0; charIndex < characterCount; charIndex++)
        {
            var character = new TblEventFgCharacter
            {
                Index = charIndex,
                Name = reader.ReadCString()
            };
            var slotCount = reader.ReadInt32();
            for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                var slot = new TblEventFgSlot
                {
                    Index = slotIndex,
                    Name = reader.ReadCString()
                };
                var eventCount = reader.ReadInt32();
                for (var eventIndex = 0; eventIndex < eventCount; eventIndex++)
                {
                    slot.Events.Add(new TblEventFgEvent
                    {
                        Index = eventIndex,
                        Field0 = reader.ReadInt32(),
                        Field1 = reader.ReadInt32(),
                        Name = reader.ReadCString()
                    });
                }

                character.Slots.Add(slot);
            }

            table.Characters.Add(character);
        }

        var kaisouCount = reader.ReadInt32();
        for (var kaisouIndex = 0; kaisouIndex < kaisouCount; kaisouIndex++)
        {
            var kaisou = new TblEventFgKaisou
            {
                Index = kaisouIndex,
                Name = reader.ReadCString()
            };
            var slotCount = reader.ReadInt32();
            for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                kaisou.Slots.Add(new TblEventFgKaiSlot
                {
                    Index = slotIndex,
                    Field0 = reader.ReadInt32(),
                    SlotName = reader.ReadCString(),
                    ScriptName = reader.ReadCString()
                });
            }

            table.Kaisou.Add(kaisou);
        }

        reader.EnsureEnd();
        return new TblSupportDocument
        {
            Kind = "eventfg",
            FileName = Path.GetFileName(fileName),
            EventFgTable = table
        };
    }

    private static byte[] WriteEventFgTable(TblEventFgTable table)
    {
        using var payload = new MemoryStream();
        WriteInt32(payload, table.Characters.Count);
        foreach (var character in table.Characters.OrderBy(e => e.Index))
        {
            WriteCString(payload, character.Name);
            WriteInt32(payload, character.Slots.Count);
            foreach (var slot in character.Slots.OrderBy(e => e.Index))
            {
                WriteCString(payload, slot.Name);
                WriteInt32(payload, slot.Events.Count);
                foreach (var evt in slot.Events.OrderBy(e => e.Index))
                {
                    WriteInt32(payload, evt.Field0);
                    WriteInt32(payload, evt.Field1);
                    WriteCString(payload, evt.Name);
                }
            }
        }

        WriteInt32(payload, table.Kaisou.Count);
        foreach (var kaisou in table.Kaisou.OrderBy(e => e.Index))
        {
            WriteCString(payload, kaisou.Name);
            WriteInt32(payload, kaisou.Slots.Count);
            foreach (var slot in kaisou.Slots.OrderBy(e => e.Index))
            {
                WriteInt32(payload, slot.Field0);
                WriteCString(payload, slot.SlotName);
                WriteCString(payload, slot.ScriptName);
            }
        }

        var plain = payload.ToArray();
        var output = new byte[plain.Length + 1];
        output[0] = table.XorKey;
        for (var i = 0; i < plain.Length; i++)
        {
            output[i + 1] = (byte)(plain[i] ^ table.XorKey);
        }

        return output;
    }

    private static string ReadLengthString(byte[] data, ref int offset)
    {
        EnsureAvailable(data, offset, 4);
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;
        if (length < 0)
        {
            throw new InvalidDataException($"Negative string length in label.tbl: {length}");
        }

        EnsureAvailable(data, offset, length);
        var text = ShiftJis.GetString(data, offset, length);
        offset += length;
        return text;
    }

    private static void WriteLengthString(Stream output, string value)
    {
        var bytes = ShiftJis.GetBytes(value);
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, bytes.Length);
        output.Write(buffer);
        output.Write(bytes);
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }

    private static void WriteCString(Stream output, string value)
    {
        var bytes = ShiftJis.GetBytes(value);
        output.Write(bytes);
        output.WriteByte(0);
    }

    private static void EnsureAvailable(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new InvalidDataException("TBL file ended unexpectedly.");
        }
    }

    private sealed class EventFgReader
    {
        private readonly byte[] _data;
        private int _offset;

        public EventFgReader(byte[] data)
        {
            _data = data;
        }

        public int ReadInt32()
        {
            EnsureAvailable(_data, _offset, 4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        public string ReadCString()
        {
            var start = _offset;
            while (_offset < _data.Length && _data[_offset] != 0)
            {
                _offset++;
            }

            if (_offset >= _data.Length)
            {
                throw new InvalidDataException("EventFg.tbl string is not null-terminated.");
            }

            var text = ShiftJis.GetString(_data, start, _offset - start);
            _offset++;
            return text;
        }

        public void EnsureEnd()
        {
            if (_offset != _data.Length)
            {
                throw new InvalidDataException($"EventFg.tbl has unread trailing bytes: {_data.Length - _offset}");
            }
        }
    }
}
