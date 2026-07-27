using System.Runtime.CompilerServices;

namespace OpenVerse.Tests;

static class TestEnvironment
{
    // booting the API indexes the bundle cache, so point it somewhere empty or the fixtures hash tens of GB
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openverse-tests-bundles");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("OPENVERSE_BUNDLE_DIR", dir);
    }
}
