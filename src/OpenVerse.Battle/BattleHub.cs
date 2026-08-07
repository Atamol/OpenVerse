using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenVerse.Common;

namespace OpenVerse.Battle;

public sealed class BattleHub
{
    readonly SessionManager _sessions;
    readonly BattleDeckStore _decks;
    readonly Dictionary<string, Session> _turnOwner = new();
    // who goes first (turnState 0), rolled once per battle. Matched and BattleStart must agree on it
    readonly Dictionary<string, Session> _firstPlayer = new();
    readonly Dictionary<string, int> _fieldId = new();
    readonly Dictionary<string, int[]> _shuffled = new();
    // per-session card ledger: Index -> cardId. Seeded from the 40-card shuffle (idx 1..40 -> deck[idx-1]) then grown from
    // add/metamorphose/copy-token/uList reveals so knownList resolves tokens (idx>40) and transforms, not just deck cards.
    // token idx space restarts at 41 per player, so each side keeps its own map. it is the knownList source of truth
    readonly Dictionary<string, Dictionary<int, int>> _ledger = new();
    // return-to-deck reshuffle mirror. The client randomizes a card's deck slot via a XorShift seeded by idxChangeSeed and
    // NEVER sends the new index (OnIndexChange is a local replay recorder), so the server replicates the same lockstep
    // stream to keep _ledger correct after a card goes back to the deck. keyed by Session.Id, per-battle flags by BattleId
    readonly Dictionary<string, XorShift> _rng = new();
    readonly Dictionary<string, int> _idxSeed = new();
    readonly Dictionary<string, List<DeckCard>> _deck = new();
    readonly Dictionary<string, List<DeckCard>> _addQueue = new();
    readonly Dictionary<string, bool> _mulliganEnd = new();
    readonly Dictionary<string, bool> _turnEndFlushed = new();
    readonly Dictionary<string, bool> _finished = new();
    readonly Dictionary<string, int> _finishCode = new();
    readonly Dictionary<string, (int Owner, int Guest)> _roomWins = new();
    // who sat where, by BattleId = the room. The client tears the battle socket down between games, so from game 2 on
    // the reconnect order is a race between two remote players and the seat cannot be read off it. RoomCreate's sender
    // is the owner by definition and RoomEntry's is the visitor, so pin it there and keep it for the room's lifetime
    readonly ConcurrentDictionary<string, string> _ownerViewer = new();
    readonly ConcurrentDictionary<string, string> _visitorViewer = new();
    readonly ConsistencyWatch _watch = new();
    // a close runs on the dying socket's continuation, concurrently with the survivor's dispatch
    readonly object _finishLock = new();

    readonly Dictionary<int, int> _baseCardIds;
    // cardId -> base cost + the per-charge cost step, for pricing a revealed hand card
    readonly Dictionary<int, CardCost> _cardCosts;
    // Session -> idx -> concrete cost deltas the actor resolved and shipped (alter.cost). authoritative
    readonly Dictionary<string, Dictionary<int, List<CostMod>>> _costMods = new();
    // Session -> idx -> SpellChargeCount, which the wire spells "spellboost". it is a counter, NOT a discount: across
    // the master, 100 cards turn a charge into a cost cut but 54 turn it into damage, tokens, healing or stat gains.
    // only _cardCosts[id].SpellboostStep says which, so never read a charge here as "this card got cheaper"
    readonly Dictionary<string, Dictionary<int, int>> _charges = new();
    readonly HashSet<string> _costBlind = new();

    public BattleHub(SessionManager sessions, BattleDeckStore decks, Dictionary<int, int>? baseCardIds = null,
        Dictionary<int, CardCost>? cardCosts = null)
    {
        _sessions = sessions;
        _decks = decks;
        _baseCardIds = baseCardIds ?? new Dictionary<int, int>();
        _cardCosts = cardCosts ?? new Dictionary<int, CardCost>();
    }

    long VidOf(Session s)
    {
        var claimed = long.TryParse(s.ViewerId, out var v) && v > 0 ? v : 1;
        var peer = _sessions.Peer(s);
        if (peer is null || peer.ViewerId != s.ViewerId) return claimed;
        // both sockets claim one id, so keep the owner's and move the visitor off it
        return IsOwner(s) ? claimed : claimed + 1;
    }

    // The WS "BattleId" header is the room_id (Room.RoomId). The pinned seat only has to be stable and distinct between
    // the two sockets, so the header viewer_id works here even though it is unusable as a BattleDeckStore key
    bool IsOwner(Session s)
    {
        // The API is the only side that sees where each player connected from, and it writes that alongside the deck.
        // the socket's own address settles the seat without trusting anything the client chose for itself
        if (!string.IsNullOrEmpty(s.RemoteIp))
        {
            var ownerIp = _decks.Get(s.BattleId, isOwner: true)?.SourceIp ?? "";
            var guestIp = _decks.Get(s.BattleId, isOwner: false)?.SourceIp ?? "";
            if (ownerIp.Length > 0 && guestIp.Length > 0 && ownerIp != guestIp)
            {
                if (s.RemoteIp == ownerIp) return true;
                if (s.RemoteIp == guestIp) return false;
            }
        }
        // The header viewer_id only means anything while the two sockets carry DIFFERENT ones, and they do not always:
        // the client sends its own cached value and two installs have been seen sending the same one. with one id for
        // both, every session matches the owner and both players get the owner's class, deck and name
        var peer = _sessions.Peer(s);
        var usable = peer is null || peer.ViewerId != s.ViewerId;
        if (usable && _ownerViewer.TryGetValue(s.BattleId, out var o)) return s.ViewerId == o;
        if (usable && _visitorViewer.TryGetValue(s.BattleId, out var v)) return s.ViewerId != v;
        var first = _sessions.ByBattle(s.BattleId).FirstOrDefault() == s;
        Console.WriteLine(usable
            ? $"[{s.Id}] seat not pinned for room {s.BattleId}, falling back to connect order -> isOwner={first}"
            : $"[{s.Id}] both sockets in room {s.BattleId} report viewer {s.ViewerId} and no source IP settled it; using connect order -> isOwner={first}");
        return first;
    }

    // RoomCreate's sender owns the room and RoomEntry's is the visitor, so either message pins both seats
    void PinSeat(Session s, bool isOwner)
    {
        var map = isOwner ? _ownerViewer : _visitorViewer;
        if (map.TryGetValue(s.BattleId, out var had) && had != s.ViewerId)
            Console.WriteLine($"[{s.Id}] seat {(isOwner ? "owner" : "visitor")} of room {s.BattleId} moved {had} -> {s.ViewerId}");
        map[s.BattleId] = s.ViewerId;
    }

    BattleDeck? DeckFor(Session s)
    {
        var isOwner = IsOwner(s);
        var d = _decks.Get(s.BattleId, isOwner);
        Console.WriteLine($"DeckFor roomId={s.BattleId} viewer={s.ViewerId} isOwner={isOwner} found={d is not null} class={d?.ClassId ?? -1}");
        return d;
    }

    // handlers are fire-and-forget, so without this two of them interleave at their first await. both sides share the
    // ledger, the boost table and the peer's playSeq counter, and the peer stocks by playSeq
    readonly ConcurrentDictionary<string, SemaphoreSlim> _turnstile = new();

    public async Task Dispatch(Session s, string uri, JsonNode? payload, int? ackId)
    {
        var gate = _turnstile.GetOrAdd(s.BattleId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await DispatchInOrder(s, uri, payload, ackId); }
        finally { gate.Release(); }
    }

    async Task DispatchInOrder(Session s, string uri, JsonNode? payload, int? ackId)
    {
        s.LastHeard = DateTimeOffset.UtcNow;
        var pubSeq = payload?["pubSeq"]?.GetValue<int>() ?? 0;
        // name the card on every action, or a later complaint about "idx 43" cannot be tied to anything
        var what = uri == "PlayActions" && payload is JsonObject pj && AsInt(pj["playIdx"]) is int px
            ? $" type={AsInt(pj["type"])} idx={px} card={LedgerFor(s).GetValueOrDefault(px)}"
            : "";
        Console.WriteLine($"[{s.Id}] recv {uri} (pubSeq={pubSeq}){what}");
        switch (uri)
        {
            case "InitNetwork":
                if (ackId is int i1) await s.SendAck(i1, pubSeq);
                await s.SendMsg("InitNetwork", new { });
                break;
            case "RoomCreate":
                // ReceiveNodeResultCode.Success = 1. also need a synchronize msg back so PlayerControllerForOpponent.OnReceiveFast
                // sets ConnectRoomResult=SUCCESS and the room UI proceeds — the ACK alone doesn't do it.
                // Room URIs aren't in IsMatchingURI, so playSeq is omitted and the msg reaches PlayReceiveData unstocked
                PinSeat(s, isOwner: true);
                if (ackId is int i2) await s.SendAckResult(i2, pubSeq, new { resultCode = 1 });
                await s.SendMsg("RoomCreate", new { resultCode = 1 }, withPlaySeq: false);
                break;
            case "RoomEntry":
                PinSeat(s, isOwner: false);
                if (ackId is int i3) await s.SendAckResult(i3, pubSeq, new { resultCode = 1 });
                // ownerWin/guestWin make the visitor's ParseWinCount flip both slots from the -1 default to 0.
                // no userName here: with userName the client treats this self-echo as an opponent-enter and rebuilds the oppo card
                await s.SendMsg("RoomEntry", new { resultCode = 1, isSelf = 1, battleNum = 1, ownerWin = 0, guestWin = 0 }, withPlaySeq: false);
                var peerEntry = _sessions.Peer(s);
                Console.WriteLine($"  RoomEntry from {s.ViewerId} (battle {s.BattleId}), peer={(peerEntry?.ViewerId ?? "(none)")}");
                if (peerEntry is not null)
                {
                    // notify the owner that the visitor joined so the room UI shows the opponent card
                    await peerEntry.SendMsg("RoomEntry", OpponentEnterPayload(s), withPlaySeq: false);
                }
                // Matched fires the battle-load flow, so it must not come until both sides have picked a deck and pressed Ready
                break;
            case "Loaded":
                if (ackId is int iL) await s.SendAck(iL, pubSeq);
                s.Loaded = true;
                await MaybeBattleStart(s);
                break;
            case "Deal":
                // client requests its opening hand once it enters the battle scene (after BattleStart moved it to Prepared)
                if (ackId is int iDl) await s.SendAck(iDl, pubSeq);
                await SendDeal(s);
                break;
            case "Swap":
                if (ackId is int iSw) await s.SendAck(iSw, pubSeq);
                await HandleSwap(s, payload);
                break;
            case "Echo":
                // turn-start / action ack from the client. Ack it so its stock queue advances, but never relay it
                if (ackId is int iEc) await s.SendAck(iEc, pubSeq);
                break;
            case "TurnStart":
            case "PlayActions":
            case "TurnEndActions":
            case "TurnEnd":
            case "TurnEndFinal":
            case "SelectSkill":
            case "SelectObject":
                // steady-state turn traffic: ack the sender, relay to the peer on its own playSeq stream
                if (ackId is int iBt) await s.SendAck(iBt, pubSeq);
                _watch.NoteRelayed(s.BattleId, $"{s.Id} {uri}{what}");
                if (uri is "TurnEnd" or "TurnEndFinal") _watch.NoteTurnEnd(s.BattleId, s.Id, payload);
                await RelayBattle(s, uri, payload);
                break;
            case "Judge":
                // turn handoff, not peer traffic: echo it back to the sender so its ControlTurnStartPlayer runs and it
                // takes the turn. relaying to the peer would instead restart the peer's turn
                if (ackId is int iJd) await s.SendAck(iJd, pubSeq);
                if (_watch.NoteJudge(s.BattleId, s.Id, payload) is { } div)
                {
                    Console.WriteLine($"[{s.BattleId}] {div}");
                    foreach (var line in div.Window) Console.WriteLine($"[{s.BattleId}]   since last agreement: {line}");
                    if (await StopOnDivergence(s, div)) break;
                }
                await HandleJudge(s, payload);
                break;
            case "JudgeResult":
                // The client reports its outcome here with no ack/pubSeq (payload["log"] = JUDGE_RESULT_STATUS). A real
                // server answers with a BattleFinish carrying a definitive result code, which is the only thing that sets
                // _isJudgeResultReceive and lets the client leave the win/lose VFX. relaying to the peer would bounce forever
                await FinalizeFromReport(s, AsInt(payload?["log"]) ?? 100);
                break;
            case "SetupComplete":
                if (ackId is int iSc) await s.SendAckResult(iSc, pubSeq, new { resultCode = 1 });
                // isSelf=1 tells the sender's UI to mark their own slot ready. Peer gets isSelf=0 so their opponent slot flips
                await s.SendMsg("SetupComplete", new { resultCode = 1, isSelf = 1 }, withPlaySeq: false);
                s.Ready = true;
                var peerSc = _sessions.Peer(s);
                if (peerSc is not null) await peerSc.SendMsg("SetupComplete", new { resultCode = 1, isSelf = 0 }, withPlaySeq: false);
                await MaybeRoomReady(s);
                break;
            case "InitBattle":
            case "InitRoomBattle":
                if (ackId is int iIb) await s.SendAck(iIb, pubSeq);
                s.InitBattleSent = true;
                await MaybeMatched(s);
                break;
            case "SetupCancel":
                if (ackId is int iSx) await s.SendAckResult(iSx, pubSeq, new { resultCode = 1 });
                s.Ready = false;
                await s.SendMsg("SetupCancel", new { resultCode = 1, isSelf = 1 }, withPlaySeq: false);
                var peerScx = _sessions.Peer(s);
                if (peerScx is not null) await peerScx.SendMsg("SetupCancel", new { resultCode = 1, isSelf = 0 }, withPlaySeq: false);
                break;
            case "DeckSelect":
            case "DeckConfirm":
            case "BeginCreateDeck":
            case "SelectClass":
            case "SelectCardSet":
            case "DeckEntry":
            case "DeckBan":
            case "Rematch":
            case "TurnSelect":
            case "DeckNotify":
            case "RoomNotify":
            case "ChatStamp":
                if (ackId is int iRel) await s.SendAckResult(iRel, pubSeq, new { resultCode = 1 });
                await RelayRoomEvent(s, uri, payload);
                break;
            case "Kick":
                if (ackId is int iK) await s.SendAckResult(iK, pubSeq, new { resultCode = 1 });
                await s.SendMsg(uri, new { resultCode = 1, isSelf = 1 }, withPlaySeq: false);
                var peerK = _sessions.Peer(s);
                if (peerK is not null) await peerK.SendMsg(uri, new { resultCode = 1, isSelf = 0 }, withPlaySeq: false);
                break;
            case "Reenter":
                if (ackId is int iRen) await s.SendAckResult(iRen, pubSeq, new { resultCode = 1 });
                await s.SendMsg("Reenter", new { resultCode = 1, isSelf = 1 }, withPlaySeq: false);
                break;
            case "Retire":
                if (ackId is int iRe) await s.SendAck(iRe, pubSeq);
                // codes are self-relative: the retiree loses, the peer wins
                await Finalize(s, reporterCode: 106, peerCode: 105);
                break;
            case "Leave":
                if (ackId is int iLv) await s.SendAckResult(iLv, pubSeq, new { resultCode = 1 });
                await s.SendMsg("Leave", new { resultCode = 1 }, withPlaySeq: false);
                var peerLv = _sessions.Peer(s);
                if (peerLv is not null) await peerLv.SendMsg("Leave", new { resultCode = 1 }, withPlaySeq: false);
                break;
            case "Release":
            case "ForceRelease":
                if (ackId is int i4) await s.SendAckResult(i4, pubSeq, new { resultCode = 1 });
                await s.SendMsg(uri, new { resultCode = 1 }, withPlaySeq: false);
                var pRel = _sessions.Peer(s);
                if (pRel is not null) await pRel.SendMsg("Release", new { resultCode = 1 }, withPlaySeq: false);
                break;
            default:
                if (ackId is int i5) await s.SendAck(i5, pubSeq);
                break;
        }
    }

