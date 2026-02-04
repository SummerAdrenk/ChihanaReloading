namespace AdvhdPictureTool.Handlers
{
    public class PngHandler : IFormatHandler
    {
        public string Tag => "png";

        // Empty metadata for PNG
        public class Metadata { }

        public bool Identify(BinaryReader reader, string filePath)
        {
            // 优先检查文件扩展名
            // 只有当文件后缀是 .png 时，才继续进行内容检查。
            if (!filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 后缀符合后，再进行文件头签名验证，确保它是一个有效的 PNG 文件。
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