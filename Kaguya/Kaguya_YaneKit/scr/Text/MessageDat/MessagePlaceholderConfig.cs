// ============================================================================
// MessagePlaceholderConfig.cs
// 基于 INI 的占位符与编码配置
//
// INI 文件结构:
//   全局选项 - ConfigurationID, AdjustBranchMessages, AdjustMsgId,
//              AdjustMsgDetails, GbkCheck, MsgLengthCheck/Fix/Set
//   配置段   - "■[profileId][...]" 标记的活动配置段
//   段内选项 - ReadingEncoding, WritingEncoding, EncryptEnabled, EncryptKey
//   占位符   - "key=hexBytes" 定义文本占位符与原始字节的映射
//   显示长度 - "key_len=n" 定义占位符在行宽计算中的显示长度
//
// 核心算法:
//   Decode() - 在字节流中查找已注册的字节序列, 替换为占位符文本, 其余用指定编码解码
//   Encode() - 先将整个文本按编码转字节, 再查找占位符文本的字节表示并替换为原始字节
//   ByteArrayKey - 内部结构体, 用于字节数组的字典键比较 (SequenceEqual + HashCode)
//
// 依赖: 无外部依赖 (纯配置/工具类)
// 被依赖: MessageDatCodec, MessageDatWorkflowProcessor, InteractiveSession
// ============================================================================
using System.Text;

namespace Kaguya_YaneKit.Text.MessageDat;

public sealed class MessagePlaceholderConfig
{
    private readonly Dictionary<string, byte[]> _placeholderToBytes = new(StringComparer.Ordinal);
    private readonly Dictionary<ByteArrayKey, string> _bytesToPlaceholder = new();

    public IReadOnlyDictionary<string, byte[]> PlaceholderToBytes => _placeholderToBytes;
    public string? ReadEncodingName { get; private set; }
    public string? WriteEncodingName { get; private set; }
    public bool? EncryptEnabled { get; private set; } = true;
    public byte? EncryptKey { get; private set; } = 0xff;
    public bool AdjustBranchMessages { get; private set; }
    public int AdjustMsgId { get; private set; } = 1;
    public bool AdjustMsgDetails { get; private set; }
    public bool GbkCheck { get; private set; } = true;
    public bool MsgLengthCheck { get; private set; }
    public bool MsgLengthFix { get; private set; }
    public int MsgLengthSet { get; private set; } = 25;
    public IReadOnlyDictionary<string, int> PlaceholderDisplayLengths => _placeholderDisplayLengths;

    private readonly Dictionary<string, int> _placeholderDisplayLengths = new(StringComparer.OrdinalIgnoreCase);

    public static MessagePlaceholderConfig Empty { get; } = new();

    public static MessagePlaceholderConfig Load(string? iniPath)
    {
        var config = new MessagePlaceholderConfig();
        if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
        {
            return config;
        }

        var lines = File.ReadAllLines(iniPath, Encoding.UTF8);
        var activeProfileId = ReadActiveProfile(lines);
        if (!ProfileExists(lines, activeProfileId))
        {
            activeProfileId = "0";
        }
        config.ReadGlobalOptions(lines);
        var inActiveProfile = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith("■[", StringComparison.Ordinal))
            {
                inActiveProfile = line.StartsWith($"■[{activeProfileId}][", StringComparison.Ordinal);
                continue;
            }

            if (!inActiveProfile)
            {
                continue;
            }

            var equal = line.IndexOf('=');
            if (equal <= 0)
            {
                continue;
            }

            var key = line[..equal].Trim();
            var value = line[(equal + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "readingencoding":
                    config.ReadEncodingName = value;
                    continue;
                case "writingencoding":
                    config.WriteEncodingName = value;
                    continue;
                case "encryptenabled":
                    if (bool.TryParse(value, out var enabled))
                    {
                        config.EncryptEnabled = enabled;
                    }
                    continue;
                case "encryptkey":
                    if (TryParseHexByte(value, out var keyByte))
                    {
                        config.EncryptKey = keyByte;
                    }
                    continue;
                case "gbkcheck":
                    if (bool.TryParse(value, out var gbkCheck))
                    {
                        config.GbkCheck = gbkCheck;
                    }
                    continue;
            }

            if (key.EndsWith("_len", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var length))
                {
                    config._placeholderDisplayLengths[key[..^4]] = length;
                }
                continue;
            }