    // relay a room-URI event to peer, echoing the sender's payload (with isSelf tags added)
    async Task RelayRoomEvent(Session s, string uri, JsonNode? payload)
    {
        var self = new JsonObject { ["resultCode"] = 1, ["isSelf"] = 1 };
        var peerObj = new JsonObject { ["resultCode"] = 1, ["isSelf"] = 0 };
        if (payload is JsonObject po)
            foreach (var (k, v) in po)
                if (k != "uri" && k != "pubSeq" && k != "resultCode" && k != "isSelf")
                {
                    self[k] = v?.DeepClone();
                    peerObj[k] = v?.DeepClone();
                }
        // the client sends a flat "stamp" but only ever reads the emote back out of a nested chatStamp:{stamp:N},
        // so without wrapping it the receiver leaves oppoChatStamp at 0 and no emote plays
        if (uri == "ChatStamp" && payload?["stamp"] is JsonNode stamp)
        {
            self["chatStamp"] = new JsonObject { ["stamp"] = stamp.DeepClone() };
            peerObj["chatStamp"] = new JsonObject { ["stamp"] = stamp.DeepClone() };
        }
        await s.SendMsg(uri, self, withPlaySeq: false);
        var peer = _sessions.Peer(s);
        if (peer is not null) await peer.SendMsg(uri, peerObj, withPlaySeq: false);
    }

    // A peer that stopped answering for this long is gone as far as the match is concerned. The client's own BattleStop is
    // 95s, so this has to fire well inside that or both sides just sit there
    public static readonly TimeSpan PeerSilence = TimeSpan.FromSeconds(45);

    // Off unless asked for. A real drop is obvious to both players anyway, and ending a live battle on a heartbeat this
    // relay had already been counting wrong is a far worse failure than the one it was added to fix
    static readonly bool DropOnSilence =
        Environment.GetEnvironmentVariable("OPENVERSE_PEER_TIMEOUT")?.Trim().ToLowerInvariant() is "1" or "true" or "on";

    // ocs=WAITING makes the client show "connection unstable" and disable touch (mulligan can't be submitted), so a
    // transient lookup miss still reports ONLINE. only a measured silence is worth telling the survivor about
    public static bool PeerIsOnline(Session? peer, DateTimeOffset now) =>
        peer is null || now - peer.LastHeard < PeerSilence;

    public async Task Alive(Session s)
    {
        var peer = _sessions.Peer(s);
        var silence = peer is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - peer.LastHeard;
        var online = PeerIsOnline(peer, DateTimeOffset.UtcNow);
        Console.WriteLine($"[{s.Id}] alive (battle={s.BattleId}, peer={(peer?.Id ?? "null")}, roster={_sessions.ByBattle(s.BattleId).Count}, peerSilent={silence.TotalSeconds:F0}s)");
        await s.SendAlive(peerOnline: online || !DropOnSilence);
        if (!online && peer is not null)
        {
            Console.WriteLine($"[{s.Id}] peer {peer.Id} has been silent {silence.TotalSeconds:F0}s"
                            + (DropOnSilence ? " -> ending the battle" : " (not acting on it; set OPENVERSE_PEER_TIMEOUT to)"));
            if (DropOnSilence) await PeerClosed(peer);
        }
    }

    // both SetupComplete: kick the client into the RoomReady flow, which triggers DoMatching HTTP then emits
    // InitRoomBattle back here
    async Task MaybeRoomReady(Session s)
    {
        var peer = _sessions.Peer(s);
        if (peer is null || !peer.Ready) return;
        await s.SendMsg("RoomReady", new { resultCode = 1 }, withPlaySeq: false);
        await peer.SendMsg("RoomReady", new { resultCode = 1 }, withPlaySeq: false);
    }

    // once both clients have emitted InitRoomBattle after DoMatching, Matched can finally go out to trigger StartBattleLoad
    async Task MaybeMatched(Session s)
    {
        var peer = _sessions.Peer(s);
        if (peer is null || !peer.InitBattleSent) return;
        // both sides reach here, and a re-entrant second pass would re-roll the coin flip, the stage, both idx seeds, both
        // deck seeds and the StableRandom seed on a battle already running
        if (s.MatchedSent || peer.MatchedSent) return;
        s.MatchedSent = peer.MatchedSent = true;
        // roll the coin flip and the stage once, here, since Matched is the first place both are needed. turnState flags
        // first/second at Ready (the client reads it to set isEnemyFirstTurn), so it must be 0/1 here (only after Ready
        // does the relay push both to 0). fieldId picks the map and must match on both clients
        var first = Random.Shared.Next(2) == 0 ? s : peer;
        var second = first == s ? peer : s;
        _firstPlayer[s.BattleId] = first;
        _turnOwner[s.BattleId] = first;
        _fieldId[s.BattleId] = Random.Shared.Next(1, 8);
        _finished[s.BattleId] = false;
        _watch.Reset(s.BattleId);
        // One reshuffle seed per player, kept for the whole battle. each seeds that player's own deck XorShift, handed out
        // mirror-swapped at Deal so a card added to a player's deck reshuffles identically on both clients and here
        int sA = RollSeed(), sB = RollSeed();
        _idxSeed[first.Id] = sA;
        _idxSeed[second.Id] = sB;
        _rng[first.Id] = new XorShift(sA);
        _rng[second.Id] = new XorShift(sB);
        // Matched is the one point per battle where a rematch is distinguishable from a reconnect, so roll draw order
        // here. keyed by room and player rather than by session so a mid-battle reconnect keeps the deck it was dealt
        foreach (var p in new[] { first, second })
        {
            _deckSeed[DeckKey(p)] = RollSeed();
            _shuffled.Remove(p.Id);
        }
        // shared between the pair, unlike the deck seeds: it drives the StableRandom stream both clients draw from
        _stableSeed[s.BattleId] = Random.Shared.Next();
        await first.SendMsg("Matched", MatchedPayload(first, second, s.BattleId, turnState: 0));
        await second.SendMsg("Matched", MatchedPayload(second, first, s.BattleId, turnState: 1));
    }

    // "warn" only reports. "stop" ends a match the two clients have stopped agreeing about, rather than letting it run on
    // a board neither of them can trust. defaults to warn until the comparator has been watched on real matches
    static readonly string DesyncMode = Environment.GetEnvironmentVariable("OPENVERSE_DESYNC")?.Trim().ToLowerInvariant() ?? "warn";
    // one diverged boundary can be a straggler, two in a row means it is not repairing itself
    const int StopAfterConsecutive = 2;

    // End + endType=VALIDATE lands on DataInconsistencyBattleEndOperation -> JudgeEndTypeToLose -> ReceiveInvalidLose, and
    // the client then reports 900/901, which DecideResult already reads as a mutual no-contest
    async Task<bool> StopOnDivergence(Session s, Divergence div)
    {
        if (DesyncMode != "stop" || div.Consecutive < StopAfterConsecutive) return false;
        lock (_finishLock)
        {
            if (_finished.GetValueOrDefault(s.BattleId)) return false;
            _finished[s.BattleId] = true;
        }
        Console.WriteLine($"[{s.BattleId}] no-contest: {div.Consecutive} diverged boundaries in a row");
        try
        {
            foreach (var p in _sessions.ByBattle(s.BattleId))
                await p.SendMsg("End", new { endType = 2 });
        }
        finally { EndShadow(s, 900); }
        return true;
    }

    // XorShift.IsActive requires seed != -1. Any other int (incl 0) is a valid seed
    static int RollSeed() { int s; do s = Random.Shared.Next(int.MinValue, int.MaxValue); while (s == -1); return s; }

    // One id for every "I cannot name this card": the wire placeholder, unresolved deck padding, and the value the
    // naming passes refuse to state. it must be the engine's dummy or the shadow and the client hold different cards
    const int Placeholder = Engine.MirrorPair.Dummy;
    const int HandState = 10;  // NetworkCardPlaceState.Hand

