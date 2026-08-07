using System.Text.Json.Nodes;

namespace OpenVerse.Battle;

// SetupConsistency folds each side's board into three numbers, shipped on TurnEnd and Judge.
// A TurnEnd and the answering Judge must mirror: what the actor says about itself, the peer says about its opponent
public enum CodePart { Totals, Hand, InPlay }

// key1-3 are the sender's own board, key4-6 its view of the opponent. Totals is a weighted sum over life, cemetery,
// deck, EP and PP, so a life gap of 5 can hide a PP gap of 1
public sealed record BoardCode(string Self0, string Self1, string Self2, string Oppo0, string Oppo1, string Oppo2)
{
    public static BoardCode? From(JsonNode? body)
    {
        if (body?["battleCode"] is not JsonObject c) return null;
        string? K(string k) => c[k]?.GetValue<string>();
        if (K("key1") is not { } a || K("key2") is not { } b || K("key3") is not { } d ||
            K("key4") is not { } e || K("key5") is not { } f || K("key6") is not { } g) return null;
        return new BoardCode(a, b, d, e, f, g);
    }

    public string[] Self => [Self0, Self1, Self2];
    public string[] Oppo => [Oppo0, Oppo1, Oppo2];
}

public sealed record Divergence(int Boundary, IReadOnlyList<CodePart> Parts, string Detail, IReadOnlyList<string> Window, int Consecutive)
{
    public override string ToString() =>
        $"boundary {Boundary} DIVERGED on {string.Join("+", Parts)} (x{Consecutive}): {Detail}";
}

public sealed class ConsistencyWatch
{
    readonly Dictionary<string, (string Sender, BoardCode Code)> _pending = new();
    readonly Dictionary<string, int> _boundary = new();
    readonly Dictionary<string, List<string>> _window = new();
    readonly Dictionary<string, int> _streak = new();
    readonly HashSet<string> _reportedFirst = new();
    readonly object _lock = new();

    public int Boundaries(string battleId) { lock (_lock) return _boundary.GetValueOrDefault(battleId); }

    public void Reset(string battleId)
    {
        lock (_lock)
        {
            _pending.Remove(battleId);
            _boundary.Remove(battleId);
            _window.Remove(battleId);
            _streak.Remove(battleId);
            _reportedFirst.Remove(battleId);
        }
    }

    public void NoteRelayed(string battleId, string what)
    {
        lock (_lock)
        {
            if (!_window.TryGetValue(battleId, out var w)) _window[battleId] = w = new List<string>();
            w.Add(what);
        }
    }

    public void NoteTurnEnd(string battleId, string sender, JsonNode? body)
    {
        if (BoardCode.From(body) is not { } code) return;
        lock (_lock) _pending[battleId] = (sender, code);
    }

    public Divergence? NoteJudge(string battleId, string sender, JsonNode? body)
    {
        if (BoardCode.From(body) is not { } judge) return null;
        (string Sender, BoardCode Code) turnEnd;
        int n;
        lock (_lock)
        {
            if (!_pending.TryGetValue(battleId, out turnEnd) || turnEnd.Sender == sender) return null;
            _pending.Remove(battleId);
            n = _boundary.GetValueOrDefault(battleId) + 1;
            _boundary[battleId] = n;
        }
        var actor = turnEnd.Code;
        var parts = new List<CodePart>();
        var detail = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var part = (CodePart)i;
            if (actor.Self[i] != judge.Oppo[i])
            {
                parts.Add(part);
                detail.Add($"{part} actor={actor.Self[i]} peerSaysOfActor={judge.Oppo[i]}");
            }
            else if (actor.Oppo[i] != judge.Self[i])
            {
                parts.Add(part);
                detail.Add($"{part} peer={judge.Self[i]} actorSaysOfPeer={actor.Oppo[i]}");
            }
        }
        lock (_lock)
        {
            var window = _window.GetValueOrDefault(battleId) ?? [];
            _window[battleId] = [];
            if (parts.Count == 0)
            {
                _streak.Remove(battleId);
                return null;
            }
            // everything after the first break is downstream of it, so only the first carries the window
            var first = _reportedFirst.Add(battleId);
            var streak = _streak.GetValueOrDefault(battleId) + 1;
            _streak[battleId] = streak;
            return new Divergence(n, parts, string.Join("; ", detail), first ? window : [], streak);
        }
    }
}
