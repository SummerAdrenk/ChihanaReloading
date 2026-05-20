// ============================================================================
// IFormatHandler.cs
// 图片格式处理器接口: 所有格式 Handler 的统一抽象
//
// 接口方法:
//   Tag       -- 格式标签 (如 "ap2", "bmp", "anm"), 同时作为子目录名
//   Identify  -- 通过读取文件头魔数判断是否匹配当前格式
//   Convert   -- 将原始格式转换为 PNG + 返回格式特有元数据
//   Repack    -- 将修改后的 PNG 重新打包为原始格式
//
// 已知实现: Ap0Handler, Ap2Handler, Ap3Handler, AnmHandler, BmpHandler, ApHandler
// ============================================================================
namespace Kaguya_YaneKit.Formats.Picture;

public interface IFormatHandler
{
    string Tag { get; }
    bool Identify(BinaryReader reader);
    object Convert(string sourceFile, string destPath);
    void Repack(string sourcePath, string destFile);
}