    // both Loaded: BattleStart is the only matching-phase msg that flips status to Prepared and stops the 25s loadedTimeout.
    // send it with no playSeq so it plays immediately (IsNoStockData) and the first stocked synchronize (Deal) stays at 3.
    // selfInfo+oppoInfo each need rank/classId/charaId or SetParameter throws and IsReady never latches
    async Task MaybeBattleStart(Session s)
    {
        var peer = _sessions.Peer(s);
        if (peer is null || !peer.Loaded || s.BattleStartSent) return;
        // MicroTimeToFromUnixTime divides by 1000 then AddSeconds, so it wants milliseconds. passing microseconds
        // overflows DateTime and throws inside ReactionReceiveUri before it can reach Prepared, causing the timeout
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // reuse the first/second roll from Matched so BattleStart's turnState agrees
        var first = _firstPlayer.TryGetValue(s.BattleId, out var f) ? f : s;
        var second = first == s ? peer : s;
        await first.SendMsg("BattleStart", BattleStartPayload(first, second, now, turnState: 0), withPlaySeq: false);
        await second.SendMsg("BattleStart", BattleStartPayload(second, first, now, turnState: 1), withPlaySeq: false);
        s.BattleStartSent = true;
        peer.BattleStartSent = true;
        // Deal is sent in reply to each client's own Deal request (MulliganStartDraw), not pushed here.
        // pushing early lands it before the mulligan receiver is bound, so it gets consumed without opening the mulligan
    }

    async Task SendDeal(Session s)
    {
        if (s.DealSent) return;
        s.DealSent = true;
        // The seeds ride the Deal reply once, mirror-swapped: the client sets its self stream from idxChangeSeed and its
        // opponent stream from oppoIdxChangeSeed, so each player's deck reshuffles from the same seed on both clients.
        // do not re-send them (would re-seed the client mid-stream). must be the Deal, not Matched/BattleStart, which the
        // matching-phase parser (SetNetworkInfo) drops these keys
        var peer = _sessions.Peer(s);
        await s.SendMsg("Deal", new
        {
            cards = DealCards(s),
            idxChangeSeed = _idxSeed.GetValueOrDefault(s.Id, -1),
            oppoIdxChangeSeed = peer is null ? -1 : _idxSeed.GetValueOrDefault(peer.Id, -1),
        });
    }

    // client Swap carries idxList = abandoned deck slots. reply with fresh slots at those hand positions, then gate on both
    async Task HandleSwap(Session s, JsonNode? payload)
    {
        s.Redraws.Clear();
        if (payload?["idxList"] is JsonArray idxList)
            foreach (var n in idxList)
            {
                // idxList holds abandoned deck Indices (1-based). The opening hand dealt deck slot k at hand position k-1,
                // so an abandoned Index maps to hand position Index-1. The replacement comes from a fresh deck slot
                int abandoned = n?.GetValue<int>() ?? 0;
                s.Redraws[abandoned - 1] = s.NextDeckIdx++;
            }
        await s.SendMsg("Swap", new { cards = RedrawCards(s, isSelf: 1) });
        s.MulliganDone = true;
        var peer = _sessions.Peer(s);
        if (peer is null || !peer.MulliganDone) return;
        // reshuffle is eligible only after mulligan (mulligan returns keep their Index, no enroll). seed each deck mirror =
        // all idx minus the 3 kept-hand indices (opening slots 1..3, replaced per redraw). The DeckCard objects here are the
        // ones the reshuffle mutates and enrolls by reference
        _mulliganEnd[s.BattleId] = true;
        foreach (var x in new[] { s, peer })
        {
            var hand = new HashSet<int>();
            for (int pos = 0; pos < 3; pos++) hand.Add(x.Redraws.TryGetValue(pos, out var r) ? r : pos + 1);
            _deck[x.Id] = LedgerFor(x).Keys.Where(k => !hand.Contains(k))
                .OrderBy(k => k).Select(k => new DeckCard { Index = k }).ToList();
            _addQueue[x.Id] = new List<DeckCard>();
        }
        // both submitted: Ready reveals each side the opponent's post-mulligan hand. no server TurnStart follows: the first
        // player self-starts turn 1 (client StartBattle IsFirst path) and emits its own TurnStart, which the relay forwards
        // to the peer. A received TurnStart always runs ControlTurnStartOpponent, so sending one to the first player would
        // make it process its own turn as the opponent's, flip IsSelfTurn off, and stall until the 95s BattleStop
        await s.SendMsg("Ready", new { cards = RedrawCards(peer, isSelf: 0) });
        await peer.SendMsg("Ready", new { cards = RedrawCards(s, isSelf: 0) });
        BeginShadow(s, peer);
    }

    // replacement cards for a session's redraws. isSelf tags whose hand this is from the receiver's view.
    // own redraws show the real (shuffled) card at the fresh deck slot. The opponent's stay hidden as the placeholder
    object[] RedrawCards(Session owner, int isSelf) =>
        owner.Redraws.Select(kv => (object)new { idx = kv.Value, cardId = isSelf == 1 ? ShuffledDeckFor(owner)[kv.Value - 1] : Placeholder, isSelf, pos = kv.Key, to = HandState }).ToArray();

    object[] DealCards(Session s)
    {
        var deck = ShuffledDeckFor(s);
        var cards = new List<object>();
        for (int isSelf = 1; isSelf >= 0; isSelf--)
            for (int i = 0; i < 3; i++)
                cards.Add(new { idx = i + 1, cardId = isSelf == 1 ? deck[i] : Placeholder, isSelf, pos = i, to = HandState });
        return cards.ToArray();
    }

    // relay a battle action to the peer on its own contiguous playSeq stream. The peer re-simulates the play from playIdx
    // (orderList/battleCode are dropped on receive) and resolves isSelf against the shared index namespace, where isSelf=1
    // already means the emitter = the peer's BattleEnemy. flipping isSelf sends board-interfering effects to the wrong
    // side, and no index needs remapping since both clients share the deck seeds.
    // BUT the payload is NOT pure passthrough: wherever the client's own send format differs from its own receive format,
    // the real server converted it, so this side has to as well (knownList/keyAction/targetList below). "the peer reads isSelf"
    // only holds for keys whose parse actually reads it - targetList's live parse does not, which is why it is renamed
    async Task RelayBattle(Session s, string uri, JsonNode? payload)
    {
        var peer = _sessions.Peer(s);
        if (peer is null) return;
        var body = new JsonObject();
        if (payload is JsonObject po)
            foreach (var (k, v) in po)
                if (k is not ("uri" or "pubSeq" or "cat" or "try" or "viewerId" or "uuid" or "bid" or "playSeq" or "turnState"))
                    body[k] = v?.DeepClone();
        // after Ready both players sit at turnState 0: the receiver needs it 0 so a relayed TurnStart runs
        // ControlTurnStartOpponent once isEnemyFirstTurn clears, and the active player needs it 0 to end its turn
        body["turnState"] = 0;
        NormalizeKeyAction(body);
        NormalizeTargetList(body);
        LearnAddedCards(s, body);
        InjectKnownCard(s, uri, body);
        InjectConditionAnswers(s, uri, body);
        InjectSummonedCardIds(s, body);
        InjectSpin(s, body);
        UpdateLedger(s, body);
        UpdateCostState(s, body);
        TrackVitals(s, body);
        ApplyDeckMoves(s, body);
        NoteExtraTurn(s, body);
        MaybeFlush(s, uri);
        await peer.SendMsg(uri, body);
        await MaybeFinalizeNaturalFinish(s);
        // the winner closes the killing action with this, which is the earliest point the loser has seen it play out
        if (uri is "TurnEndFinal") await FlushVerdict(s.BattleId, "action closed");
        RecordShape(uri, body);
        // after the send, never before: the shadow is an observer and the peer should not wait on it
        ObserveInRoom(s, uri, body);
    }

    // which session each room's A board holds, and the pair it holds it on. keyed by room rather than kept in one field
    // so two rooms playing at once do not answer each other's questions
    readonly ConcurrentDictionary<string, (string PlayerId, Engine.MirrorPair? Pair)> _shadowOf = new();

    string? ShadowPlayerId(Session s) => _shadowOf.TryGetValue(s.BattleId, out var o) ? o.PlayerId : null;
    Engine.MirrorPair? PairFor(Session s) => _shadowOf.TryGetValue(s.BattleId, out var o) ? o.Pair : null;
    bool IsShadowPlayer(Session s) => s.Id == ShadowPlayerId(s);

    // a question about this session's own cards goes to the board that holds them as real cards rather than as one of
    // the forty dummies, which is why every call below asks it about isSelfPlayer: true
    Engine.ShadowMirror? Board(Session s) => PairFor(s)?.Actor(IsShadowPlayer(s));

    // both boards take every message, and the receiving one then says what a stock client would have done with it -
    // the same check that writes ConductError, read here instead of out of a client log afterwards
    void ObserveInRoom(Session s, string uri, JsonObject body)
    {
        if (PairFor(s) is not { } pair) return;
        var fromA = IsShadowPlayer(s);
        pair.Observe(uri, body, fromA, l => Console.WriteLine($"[{s.Id}] {l}"));
        if (Engine.ShadowBridge.Role < Engine.ShadowBridge.EngineRole.AnswerBlanks) return;
        if (pair.PeerRefusedLast(fromA, out var verdict))
            Console.WriteLine($"[{s.Id}] the peer will REFUSE this {uri}: {verdict}");
    }

    void BeginShadow(Session a, Session b)
    {
        var first = _firstPlayer.TryGetValue(a.BattleId, out var f) ? f : a;
        var second = first == a ? b : a;
        var pair = Engine.ShadowBridge.For(a.BattleId);
        _shadowOf[a.BattleId] = (first.Id, pair);
        if (pair is null)
        {
            Console.WriteLine($"[{a.BattleId}] no engine available for this room (all pairs are busy); relaying without one");
            return;
        }
        // the StableRandom stream has to be the one the two clients draw from, not a deck seed: every answer either
        // board gives about a random effect is computed from it
        pair.Begin(_stableSeed.GetValueOrDefault(a.BattleId, 12345), aFirst: true,
            ShuffledDeckFor(first), ShuffledDeckFor(second),
            OpeningHandIdx(first), OpeningHandIdx(second),
            l => Console.WriteLine($"[{first.Id}] {l}"));
        // The clients reshuffle a card's deck slot when it goes back, off idxChangeSeed, and the shadow never sees the
        // Deal that carries it. ShadowMatch.SetDeckMirror installs one, but doing so measurably worsens the only
        // end-to-end check there is (ShadowCostFidelityTests goes 3 charges short), so leave it off until a capture
        // shows which way round is in step with the players
    }

    static int[] OpeningHandIdx(Session s)
    {
        var hand = new int[3];
        for (int pos = 0; pos < 3; pos++) hand[pos] = s.Redraws.TryGetValue(pos, out var r) ? r : pos + 1;
        return hand;
    }

    readonly HashSet<string> _seenShapes = new();

    void RecordShape(string uri, JsonObject body)
    {
        var shape = uri + " {" + string.Join(",", body.Select(kv => kv.Key).OrderBy(k => k)) + "}";
        if (body["keyAction"] is JsonArray { Count: > 0 } ka && ka[0] is JsonObject e0)
            shape += " keyAction[0]{" + string.Join(",", e0.Select(kv => kv.Key).OrderBy(k => k)) + "}";
        if (_seenShapes.Add(shape)) Console.WriteLine($"shape {shape}");
    }

    // track each deck's index set from the move entries and enroll cards that land in the deck, so a later flush reshuffles
    // them. A card enters the deck as a move to=Deck(0) (returns + a token's companion move); leaves as a move from=Deck.
    // the add/uList reveals in UpdateLedger set cardIds only and never enroll, so enrollment has a single channel
    // observed from the playerParam entries the clients broadcast, not simulated. MaybeFinalizeNaturalFinish decides
    // matches off Life, so a wrong value here loses someone a game
    public sealed class Vitals
    {
        public int Life = 20, Pp, MaxPp, Cemetery;
        public override string ToString() => $"life={Life} pp={Pp}/{MaxPp} grave={Cemetery}";
    }

