using System.Text;

namespace Kaguya_YaneKit.Formats.Pe;

internal sealed class PeImage
{
    private const ushort Pe32Magic = 0x10B;

    private byte[] _data;

    private PeImage(byte[] data)
    {
        _data = data;
        PeHeaderOffset = ReadU32(data, 0x3C);
        if (PeHeaderOffset > data.Length - 4 || data[(int)PeHeaderOffset] != 'P' || data[(int)PeHeaderOffset + 1] != 'E')
        {
            throw new InvalidDataException("Not a PE file.");
        }

        Machine = ReadU16(data, (int)PeHeaderOffset + 4);
        SectionCount = ReadU16(data, (int)PeHeaderOffset + 6);
        OptionalHeaderSize = ReadU16(data, (int)PeHeaderOffset + 20);
        OptionalHeaderOffset = (int)PeHeaderOffset + 24;
        OptionalHeaderMagic = ReadU16(data, OptionalHeaderOffset);
        if (OptionalHeaderMagic != Pe32Magic)
        {
            throw new NotSupportedException($"Only PE32 is supported for now. OptionalHeader.Magic=0x{OptionalHeaderMagic:X4}");
        }

        ImageBase = ReadU32(data, OptionalHeaderOffset + 28);
        SectionAlignment = ReadU32(data, OptionalHeaderOffset + 32);
        FileAlignment = ReadU32(data, OptionalHeaderOffset + 36);
        SizeOfImage = ReadU32(data, OptionalHeaderOffset + 56);
        SizeOfHeaders = ReadU32(data, OptionalHeaderOffset + 60);
        SectionTableOffset = OptionalHeaderOffset + OptionalHeaderSize;

        for (var i = 0; i < SectionCount; i++)
        {
            Sections.Add(ReadSection(i));
        }
    }

    public uint PeHeaderOffset { get; }
    public ushort Machine { get; }
    public ushort SectionCount { get; }
    public ushort OptionalHeaderSize { get; }
    public ushort OptionalHeaderMagic { get; }
    public int OptionalHeaderOffset { get; }
    public int SectionTableOffset { get; }
    public uint ImageBase { get; }
    public uint SectionAlignment { get; }
    public uint FileAlignment { get; }
    public uint SizeOfImage { get; private set; }
    public uint SizeOfHeaders { get; }
    public List<PeSection> Sections { get; } = [];

    public static PeImage Read(byte[] data) => new(data);

    public PeSection? FindSectionByRva(uint rva)
    {
        foreach (var section in Sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.Rva && rva < section.Rva + span)
            {
                return section;
            }
        }

        return null;
    }

    public PeSection? FindSectionByFileOffset(int fileOffset)
    {
        foreach (var section in Sections)
        {
            if (fileOffset >= section.RawOffset && fileOffset < section.RawOffset + section.RawSize)
            {
                return section;
            }
        }

        return null;
    }

    public bool TryRvaToFileOffset(uint rva, out int fileOffset, out PeSection section)
    {
        section = FindSectionByRva(rva)!;
        if (section is null)
        {
            fileOffset = 0;
            return false;
        }

        fileOffset = checked((int)(section.RawOffset + (rva - section.Rva)));
        return fileOffset >= 0 && fileOffset < _data.Length;
    }

    public bool TryFileOffsetToRva(int fileOffset, out uint rva, out PeSection section)
    {
        section = FindSectionByFileOffset(fileOffset)!;
        if (section is null)
        {
            rva = 0;
            return false;
        }

        rva = checked((uint)(section.Rva + (fileOffset - section.RawOffset)));
        return true;
    }

    public uint VaToRva(uint va) => checked(va - ImageBase);
    public uint RvaToVa(uint rva) => checked(ImageBase + rva);

    public int AddSection(byte[] data, string sectionName, uint characteristics, out uint newSectionRva)
    {
        if (Encoding.ASCII.GetByteCount(sectionName) > 8)
        {
            throw new ArgumentException("Section name must be at most 8 ASCII bytes.", nameof(sectionName));
        }

        var sectionHeaderOffset = SectionTableOffset + Sections.Count * 40;
        var firstRaw = Sections.Where(s => s.RawOffset != 0).Min(s => s.RawOffset);
        if (firstRaw < sectionHeaderOffset + 40)
        {
            throw new InvalidDataException("PE header has no room for a new section header.");
        }

        var last = Sections.OrderBy(s => s.Rva).Last();
        newSectionRva = Align(last.Rva + Math.Max(last.VirtualSize, last.RawSize), SectionAlignment);
        var rawOffset = Align(Sections.Max(s => s.RawOffset + s.RawSize), FileAlignment);
        var rawSize = Align((uint)data.Length, FileAlignment);

        EnsureLength(checked((int)(rawOffset + rawSize)));
        Array.Clear(_data, checked((int)rawOffset), checked((int)rawSize));
        Buffer.BlockCopy(data, 0, _data, checked((int)rawOffset), data.Length);

        WriteSectionHeader(sectionHeaderOffset, sectionName, (uint)data.Length, newSectionRva, rawSize, rawOffset, characteristics);
        WriteU16(_data, (int)PeHeaderOffset + 6, checked((ushort)(Sections.Count + 1)));
        SizeOfImage = Align(newSectionRva + (uint)data.Length, SectionAlignment);
        WriteU32(_data, OptionalHeaderOffset + 56, SizeOfImage);

        Sections.Add(new PeSection(sectionName, newSectionRva, (uint)data.Length, rawOffset, rawSize, characteristics));
        return checked((int)rawOffset);
    }

    public byte[] GetData() => _data;

    private PeSection ReadSection(int index)
    {
        var offset = SectionTableOffset + index * 40;
        var nameBytes = _data.Skip(offset).Take(8).TakeWhile(b => b != 0).ToArray();
        var name = Encoding.ASCII.GetString(nameBytes);
        return new PeSection(
            name,
            ReadU32(_data, offset + 12),
            ReadU32(_data, offset + 8),
            ReadU32(_data, offset + 20),
            ReadU32(_data, offset + 16),
            ReadU32(_data, offset + 36));
    }

    private void WriteSectionHeader(int offset, string name, uint virtualSize, uint rva, uint rawSize, uint rawOffset, uint characteristics)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        Array.Clear(_data, offset, 40);
        Buffer.BlockCopy(nameBytes, 0, _data, offset, nameBytes.Length);
        WriteU32(_data, offset + 8, virtualSize);
        WriteU32(_data, offset + 12, rva);
        WriteU32(_data, offset + 16, rawSize);
        WriteU32(_data, offset + 20, rawOffset);
        WriteU32(_data, offset + 36, characteristics);
    }

    private void EnsureLength(int length)
    {
        if (_data.Length >= length)
        {
            return;
        }

        Array.Resize(ref _data, length);
    }

    public static uint Align(uint value, uint alignment) =>
        alignment == 0 ? value : (value + alignment - 1) / alignment * alignment;

    public static ushort ReadU16(byte[] data, int offset) =>
        BitConverter.ToUInt16(data, offset);

    public static uint ReadU32(byte[] data, int offset) =>
        BitConverter.ToUInt32(data, offset);

    public static void WriteU16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    public static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}

internal sealed record PeSection(string Name, uint Rva, uint VirtualSize, uint RawOffset, uint RawSize, uint Characteristics)
{
    public bool IsExecutable => (Characteristics & 0x20000000) != 0;
    public bool IsReadable => (Characteristics & 0x40000000) != 0;
}
