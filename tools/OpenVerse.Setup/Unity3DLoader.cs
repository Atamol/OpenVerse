using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

/// <summary>
/// Unity 3d asset load helper
/// </summary>
public static class Unity3DLoader
{
    /// <summary>
    /// assumes that the target bundle is text asset bundle without any dependencies.
    /// </summary>
    /// <param name="unity3dPath"></param>
    /// <param name="jsonOutputPath"></param>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static void ConvertToJson(string unity3dPath, string jsonOutputPath)
    {
        if (!File.Exists(unity3dPath))
        {
            throw new FileNotFoundException("unity3d bundle not found", unity3dPath);
        }

        var manager = new AssetsManager();
        var bundleInst = manager.LoadBundleFile(unity3dPath, true);
        var assetsFileInst = manager.LoadAssetsFileFromBundle(bundleInst, 0, false);
        var assetsFile = assetsFileInst.file;

        var textAssetInfo = assetsFile.GetAssetsOfType(AssetClassID.TextAsset).FirstOrDefault()
            ?? throw new InvalidOperationException($"no TextAsset found in {unity3dPath}");

        var baseField = manager.GetBaseField(assetsFileInst, textAssetInfo);
        var script = baseField["m_Script"].AsString;

        // re-parse and re-emit indented rather than writing the raw string, so a malformed payload fails loudly
        // here instead of producing a JSON file that only breaks later, and so the file is actually readable
        using var doc = JsonDocument.Parse(script);
        var outDir = Path.GetDirectoryName(Path.GetFullPath(jsonOutputPath));
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }
        using var stream = File.Create(jsonOutputPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        doc.WriteTo(writer);
    }
}
