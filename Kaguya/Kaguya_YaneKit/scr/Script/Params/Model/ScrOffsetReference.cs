// ============================================================================
// ScrOffsetReference.cs
// 偏移引用模型: 表示 SAVE/LAYER 偏移表中的单个条目
//
// 每个条目可以是:
//   - 标签引用 (Label != null): 写出时通过 ScrLabelService 解析为字节偏移
//   - 原始值 (RawValue != null): 直接写出 u32 值 (无法解析为标签时的回退)
//
// ScrOffsetEncoding 枚举:
//   CodeRelative  - 偏移相对于代码段起始 (LAYER 表使用)
//   FileAbsolute  - 偏移相对于文件起始 (SAVE 表使用)
//
// 依赖: 无
// 被依赖: ScrFileDocument, ScrContainerCodec, ScrTextCodec
// ============================================================================
namespace Kaguya_YaneKit.Script.Paramsipt.Params.Model;

public enum ScrOffsetEncoding
{
    CodeRelative,
    FileAbsolute
}

public sealed class ScrOffsetReference
{
    public string? Label { get; set; }

    public uint? RawValue { get; set; }

    public ScrOffsetEncoding Encoding { get; set; }

    public static ScrOffsetReference FromLabel(string label, ScrOffsetEncoding encoding) =>
        new() { Label = label, Encoding = encoding };

    public static ScrOffsetReference FromRaw(uint value, ScrOffsetEncoding encoding) =>
        new() { RawValue = value, Encoding = encoding };
}