    readonly Dictionary<string, Vitals> _vitals = new();

    public Vitals VitalsFor(Session s) => _vitals.TryGetValue(s.Id, out var v) ? v : _vitals[s.Id] = new Vitals();

    // damage/heal/addPP arrive as magnitudes (the sender takes Math.Abs), so the key carries the sign. Set/cemetery/maxPP
    // are absolute. isSelf is sender-relative, the same convention the move entries use
    void TrackVitals(Session s, JsonObject body)
    {
        if (body["orderList"] is not JsonArray ol) return;
        var peer = _sessions.Peer(s);
        foreach (var el in ol)
        {
            if (el is not JsonObject eo || eo["playerParam"] is not JsonObject e) continue;
            var owner = (AsInt(e["isSelf"]) ?? 1) == 0 ? peer : s;
            if (owner is null) continue;
            var v = VitalsFor(owner);
            foreach (var (k, node) in e)
            {
                if (AsInt(node) is not int n) continue;
                switch (k)
                {
                    case "damage": v.Life -= n; break;
                    case "heal": v.Life += n; break;
                    case "set": v.Life = n; break;
                    case "addPP": v.Pp += n; break;
                    case "usePP": v.Pp -= n; break;
                    case "maxPP": v.MaxPp = n; break;
                    case "cemetery": v.Cemetery = n; break;
                }
            }
            Console.WriteLine($"[{owner.Id}] vitals {v}");
        }
    }

    void ApplyDeckMoves(Session s, JsonObject body)
    {
        if (body["orderList"] is not JsonArray ol) return;
        var peer = _sessions.Peer(s);
        foreach (var el in ol)
            if (el is JsonObject eo && eo["move"] is JsonObject e)
            {
                var owner = (AsInt(e["isSelf"]) ?? 1) == 0 ? peer : s;
                if (owner is null || _deck.GetValueOrDefault(owner.Id) is not { } deck) continue;
                int from = AsInt(e["from"]) ?? -1, to = AsInt(e["to"]) ?? -1;
                foreach (var ix in ReadIdxList(e["idx"]))
                {
                    if (ix == -99) continue;  // deck-shortage sentinel, not a real card
                    if (to == 0)
                    {
                        var dc = new DeckCard { Index = ix };
                        int at = deck.FindIndex(d => d.Index > ix);
                        if (at < 0) deck.Add(dc); else deck.Insert(at, dc);
                        if (_mulliganEnd.GetValueOrDefault(owner.BattleId)) _addQueue[owner.Id].Add(dc);
                    }
                    else if (from == 0) deck.RemoveAll(d => d.Index == ix);
                }
            }
    }

    // Flush the reshuffle queues at exactly the client's 6 completion events: every PlayActions (play/evolve/attack/fusion
    // complete), TurnStart, and once per turn end (TurnEndActions if present, else TurnEnd). The sender owns the trigger
    void MaybeFlush(Session s, string uri)
    {
        var battle = s.BattleId;
        if (uri == "TurnStart") _turnEndFlushed[battle] = false;  // new turn
        bool flush = uri switch
        {
            "PlayActions" or "TurnStart" or "TurnEndActions" => true,
            "TurnEnd" => !_turnEndFlushed.GetValueOrDefault(battle),  // skip if TurnEndActions already flushed this turn end
            _ => false,
        };
        if (uri == "TurnEndActions") _turnEndFlushed[battle] = true;
        if (!flush) return;
        FlushQueue(s);
        if (_sessions.Peer(s) is { } peer) FlushQueue(peer);
    }

    // Drain a player's queue: each in-deck card in insertion order draws one Next() from that player's stream and swaps its
    // deck slot with the changeInt-th card, mirroring AddToDeckCardIndexChange. The two queues use independent streams so the
    // owner-first order is not load-bearing across them, but order WITHIN a queue is (each consumes one Next)
    void FlushQueue(Session owner)
    {
        if (_addQueue.GetValueOrDefault(owner.Id) is not { Count: > 0 } q) return;
        var rng = _rng.GetValueOrDefault(owner.Id);
        var deck = _deck.GetValueOrDefault(owner.Id);
        if (rng is null || !rng.IsActive || deck is null || !_mulliganEnd.GetValueOrDefault(owner.BattleId)) { q.Clear(); return; }
        var ledger = LedgerFor(owner);
        foreach (var dc in q)
        {
            if (!deck.Contains(dc)) continue;  // IsInDeck: a card drawn/removed before flush is skipped, consumes no RNG
            int ci = rng.GetChangeInt(deck.Count);  // count includes the just-added card; stream advances even if we skip
            if (ci < 0 || ci >= deck.Count) continue;  // client would throw here; skip the one swap rather than crash the relay
            var tgt = deck[ci];
            int a = dc.Index, t = tgt.Index;
            (ledger[a], ledger[t]) = (ledger.GetValueOrDefault(t), ledger.GetValueOrDefault(a));
            // the cost/boost state is keyed by index too, so it has to ride along or a later reveal prices the wrong card
            var cm = ModsFor(owner);
            var bm = ChargesFor(owner);
            (cm[a], cm[t]) = (cm.GetValueOrDefault(t) ?? new List<CostMod>(), cm.GetValueOrDefault(a) ?? new List<CostMod>());
            (bm[a], bm[t]) = (bm.GetValueOrDefault(t), bm.GetValueOrDefault(a));
            dc.Index = t; tgt.Index = a;
            deck.Sort((p, q2) => p.Index - q2.Index);
        }
        q.Clear();
    }

    // PlayActionType that plays a card from hand and so resolves it via GetPlayCard (needs the knownList reveal):
    // PLAY_HAND (untargeted), PLAY_HAND_SELECT (targeted), FUSION (the fusion card comes from hand). ATTACK/EVOLUTION
    // act on an on-board card and don't
    const int PlayHand = 30;
    const int PlayHandSelect = 31;
    const int Fusion = 40;

    // The one place the client's own send and receive formats disagree, so the real server must have converted it: the
    // sender always nests the selection as selectCard:{cardId|cardIdx:[...], open:N}, but the receiver wants selectCard
    // to BE the array and BurialRate's cardIdx hoisted onto the entry. passing the nested form through NREs the peer's
    // ConvertToListInt, which drops the whole play and freezes its receive sequence - every later message strands behind
    // the gap. `open` has no reader anywhere in the client, which is what gives the server-side conversion away
    // the sender only ever emits `targetList`, but the receiver's targetList case hardcodes isWatch:true and that branch
    // reads `vid` - which no client writes - so isSelf keeps its `false` initializer and every sender-owned target
    // resolves against the RECEIVER's own board. The isSelf-reading parse is reached only via `oppoTargetList`, a key
    // nothing in the client writes: the real server renamed it per recipient. rename rather than copy, or dictionary
    // order decides which parse wins. never inject `vid` instead - its handler is null in live play and would throw
    public static void NormalizeTargetList(JsonObject body)
    {
        if (body["targetList"] is not JsonArray tl) return;
        body["oppoTargetList"] = tl.DeepClone();
        body.Remove("targetList");
    }

    public static void NormalizeKeyAction(JsonObject body)
    {
        if (body["keyAction"] is not JsonArray ka) return;
        foreach (var el in ka)
        {
            if (el is not JsonObject e || e["selectCard"] is not JsonObject sc) continue;
            if (sc["cardIdx"] is JsonArray idx) { e["cardIdx"] = idx.DeepClone(); e.Remove("selectCard"); }
            else if (sc["cardId"] is JsonArray ids) e["selectCard"] = ids.DeepClone();
        }
    }

    const int FieldState = 20;     // NetworkCardPlaceState.Field
    const int CemeteryState = 30;
    const int BanishState = 40;
    const int RidingState = 70;
    const int UniteState = 90;

    // NetworkCardPlaceState, decided once for every destination rather than one bug report at a time. A card that lands
    // somewhere both players can look at has to be named, or the peer builds the entry off its own forty dummies and
    // shows a Goblin. 葬送 (hand -> cemetery) is the one that surfaced it, and it is the second commonest move in the
    // shipped capture, so it had been wrong the whole time.
    // public: Field, Cemetery, Banish, and Riding/Unite (both are attached to a unit standing on the board).
    // private: Deck and Hand hide their contents, and Reservation is a card queued for hand.
    // undecided: None(50), FusionIngredient(60), BlackHole(999) - they are declined, and the decline is logged, so a
    // gap shows up as a line rather than as a player noticing a Goblin
    static bool PublicDestination(int? to) =>
        to is FieldState or CemeteryState or BanishState or RidingState or UniteState;

    void InjectSummonedCardIds(Session s, JsonObject body)
    {
        var ledger = LedgerFor(s);
        InjectSummonedCardIds(body, ix =>
        {
            if (ledger.TryGetValue(ix, out var known)) return known;
            if (Board(s) is not {} bd1 || !bd1.TryCardIdOf(true, ix, out var fromEngine)) return 0;
            Console.WriteLine($"[{s.Id}] engine resolved idx={ix} -> {fromEngine} (ledger had nothing)");
            return fromEngine;
        }, (from, to, idx, why) =>
            Console.WriteLine($"[{s.Id}] NOT NAMED idx={idx} moving {Zone(from)}->{Zone(to)}: {why}. "
                            + "the peer will show whatever its own deck has at that slot"));
    }

    static string Zone(int? z) => z switch
    {
        0 => "deck", 10 => "hand", FieldState => "field", CemeteryState => "cemetery", BanishState => "banish",
        50 => "none", 60 => "fusion", RidingState => "riding", 80 => "reservation", UniteState => "unite",
        999 => "blackhole", null => "(unstated)", _ => z.ToString()!,
    };

    // cardId is one scalar per entry, but the sender merges adjacent records whose fields all match - and its merge gate
    // compares CardId, which is 0 on every one of these, so two DIFFERENT cards collapse into a single entry. un-merging
    // is the exact inverse of that merge: the receiver explodes idxList into one card per index and broadcasts every other
    // key to all of them, so N one-idx entries parse identically to one N-idx entry. randomTargetIdx is the lone
    // positional field (it reads against the idxList loop), so decline rather than slice it - these never carry one.
    // splice in place: the first entry's cardId is latched for accelerate, and the deck-skill dedup indexes this list
    public static void InjectSummonedCardIds(JsonObject body, Dictionary<int, int> ledger)
        => InjectSummonedCardIds(body, ix => ledger.TryGetValue(ix, out var c) ? c : 0);

