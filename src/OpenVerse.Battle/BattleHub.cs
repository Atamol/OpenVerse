using System.Text.Json.Nodes;
using OpenVerse.Common;

namespace OpenVerse.Battle;

public sealed class BattleHub
{
    readonly SessionManager _sessions;
    readonly BattleDeckStore _decks;
    // session whose turn it currently is, per battle. a client emits Judge only when it is NOT its turn (reacting to the
    // opponent's turn-end), so echoing that Judge back hands it the turn. tracking the owner makes the handoff fire once
    // even though the client sends Judge for both TurnEnd and TurnEndFinal
    readonly Dictionary<string, Session> _turnOwner = new();
    // who goes first (turnState 0), rolled once per battle. Matched and BattleStart must agree on it
    readonly Dictionary<string, Session> _firstPlayer = new();
    // battle field (map), rolled once per battle and mirrored to both players so they share the same stage
    readonly Dictionary<string, int> _fieldId = new();
    // each player's deck shuffled once per match, stable for the whole battle (the idx->card mapping Deal/Swap/draws use)
    readonly Dictionary<string, int[]> _shuffled = new();
    // per-session card ledger: Index -> cardId. seeded from the 40-card shuffle (idx 1..40 -> deck[idx-1]) then grown from
    // add/metamorphose/copy-token/uList reveals so knownList resolves tokens (idx>40) and transforms, not just deck cards.
    // token idx space restarts at 41 per player, so each side keeps its own map. it is the knownList source of truth
    readonly Dictionary<string, Dictionary<int, int>> _ledger = new();
    // return-to-deck reshuffle mirror. the client randomizes a card's deck slot via a XorShift seeded by idxChangeSeed and
    // NEVER sends the new index (OnIndexChange is a local replay recorder), so the server replicates the same lockstep
    // stream to keep _ledger correct after a card goes back to the deck. keyed by Session.Id, per-battle flags by BattleId
    readonly Dictionary<string, XorShift> _rng = new();         // one persistent stream per session, seeded from its own seed
    readonly Dictionary<string, int> _idxSeed = new();          // each session's own reshuffle seed, rolled once
    readonly Dictionary<string, List<DeckCard>> _deck = new();  // each session's DeckCardList mirror, kept sorted by Index
    readonly Dictionary<string, List<DeckCard>> _addQueue = new(); // enrolled DeckCard refs awaiting the next flush
    readonly Dictionary<string, bool> _mulliganEnd = new();     // gates enrollment; flips true after Ready
    readonly Dictionary<string, bool> _turnEndFlushed = new();  // de-dups the turn-end flush across TurnEndActions + TurnEnd
    // the client only leaves the win/lose VFX once it RECEIVES a BattleFinish with a definitive result code, so the relay
    // must author the finish. _finished guards a battle against a double-decide; _finishCode remembers each side's code to
    // re-answer a retry report. both reset per battle at Matched so a rematch in the same room finalizes fresh
    readonly Dictionary<string, bool> _finished = new();        // battle already finalized, by BattleId
    readonly Dictionary<string, int> _finishCode = new();       // Session.Id -> RESULT_CODE to (re)send on a retry
    // the room's running score. the client only ever displays what it is told (ParseWinCount reads ownerWin/guestWin off
    // any received message), so nothing counts a win unless the relay does it. keyed by BattleId = the room, so it
    // survives the battle sockets closing between games
    readonly Dictionary<string, (int Owner, int Guest)> _roomWins = new();
    // a close runs on the dying socket's continuation, concurrently with the survivor's dispatch
    readonly object _finishLock = new();

    // cardId -> base_card_id, for the highlander check. empty (master absent) disables the synthesis rather than
    // falling back to raw cardId equality, which would call an alt-art + normal pair distinct and inject a false 1
    readonly Dictionary<int, int> _baseCardIds;
    // cardId -> base cost + per-boost discount, for pricing a revealed hand card
    readonly Dictionary<int, CardCost> _cardCosts;
    // the peer prices an opponent's hand card at its MASTER BASE cost unless knownList states a `cost`, then charges
    // that against its model of the opponent's Pp. every cost change therefore has to be mirrored here or the peer
    // over-deducts, refuses a later play ("PPover" -> NotPlayCard -> ConductError), and silently drops it
    readonly Dictionary<string, Dictionary<int, List<CostMod>>> _costMods = new();  // idx -> modifier stack, in arrival order
    readonly Dictionary<string, Dictionary<int, int>> _boost = new();               // idx -> SpellChargeCount
    readonly HashSet<string> _costBlind = new();                                    // sessions whose cost state is no longer provable

    public BattleHub(SessionManager sessions, BattleDeckStore decks, Dictionary<int, int>? baseCardIds = null,
        Dictionary<int, CardCost>? cardCosts = null)
    {
        _sessions = sessions;
        _decks = decks;
        _baseCardIds = baseCardIds ?? new Dictionary<int, int>();
        _cardCosts = cardCosts ?? new Dictionary<int, CardCost>();
    }

