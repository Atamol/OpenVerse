using System.Text.RegularExpressions;

namespace OpenVerse.Common;

// OperateReceiveChecker.IsOperateReceive is the client's own check of a received message against its board, and on
// failure it installs NullOperationCollection and writes ConductError. The client ships that to add_client_log, so the
// relay can read Cygames' desync verdict directly
public sealed record ConductError(string BattleId, string ViewerId, string Reason, int CardId, int Idx)
{
    public override string ToString() =>
        $"[{BattleId}] ConductError from {ViewerId}: {Reason}" + (CardId > 0 ? $" (card {CardId} idx {Idx})" : "");
}

public static partial class ClientLog
{
    [GeneratedRegex(@"\[bid\s*(?<bid>\S*)\s+vid\s+(?<vid>\d+)\](?<text>[^&]*)")]
    private static partial Regex Entry();

    [GeneratedRegex(@"CardID(?<card>\d+)Idx(?<idx>\d+)")]
    private static partial Regex Card();

    // An IsOperateReceive line names the reason and lands right before the ConductError it caused
    public static List<ConductError> Read(string log)
    {
        var found = new List<ConductError>();
        string reason = "", bid = "", vid = "";
        var cardId = 0;
        var idx = 0;
        foreach (Match m in Entry().Matches(log))
        {
            var text = m.Groups["text"].Value.Trim();
            if (text.Contains("IsOperateReceive") || text.Contains("IsSelectSkillCheck"))
            {
                bid = m.Groups["bid"].Value;
                vid = m.Groups["vid"].Value;
                reason = text;
                cardId = idx = 0;
                if (Card().Match(text) is { Success: true } c)
                {
                    cardId = int.Parse(c.Groups["card"].Value);
                    idx = int.Parse(c.Groups["idx"].Value);
                    reason = text[(c.Index + c.Length)..].Trim();
                }
            }
            else if (text.Contains("ConductError"))
            {
                found.Add(new ConductError(
                    bid.Length > 0 ? bid : m.Groups["bid"].Value,
                    vid.Length > 0 ? vid : m.Groups["vid"].Value,
                    reason.Length > 0 ? reason : "(no reason logged)", cardId, idx));
                reason = "";
                cardId = idx = 0;
            }
        }
        return found;
    }
}
