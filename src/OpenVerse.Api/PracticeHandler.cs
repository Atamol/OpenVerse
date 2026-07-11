namespace OpenVerse.Api;

// Practice (CP対戦): the battle and AI run client-side, so the server only serves the opponent roster
// and records the result. The AI deck/logic load from the client's master_practice_ai_setting bundle,
// keyed by each entry's ai_deck_level/ai_logic_level
public sealed class PracticeHandler
{
    readonly string _rosterJson;
    readonly DeckHandler _deck;

    public PracticeHandler(string rosterJson, DeckHandler deck)
    {
        _rosterJson = rosterJson;
        _deck = deck;
    }

    public static bool CanHandle(string path) => path.Contains("practice/");

    public string Handle(string path, string userKey)
    {
        if (path.Contains("practice/info")) return _rosterJson;
        if (path.Contains("practice/deck_list")) return _deck.PracticeDeckList(userKey);
        if (path.Contains("practice/start")) return "{}";
        if (path.Contains("practice/finish"))
            return """{"get_class_experience":0,"class_experience":0,"class_level":1,"achieved_info":{},"reward_list":[]}""";
        return "{}";
    }
}
