using System.Net.NetworkInformation;

// 我是真没想到这个.mos就是.png，我还以为是GARbro里提到的这个MOS，结果被摆了一道
namespace AdvhdPictureTool.Handlers
{
    public class MosHandler : IFormatHandler
    {
        public string Tag => "mos";

        // 元数据类为空，因为 PNG 不需要特殊处理元数据
        public class Metadata { }

        public bool Identify(BinaryReader reader, string filePath)
        {
            // 严格检查文件后缀是否为 .mos
            if (!filePath.EndsWith(".mos", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 验证文件内容是否为 PNG 格式
            if (reader.BaseStream.Length < 8) return false;
            reader.BaseStream.Position = 0;
            // PNG signature: 89 50 4E 47 0D 0A 1A 0A
            return reader.ReadUInt64() == 0x0A1A0A0D474E5089;
        }

        public object Convert(string sourceFile, string destPath)
        {
            File.Copy(sourceFile, destPath + ".png", true);
            return new Metadata();
        }

        public void Repack(string sourcePath, string destFile)
        {
            File.Copy(sourcePath + ".png", destFile, true);
        }
    }
}