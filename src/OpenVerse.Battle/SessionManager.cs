using System.Collections.Concurrent;

namespace OpenVerse.Battle;

public sealed class SessionManager
{
    readonly ConcurrentDictionary<string, Session> _byId = new();
    readonly ConcurrentDictionary<string, List<Session>> _byBattle = new();

    public void Add(Session s)
    {
        _byId[s.Id] = s;
        _byBattle.AddOrUpdate(s.BattleId, [s], (_, list) => { lock (list) { list.Add(s); return list; } });
    }

    public void Remove(Session s)
    {
        _byId.TryRemove(s.Id, out _);
        if (_byBattle.TryGetValue(s.BattleId, out var list))
            lock (list) { list.Remove(s); if (list.Count == 0) _byBattle.TryRemove(s.BattleId, out _); }
    }

    public IReadOnlyList<Session> ByBattle(string battleId) =>
        _byBattle.TryGetValue(battleId, out var list) ? list.ToArray() : Array.Empty<Session>();

    public Session? Peer(Session self) =>
        ByBattle(self.BattleId).FirstOrDefault(s => s.Id != self.Id);
}