    // resolved deck the API wrote at do_matching, or null if the client never set one (fall back to the starter shape)
    // the WS "BattleId" header is the room_id (Room.RoomId). owner = first session to join the battle group, matching
    // the API's room.OwnerUdid ordering (owner creates the room and connects first)
    BattleDeck? DeckFor(Session s)
    {
        var isOwner = _sessions.ByBattle(s.BattleId).FirstOrDefault() == s;
        var d = _decks.Get(s.BattleId, isOwner);
        Console.WriteLine($"DeckFor roomId={s.BattleId} isOwner={isOwner} found={d is not null} class={d?.ClassId ?? -1}");
        return d;
    }

    public async Task Dispatch(Session s, string uri, JsonNode? payload, int? ackId)
    {
        var pubSeq = payload?["pubSeq"]?.GetValue<int>() ?? 0;
        Console.WriteLine($"[{s.Id}] recv {uri} (pubSeq={pubSeq})");
        switch (uri)
        {
            case "InitNetwork":
                if (ackId is int i1) await s.SendAck(i1, pubSeq);
                await s.SendMsg("InitNetwork", new { });
                break;
            case "RoomCreate":
                // ReceiveNodeResultCode.Success = 1. Also need a synchronize msg back so PlayerControllerForOpponent.OnReceiveFast
                // sets ConnectRoomResult=SUCCESS and the room UI proceeds — the ACK alone doesn't do it.
                // Room URIs aren't in IsMatchingURI, so we omit playSeq so the msg isn't stocked and reaches PlayReceiveData
                if (ackId is int i2) await s.SendAckResult(i2, pubSeq, new { resultCode = 1 });
                await s.SendMsg("RoomCreate", new { resultCode = 1 }, withPlaySeq: false);
                break;
            case "RoomEntry":
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
                // turn-start / action ack from the client. ack it so its stock queue advances, but never relay it
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
                await RelayBattle(s, uri, payload);
                break;
            case "Judge":
                // turn handoff, not peer traffic: echo it back to the sender so its ControlTurnStartPlayer runs and it
                // takes the turn. relaying to the peer would instead restart the peer's turn
                if (ackId is int iJd) await s.SendAck(iJd, pubSeq);
                await HandleJudge(s, payload);
                break;
            case "JudgeResult":
                // the client reports its outcome here with no ack/pubSeq (payload["log"] = JUDGE_RESULT_STATUS). a real
                // server answers with a BattleFinish carrying a definitive result code, which is the only thing that sets
                // _isJudgeResultReceive and lets the client leave the win/lose VFX. relaying to the peer would bounce forever
                await FinalizeFromReport(s, AsInt(payload?["log"]) ?? 100);
                break;
            case "SetupComplete":
                if (ackId is int iSc) await s.SendAckResult(iSc, pubSeq, new { resultCode = 1 });
                // isSelf=1 tells the sender's UI to mark their own slot ready. peer gets isSelf=0 so their opponent slot flips
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

    public async Task Alive(Session s)
    {
        var peer = _sessions.Peer(s);
        // ocs=WAITING makes the client show "connection unstable" and disable touch (mulligan can't be submitted).
        // in a 1v1 the opponent is always meant to be present, so report ONLINE regardless of a transient peer lookup miss
        Console.WriteLine($"[{s.Id}] alive (battle={s.BattleId}, peer={(peer?.Id ?? "null")}, roster={_sessions.ByBattle(s.BattleId).Count})");
        await s.SendAlive(peerOnline: true);
    }

    // both SetupComplete: kick the client into the RoomReady flow, which triggers DoMatching HTTP then emits InitRoomBattle back to us
    async Task MaybeRoomReady(Session s)
    {
        var peer = _sessions.Peer(s);
        if (peer is null || !peer.Ready) return;
        await s.SendMsg("RoomReady", new { resultCode = 1 }, withPlaySeq: false);
        await peer.SendMsg("RoomReady", new { resultCode = 1 }, withPlaySeq: false);
    }

    // once both clients have emitted InitRoomBattle after DoMatching, we can finally send Matched to trigger StartBattleLoad
    async Task MaybeMatched(Session s)
    {
        var peer = _sessions.Peer(s);
        if (peer is null || !peer.InitBattleSent) return;
        // roll the coin flip and the stage once, here, since Matched is the first place both are needed. turnState flags
        // first/second at Ready (the client reads it to set isEnemyFirstTurn), so it must be 0/1 here (only after Ready
        // does the relay push both to 0). fieldId picks the map and must match on both clients
        var first = Random.Shared.Next(2) == 0 ? s : peer;
        var second = first == s ? peer : s;
        _firstPlayer[s.BattleId] = first;
        _turnOwner[s.BattleId] = first;
        _fieldId[s.BattleId] = Random.Shared.Next(1, 8);
        _finished[s.BattleId] = false;
        // one reshuffle seed per player, kept for the whole battle. each seeds that player's own deck XorShift, handed out
        // mirror-swapped at Deal so a card added to a player's deck reshuffles identically on both clients and here
        int sA = RollSeed(), sB = RollSeed();
        _idxSeed[first.Id] = sA;
        _idxSeed[second.Id] = sB;
        _rng[first.Id] = new XorShift(sA);
        _rng[second.Id] = new XorShift(sB);
        await first.SendMsg("Matched", MatchedPayload(first, second, s.BattleId, turnState: 0));
        await second.SendMsg("Matched", MatchedPayload(second, first, s.BattleId, turnState: 1));
    }

    // XorShift.IsActive requires seed != -1; any other int (incl 0) is a valid seed
    static int RollSeed() { int s; do s = Random.Shared.Next(int.MinValue, int.MaxValue); while (s == -1); return s; }

    const int StarterCard = 100111010;
    const int HandState = 10;  // NetworkCardPlaceState.Hand

    // both Loaded: BattleStart is the only matching-phase msg that flips status to Prepared and stops the 25s loadedTimeout.
    // send it with no playSeq so it plays immediately (IsNoStockData) and the first stocked synchronize (Deal) stays at 3.
    // selfInfo+oppoInfo each need rank/classId/charaId or SetParameter throws and IsReady never latches.
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
        // the seeds ride the Deal reply once, mirror-swapped: the client sets its self stream from idxChangeSeed and its
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

    // client Swap carries idxList = abandoned deck slots. reply with fresh slots at those hand positions, then gate on both.
    async Task HandleSwap(Session s, JsonNode? payload)
    {
        s.Redraws.Clear();
        if (payload?["idxList"] is JsonArray idxList)
            foreach (var n in idxList)
            {
                // idxList holds abandoned deck Indices (1-based). the opening hand dealt deck slot k at hand position k-1,
                // so an abandoned Index maps to hand position Index-1; the replacement comes from a fresh deck slot
                int abandoned = n?.GetValue<int>() ?? 0;
                s.Redraws[abandoned - 1] = s.NextDeckIdx++;
            }
        await s.SendMsg("Swap", new { cards = RedrawCards(s, isSelf: 1) });
        s.MulliganDone = true;
        var peer = _sessions.Peer(s);
        if (peer is null || !peer.MulliganDone) return;
        // reshuffle is eligible only after mulligan (mulligan returns keep their Index, no enroll). seed each deck mirror =
        // all idx minus the 3 kept-hand indices (opening slots 1..3, replaced per redraw). the DeckCard objects here are the
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
        // to the peer. a received TurnStart always runs ControlTurnStartOpponent, so sending one to the first player would
        // make it process its own turn as the opponent's, flip IsSelfTurn off, and stall until the 95s BattleStop
        await s.SendMsg("Ready", new { cards = RedrawCards(peer, isSelf: 0) });
        await peer.SendMsg("Ready", new { cards = RedrawCards(s, isSelf: 0) });
        BeginShadow(s, peer);
    }

    // replacement cards for a session's redraws; isSelf tags whose hand this is from the receiver's view.
    // own redraws show the real (shuffled) card at the fresh deck slot; the opponent's stay hidden as the placeholder
    object[] RedrawCards(Session owner, int isSelf) =>
        owner.Redraws.Select(kv => (object)new { idx = kv.Value, cardId = isSelf == 1 ? ShuffledDeckFor(owner)[kv.Value - 1] : StarterCard, isSelf, pos = kv.Key, to = HandState }).ToArray();

    // opening hand: 3 cards per side, isSelf splits self (1) from opponent (0), pos orders them.
    // the client builds each deck with card Index = slot+1 (SBattleLoad CreateCard passes i+1), so deck cards are 1..40.
    // idx must be that 1-based deck Index or GetBattleCardIdx returns null and DrawCard NREs every frame (Deal never advances)
    object[] DealCards(Session s)
    {
        var deck = ShuffledDeckFor(s);
        var cards = new List<object>();
        for (int isSelf = 1; isSelf >= 0; isSelf--)
            for (int i = 0; i < 3; i++)
                cards.Add(new { idx = i + 1, cardId = isSelf == 1 ? deck[i] : StarterCard, isSelf, pos = i, to = HandState });
        return cards.ToArray();
    }

    // relay a battle action to the peer on its own contiguous playSeq stream. the peer re-simulates the play from playIdx
    // (orderList/battleCode are dropped on receive) and resolves isSelf against the shared index namespace, where isSelf=1
    // already means the emitter = the peer's BattleEnemy. flipping isSelf sends board-interfering effects to the wrong
    // side, and no index needs remapping since both clients share the deck seeds.
    // BUT the payload is NOT pure passthrough: wherever the client's own send format differs from its own receive format,
    // the real server converted it and we have to as well (knownList/keyAction/targetList below). "the peer reads isSelf"
    // only holds for keys whose parse actually reads it - targetList's live parse does not, which is why it is renamed.
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
        InjectKnownCard(s, uri, body);
        InjectConditionAnswers(s, uri, body);
        InjectSummonedCardIds(s, body);
        UpdateLedger(s, body);
        UpdateCostState(s, body);
        TrackVitals(s, body);
        ApplyDeckMoves(s, body);
        MaybeFlush(s, uri);
        await peer.SendMsg(uri, body);
        await MaybeFinalizeNaturalFinish(s);
        RecordShape(uri, body);
        // after the send, never before: the shadow is an observer and the peer should not wait on it
        Engine.ShadowBridge.Observe(uri, body, s.Id == _shadowPlayerId, l => Console.WriteLine($"[{s.Id}] {l}"));
    }

    // the session the shadow holds as its BattlePlayer; everything it is told is relative to this, so pin it once
    string? _shadowPlayerId;

    void BeginShadow(Session a, Session b)
    {
        var first = _firstPlayer.TryGetValue(a.BattleId, out var f) ? f : a;
        var second = first == a ? b : a;
        _shadowPlayerId = first.Id;
        Engine.ShadowBridge.Begin(_idxSeed.GetValueOrDefault(first.Id, 0), true,
            ShuffledDeckFor(first), ShuffledDeckFor(second),
            OpeningHandIdx(first), OpeningHandIdx(second),
            l => Console.WriteLine($"[{first.Id}] {l}"));
    }

    // the post-mulligan opening hand: deck slot pos+1 for each of the three opening positions, or its redraw
    // replacement. same rule HandleSwap uses to seed the deck mirror
    static int[] OpeningHandIdx(Session s)
    {
        var hand = new int[3];
        for (int pos = 0; pos < 3; pos++) hand[pos] = s.Redraws.TryGetValue(pos, out var r) ? r : pos + 1;
        return hand;
    }

    // what the real client puts on the wire, one line per distinct shape. the send/receive mismatches were only ever
    // found from live traffic, so record them rather than guess. printed once per shape
    readonly HashSet<string> _seenShapes = new();

    void RecordShape(string uri, JsonObject body)
    {
        var shape = uri + " {" + string.Join(",", body.Select(kv => kv.Key).OrderBy(k => k)) + "}";
        if (body["keyAction"] is JsonArray { Count: > 0 } ka && ka[0] is JsonObject e0)
            shape += " keyAction[0]{" + string.Join(",", e0.Select(kv => kv.Key).OrderBy(k => k)) + "}";
        if (_seenShapes.Add(shape)) Console.WriteLine($"shape {shape}");
    }

    // track each deck's index set from the move entries and enroll cards that land in the deck, so a later flush reshuffles
    // them. a card enters the deck as a move to=Deck(0) (returns + a token's companion move); leaves as a move from=Deck.
    // the add/uList reveals in UpdateLedger set cardIds only and never enroll, so enrollment has a single channel
    // leader life / PP / cemetery per session. the clients broadcast every change as a playerParam entry, so this is
    // observed rather than simulated - which is why conditions that read these (vengeance, overflow) were never actually
    // out of reach, only unparsed. read-only: nothing here is sent, so a wrong value costs a log line, not a match
    public sealed class Vitals
    {
        public int Life = 20, Pp, MaxPp, Cemetery;
        public override string ToString() => $"life={Life} pp={Pp}/{MaxPp} grave={Cemetery}";
    }

    readonly Dictionary<string, Vitals> _vitals = new();

    public Vitals VitalsFor(Session s) => _vitals.TryGetValue(s.Id, out var v) ? v : _vitals[s.Id] = new Vitals();

    // damage/heal/addPP arrive as magnitudes (the sender takes Math.Abs), so the key carries the sign. set/cemetery/maxPP
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

    // flush the reshuffle queues at exactly the client's 6 completion events: every PlayActions (play/evolve/attack/fusion
    // complete), TurnStart, and once per turn end (TurnEndActions if present, else TurnEnd). the sender owns the trigger
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

    // drain a player's queue: each in-deck card in insertion order draws one Next() from that player's stream and swaps its
    // deck slot with the changeInt-th card, mirroring AddToDeckCardIndexChange. the two queues use independent streams so the
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
            var bm = BoostFor(owner);
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

    // the one place the client's own send and receive formats disagree, so the real server must have converted it: the
    // sender always nests the selection as selectCard:{cardId|cardIdx:[...], open:N}, but the receiver wants selectCard
    // to BE the array and BurialRate's cardIdx hoisted onto the entry. passing the nested form through NREs the peer's
    // ConvertToListInt, which drops the whole play and freezes its receive sequence - every later message strands behind
    // the gap. `open` has no reader anywhere in the client, which is what gives the server-side conversion away
    // the sender only ever emits `targetList`, but the receiver's targetList case hardcodes isWatch:true and that branch
    // reads `vid` - which no client writes - so isSelf keeps its `false` initializer and every sender-owned target
    // resolves against the RECEIVER's own board. the isSelf-reading parse is reached only via `oppoTargetList`, a key
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

    const int FieldState = 20; // NetworkCardPlaceState.Field

    // a uList entry landing on the field names no card: RegisterUnapproved.CardId stays 0 unless isCardId, which only
    // the banish/discard and own-hand-play paths set. the peer models the enemy's hidden zones as placeholders and
    // treats CardId==0 as "stay a placeholder", so the arrival never lands, the slot never leaves the zone it came
    // from, and a later hand play of that idx dies as notHandCard. the client enrolls the real card into
    // DeckSkillCardList (which is what lets a deck skill fire at all) only on from==Deck AND CardId!=0 - a pair no
    // client writer can emit, so that path is one the real server fed.
    // Field and only Field: the destination is what makes a card public, so a reveal into HAND would leak the
    // opponent's draws, and whether a draw is revealed is the skill's call rather than ours. from==Hand is skipped
    // because a play already gets its identity through InjectKnownCard.
    // isSelf must be 1: the peer replaces on BattleEnemy regardless, so an isSelf=0 reveal would stamp the peer's cardId
    // onto the sender's slot at that index
    // Ledger first, engine second. The ledger is a reconstruction and only knows indices it has seen enrolled, so it
    // goes blank on cards the relay never watched arrive; the engine is playing the same match and holds every zone,
    // so it answers those. Strictly additive - it can only fill a blank the ledger already declined, never overrule a
    // resolution that works today - and every fill is logged so the live run shows how far the engine is carrying this.
    void InjectSummonedCardIds(Session s, JsonObject body)
    {
        var ledger = LedgerFor(s);
        var self = s.Id == _shadowPlayerId;
        InjectSummonedCardIds(body, ix =>
        {
            if (ledger.TryGetValue(ix, out var known)) return known;
            if (!Engine.ShadowBridge.TryCardIdOf(self, ix, out var fromEngine)) return 0;
            Console.WriteLine($"[{s.Id}] engine resolved idx={ix} -> {fromEngine} (ledger had nothing)");
            return fromEngine;
        });
    }

    // cardId is one scalar per entry, but the sender merges adjacent records whose fields all match - and its merge gate
    // compares CardId, which is 0 on every one of these, so two DIFFERENT cards collapse into a single entry. un-merging
    // is the exact inverse of that merge: the receiver explodes idxList into one card per index and broadcasts every other
    // key to all of them, so N one-idx entries parse identically to one N-idx entry. randomTargetIdx is the lone
    // positional field (it reads against the idxList loop), so decline rather than slice it - these never carry one.
    // splice in place: the first entry's cardId is latched for accelerate, and the deck-skill dedup indexes this list
    public static void InjectSummonedCardIds(JsonObject body, Dictionary<int, int> ledger)
        => InjectSummonedCardIds(body, ix => ledger.TryGetValue(ix, out var c) ? c : 0);

    /// <param name="resolve">idx -> cardId, or 0 when the card cannot be named (which keeps the safe decline)</param>
    public static void InjectSummonedCardIds(JsonObject body, Func<int, int> resolve)
    {
        if (body["uList"] is not JsonArray ul) return;
        var next = new JsonArray();
        var split = false;
        foreach (var uo in ul)
        {
            var keep = uo?.DeepClone();
            // anything ARRIVING on the field is public once it lands, so the source zone does not matter: deck
            // (direct summon), cemetery (reanimate), banish and reservation all need the same id the actor never
            // writes. two exclusions: a destination other than the field may be private (a reveal into HAND would
            // leak the opponent's draws), and a play out of hand already gets its identity from InjectKnownCard, so
            // leaving that path alone keeps the busiest route untouched
            if (uo is not JsonObject u
                || AsInt(u["cardId"]) is not null            // the client named it; that id wins
                || (AsInt(u["isSelf"]) ?? 1) != 1
                || AsInt(u["to"]) != FieldState || AsInt(u["from"]) is null or HandState)
            { next.Add(keep); continue; }

            var idxs = ReadIdxList(u["idxList"] ?? u["idx"]).ToList();
            // resolve once per index: the resolver may reach the engine, and asking twice for the same slot would
            // double that cost for no gain
            var ids = idxs.Select(resolve).ToList();
            // all-or-nothing: an unresolved index (the -99 shortage sentinel included) keeps the known-safe decline
            if (idxs.Count == 0 || ids.Any(c => c == 0)) { next.Add(keep); continue; }

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

    // the client never emits knownList (the real server injects it): on a hand play the peer resolves the played card via
    // GetPlayCard() = knownCardList.First(c => c.Index == playIdx), so with no knownList it dereferences null and NREs
    // before rendering, logging, or echoing - every opponent hand play is silently dropped. knownList is also the only
    // channel that reveals the face-down enemy card. synthesize the one entry from the sender's deck (Index == deck slot)
    void InjectKnownCard(Session s, string uri, JsonObject body)
    {
        if (uri != "PlayActions" || body["type"]?.GetValue<int>() is not (PlayHand or PlayHandSelect or Fusion)) return;
        if (body["playIdx"]?.GetValue<int>() is not int pIdx) return;
        if (!LedgerFor(s).TryGetValue(pIdx, out var cardId)) return;
        var entry = new JsonObject { ["idx"] = pIdx, ["cardId"] = cardId, ["isSelf"] = 0, ["is_open"] = 1 };
        // without a cost the peer prices this at the master base cost and over-deducts the opponent's Pp until it starts
        // refusing plays outright. state the actor's post-modifier Cost, but NOT the fixed-use/accelerate price: the
        // receive check gates accelerate on CalcFixedUseCost >= Cost, so pinning that would reject every accelerate play
        int? relayCost = TryFinalCost(s, pIdx, cardId, out var finalCost) ? finalCost : null;
        // the spellboost discount is the one price the actor never sends, so the relay reconstructs it from the master
        // and goes silent when it cannot prove the value. above Observe the engine answers with the real cost instead;
        // at Observe it only logs where the two disagree, the evidence for enabling it
        if (Engine.ShadowBridge.Role >= Engine.ShadowBridge.EngineRole.AdviseCost
            && Engine.ShadowBridge.TryCostOf(s.Id == _shadowPlayerId, pIdx, out var live))
            relayCost = live;
        else
            Engine.ShadowBridge.CompareCost(s.Id == _shadowPlayerId, pIdx, relayCost, l => Console.WriteLine($"[{s.Id}] {l}"));
        if (relayCost is int c) entry["cost"] = c;
        // the highlander bit rides this same entry: the peer reads it as a global Any(c => c.IsHighlander) over
        // knownList. never add activate/count/callCount/param alongside it - that reroutes the whole entry into
        // SkillConditionCheckList, out of knownList, and the bit becomes invisible
        if (TryReadHighlanderSpec(body, out var excludeBase) && TryIsHighlanderDeck(s, excludeBase, out var isHl) && isHl)
            entry["highlander"] = 1;
        body["knownList"] = new JsonArray { entry };
    }

    // the actor asks a condition question on the wire and never answers it; the peer's CheckCondition discards its own
    // reading and returns the (absent) injected value, so the skill silently does not fire - 169 receive-gated skills
    // are in that state. the engine plays the same match, so it answers what the acting engine asked itself.
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
        if (!Engine.ShadowBridge.TryConditionAnswers(s.Id == _shadowPlayerId, pIdx, specs, out var answers)) return;

        var list = body["knownList"] as JsonArray;
        if (list is null) { list = new JsonArray(); body["knownList"] = list; }
        // each answer is its OWN entry: folded into the card-state entry, the activate/count key would reroute that
        // entry out of knownCardList and the cardId/cost it carries would be lost
        foreach (var a in answers) list.Add(a!.DeepClone());

        // the flag and the condition channel are mutually exclusive per card index: once any condition entry exists
        // for an index, the peer sends every condition skill on that card down the injected-answer branch and never
        // reaches the highlander flag. the engine answers check_highlander itself, so drop the now-dead flag
        var answered = answers.Select(a => AsInt(a?["idx"])).Where(i => i is not null).ToHashSet();
        foreach (var e in list)
            if (e is JsonObject eo2 && eo2.ContainsKey("highlander") && answered.Contains(AsInt(eo2["idx"])))
                eo2.Remove("highlander");

        Console.WriteLine($"[{s.Id}] engine answered {answers.Count} of {specs.Count} skill conditions");
    }

    // the actor sends the check spec but never its result - only a server-injected knownList carries it, so without
    // this every highlander skill silently fails to activate on the opponent. recognize on `type` alone: check_highlander
    // never ships a `condition`, and the compare stays client-side (it negates itself via num == flag2), so the relay
    // reports ground truth and must not invert. false = decline, which leaves today's (correct-for-non-highlander) default
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
    Dictionary<int, int> BoostFor(Session s) => _boost.TryGetValue(s.Id, out var m) ? m : _boost[s.Id] = new();
    List<CostMod> ModList(Session s, int ix) => ModsFor(s).TryGetValue(ix, out var l) ? l : ModsFor(s)[ix] = new();

    // deltas ride as an opcode char plus a value, e.g. "a3" / "s2" / "d2"
    static (char Op, int Val)? ParseDelta(string? v) => v is { Length: > 1 } && int.TryParse(v[1..], out var n) ? (v[0], n) : null;

    // the peer drops orderList wholesale, so every cost change the actor ALREADY RESOLVED (alter.cost carries a concrete
    // delta - the actor evaluates the formula itself) and every spellboost delta die on the floor. mirror both per idx so
    // InjectKnownCard can state the price the real server used to state
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
            // a group idx ("gN") is a filter spec the real server evaluated against the board. with no board a missed
            // modifier would turn a later emitted cost into a WRONG absolute pin - worse than saying nothing - so stop
            // pricing this player for the rest of the match
            if (AsStr(e["idx"]) is not null) { _costBlind.Add(owner.Id); continue; }
            foreach (var ix in ReadIdxList(e["idx"]))
            {
                if (bd is { } b)
                {
                    // "a"+addCount, or "s"+target for diff_charge which raises to N and skips when already there
                    var cur = BoostFor(owner).GetValueOrDefault(ix);
                    var add = b.Op == 's' ? Math.Max(0, b.Val - cur) : b.Val;
                    if (add <= 0) continue;
                    BoostFor(owner)[ix] = cur + add;
                    if (!LedgerFor(owner).TryGetValue(ix, out var bcid) || !_cardCosts.TryGetValue(bcid, out var bcc))
                    { _costBlind.Add(owner.Id); continue; }
                    if (bcc.SpellboostStep > 0) ModList(owner, ix).Add(new CostMod(CostOp.Add, -add * bcc.SpellboostStep));
                }
                if (cd is { } c)
                {
                    var op = c.Op switch
                    {
                        'a' => CostOp.Add, 's' => CostOp.Set, 'd' => CostOp.HalfUp, 'D' => CostOp.HalfDown,
                        _ => (CostOp?)null,
                    };
                    if (op is null) { _costBlind.Add(owner.Id); continue; }
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

    // mirror BattleCardBase.Cost: fold the non-half modifiers, then the halves, then clamp at 0. false = say nothing,
    // which leaves the peer at the base cost (today's behaviour) rather than pinning a value we cannot prove
    bool TryFinalCost(Session s, int idx, int cardId, out int cost)
    {
        cost = 0;
        if (_cardCosts.Count == 0 || _costBlind.Contains(s.Id)) return false;
        if (!_cardCosts.TryGetValue(cardId, out var cc)) return false;
        // boosted a card whose discount rule we could not read: decline rather than emit an unboosted pin
        if (BoostFor(s).GetValueOrDefault(idx) > 0 && cc.SpellboostStep == 0) return false;
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

    // idx -> cardId for a session, seeded from its shuffled deck (idx 1..40). for a token-free match this equals the old
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
    // says whose card it is: 1 = the sender's own, 0 = the peer's (token idx space is per-player). call it AFTER the knownList
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

    // add entry card sub-dict: plain {cardId} sets it directly; copy {baseIdx} inherits the source card's id (array order
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

    // turn handoff: a client emits Judge only when it is NOT its turn, reacting to the opponent's turn-end. echoing it
    // back (turnState 0) runs the sender's JudgeOperation -> ControlTurnStartPlayer, handing it the turn. the owner guard
    // keeps the handoff to once per turn since the client sends Judge for both the peer's TurnEnd and TurnEndFinal
    async Task HandleJudge(Session s, JsonNode? payload)
    {
        if (_turnOwner.TryGetValue(s.BattleId, out var owner) && owner == s)
        {
            Console.WriteLine($"[{s.Id}] Judge ignored (already turn owner)");
            return;
        }
        Console.WriteLine($"[{s.Id}] turn handoff -> {s.Id} (battle {s.BattleId})");
        _turnOwner[s.BattleId] = s;
        var body = new JsonObject { ["turnState"] = 0 };
        if (payload is JsonObject po)
            foreach (var (k, v) in po)
                if (k is not ("uri" or "pubSeq" or "cat" or "try" or "viewerId" or "uuid" or "bid" or "playSeq" or "turnState"))
                    body[k] = v?.DeepClone();
        await s.SendMsg("Judge", body);
    }

    static object OpponentEnterPayload(Session visitor) => new
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
        oppoId = long.TryParse(visitor.ViewerId, out var v) ? v : 0,
        isOfficial = 0,
        isFriend = 0,
        // Player.WinCount defaults to -1 (NO_GET_WIN). the visitor's EnterRoomServer doesn't reset it, so send battleNum
        // to make the switch call ReceiveWinCount and flip both slots to 0
        battleNum = 1,
        ownerWin = 0,
        guestWin = 0,
    };

    // decide the finish from the reporter's JUDGE_RESULT_STATUS. only codes that are a mutually-derivable end finalize;
    // disconnect-checks (302) and unilateral timeout/offline victory claims (300/301/400/...) return null so a transient
    // relay stall can't end a live game (a real server cross-checks those, an engine-less relay can't).
    // RESULT_CODE is SELF-relative: RetireWin means "you win, they retired". (JudgeResultReceive passes a
    // BATTLE_RESULT_TYPE to SettingResultUI_SpecialResultTypeText, but that only picks the flavour text - it is not
    // the recipient's outcome, so the codes are NOT inverted.)
    async Task FinalizeFromReport(Session s, int log)
    {
        var (rep, pr) = DecideResult(log);
        if (rep == 0) return;
        await Finalize(s, rep, pr);
    }

    // (reporterCode, peerCode); (0,0) means "not an adjudicable end here, do nothing". reporter of a neutral life/deckout
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
        await reporter.SendMsg("BattleFinish", new { result = reporterCode });
        if (peer is not null) await peer.SendMsg("BattleFinish", new { result = peerCode });
        await RecordWin(reporter, reporterCode);
        EndShadow(reporter, reporterCode);
    }

    // the relay authors a BattleFinish only from a client JudgeResult/Retire/close, and a natural lethal produces none:
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
        if (sDead && !pDead) await Finalize(s, 102, 101);       // s's leader died -> s loses
        else if (pDead && !sDead) await Finalize(s, 101, 102);  // peer's leader died -> s wins
        else
        {
            // both leaders dead in one action: the active player (turn owner) loses, matching the engine's
            // JudgeCurrentFinishStatus IsSelfTurn tie-break
            var owner = _turnOwner.GetValueOrDefault(s.BattleId);
            if (owner == s) await Finalize(s, 102, 101);
            else await Finalize(s, 101, 102);
        }
    }

    // the comparison the shadow exists for: relay result (hand-written) vs engine result (read off the board). codes
    // are self-relative, so compare against whichever session the engine held as its BattlePlayer
    void EndShadow(Session reporter, int reporterCode)
    {
        int relayCode = reporter.Id == _shadowPlayerId
            ? reporterCode
            : _finishCode.GetValueOrDefault(_shadowPlayerId ?? "", 0);
        _shadowPlayerId = null;
        Engine.ShadowBridge.End((engineCode, state) => Console.WriteLine(
            engineCode == relayCode
                ? $"shadow: agrees, result {engineCode} | {state}"
                : $"shadow: DISAGREES, relay {relayCode} vs engine {engineCode} | {state}"));
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
            // the room owner is whoever joined the battle group first, the same rule DeckFor uses
            if (_sessions.ByBattle(reporter.BattleId).FirstOrDefault() == winner) o++; else g++;
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
    // transport is ground truth. only the survivor is written; the closing session's writer is already completed
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
        // self-relative: DisconnectWin is "you win, they dropped"
        await survivor.SendAlive(peerOnline: false, peerGone: true);
        await survivor.SendMsg("BattleFinish", new { result = 201 });
        // 202 = "the other side lost", i.e. the survivor won
        await RecordWin(survivor, 202);
    }

    const int FallbackCard = 100111010;

    object MatchedPayload(Session self, Session oppo, string bid, int turnState) => new
    {
        // put selfDeck near the top so it's dispatched right after "uri" (which nulls _selfDeck via InitializeSelfInfo).
        // if Mono's Dictionary iteration in the client had drifted from insertion order, selfDeck showing up late would let uri's null win.
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

    // the client never shuffles, it just draws deck slots in order, so the server owns draw order. shuffle once and cache
    // so Matched, Deal and Swap all agree on the idx->card mapping for the match
    int[] ShuffledDeckFor(Session s)
    {
        if (_shuffled.TryGetValue(s.Id, out var cached)) return cached;
        var ids = (DeckFor(s)?.CardIds is { Length: > 0 } a ? a : Enumerable.Repeat(FallbackCard, 40).ToArray()).ToArray();
        var rng = new Random(DeckSeed(s));
        for (int i = ids.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        _shuffled[s.Id] = ids;
        return ids;
    }

    // stable per (room, viewer) so a re-call inside the match reshuffles identically. string.GetHashCode is randomized
    // per run, so fold the chars by hand for a deterministic seed
    static int DeckSeed(Session s)
    {
        int h = 17;
        foreach (var ch in s.BattleId) h = h * 31 + ch;
        foreach (var ch in s.ViewerId) h = h * 31 + ch;
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

    // keys the client reads unguarded off _selfInfo/_oppoInfo. LoadOpponentAssets (gates BattleStartControl.IsReady) reads
    // country_code, degreeId, emblemId — a missing key throws KeyNotFoundException and IsReady never latches, which strands
    // the client in LoadingPhase forever (and past Prepared it no longer times out). values mirror the AI practice battle
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
            viewerId = long.TryParse(s.ViewerId, out var v) ? v : 0,
            // the API resolved this from name.txt / Steam and wrote it alongside the deck; only fall back if it didn't
            userName = string.IsNullOrEmpty(d?.UserName) ? "player_" + s.ViewerId : d.UserName,
            // the client reads its own selfInfo.fieldId as the stage (NetworkBattleManagerBase.CreateBackgroundId), so both
            // players carry the same per-battle roll to land on the same map
            fieldId = _fieldId.TryGetValue(s.BattleId, out var fid) ? fid : 1,
            seed = 12345,
            deckCount = d?.CardIds.Length ?? 40,
            oppoDeckCount = DeckFor(peer)?.CardIds.Length ?? 40,
            // GetOpponentSleeveId does Convert.ToInt64(_oppoInfo["sleeveId"]) so a missing field kills the battle-load coroutine
            sleeveId = d?.SleeveId ?? 3000011L,
            emblemId = -1L,
            degreeId = -1,
            country_code = "NONE",
            isOfficial = 0,
            oppoId = long.TryParse(peer.ViewerId, out var pv) ? pv : 0,
        };
    }

    // a deck card the reshuffle moves. only Index is tracked (cardId is read fresh from _ledger at swap time so a card
    // transformed while in the deck stays correct). Index is mutable and followed by reference, since an earlier swap in
    // the same flush can move a still-queued card
    sealed class DeckCard { public int Index; }

    // ported verbatim from BattleManagerBase.XorShift so the server produces bit-identical values (same .NET runtime).
    // only w is seeded; x/y/z keep their constants. do NOT change the arithmetic or GetChangeInt would drift
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
