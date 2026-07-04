using System.Collections.Concurrent;

namespace OpenVerse.Common;

public sealed class UserProfile
{
    public long ViewerId { get; set; }
    public string Name { get; set; } = "";
    public long EmblemId { get; set; }
    public int DegreeId { get; set; }
    public string CountryCode { get; set; } = "";
    public int Rank { get; set; }
    public int MaxRank { get; set; }
    public int BattlePoint { get; set; }
    public int IsOfficial { get; set; }
}

public sealed class UserStore
{
    readonly ConcurrentDictionary<string, UserProfile> _byUdid = new();
    long _nextViewerId = 100000;

    public UserProfile GetOrCreate(string udid)
    {
        return _byUdid.GetOrAdd(udid, key =>
        {
            var vid = Interlocked.Increment(ref _nextViewerId);
            return new UserProfile
            {
                ViewerId = vid,
                Name = "player_" + key[^6..],
            };
        });
    }

    public void Set(string udid, UserProfile p) => _byUdid[udid] = p;
}