            if (TryParseHexBytes(value, out var bytes))
            {
                config.Add(key, bytes);
                config._placeholderDisplayLengths.TryAdd(key, 3);
            }
        }

        return config;
    }

    public void Add(string placeholder, byte[] bytes)
    {
        _placeholderToBytes[placeholder] = bytes;
        _bytesToPlaceholder[new ByteArrayKey(bytes)] = placeholder;
    }

    public string Decode(byte[] bytes, Encoding encoding)
    {
        if (_bytesToPlaceholder.Count == 0)
        {
            return encoding.GetString(bytes);
        }

        var builder = new StringBuilder();
        var plain = new List<byte>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var replaced = false;
            foreach (var pair in _bytesToPlaceholder)
            {
                var code = pair.Key.Bytes;
                if (offset + code.Length <= bytes.Length && bytes.AsSpan(offset, code.Length).SequenceEqual(code))
                {
                    if (plain.Count > 0)
                    {
                        builder.Append(encoding.GetString(plain.ToArray()));
                        plain.Clear();
                    }

                    builder.Append(pair.Value);
                    offset += code.Length;
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                plain.Add(bytes[offset]);
                offset++;
            }
        }

        if (plain.Count > 0)
        {
            builder.Append(encoding.GetString(plain.ToArray()));
        }

        return builder.ToString();
    }

    public byte[] Encode(string text, Encoding encoding)
    {
        if (_placeholderToBytes.Count == 0)
        {
            return encoding.GetBytes(text);
        }

        var bytes = new List<byte>(encoding.GetBytes(text));
        foreach (var pair in _placeholderToBytes)
        {
            var placeholderBytes = encoding.GetBytes(pair.Key);
            for (var i = bytes.Count - placeholderBytes.Length; i >= 0; i--)
            {
                if (i + placeholderBytes.Length > bytes.Count)
                {
                    continue;
                }

                var matches = true;
                for (var j = 0; j < placeholderBytes.Length; j++)
                {
                    if (bytes[i + j] != placeholderBytes[j])
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                bytes.RemoveRange(i, placeholderBytes.Length);
                bytes.InsertRange(i, pair.Value);
            }
        }

        return bytes.ToArray();
    }

    private static string ReadActiveProfile(string[] lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith(';'))
            {
                continue;
            }

            var equal = line.IndexOf('=');
            if (equal <= 0)
            {
                continue;
            }

            if (string.Equals(line[..equal].Trim(), "ConfigurationID", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(equal + 1)..].Trim();
                return value.Length == 0 ? "0" : value;
            }
        }

        return "0";
    }

    private static bool ProfileExists(IEnumerable<string> lines, string profileId) =>
        lines.Any(rawLine => rawLine.Trim().StartsWith($"■[{profileId}][", StringComparison.Ordinal));

    private void ReadGlobalOptions(string[] lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("■[", StringComparison.Ordinal))
            {
                continue;
            }

            var equal = line.IndexOf('=');
            if (equal <= 0)
            {
                continue;
            }

            var key = line[..equal].Trim();
            var value = line[(equal + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "adjustbranchmessages":
                    bool.TryParse(value, out var adjust);
                    AdjustBranchMessages = adjust;
                    break;
                case "adjustmsgid":
                    if (int.TryParse(value, out var adjustId) && adjustId > 0)
                    {
                        AdjustMsgId = adjustId;
                    }
                    break;
                case "adjustmsgdetails":
                    bool.TryParse(value, out var details);
                    AdjustMsgDetails = details;
                    break;
                case "gbkcheck":
                    bool.TryParse(value, out var gbk);
                    GbkCheck = gbk;
                    break;
                case "msglengthcheck":
                    bool.TryParse(value, out var check);
                    MsgLengthCheck = check;
                    break;
                case "msglengthfix":
                    bool.TryParse(value, out var fix);
                    MsgLengthFix = fix;
                    break;
                case "msglengthset":
                    if (int.TryParse(value, out var length) && length > 0)
                    {
                        MsgLengthSet = length;
                    }
                    break;
            }
        }
    }

    private static bool TryParseHexBytes(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || text.Length % 2 != 0)
        {
            return false;
        }

        var result = new byte[text.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(text.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out result[i]))
            {
                return false;
            }
        }

        bytes = result;
        return true;
    }

    private static bool TryParseHexByte(string text, out byte value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return byte.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    private readonly struct ByteArrayKey : IEquatable<ByteArrayKey>
    {
        public ByteArrayKey(byte[] bytes) => Bytes = bytes;
        public byte[] Bytes { get; }

        public bool Equals(ByteArrayKey other) => Bytes.SequenceEqual(other.Bytes);
        public override bool Equals(object? obj) => obj is ByteArrayKey other && Equals(other);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in Bytes)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
