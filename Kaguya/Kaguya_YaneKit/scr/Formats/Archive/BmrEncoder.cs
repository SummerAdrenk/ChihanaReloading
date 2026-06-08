using System.Buffers.Binary;

namespace Kaguya_YaneKit.Formats.Archive;

public static class BmrEncoder
{
    private const int MaxRleCount = 0x7FFF + 128;

    public static byte[] PackHuffmanOnly(byte[] data) => HuffmanEncode(data);

    public static byte[] Pack(byte[] data)
    {
        var core = EncodeCore(data);

        var output = new byte[0x14 + core.Huffman.Length];
        output[0] = (byte)'B';
        output[1] = (byte)'M';
        output[2] = (byte)'R';
        output[3] = (byte)core.Step;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), core.Key);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), core.UnpackedSize);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), core.Huffman.Length);
        core.Huffman.CopyTo(output.AsSpan(0x14));
        return output;
    }

    public static byte[] PackAn20(byte[] data)
    {
        var core = EncodeCore(data);
        var output = new byte[0x14 + core.Huffman.Length];
        output[0] = (byte)'B';
        output[1] = (byte)'M';
        output[2] = (byte)'R';
        output[3] = (byte)core.Step;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), core.Key);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), core.UnpackedSize);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), core.Huffman.Length);
        core.Huffman.CopyTo(output.AsSpan(0x14));
        return output;
    }

    private static BmrEncodedCore EncodeCore(byte[] data)
    {
        var packedRle = PackRle(data, step: 4);
        var step = packedRle is not null && packedRle.Length < data.Length ? 4 : 0;
        var source = step == 0 ? data : packedRle!;
        var transformed = BurrowsWheelerTransform(source, out var key);
        var mtf = MoveToFrontEncode(transformed);
        return new BmrEncodedCore(step, key, mtf.Length, HuffmanEncode(mtf));
    }

    private readonly record struct BmrEncodedCore(int Step, int Key, int UnpackedSize, byte[] Huffman);

    private static byte[]? PackRle(byte[] data, int step)
    {
        if (data.Length == 0)
        {
            return [];
        }

        using var output = new MemoryStream(data.Length);
        for (var lane = 0; lane < step; lane++)
        {
            if (lane >= data.Length)
            {
                break;
            }

            var values = new List<byte>((data.Length + step - 1) / step);
            for (var pos = lane; pos < data.Length; pos += step)
            {
                values.Add(data[pos]);
            }

            if (!WriteRleLane(values, output))
            {
                return null;
            }
        }

        return output.ToArray();
    }

    private static bool WriteRleLane(List<byte> values, Stream output)
    {
        output.WriteByte(values[0]);
        var previous = values[0];
        var index = 1;
        while (index < values.Count)
        {
            var current = values[index++];
            output.WriteByte(current);
            if (current != previous)
            {
                previous = current;
                continue;
            }

            var repeated = 0;
            while (repeated < MaxRleCount && index < values.Count && values[index] == current)
            {
                repeated++;
                index++;
            }

            WriteRleCount(output, repeated);
            if (index < values.Count)
            {
                current = values[index++];
                output.WriteByte(current);
            }

            previous = current;
        }

        return true;
    }

    private static void WriteRleCount(Stream output, int count)
    {
        if (count < 128)
        {
            output.WriteByte((byte)count);
            return;
        }

        var encoded = count - 128;
        output.WriteByte((byte)(0x80 | ((encoded >> 8) & 0x7F)));
        output.WriteByte((byte)(encoded & 0xFF));
    }

    private static byte[] BurrowsWheelerTransform(byte[] data, out int key)
    {
        var length = data.Length;
        if (length == 0)
        {
            key = 0;
            return [];
        }

        var order = BuildCyclicSuffixArray(data);
        var output = new byte[length];
        key = 0;
        for (var i = 0; i < order.Length; i++)
        {
            var start = order[i];
            if (start == 0)
            {
                key = i;
            }

            output[i] = data[start == 0 ? length - 1 : start - 1];
        }

        return output;
    }

    private static int[] BuildCyclicSuffixArray(byte[] data)
    {
        var length = data.Length;
        var order = new int[length];
        var tempOrder = new int[length];
        var ranks = new int[length];
        for (var i = 0; i < length; i++)
        {
            ranks[i] = data[i];
        }

        var newRanks = new int[length];
        var counts = new int[Math.Max(256, length)];
        var rankCount = 256;
        for (var span = 1; span < length; span <<= 1)
        {
            FillSequential(order);
            CountingSortByRank(order, tempOrder, ranks, span, rankCount, counts);
            CountingSortByRank(tempOrder, order, ranks, 0, rankCount, counts);

            var rank = 0;
            newRanks[order[0]] = 0;
            for (var i = 1; i < order.Length; i++)
            {
                var previous = order[i - 1];
                var current = order[i];
                if (ranks[previous] != ranks[current] ||
                    ranks[(previous + span) % length] != ranks[(current + span) % length])
                {
                    rank++;
                }

                newRanks[current] = rank;
            }

            (ranks, newRanks) = (newRanks, ranks);
            rankCount = rank + 1;
            if (rank == length - 1)
            {
                break;
            }
        }

        return order;
    }

    private static void FillSequential(int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i;
        }
    }

    private static void CountingSortByRank(
        int[] input,
        int[] output,
        int[] ranks,
        int offset,
        int rankCount,
        int[] counts)
    {
        Array.Clear(counts, 0, rankCount);
        var length = input.Length;
        for (var i = 0; i < input.Length; i++)
        {
            counts[GetRank(ranks, input[i], offset, length)]++;
        }

        for (var i = 1; i < rankCount; i++)
        {
            counts[i] += counts[i - 1];
        }

        for (var i = input.Length - 1; i >= 0; i--)
        {
            var value = input[i];
            var rank = GetRank(ranks, value, offset, length);
            output[--counts[rank]] = value;
        }
    }

    private static int GetRank(int[] ranks, int index, int offset, int length)
    {
        var rankIndex = index + offset;
        if (rankIndex >= length)
        {
            rankIndex -= length;
        }

        return ranks[rankIndex];
    }

    private static byte[] MoveToFrontEncode(byte[] data)
    {
        var table = new byte[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (byte)i;
        }

        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var value = data[i];
            var index = 0;
            while (table[index] != value)
            {
                index++;
            }

            output[i] = (byte)index;
            for (var j = index; j > 0; j--)
            {
                table[j] = table[j - 1];
            }

            table[0] = value;
        }

        return output;
    }

    private static byte[] HuffmanEncode(byte[] data)
    {
        var frequencies = new int[256];
        foreach (var value in data)
        {
            frequencies[value]++;
        }

        var root = BuildHuffmanTree(frequencies);
        var codes = new List<bool>[256];
        BuildCodes(root, [], codes);

        var writer = new MsbBitWriter();
        WriteTree(root, writer);
        foreach (var value in data)
        {
            foreach (var bit in codes[value])
            {
                writer.WriteBit(bit);
            }
        }

        return writer.ToArray();
    }

    private static HuffmanNode BuildHuffmanTree(int[] frequencies)
    {
        var queue = new PriorityQueue<HuffmanNode, HuffmanPriority>();
        var ordinal = 0;
        for (var symbol = 0; symbol < frequencies.Length; symbol++)
        {
            if (frequencies[symbol] == 0)
            {
                continue;
            }

            queue.Enqueue(new HuffmanNode((byte)symbol, frequencies[symbol]), new HuffmanPriority(frequencies[symbol], ordinal++));
        }

        if (queue.Count == 0)
        {
            return new HuffmanNode(0, 0);
        }

        while (queue.Count > 1)
        {
            var left = queue.Dequeue();
            var right = queue.Dequeue();
            var parent = new HuffmanNode(left, right);
            queue.Enqueue(parent, new HuffmanPriority(parent.Frequency, ordinal++));
        }

        return queue.Dequeue();
    }

    private static void BuildCodes(HuffmanNode node, List<bool> prefix, List<bool>[] codes)
    {
        if (node.Symbol is byte symbol)
        {
            codes[symbol] = new List<bool>(prefix);
            return;
        }

        prefix.Add(false);
        BuildCodes(node.Left!, prefix, codes);
        prefix[^1] = true;
        BuildCodes(node.Right!, prefix, codes);
        prefix.RemoveAt(prefix.Count - 1);
    }

    private static void WriteTree(HuffmanNode node, MsbBitWriter writer)
    {
        if (node.Symbol is byte symbol)
        {
            writer.WriteBit(false);
            writer.WriteBits(symbol, 8);
            return;
        }

        writer.WriteBit(true);
        WriteTree(node.Left!, writer);
        WriteTree(node.Right!, writer);
    }

    private sealed class HuffmanNode
    {
        public HuffmanNode(byte symbol, int frequency)
        {
            Symbol = symbol;
            Frequency = frequency;
        }

        public HuffmanNode(HuffmanNode left, HuffmanNode right)
        {
            Left = left;
            Right = right;
            Frequency = left.Frequency + right.Frequency;
        }

        public byte? Symbol { get; }
        public HuffmanNode? Left { get; }
        public HuffmanNode? Right { get; }
        public int Frequency { get; }
    }

    private readonly record struct HuffmanPriority(int Frequency, int Ordinal) : IComparable<HuffmanPriority>
    {
        public int CompareTo(HuffmanPriority other)
        {
            var cmp = Frequency.CompareTo(other.Frequency);
            return cmp != 0 ? cmp : Ordinal.CompareTo(other.Ordinal);
        }
    }

    private sealed class MsbBitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _current;
        private int _bits;

        public void WriteBit(bool bit)
        {
            _current = (_current << 1) | (bit ? 1 : 0);
            _bits++;
            if (_bits == 8)
            {
                FlushByte();
            }
        }

        public void WriteBits(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                WriteBit(((value >> i) & 1) != 0);
            }
        }

        public byte[] ToArray()
        {
            if (_bits > 0)
            {
                _current <<= 8 - _bits;
                FlushByte();
            }

            return _bytes.ToArray();
        }

        private void FlushByte()
        {
            _bytes.Add((byte)_current);
            _current = 0;
            _bits = 0;
        }
    }
}
