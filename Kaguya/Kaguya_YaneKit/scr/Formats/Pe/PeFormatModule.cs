using System.Text;

namespace Kaguya_YaneKit.Formats.Pe;

public static class PeFormatModule
{
    public static Encoding ResolveEncoding(string? encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        var value = encodingName.Trim().ToLowerInvariant();
        return value switch
        {
            "cp932" or "sjis" or "shift-jis" or "shift_jis" => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "cp936" or "gbk" => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "utf8" or "utf-8" => Encoding.GetEncoding(65001, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => int.TryParse(value, out var codePage)
                ? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                : Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        };
    }
}
