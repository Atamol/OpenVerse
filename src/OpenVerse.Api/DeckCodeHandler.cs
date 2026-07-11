using System.Security.Cryptography;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Api;

public sealed class DeckCodeHandler
{
    const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no I/L/O/0/1

    readonly DeckCodeStore _store;

    public DeckCodeHandler(DeckCodeStore store) => _store = store;

    // game_api prefix keeps this off the server's own deck/* routes
    public static bool CanHandle(string path) =>
        path.Contains("game_api", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith("/deck", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/deck_code", StringComparison.OrdinalIgnoreCase));

    // result_code != 1 makes the client report an invalid code
    public (int ResultCode, string Data) Handle(string reqJson)
    {
        using var doc = JsonDocument.Parse(reqJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("deck_code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
            return Resolve(codeEl.GetString() ?? "");
        if (root.TryGetProperty("cardID", out var cardsEl) && cardsEl.ValueKind == JsonValueKind.Array)
            return Generate(root, cardsEl);
        return (1, "{}");
    }

    (int, string) Generate(JsonElement root, JsonElement cardsEl)
    {
        var cardIds = cardsEl.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToArray();
        var rotationId = root.TryGetProperty("rotation_id", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        var entry = new DeckCodeStore.Entry(GetInt(root, "clan"), TryGetInt(root, "sub_clan") ?? 10,
            GetInt(root, "deck_format"), cardIds, rotationId);
        var code = NewCode();
        _store.Save(code, entry);
        return (1, $"{{\"deck_code\":\"{code}\"}}");
    }

    (int, string) Resolve(string code)
    {
        var e = _store.Get(code.Trim().ToUpperInvariant());
        if (e is null) return (2, "{}");
        var fields = $"\"clan\":{e.Clan},\"sub_clan\":{e.SubClan},\"cardID\":[{string.Join(',', e.CardIds)}]";
        if (e.RotationId is not null) fields += $",\"rotation_id\":\"{e.RotationId}\"";
        return (1, $"{{\"deck\":{{{fields}}}}}");
    }

    string NewCode()
    {
        for (var i = 0; i < 20; i++)
        {
            var code = string.Concat(Enumerable.Range(0, 6)
                .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));
            if (!_store.Exists(code)) return code;
        }
        return "OV" + RandomNumberGenerator.GetInt32(1000, 9999);
    }

    static int GetInt(JsonElement e, string k) => TryGetInt(e, k) ?? 0;
    static int? TryGetInt(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
