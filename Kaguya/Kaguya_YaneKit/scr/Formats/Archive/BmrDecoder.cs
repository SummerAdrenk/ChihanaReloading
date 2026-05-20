// ============================================================================
// BmrDecoder.cs
// BMR 压缩格式解码器: LINK 档案内嵌的自定义压缩算法
//
// BMR 文件头 (0x14 字节):
//   [0..2]  "BMR"        -- 魔数
//   [3]     step         -- RLE 步长 (0 = 跳过 RLE 阶段)
//   [4..7]  finalSize    -- 最终解压大小
//   [8..B]  key          -- BWT 逆变换的起始位置索引
//   [C..F]  unpackedSize -- Huffman 解码后的中间缓冲区大小
//
// 解压流程 (Unpack):
//   1. UnpackHuffman    -- 从偏移 0x14 开始, MSB-first 位流读取
//      - CreateHuffmanTree: 递归构建二叉树 (bit=1 -> 内部节点, bit=0 -> 8-bit 叶)
//      - 逐符号遍历树解码, 输出 unpackedSize 字节
//
//   2. UndoMoveToFront  -- Move-To-Front 逆变换
//      - 维护 256 字节字典, 输入值为字典索引
//      - 取出对应字节, 将其移到字典头部, 恢复原始字节值
//
//   3. Decode (BWT 逆变换) -- Burrows-Wheeler 逆变换
//      - 统计字节频率 -> 前缀和 -> 分布表
//      - 从 key 位置开始沿分布表链式遍历, 恢复原始序列
//
//   4. DecompressRLE    -- 步进式 RLE 解压 (仅当 step != 0)
//      - 以 step 为步长交错填充 result[]
//      - 连续重复字节用 1~2 字节计数编码:
//        count < 128 -> 1 字节; count >= 128 -> 2 字节 (高位标记 + 低位扩展)
//
// 位读取器: MsbBitReader -- MSB-first 位流读取, 逐字节填充
//
// 依赖: System.Buffers.Binary (小端整数读取)
// 被依赖: LinkArchiveCodec (压缩条目解包)
// ============================================================================
using System.Buffers.Binary;

namespace Kaguya_YaneKit.Formats.Archive;

public sealed class BmrDecoder
{
    private readonly int _step;
    private readonly int _finalSize;
    private readonly int _key;
    private readonly int _unpackedSize;
    private readonly byte[] _input;

    public BmrDecoder(byte[] data)
    {
        if (data.Length < 0x14)
            throw new InvalidDataException("BMR data too short.");
        if (data[0] != 'B' || data[1] != 'M' || data[2] != 'R')
            throw new InvalidDataException("Invalid BMR magic.");

        _step = data[3];
        _finalSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        _key = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        _unpackedSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        _input = data;
    }

    public static bool IsBmr(byte[] data) =>
        data.Length >= 3 && data[0] == 'B' && data[1] == 'M' && data[2] == 'R';

    public byte[] Unpack()
    {
        var bitReader = new MsbBitReader(_input, 0x14);
        var output = UnpackHuffman(bitReader, _unpackedSize);
        UndoMoveToFront(output);
        output = Decode(output, _key);
        if (_step != 0)
            output = DecompressRLE(output, _finalSize, _step);
        return output;
    }

    private static byte[] UnpackHuffman(MsbBitReader reader, int outputSize)
    {
        var output = new byte[outputSize];
        ushort token = 256;
        var tree = new ushort[2, 256];
        var root = CreateHuffmanTree(reader, tree, ref token);
        for (int dst = 0; dst < output.Length; dst++)
        {
            ushort symbol = root;
            while (symbol >= 0x100)
            {
                int bit = reader.GetNextBit();
                if (bit < 0)
                    throw new EndOfStreamException("Unexpected end of BMR Huffman stream.");
                symbol = tree[bit, symbol - 256];
            }
            output[dst] = (byte)symbol;
        }
        return output;
    }

    private static ushort CreateHuffmanTree(MsbBitReader reader, ushort[,] tree, ref ushort token)
    {
        if (reader.GetNextBit() != 0)
        {
            ushort v = token++;
            tree[0, v - 256] = CreateHuffmanTree(reader, tree, ref token);
            tree[1, v - 256] = CreateHuffmanTree(reader, tree, ref token);
            return v;
        }
        return (ushort)reader.GetBits(8);
    }

    private static void UndoMoveToFront(byte[] data)
    {
        var dict = new byte[256];
        for (int i = 0; i < 256; i++)
            dict[i] = (byte)i;

        for (int i = 0; i < data.Length; i++)
        {
            byte v = data[i];
            data[i] = dict[v];
            byte saved = dict[v];
            for (int j = v; j > 0; j--)
                dict[j] = dict[j - 1];
            dict[0] = saved;
        }
    }

    private static byte[] Decode(byte[] input, int key)
    {
        var freqTable = new int[256];
        for (int i = 0; i < input.Length; i++)
            freqTable[input[i]]++;

        for (int i = 1; i < 256; i++)
            freqTable[i] += freqTable[i - 1];

        var distribTable = new int[input.Length];
        for (int i = input.Length - 1; i >= 0; i--)
        {
            int v = input[i];
            int freq = --freqTable[v];
            distribTable[freq] = i;
        }

        int pos = key;
        var output = new byte[input.Length];
        for (int i = 0; i < output.Length; i++)
        {
            pos = distribTable[pos];
            output[i] = input[pos];
        }
        return output;
    }

    private static byte[] DecompressRLE(byte[] input, int finalSize, int step)
    {
        var result = new byte[finalSize];
        int src = 0;
        for (int i = 0; i < step; i++)
        {
            byte v1 = input[src++];
            result[i] = v1;
            int dst = i + step;
            while (dst < result.Length)
            {
                byte v2 = input[src++];
                result[dst] = v2;
                dst += step;
                if (v2 == v1)
                {
                    int count = input[src++];
                    if ((count & 0x80) != 0)
                        count = input[src++] + ((count & 0x7F) << 8) + 128;
                    while (count-- > 0 && dst < result.Length)
                    {
                        result[dst] = v2;
                        dst += step;
                    }
                    if (dst < result.Length)
                    {
                        v2 = input[src++];
                        result[dst] = v2;
                        dst += step;
                    }
                }
                v1 = v2;
            }
        }
        return result;
    }

    private sealed class MsbBitReader
    {
        private readonly byte[] _data;
        private int _pos;
        private int _bitsLeft;
        private int _currentByte;

        public MsbBitReader(byte[] data, int offset)
        {
            _data = data;
            _pos = offset;
            _bitsLeft = 0;
            _currentByte = 0;
        }

        public int GetNextBit()
        {
            if (_bitsLeft == 0)
            {
                if (_pos >= _data.Length) return -1;
                _currentByte = _data[_pos++];
                _bitsLeft = 8;
            }
            _bitsLeft--;
            return (_currentByte >> _bitsLeft) & 1;
        }

        public int GetBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = GetNextBit();
                if (bit < 0)
                    throw new EndOfStreamException("Unexpected end of BMR bit stream.");
                value = (value << 1) | bit;
            }
            return value;
        }
    }
}
