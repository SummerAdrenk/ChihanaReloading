using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kaguya_YaneKit.Core;

namespace Kaguya_YaneKit.Formats.Archive;

public static class Af01ArchiveManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Write(string outputPath, Af01ArchiveManifest manifest)
    {
        ReadableUnicodeJson.WriteAllText(outputPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public static Af01ArchiveManifest Read(string inputPath)
    {
        return JsonSerializer.Deserialize<Af01ArchiveManifest>(File.ReadAllText(inputPath, Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("Manifest JSON did not contain an AF01 archive manifest.");
    }
}