    /// <param name="resolve">idx -> cardId, or 0 when the card cannot be named (which keeps the safe decline)</param>
    /// <param name="declined">told about every entry left unnamed, so a gap is a log line rather than a bug report</param>
    public static void InjectSummonedCardIds(JsonObject body, Func<int, int> resolve,
                                             Action<int?, int?, int, string>? declined = null)
    {
        if (body["uList"] is not JsonArray ul) return;
        var next = new JsonArray();
        var split = false;
        void Report(int? from, int? to, int idx, string why) => declined?.Invoke(from, to, idx, why);
        foreach (var uo in ul)
        {
            var keep = uo?.DeepClone();
            if (uo is not JsonObject u
                || AsInt(u["cardId"]) is not null            // the client named it; that id wins
                || (AsInt(u["isSelf"]) ?? 1) != 1)
            { next.Add(keep); continue; }

            var to = AsInt(u["to"]);
            var from = AsInt(u["from"]);
            // Hand -> field is the play route InjectKnownCard owns. Hand -> cemetery/banish is 葬送 and needs naming
            var why = !PublicDestination(to) ? "that destination is not public"
                    : from is null ? "the actor did not say where it came from"
                    : from == HandState && to == FieldState ? null      // InjectKnownCard's route, not a gap
                    : null;
            if (why is not null)
            {
                // a destination the peer can see is the only thing that has ever caused this, so say which one it was
                if (PublicDestination(to) || to is not (0 or HandState or 80))
                    Report(from, to, ReadIdxList(u["idxList"] ?? u["idx"]).FirstOrDefault(), why);
                next.Add(keep); continue;
            }
            if (from == HandState && to == FieldState) { next.Add(keep); continue; }

            var idxs = ReadIdxList(u["idxList"] ?? u["idx"]).ToList();
            // resolve once per index: the resolver may reach the engine, and asking twice for the same slot would
            // double that cost for no gain
            var ids = idxs.Select(resolve).ToList();
            // All-or-nothing: an unresolved index (the -99 shortage sentinel included) keeps the known-safe decline.
            // this is the one that actually shows a Goblin - the destination is public, so the peer WILL draw the entry
            // off its own deck - so it is the loudest of the declines
            if (idxs.Count == 0 || ids.Any(c => c == 0))
            {
                Report(from, to, idxs.FirstOrDefault(), "neither the ledger nor the engine could name it");
                next.Add(keep); continue;
            }

            var cardId = ids[0];
            if (ids.All(c => c == cardId)) { u["cardId"] = cardId; next.Add(u.DeepClone()); continue; }
            if (u.ContainsKey("randomTargetIdx")) { next.Add(keep); continue; }

            for (int i = 0; i < idxs.Count; i++)
            {
                var one = u.DeepClone()!.AsObject();
                one.Remove("idx");
                one["idxList"] = new JsonArray(idxs[i]);
                one["cardId"] = ids[i];
                next.Add(one);
            }
            split = true;
        }
        if (split) body["uList"] = next;
    }

    // The client never emits knownList (the real server injects it): on a hand play the peer resolves the played card via
    // GetPlayCard() = knownCardList.First(c => c.Index == playIdx), so with no knownList it dereferences null and NREs
    // before rendering, logging, or echoing - every opponent hand play is silently dropped. knownList is also the only
    // channel that reveals the face-down enemy card. synthesize the one entry from the sender's deck (Index == deck slot)
    void InjectKnownCard(Session s, string uri, JsonObject body)
    {
        if (uri != "PlayActions" || body["type"]?.GetValue<int>() is not (PlayHand or PlayHandSelect or Fusion)) return;
        if (body["playIdx"]?.GetValue<int>() is not int pIdx) return;
        if (!LedgerFor(s).TryGetValue(pIdx, out var cardId))
        {
            // A card that entered hand during the match has no deck slot, and the actor sends cardId 0 for anything the
            // peer cannot see, so the ledger never learns it. declining here drops the whole play on the peer, so ask
            // the engine, which has been playing the same match
            if (Board(s) is not {} bd0 || !bd0.TryCardIdOf(true, pIdx, out cardId) || cardId == 0) return;
            Console.WriteLine($"[{s.Id}] play idx={pIdx}: engine named {cardId} (ledger had nothing)");
        }
        var entry = new JsonObject { ["idx"] = pIdx, ["cardId"] = cardId, ["isSelf"] = 0, ["is_open"] = 1 };
        // state the actor's post-modifier Cost, but NOT the fixed-use/accelerate price: the receive check gates
        // accelerate on CalcFixedUseCost >= Cost, so pinning that would reject every accelerate play
        int? relayCost = TryFinalCost(s, pIdx, cardId, out var finalCost) ? finalCost : null;
        if (TryEngineCostAdvice(s, pIdx, cardId, relayCost, out var advised)) relayCost = advised;
        else
            Board(s)?.CompareCost(true, pIdx, relayCost,
                l => Console.WriteLine($"[{s.Id}] {l} (card {cardId})"));
        // an absent cost is not "no opinion": ReplaceReceivedCard only adds a CostSetModifier when one is stated, so the
        // peer bills the printed cost, drains the actor's Pp and starts refusing plays. every board desync so far
        // started there
        if (relayCost is null && Project(s, pIdx, cardId) is int projected) relayCost = projected;
        if (relayCost is int c) entry["cost"] = c;
        else if (_cardCosts.TryGetValue(cardId, out var known) && known.BaseCost > 0)
            Console.WriteLine($"[{s.Id}] cost idx={pIdx} card={cardId}: NOT STATED, the peer will charge {known.BaseCost}");
        // the peer throws its face-down placeholder away and rebuilds the card from cardId, so it starts at charge 0
        // (CardDataModel.Spellboost defaults to -1, which SetSpellChargeCount ignores) and every skill reading the
        // count then behaves differently on the two machines
        if (!_costBlind.Contains(s.Id) && ChargesFor(s).GetValueOrDefault(pIdx) is var charges && charges > 0)
            entry["spellboost"] = charges;
        // The highlander bit rides this same entry: the peer reads it as a global Any(c => c.IsHighlander) over
        // knownList. never add activate/count/callCount/param alongside it - that reroutes the whole entry into
        // SkillConditionCheckList, out of knownList, and the bit becomes invisible
        if (TryReadHighlanderSpec(body, out var excludeBase) && TryIsHighlanderDeck(s, excludeBase, out var isHl) && isHl)
            entry["highlander"] = 1;
        var reveal = new JsonArray { entry };
        AddMovedCards(s, body, pIdx, reveal);
        AddResolvedCards(s, body, pIdx, reveal);
        body["knownList"] = reveal;
    }

    // A token minted mid-match has no deck slot, so the ledger - which is built from the deck - can never name it, and
    // its index runs past 40. The actor DOES state what it is, in orderList: {"add":{"idx":[41],"card":{"cardId":N}}}.
    // orderList has no readers on the client, so that identity dies on arrival unless the relay moves it somewhere the
    // client does read. Learn it here, before every naming pass, and the rest of the pipeline can name it like any card
    void LearnAddedCards(Session s, JsonObject body)
    {
        if (body["orderList"] is not JsonArray ol) return;
        foreach (var o in ol)
        {
            if (o is not JsonObject oo || oo["add"] is not JsonObject add) continue;
            if ((AsInt(add["isSelf"]) ?? 1) != 1) continue;
            if (AsInt(add["card"]?["cardId"]) is not int cardId || cardId <= 0) continue;
            foreach (var ix in ReadIdxList(add["idx"] ?? add["idxList"]))
            {
                var ledger = LedgerFor(s);
                if (ledger.TryGetValue(ix, out var had) && had == cardId) continue;
                ledger[ix] = cardId;
                Console.WriteLine($"[{s.Id}] learned idx={ix} is {cardId} from its add record");
            }
        }
    }

    // The rule that generalises. naming by DESTINATION was the wrong axis: a fusion consumes its ingredients into
    // FusionIngredient(60), emits no `move` at all, and AddMovedCards cannot see it. Name by RESOLUTION instead - every
    // actor-side index this message makes the peer look up.
    // left unnamed, the peer resolves the index against its own forty placeholders, and any local check that reads a
    // PROPERTY of that card takes the other branch. The fusion of 119241030 wants {tribe=lord} and a placeholder is
    // TribeType.ALL, so IsFusionable is false, BattlePlayerBase.Fusion returns before depositing anything, and every
    // later condition reading fusion_ingrediented_card_list is false on that machine forever after.
    // OperateReceiveChecker has no FUSION case, so the message passes its check either way, which is why this went
    // unnoticed: nothing complained, the two boards just stopped agreeing
    void AddResolvedCards(Session s, JsonObject body, int playIdx, JsonArray known)
    {
        var named = new HashSet<int> { playIdx };
        foreach (var e in known) if (AsInt(e?["idx"]) is int have) named.Add(have);

        var want = new List<int>();
        // isSelf is sender-relative, so 1 is the actor's own card - the half the peer holds as placeholders
        if (body["oppoTargetList"] is JsonArray tl)
            foreach (var t in tl)
                if (t is JsonObject to && (AsInt(to["isSelf"]) ?? 0) == 1 && AsInt(to["targetIdx"]) is int i) want.Add(i);
        if (body["orderList"] is JsonArray ol)
            foreach (var o in ol)
                if (o is JsonObject oo && oo["fusion"] is JsonObject fu && (AsInt(fu["isSelf"]) ?? 1) == 1)
                    want.AddRange(ReadIdxList(fu["ingredients"]));

        foreach (var ix in want)
        {
            if (!named.Add(ix)) continue;
            var id = LedgerFor(s).TryGetValue(ix, out var c) ? c
                   : Board(s) is {} bd && bd.TryCardIdOf(true, ix, out var fromEngine) ? fromEngine : 0;
            if (id == Placeholder) id = 0;
            if (id == 0)
            {
                Console.WriteLine($"[{s.Id}] NOT NAMED idx={ix}, which play {playIdx} makes the peer resolve: "
                                + "it will look the index up against its own placeholders and may take a different branch");
                continue;
            }
            Console.WriteLine($"[{s.Id}] named idx={ix} ({id}) for the peer to resolve against, alongside play {playIdx}");
            known.Add(new JsonObject { ["idx"] = ix, ["cardId"] = id, ["isSelf"] = 0, ["is_open"] = 1 });
        }
    }

    // knownList is a LIST and the peer looks entries up by index, but the relay only ever put the played card in it. A
    // play that also moves ANOTHER card somewhere public - 葬送 entombing a follower chosen from hand is the one that
    // surfaced this - left that card unnamed, so the peer resolved it against its own forty dummies and drew a Goblin.
    // the moves ride orderList, which no client reads, so this is the only channel that can carry the identity
    void AddMovedCards(Session s, JsonObject body, int playIdx, JsonArray known)
    {
        if (body["orderList"] is not JsonArray orders) return;
        var named = new HashSet<int> { playIdx };
        foreach (var o in orders)
        {
            if (o is not JsonObject oo || oo["move"] is not JsonObject mv) continue;
            if ((AsInt(mv["isSelf"]) ?? 1) != 1) continue;
            var to = AsInt(mv["to"]);
            var from = AsInt(mv["from"]);
            if (!PublicDestination(to)) continue;
            // hand -> field is the play itself, already named by the entry above
            if (from == HandState && to == FieldState) continue;
            foreach (var ix in ReadIdxList(mv["idx"] ?? mv["idxList"]))
            {
                if (!named.Add(ix)) continue;
                var id = LedgerFor(s).TryGetValue(ix, out var c) ? c
                       : Board(s) is {} bd && bd.TryCardIdOf(true, ix, out var e) ? e : 0;
                // The filler is what a deck the relay never resolved is padded with, and it is the very card the peer
                // would have shown anyway. stating it would turn "I do not know" into "it is a Goblin", which is the
                // same picture with none of the doubt
                if (id == Placeholder) id = 0;
                if (id == 0)
                {
                    Console.WriteLine($"[{s.Id}] NOT NAMED idx={ix} moving {Zone(from)}->{Zone(to)} alongside play {playIdx}: "
                                    + "neither the ledger nor the engine could name it. the peer will show a placeholder");
                    continue;
                }
                Console.WriteLine($"[{s.Id}] named idx={ix} ({id}) moving {Zone(from)}->{Zone(to)} alongside play {playIdx}");
                known.Add(new JsonObject { ["idx"] = ix, ["cardId"] = id, ["isSelf"] = 0, ["is_open"] = 1 });
            }
        }
    }

    // The receiver re-simulates the actor's action against forty dummies, so its StableRandom cursor lands short of the
    // actor's. spin is how far, and OperateReceive burns exactly that many draws to catch up. it is forward-only and
    // was the original repair channel. The relay has never sent it, so every random effect left the two cursors apart.
    // both numbers only exist once each client has a board of its own
    void InjectSpin(Session s, JsonObject body)
    {
        if (Engine.ShadowBridge.Role < Engine.ShadowBridge.EngineRole.AnswerBlanks) return;
        if (body.ContainsKey("spin")) return;
        if (PairFor(s) is not {} sp || !sp.TrySpin(IsShadowPlayer(s), out var spin) || spin <= 0) return;
        body["spin"] = spin;
        Console.WriteLine($"[{s.Id}] spin={spin} (the receiver is that many draws behind)");
    }

