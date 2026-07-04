namespace OpenVerse.Common;

public sealed class Deck
{
    public int DeckNo { get; set; }
    public string UserKey { get; set; } = "";
    public int Format { get; set; }
    public int ClassId { get; set; }
    public int SubClassId { get; set; } = 10;
    public string DeckName { get; set; } = "";
    public long SleeveId { get; set; } = 3000011L;
    public int LeaderSkinId { get; set; }
    public bool IsRandomLeaderSkin { get; set; }
    public int[] LeaderSkinIdList { get; set; } = [];
    public string? RotationId { get; set; }
    public int[] CardIdArray { get; set; } = [];
    public int DisplayOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
