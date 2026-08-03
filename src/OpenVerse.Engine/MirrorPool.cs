using System.Collections.Concurrent;

namespace OpenVerse.Engine;

// a pair can hold one battle at a time, so two rooms playing at once need two pairs. booting one costs an engine's
// startup and about 190MB, so they are pooled and reused rather than built per battle, and the cap is what stops a
// busy evening from eating the machine
public sealed class MirrorPool : IDisposable
{
    readonly string _csv;
    readonly int _cap;
    readonly ConcurrentBag<MirrorPair> _idle = new();
    readonly ConcurrentDictionary<string, MirrorPair> _out = new();
    int _built;

    public MirrorPool(string cardMasterCsv, int cap)
    {
        _csv = cardMasterCsv;
        _cap = Math.Max(1, cap);
    }

    public int Built => Volatile.Read(ref _built);
    public int Rented => _out.Count;

    // The caller relays with no engine when this returns null, so do not turn it into a throw
    public MirrorPair? Rent(string battleId)
    {
        if (_out.TryGetValue(battleId, out var already)) return already;
        if (!_idle.TryTake(out var pair))
        {
            if (Interlocked.Increment(ref _built) > _cap)
            {
                Interlocked.Decrement(ref _built);
                return null;
            }
            pair = MirrorPair.Boot(_csv, $"engine{Built}");
            if (!pair.Ready)
            {
                Interlocked.Decrement(ref _built);
                pair.Dispose();
                return null;
            }
        }
        return _out.TryAdd(battleId, pair) ? pair : Give(pair, battleId);
    }

    MirrorPair Give(MirrorPair spare, string battleId)
    {
        _idle.Add(spare);
        return _out[battleId];
    }

    public void Release(string battleId)
    {
        if (!_out.TryRemove(battleId, out var pair)) return;
        pair.End((_, _, _) => { });
        _idle.Add(pair);
    }

    public void Dispose()
    {
        foreach (var id in _out.Keys.ToArray()) Release(id);
        while (_idle.TryTake(out var p)) p.Dispose();
    }
}