    // The last resort, and the only one that generalises: read the price off the actor's own board rather than
    // reconstructing it from a wire that does not carry it. NetworkSkill_cost_change.IsSend is false for any cost
    // change whose owner card is in hand, so a discount that came from ANOTHER card is invisible to every route above
    // this one. gated with the rest of the engine's opinions
    int? Project(Session s, int idx, int cardId)
    {
        if (Engine.ShadowBridge.Role < Engine.ShadowBridge.EngineRole.AdviseCost) return null;
        if (PairFor(s) is not {} pj || !pj.TryProject(IsShadowPlayer(s), idx, out var e)) return null;
        if (e["cardId"]?.GetValue<int>() != cardId) return null;   // a different card at that index is a board that drifted
        if (e["cost"]?.GetValue<int>() is not int cost || cost < 0) return null;
        // The engine's own fold can only be trusted down to zero and up to the printed price. anything outside that is
        // the board disagreeing with the master, not a discount
        if (!_cardCosts.TryGetValue(cardId, out var cc) || cost > cc.BaseCost) return null;
        Console.WriteLine($"[{s.Id}] cost idx={idx} card={cardId}: projected {cost} off the engine's board");
        return cost;
    }

    // The actor asks a condition question on the wire and never answers it. The peer's CheckCondition discards its own
    // reading and returns the (absent) injected value, so the skill silently does not fire - 169 receive-gated skills
    // are in that state. The engine plays the same match, so it answers what the acting engine asked itself.
    // runs before the peer send and the shadow ingests after, so the engine still holds the pre-play board its
    // played-card exclusion is written against
    void InjectConditionAnswers(Session s, string uri, JsonObject body)
    {
        if (Engine.ShadowBridge.Role < Engine.ShadowBridge.EngineRole.AnswerBlanks) return;
        if (uri != "PlayActions" || body["orderList"] is not JsonArray ol) return;

        var specs = new JsonArray();
        foreach (var el in ol)
            if (el is JsonObject eo && eo["skillConditionCheck"] is JsonObject sc) specs.Add(sc.DeepClone());
        if (specs.Count == 0) return;

        var pIdx = AsInt(body["playIdx"]) ?? -1;
        if (Board(s) is not {} bd2 || !bd2.TryConditionAnswers(true, pIdx, specs, out var answers)) return;

        var list = body["knownList"] as JsonArray;
        if (list is null) { list = new JsonArray(); body["knownList"] = list; }
        // each answer is its OWN entry: folded into the card-state entry, the activate/count key would reroute that
        // entry out of knownCardList and the cardId/cost it carries would be lost
        foreach (var a in answers) list.Add(a!.DeepClone());

        // The flag and the condition channel are mutually exclusive per card index: once any condition entry exists
        // for an index, the peer sends every condition skill on that card down the injected-answer branch and never
        // reaches the highlander flag. The engine answers check_highlander itself, so drop the now-dead flag
        var answered = answers.Select(a => AsInt(a?["idx"])).Where(i => i is not null).ToHashSet();
        foreach (var e in list)
            if (e is JsonObject eo2 && eo2.ContainsKey("highlander") && answered.Contains(AsInt(eo2["idx"])))
                eo2.Remove("highlander");

        Console.WriteLine($"[{s.Id}] engine answered {answers.Count} of {specs.Count} skill conditions");
    }

    // The actor sends the check spec but never its result - only a server-injected knownList carries it, so without
    // this every highlander skill silently fails to activate on the opponent. recognize on `type` alone: check_highlander
    // never ships a `condition`, and the compare stays client-side (it negates itself via num == flag2), so the relay
    // reports ground truth and must not invert. False = decline, which leaves today's (correct-for-non-highlander) default
    bool TryReadHighlanderSpec(JsonObject body, out int excludeBase)
    {
        excludeBase = -1;
        if (body["orderList"] is not JsonArray ol) return false;
        foreach (var el in ol)
        {
            if (el is not JsonObject eo || eo["skillConditionCheck"] is not JsonObject sc) continue;
            if (AsStr(sc["type"]) != "check_highlander") continue;
            if (sc["target"] is not JsonArray tg || tg.FirstOrDefault() is not JsonObject t0) return false;
            // an excludeTribe rides `tribe`, not excludeList. ignoring it would check the wrong deck subset, which is a
            // wrong answer rather than a conservative one
            if (t0.ContainsKey("tribe")) { Console.WriteLine("highlander: excludeTribe spec, declined"); return false; }
            // the key is omitted entirely when the check isn't zone-scoped, and Deck is the only zone _deck models
            if (AsInt(t0["state"]) is not 0) return false;
            foreach (var ix in ReadIdxList(t0["excludeList"])) { excludeBase = ix; break; }
            return true;
        }
        return false;
    }

    // highlander = every deck card distinct by base_card_id. an alt-art copy and a normal copy share one base, so
    // comparing raw cardIds would call that deck highlander and fire a skill that must not. anything unresolvable
    // aborts: an unknown card can't be proven distinct, and a wrong bit is worse than the silent default
    bool TryIsHighlanderDeck(Session s, int excludeBase, out bool result)
    {
        result = false;
        if (_baseCardIds.Count == 0) return false;
        if (!_deck.TryGetValue(s.Id, out var deck) || deck.Count == 0) return false;
        var ledger = LedgerFor(s);
        var bases = new List<int>();
        foreach (var dc in deck)
        {
            if (!ledger.TryGetValue(dc.Index, out var cid) || cid == 0) return false;
            if (!_baseCardIds.TryGetValue(cid, out var b)) return false;
            if (b == excludeBase) continue;
            bases.Add(b);
        }
        result = bases.Count == bases.Distinct().Count();
        return true;
    }

    enum CostOp { Add, Set, HalfUp, HalfDown }
    readonly record struct CostMod(CostOp Op, int Val);

    Dictionary<int, List<CostMod>> ModsFor(Session s) => _costMods.TryGetValue(s.Id, out var m) ? m : _costMods[s.Id] = new();
    Dictionary<int, int> ChargesFor(Session s) => _charges.TryGetValue(s.Id, out var m) ? m : _charges[s.Id] = new();
    List<CostMod> ModList(Session s, int ix) => ModsFor(s).TryGetValue(ix, out var l) ? l : ModsFor(s)[ix] = new();

    // deltas ride as an opcode char plus a value, e.g. "a3" / "s2" / "d2"
    static (char Op, int Val)? ParseDelta(string? v) => v is { Length: > 1 } && int.TryParse(v[1..], out var n) ? (v[0], n) : null;

    // The peer drops orderList wholesale, so every cost change the actor ALREADY RESOLVED (alter.cost carries a concrete
    // delta - the actor evaluates the formula itself) and every spellboost delta die on the floor. mirror both per idx so
    // InjectKnownCard can state the price the real server used to state
    // going blind is permanent for the match and silently drops every later price to the peer, so say why the first time
    void GoCostBlind(Session s, string why)
    {
        if (_costBlind.Add(s.Id)) Console.WriteLine($"[{s.Id}] cost: blind for the rest of the match ({why})");
    }

    void UpdateCostState(Session s, JsonObject body)
    {
        if (body["orderList"] is not JsonArray ol) return;
        var peer = _sessions.Peer(s);
        foreach (var el in ol)
        {
            if (el is not JsonObject eo || eo["alter"] is not JsonObject e) continue;
            var cd = ParseDelta(AsStr(e["cost"]));
            var bd = ParseDelta(AsStr(e["spellboost"]));
            if (cd is null && bd is null) continue;
            var owner = (AsInt(e["isSelf"]) ?? 1) == 0 ? peer : s;
            if (owner is null) continue;
            // A group idx ("gN") is a filter spec the real server evaluated against the board. with no board a missed
            // modifier would turn a later emitted cost into a WRONG absolute pin - worse than saying nothing - so stop
            // pricing this player for the rest of the match
            if (AsStr(e["idx"]) is not null) { GoCostBlind(owner, $"group idx {AsStr(e["idx"])}"); continue; }
            foreach (var ix in ReadIdxList(e["idx"]))
            {
                if (bd is { } b)
                {
                    // "a"+addCount, or "s"+target for diff_charge which raises to N and skips when already there
                    var cur = ChargesFor(owner).GetValueOrDefault(ix);
                    var add = b.Op == 's' ? Math.Max(0, b.Val - cur) : b.Val;
                    if (add <= 0) continue;
                    ChargesFor(owner)[ix] = cur + add;
                    if (!LedgerFor(owner).TryGetValue(ix, out var bcid) || !_cardCosts.TryGetValue(bcid, out var bcc))
                    { GoCostBlind(owner, $"boosted idx {ix} has no known card"); continue; }
                    if (bcc.SpellboostStep > 0) ModList(owner, ix).Add(new CostMod(CostOp.Add, -add * bcc.SpellboostStep));
                }
                if (cd is { } c)
                {
                    var op = c.Op switch
                    {
                        'a' => CostOp.Add, 's' => CostOp.Set, 'd' => CostOp.HalfUp, 'D' => CostOp.HalfDown,
                        _ => (CostOp?)null,
                    };
                    if (op is null) { GoCostBlind(owner, $"idx {ix} carries a cost opcode I do not know"); continue; }
                    var mod = new CostMod(op.Value, c.Val);
                    if (AsStr(e["type"]) == "del") ModList(owner, ix).Remove(mod);
                    else
                    {
                        // CostSetModifier clears everything non-resident ahead of it, and residency never reaches the
                        // wire, so a replayed set is treated as clearing
                        if (op == CostOp.Set) ModList(owner, ix).Clear();
                        ModList(owner, ix).Add(mod);
                    }
                }
            }
        }
    }

    // asked for every play the relay cannot price itself, since silence bills the peer the printed cost. an answer that
    // matches the base costs nothing, so there is no reason to hold back
    bool TryEngineCostAdvice(Session s, int idx, int cardId, int? relayCost, out int cost)
    {
        cost = 0;
        if (relayCost is not null) return false;
        if (Engine.ShadowBridge.Role < Engine.ShadowBridge.EngineRole.AdviseCost) return false;
        if (Board(s) is not {} bd3 || !bd3.TryCostOf(true, idx, out var live)) return false;
        // a discount can only take a cost down, so a value outside that window is shadow drift and not a price
        if (!_cardCosts.TryGetValue(cardId, out var cc) || live < 0 || live > cc.BaseCost) return false;
        if (live != cc.BaseCost)
            Console.WriteLine($"[{s.Id}] cost idx={idx} card={cardId}: engine says {live}, base {cc.BaseCost}"
                            + $"{(_costBlind.Contains(s.Id) ? " (relay blind)" : "")}");
        cost = live;
        return true;
    }

    // mirror BattleCardBase.Cost: fold the non-half modifiers, then the halves, then clamp at 0. False = say nothing,
    // which leaves the peer at the base cost (today's behaviour) rather than pinning a value nothing can prove
    bool TryFinalCost(Session s, int idx, int cardId, out int cost)
    {
        cost = 0;
        if (_cardCosts.Count == 0 || _costBlind.Contains(s.Id)) return false;
        if (!_cardCosts.TryGetValue(cardId, out var cc)) return false;
        // A card whose discount rule I could not read: decline rather than emit an unboosted pin. step 0 does NOT
        // belong here - a charge lands on every card in the target set, discount or not, so treating "charged" as
        // "unpriceable" discarded real modifiers and the peer then billed base cost and refused the play at PPover
        if (ChargesFor(s).GetValueOrDefault(idx) > 0 && cc.SpellboostStep == CardCostMap.UnreadableStep) return false;
        var mods = ModsFor(s).GetValueOrDefault(idx);
        if (mods is null || mods.Count == 0) return false;  // nothing touched it, so the base cost is already right
        var n = cc.BaseCost;
        foreach (var m in mods) { if (m.Op == CostOp.Add) n += m.Val; else if (m.Op == CostOp.Set) n = m.Val; }
        foreach (var m in mods)
        {
            if (m.Op == CostOp.HalfUp) n = (int)Math.Ceiling(n / 2f);
            else if (m.Op == CostOp.HalfDown) n = (int)Math.Floor(n / 2f);
        }
        cost = Math.Max(0, n);
        return true;
    }

