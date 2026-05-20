// ============================================================================
// ArchiveFormatModule.cs
// LINK 档案格式模块: 编解码器工厂入口
//
// 提供: CreateLinkCodec() -> LinkArchiveCodec 实例
// 用途: 上层命令通过本模块获取 LINK 档案编解码器, 解耦具体实现
// ============================================================================
namespace Kaguya_YaneKit.Formats.Archive;

public static class ArchiveFormatModule
{
    public static LinkArchiveCodec CreateLinkCodec() => new();
}
