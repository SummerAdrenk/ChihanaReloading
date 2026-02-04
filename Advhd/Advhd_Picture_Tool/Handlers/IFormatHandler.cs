namespace AdvhdPictureTool.Handlers
{
    public interface IFormatHandler
    {
        string Tag { get; }
        bool Identify(BinaryReader reader, string filePath);

        object Convert(string sourceFile, string destPath);

        void Repack(string sourcePath, string destFile);
    }
}