    // idx -> cardId for a session, seeded from its shuffled deck (idx 1..40). For a token-free match this equals the old
    // deck[idx-1] lookup so the simple path is unchanged
    Dictionary<int, int> LedgerFor(Session s)
    {
        if (_ledger.TryGetValue(s.Id, out var m)) return m;
        m = new Dictionary<int, int>();
        var deck = ShuffledDeckFor(s);
        for (int i = 0; i < deck.Length; i++) m[i + 1] = deck[i];
        _ledger[s.Id] = m;
        return m;
    }

    // fold a relayed message's card reveals into the ledger so a later play at those indices resolves. each entry's isSelf
    // says whose card it is: 1 = the sender's own, 0 = the peer's (token idx space is per-player). Call it AFTER the knownList
    // for this message is injected, since the played card's identity is as-of prior messages
    void UpdateLedger(Session s, JsonObject body)
    {
        var peer = _sessions.Peer(s);
        Dictionary<int, int>? Target(JsonObject e) =>
            (AsInt(e["isSelf"]) ?? 1) == 0 ? (peer is null ? null : LedgerFor(peer)) : LedgerFor(s);
        if (body["orderList"] is JsonArray ol)
            foreach (var el in ol)
                if (el is JsonObject eo)
                    foreach (var (k, v) in eo)
                        if (v is JsonObject e && Target(e) is { } t)
                        {
                            if (k == "add") ApplyAdd(t, e["idx"], e["card"] as JsonObject);
                            else if (k == "metamorphose" && AsInt((e["after"] as JsonObject)?["cardId"]) is int after)
                                foreach (var ix in ReadIdxList(e["idx"])) t[ix] = after;
                        }
        if (body["uList"] is JsonArray ul)
            foreach (var uo in ul)
                if (uo is JsonObject u && AsInt(u["cardId"]) is int cid && cid != 0 && Target(u) is { } t)
                    foreach (var ix in ReadIdxList(u["idxList"] ?? u["idx"])) t[ix] = cid;
    }

    // Add entry card sub-dict: plain {cardId} sets it directly. Copy {baseIdx} inherits the source card's id (array order
    // guarantees the base add landed first); choice {candidates} has no id yet and resolves later via a metamorphose
    static void ApplyAdd(Dictionary<int, int> t, JsonNode? idx, JsonObject? card)
    {
        if (card is null) return;
        if (AsInt(card["cardId"]) is int cid)
            foreach (var ix in ReadIdxList(idx)) t[ix] = cid;
        else if (AsInt(card["baseIdx"]) is int baseIdx && t.TryGetValue(baseIdx, out var baseCid))
            foreach (var ix in ReadIdxList(idx)) t[ix] = baseCid;
    }

    // idx rides the wire as a List<int>, but tolerate a bare int
    static IEnumerable<int> ReadIdxList(JsonNode? node)
    {
        if (node is JsonArray arr)
            foreach (var e in arr) { if (AsInt(e) is int i) yield return i; }
        else if (AsInt(node) is int one) yield return one;
    }

