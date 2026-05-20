// ============================================================================
// ParamsFormatModule.cs
// params.dat 格式模块: 编解码器工厂入口
//
// 提供: CreateCodec() -> ParamsDatCodec 实例
// 用途: 上层命令通过本模块获取编解码器, 解耦具体实现
// ============================================================================
namespace Kaguya_YaneKit.Formats.Params;

public static class ParamsFormatModule
{
    public static ParamsDatCodec CreateCodec() => new();
}
