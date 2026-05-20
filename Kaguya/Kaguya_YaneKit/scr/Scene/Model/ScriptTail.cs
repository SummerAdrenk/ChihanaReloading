// ============================================================================
// ScriptTail.cs
// 脚本尾部数据元素: 存放代码段末尾无法解析为完整指令的剩余字节
//
// 当 ScriptBinaryCodec 解码时遇到不足 4 字节或长度异常的数据,
// 将其作为 ScriptTail 保存, 确保二进制往返 (round-trip) 无损
//
// 依赖: ScriptElement (基类)
// 被依赖: ScriptBinaryCodec, ScrTextCodec, ScrLabelService, ScrListingFormatter
// ============================================================================
namespace Kaguya_YaneKit.Scr.Model;

public sealed class ScriptTail : ScriptElement
{
    private byte[] _data = [];

    public byte[] Data
    {
        get => _data;
        set => _data = value ?? [];
    }
}