    static int? AsInt(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    static string? AsStr(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    readonly Dictionary<string, int> _extraTurns = new();

    void NoteExtraTurn(Session s, JsonObject body)
    {
        if (body["orderList"] is not JsonArray ol) return;
        foreach (var el in ol)
        {
            if (el is not JsonObject eo || eo["exTurn"] is not JsonObject ex) continue;
            if (AsInt(ex["count"]) is not int n || n <= 0) continue;
            // isSelf is actor-relative, so 1 means the player who cast it takes the turn
            var owner = (AsInt(ex["isSelf"]) ?? 1) == 1 ? s : _sessions.Peer(s);
            if (owner is null) continue;
            _extraTurns[owner.Id] = _extraTurns.GetValueOrDefault(owner.Id) + n;
            Console.WriteLine($"[{owner.Id}] extra turn granted (x{n})");
        }
    }

    // normally the sender, since a client only emits Judge when it is not its turn. an extra turn inverts that: the
    // caster ends its turn like any other, so the OPPONENT emits the Judge and the turn has to go back to the caster.
    // echoing to the sender instead leaves the caster with a greyed-out end-turn button. Null = a duplicate to drop
    Session? TurnGoesTo(Session s)
    {
        var peer = _sessions.Peer(s);
        if (peer is not null && _extraTurns.GetValueOrDefault(peer.Id) is var owed && owed > 0)
        {
            _extraTurns[peer.Id] = owed - 1;
            Console.WriteLine($"[{peer.Id}] extra turn taken ({owed - 1} left)");
            return peer;
        }
        if (!_turnOwner.TryGetValue(s.BattleId, out var owner) || owner != s) return s;
        // the client emits Judge for both the peer's TurnEnd and its TurnEndFinal, so the second one is a duplicate
        var mine = _extraTurns.GetValueOrDefault(s.Id);
        if (mine <= 0) return null;
        _extraTurns[s.Id] = mine - 1;
        Console.WriteLine($"[{s.Id}] extra turn taken ({mine - 1} left)");
        return s;
    }

    async Task HandleJudge(Session s, JsonNode? payload)
    {
        if (TurnGoesTo(s) is not { } taker)
        {
            Console.WriteLine($"[{s.Id}] Judge ignored (already turn owner)");
            return;
        }
        Console.WriteLine($"[{s.Id}] turn handoff -> {taker.Id} (battle {s.BattleId})");
        _turnOwner[s.BattleId] = taker;
        var body = new JsonObject { ["turnState"] = 0 };
        if (payload is JsonObject po)
            foreach (var (k, v) in po)
                if (k is not ("uri" or "pubSeq" or "cat" or "try" or "viewerId" or "uuid" or "bid" or "playSeq" or "turnState"))
                    body[k] = v?.DeepClone();
        await taker.SendMsg("Judge", body);
    }

    object OpponentEnterPayload(Session visitor) => new
    {
        resultCode = 1,
        userName = "player_" + visitor.ViewerId,
        emblemId = 0L,
        degreeId = 0,
        countryCode = "JP",
        rank = 1,
        maxRank = 1,
        // client parses guild flags with ConvertValue.ToBool -> bool.Parse, which only accepts "True"/"False"
        isGuildMember = false,
        isGuildJoined = false,
        oppoId = VidOf(visitor),
        isOfficial = 0,
        isFriend = 0,
        // player.WinCount defaults to -1 (NO_GET_WIN). The visitor's EnterRoomServer doesn't reset it, so send battleNum
        // to make the switch call ReceiveWinCount and flip both slots to 0
        battleNum = 1,
        ownerWin = 0,
        guestWin = 0,
    };

    // decide the finish from the reporter's JUDGE_RESULT_STATUS. only codes that are a mutually-derivable end finalize,
    // disconnect-checks (302) and unilateral timeout/offline victory claims (300/301/400/...) return null so a transient
    // relay stall can't end a live game (a real server cross-checks those, an engine-less relay can't).
    // RESULT_CODE is SELF-relative: RetireWin means "you win, they retired". (JudgeResultReceive passes a
    // BATTLE_RESULT_TYPE to SettingResultUI_SpecialResultTypeText, but that only picks the flavour text - it is not
    // the recipient's outcome, so the codes are NOT inverted.)
    readonly HashSet<string> _reported = new();

    async Task FinalizeFromReport(Session s, int log)
    {
        var (rep, pr) = DecideResult(log);
        if (rep != 0) { await Finalize(s, rep, pr); return; }

        // The client only sends this once it has decided the battle is over, and repeats it until a BattleFinish
        // answers. dropping an unrecognised code leaves both clients doing that forever, which is worse than being
        // wrong about who won, so once BOTH have reported, answer from the life the relay tracked
        _reported.Add(s.Id);
        var peer = _sessions.Peer(s);
        Console.WriteLine($"[{s.Id}] JudgeResult log={log} is not a result the relay maps"
                        + $"{(peer is not null && _reported.Contains(peer.Id) ? ", both sides have reported" : "")}");
        if (peer is null || !_reported.Contains(peer.Id) || _finished.GetValueOrDefault(s.BattleId)) return;

        var (mine, theirs) = (VitalsFor(s).Life, VitalsFor(peer).Life);
        Console.WriteLine($"[{s.Id}] deciding it from life {mine} vs {theirs}");
        var owner = _turnOwner.GetValueOrDefault(s.BattleId);
        if (mine != theirs) await Finalize(s, mine < theirs ? 102 : 101, mine < theirs ? 101 : 102);
        else await Finalize(s, owner == s ? 102 : 101, owner == s ? 101 : 102);
    }

    // (reporterCode, peerCode); (0,0) means "not an adjudicable end here, do nothing". Reporter of a neutral life/deckout
    // is the loser (the non-active defender reports while the winner waits on its own BattleFinish)
    static (int reporter, int peer) DecideResult(int log) => log switch
    {
        100 or 110 => (102, 101),  // natural life/deckout: reporter loses. Life family renders the same as Deckout
        800 => (105, 106),         // ReceiveRetire: reporter (the winner) saw the opponent retire
        900 or 901 => (1, 1),      // data-inconsistency / invalid: both no-contest
        _ => (0, 0),
    };

    async Task Finalize(Session reporter, int reporterCode, int peerCode)
    {
        var peer = _sessions.Peer(reporter);
        bool already;
        int? resend = null;
        lock (_finishLock)
        {
            already = _finished.GetValueOrDefault(reporter.BattleId);
            if (already)
            {
                if (_finishCode.TryGetValue(reporter.Id, out var c)) resend = c;
            }
            else
            {
                _finished[reporter.BattleId] = true;
                _finishCode[reporter.Id] = reporterCode;
                if (peer is not null) _finishCode[peer.Id] = peerCode;
            }
        }
        if (already)
        {
            // already decided (both sides may report, or a retry arrives): just re-answer this client's own code
            if (resend is int c2) await reporter.SendMsg("BattleFinish", new { result = c2 });
            return;
        }
        // a socket that closed first makes SendMsg throw, and the engine has to be released either way: a match left open
        // answers the NEXT battle's questions off this one's board
        try
        {
            await reporter.SendMsg("BattleFinish", new { result = reporterCode });
            if (peer is not null) await peer.SendMsg("BattleFinish", new { result = peerCode });
            await RecordWin(reporter, reporterCode);
        }
        finally { EndShadow(reporter, reporterCode); }
    }

    // The relay authors a BattleFinish only from a client JudgeResult/Retire/close, and a natural lethal produces none:
    // the winner emits only TurnEndFinal then blocks in the disconnect-wait dialog, and the loser's JudgeResult(100)
    // rides a TurnEndFinal->Judge->echo->JudgeOperation handshake the passthrough relay never completes, so nothing
    // decides. author it from the leader life TrackVitals already follows, off the killing action. gated strictly on
    // Life<=0 so a transient disconnect (302, live board) still can't end the game.
    // this makes TrackVitals load-bearing (it was best-effort before) - the value to suspect if a game ever ends wrong
    async Task MaybeFinalizeNaturalFinish(Session s)
    {
        if (_finished.GetValueOrDefault(s.BattleId)) return;
        var peer = _sessions.Peer(s);
        if (peer is null) return;
        bool sDead = VitalsFor(s).Life <= 0;
        bool pDead = VitalsFor(peer).Life <= 0;
        if (!sDead && !pDead) return;
        // RESULT_CODE is self-relative: the dead side receives LifeLose(102) and displays "lose", the survivor LifeWin(101)
        if (sDead && !pDead) HoldVerdict(s, 102, 101);       // s's leader died -> s loses
        else if (pDead && !sDead) HoldVerdict(s, 101, 102);  // peer's leader died -> s wins
        else
        {
            // both leaders dead in one action: the active player (turn owner) loses, matching the engine's
            // JudgeCurrentFinishStatus IsSelfTurn tie-break
            var owner = _turnOwner.GetValueOrDefault(s.BattleId);
            HoldVerdict(s, owner == s ? 102 : 101, owner == s ? 101 : 102);
        }
        await Task.CompletedTask;
    }

    readonly Dictionary<string, (Session Reporter, int ReporterCode, int PeerCode)> _pendingFinish = new();
    static readonly TimeSpan LethalGrace = TimeSpan.FromSeconds(6);

    // The killing blow still has to play out. sending BattleFinish from the same handler that relayed it cuts straight
    // to the result screen, so the loser never sees the attack that killed them (the winner's TurnEndFinal does not
    // even reach the relay until after that). The timer is there so a winner that never sends one cannot leave the
    // match undecided
    void HoldVerdict(Session s, int reporterCode, int peerCode)
    {
        if (_pendingFinish.ContainsKey(s.BattleId)) return;
        _pendingFinish[s.BattleId] = (s, reporterCode, peerCode);
        Console.WriteLine($"[{s.Id}] lethal seen, holding the result until the action closes");
        var battleId = s.BattleId;
        _ = Task.Run(async () =>
        {
            await Task.Delay(LethalGrace);
            await FlushVerdict(battleId, "grace elapsed");
        });
    }

    async Task FlushVerdict(string battleId, string why)
    {
        (Session Reporter, int ReporterCode, int PeerCode) v;
        lock (_finishLock)
        {
            if (!_pendingFinish.TryGetValue(battleId, out v)) return;
            _pendingFinish.Remove(battleId);
        }
        Console.WriteLine($"[{v.Reporter.Id}] result decided ({why})");
        try { await Finalize(v.Reporter, v.ReporterCode, v.PeerCode); }
        catch (Exception e) { Console.WriteLine($"[{v.Reporter.Id}] finalize failed: {e.Message}"); }
    }

    // The comparison the shadow exists for: relay result (hand-written) vs engine result (read off the board). codes
    // are self-relative, so compare against whichever session the engine held as its BattlePlayer
    void EndShadow(Session reporter, int reporterCode)
    {
        int relayCode = IsShadowPlayer(reporter)
            ? reporterCode
            : _finishCode.GetValueOrDefault(ShadowPlayerId(reporter) ?? "", 0);
        var pair = PairFor(reporter);
        _shadowOf.TryRemove(reporter.BattleId, out _);
        pair?.End((name, engineCode, state) => Console.WriteLine(
            engineCode == relayCode
                ? $"[{name}] agrees, result {engineCode} | {state}"
                : $"[{name}] DISAGREES, relay {relayCode} vs engine {engineCode} | {state}"));
        // back to the pool warm, so the next battle in this room does not pay an engine boot
        Engine.ShadowBridge.Release(reporter.BattleId);
    }

    // RESULT_CODE names the RECIPIENT's own outcome: odd = *Win (they won), even = *Lose (they lost). settled by the
    // retire report - the retiree is handed 105 and its opponent sees the losing screen, so the code describes the
    // holder, not the other side. easy to invert by mistake. 1/2 are no-contest and must not move the score
    public static bool? WonBy(int code) => code switch
    {
        101 or 103 or 105 or 107 or 201 or 203 or 205 or 207 => true,
        102 or 104 or 106 or 108 or 202 or 204 or 206 or 208 => false,
        _ => null,
    };

    // nothing counts a win unless the relay does: the client just displays the ownerWin/guestWin it is handed. push the
    // new score to both sides, since ParseWinCount picks it up off any received room message
    async Task RecordWin(Session reporter, int reporterCode)
    {
        if (WonBy(reporterCode) is not bool reporterWon) return;
        var peer = _sessions.Peer(reporter);
        var winner = reporterWon ? reporter : peer;
        if (winner is null) return;
        int owner, guest;
        lock (_finishLock)
        {
            var (o, g) = _roomWins.GetValueOrDefault(reporter.BattleId);
            // the same seat DeckFor reads, so the scoreboard cannot credit the other player's slot
            if (IsOwner(winner)) o++; else g++;
            _roomWins[reporter.BattleId] = (o, g);
            (owner, guest) = (o, g);
        }
        Console.WriteLine($"[{reporter.BattleId}] score now owner={owner} guest={guest}");
        var score = new { resultCode = 1, battleNum = owner + guest + 1, ownerWin = owner, guestWin = guest };
        await reporter.SendMsg("RoomNotify", score, withPlaySeq: false);
        if (peer is not null) await peer.SendMsg("RoomNotify", score, withPlaySeq: false);
    }

    // an observed socket close is the only disconnect signal the relay can trust: a client's own timeout claim
    // (log=300 etc.) is left un-adjudicated in DecideResult so a transient stall can't end a live game, but a dead
    // transport is ground truth. only the survivor is written. The closing session's writer is already completed
    public async Task PeerClosed(Session closed)
    {
        Session? survivor;
        lock (_finishLock)
        {
            // no key = never reached Matched (lobby close); already true = finished normally
            if (!_finished.TryGetValue(closed.BattleId, out var fin) || fin) return;
            survivor = _sessions.Peer(closed);
            if (survivor is null) return;
            _finished[closed.BattleId] = true;
            _finishCode[survivor.Id] = 201;
        }
        Console.WriteLine($"[{closed.Id}] peer closed -> {survivor.Id} wins by disconnect");
        try
        {
            // self-relative: DisconnectWin is "you win, they dropped"
            await survivor.SendAlive(peerOnline: false, peerGone: true);
            await survivor.SendMsg("BattleFinish", new { result = 201 });
            // 202 = "the other side lost", i.e. the survivor won
            await RecordWin(survivor, 202);
        }
        finally { EndShadow(survivor, 201); }
    }

    object MatchedPayload(Session self, Session oppo, string bid, int turnState) => new
    {
        // Put selfDeck near the top so it's dispatched right after "uri" (which nulls _selfDeck via InitializeSelfInfo).
        // if Mono's Dictionary iteration in the client had drifted from insertion order, selfDeck showing up late would let uri's null win
        selfDeck = DeckCards(self),
        selfInfo = UserInfo(self, oppo),
        oppoInfo = UserInfo(oppo, self),
        bid,
        turnState,
    };

    // real 40-card deck the API resolved from the player's set_deck, or the starter shape if none was set.
    // idx is the 1-based deck slot (the battle deck is built with card Index = slot+1)
    object[] DeckCards(Session s) =>
        ShuffledDeckFor(s).Select((c, i) => (object)new { idx = i + 1, cardId = c }).ToArray();

    // The client never shuffles, it just draws deck slots in order, so the server owns draw order. shuffle once and cache
    // so Matched, Deal and Swap all agree on the idx->card mapping for the match
    int[] ShuffledDeckFor(Session s)
    {
        if (_shuffled.TryGetValue(s.Id, out var cached)) return cached;
        var ids = (DeckFor(s)?.CardIds is { Length: > 0 } a ? a : Enumerable.Repeat(Placeholder, 40).ToArray()).ToArray();
        var rng = new Random(DeckSeed(s));
        for (int i = ids.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        _shuffled[s.Id] = ids;
        return ids;
    }

    // stable per (room, viewer) so a re-call inside the match reshuffles identically. String.GetHashCode is randomized
    // per run, so fold the chars by hand for a deterministic seed
    readonly Dictionary<string, int> _deckSeed = new();
    readonly Dictionary<string, int> _stableSeed = new(); // room -> the StableRandom stream both players share

    // The seat, not the header viewer_id. it has to be stable across a mid-battle reconnect so the player keeps the
    // deck it was dealt, and distinct between the two players - and viewer_id is not distinct: a copied install sends
    // one cached value for both, so both keys collided, the second roll overwrote the first and the two players were
    // dealt from the SAME shuffle
    string DeckKey(Session s) => s.BattleId + (IsOwner(s) ? " owner" : " guest");

    // Matched rolls this. The fallback only covers a deck asked for before Matched, and folds the chars by hand because
    // string.GetHashCode is randomized per run and the two players must not disagree about a deck
    int DeckSeed(Session s)
    {
        if (_deckSeed.TryGetValue(DeckKey(s), out var rolled)) return rolled;
        int h = 17;
        foreach (var ch in DeckKey(s)) h = h * 31 + ch;
        return h;
    }

    // BattleStart only feeds SetParameter (rank/classId/charaId + optionals); display fields already came in Matched
    object BattleStartPayload(Session self, Session oppo, long date, int turnState) => new
    {
        battleStartDate = date,
        turnState,
        selfInfo = BattleStartInfo(self),
        oppoInfo = BattleStartInfo(oppo),
    };

    object BattleStartInfo(Session s)
    {
        var d = DeckFor(s);
        return new
        {
            rank = 1,
            classId = d?.ClassId ?? 1,
            charaId = d?.CharaId ?? 1,
            isMasterRank = 0,
            battlePoint = 0,
            masterPoint = 0,
            subclassId = d?.SubClassId ?? 10,
            rotationId = "0",
        };
    }

    // Keys the client reads unguarded off _selfInfo/_oppoInfo. LoadOpponentAssets (gates BattleStartControl.IsReady) reads
    // country_code, degreeId, emblemId — a missing key throws KeyNotFoundException and IsReady never latches, which strands
    // the client in LoadingPhase forever (and past Prepared it no longer times out). Values mirror the AI practice battle
    // (AIBattleStartTask), the one flow proven to clear LoadOpponentAssets: country_code "NONE", emblem/degree -1
    // (emblem 0 / degree 0 have no asset and DegreeMgr.Get(0) NREs). classId/charaId/sleeve come from the resolved deck
    object UserInfo(Session s, Session peer)
    {
        var d = DeckFor(s);
        return new
        {
            rank = 1,
            classId = d?.ClassId ?? 1,
            charaId = d?.CharaId ?? 1,
            viewerId = VidOf(s),
            // The API resolved this from name.txt / Steam and wrote it alongside the deck. only fall back if it didn't
            userName = string.IsNullOrEmpty(d?.UserName) ? "player_" + s.ViewerId : d.UserName,
            // the client reads its own selfInfo.fieldId as the stage (NetworkBattleManagerBase.CreateBackgroundId), so both
            // players carry the same per-battle roll to land on the same map
            fieldId = _fieldId.TryGetValue(s.BattleId, out var fid) ? fid : 1,
            // both players seed one System.Random from this and every random effect draws from it in step, so it has to
            // be the same number for the pair and a different one each battle. System.Random takes Math.Abs of it on the
            // client's runtime, so keep it non-negative
            seed = _stableSeed.TryGetValue(s.BattleId, out var rs) ? rs : 12345,
            deckCount = d?.CardIds.Length ?? 40,
            oppoDeckCount = DeckFor(peer)?.CardIds.Length ?? 40,
            // GetOpponentSleeveId does Convert.ToInt64(_oppoInfo["sleeveId"]) so a missing field kills the battle-load coroutine
            sleeveId = d?.SleeveId ?? 3000011L,
            emblemId = -1L,
            degreeId = -1,
            country_code = "NONE",
            isOfficial = 0,
            oppoId = VidOf(peer),
        };
    }

    // A deck card the reshuffle moves. only Index is tracked (cardId is read fresh from _ledger at swap time so a card
    // transformed while in the deck stays correct). Index is mutable and followed by reference, since an earlier swap in
    // the same flush can move a still-queued card
    sealed class DeckCard { public int Index; }

    // ported verbatim from BattleManagerBase.XorShift so the server produces bit-identical values (same .NET runtime).
    // only w is seeded. X/y/z keep their constants. do NOT change the arithmetic or GetChangeInt would drift
    sealed class XorShift
    {
        int w, x = 123456789, y = 987654321, z = 555555555;
        public bool IsActive { get; }
        public XorShift(int seed) { IsActive = seed != -1; w = seed; }
        public int GetChangeInt(double val)
        {
            double num = Math.Floor((double)Next() / 2147483647.0 * 10000000000.0) / 10000000000.0;
            return (int)Math.Floor(val * num);
        }
        int Next()
        {
            int n = x ^ (x << 11);
            x = y; y = z; z = w;
            return w = w ^ (w >> 19) ^ (n ^ (n >> 8));
        }
    }
}
