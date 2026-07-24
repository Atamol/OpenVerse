using OpenVerse.Common;

namespace OpenVerse.Tests;

public class BaseCardIdMapTests
{
    // the real master isn't shipped (each host extracts it), so skip rather than fail when it's absent
    static string? DataDir()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "release", "data");
        return File.Exists(Path.Combine(d, "card_master_full.csv.gz")) ? d : null;
    }

    [Fact]
    public void MissingMasterYieldsEmptyMap()
    {
        Assert.Empty(BaseCardIdMap.Load(Path.Combine(Path.GetTempPath(), "openverse-no-such-dir")));
    }

    [Fact]
    public void AltArtMapsToBase()
    {
        if (DataDir() is not { } dir) return;
        var map = BaseCardIdMap.Load(dir);
        Assert.Equal(100314010, map[705314010]);
    }

    [Fact]
    public void FoilMapsToBase()
    {
        if (DataDir() is not { } dir) return;
        var map = BaseCardIdMap.Load(dir);
        Assert.Equal(100314010, map[100314011]);
    }

    // base_card_id sits after the quoted voice-cue columns, so a naive Split(',') reads "SHURIKEN" for this row
    [Fact]
    public void QuotedRowParsesToTheIdNotAVoiceCue()
    {
        if (DataDir() is not { } dir) return;
        var map = BaseCardIdMap.Load(dir);
        Assert.Equal(930844060, map[930844060]);
    }

    [Fact]
    public void NormalCardIsItsOwnBase()
    {
        if (DataDir() is not { } dir) return;
        var map = BaseCardIdMap.Load(dir);
        Assert.Equal(100314010, map[100314010]);
    }
}
