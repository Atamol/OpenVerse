### ![ja](https://flagcdn.com/20x15/jp.png) [日本語版](../desync.md)

# Desync

## What the problem is

In PvP the two clients each compute the match independently, and the server only hands one client's messages to the other without computing the match itself. So when the result the acting side computed and the result the receiving side recomputed disagree, every board state after that point belongs to a different match, which is the desync.

For example:

- a card played and paid for never appears on the opponent's screen
- a follower is destroyed on one screen and survives on the other
- an attack on the opponent's leader takes life off on your screen while the opponent never sees the attack at all
- life or PP totals do not match

Nothing shows up as an error in the game, and a player usually notices at some later point, so telling these apart by eye is hard.

Below, the relay is this server, the actor is the client whose message it is, and the receiver is the client being handed that message.

## Cause

In PvP the client has no model of the opponent's deck. There is a method that installs a real opponent deck, but only the replay loader calls it and it never runs during a match (`SetOppoDeck`). So during a match the opponent's deck comes back as 40 copies of a dummy card.

Which card the opponent holds, and its cost, stats, counters and tribe, are values the original server computed the match to produce and then wrote. The relay does not compute a match, so it cannot write them.

That leaves some of what is needed off the wire:

- cost reductions (for example `NetworkSkill_cost_change.IsSend`) return false for as long as the affected card is in hand
- the actor's resolution record (`orderList`) is read nowhere in the 607k-line client (one write, no reads)
- around eighteen of the fields the receiver accepts have no writer under any spelling

Saying nothing about cost is not neutral. The receiver only installs a modifier when the value is stated, so with nothing stated it bills the base cost. It subtracts its own PP, so it eventually refuses a whole play for lack of PP, and checks like whether an attack is legal fail after that. The log keeps `IsPlayCard PPover` and `ConductError`.

### Why practice never desyncs

Practice is built on a different battle type from network play and cannot reach the receive path at all (`BattleType.Practice`). It emits no messages either, so it produces no output a PvP implementation could work from.

What it does give is an operation record that writes both players' plays with the effective cost (`SingleBattleOperationRecorder`). Without modifying the client and without a second player, that yields the correct value for the cost the relay currently guesses.

## Plan

The client's own battle engine runs headless alongside the match, one per client, both held by the server. The rest of this doc calls one a mirror, which is what the code calls it too (`MirrorPair` / `ShadowMirror`). The relay no longer has to assemble the values, and what it tells the peer is read straight off that client's mirror.

Why one is not enough:

- there is a second random stream advanced only by its owner's effects, drawn without touching the shared counter (`_stableRandomOnlySelf`). One per client by construction
- `spin` is the actor's draws minus that receiver's re-simulation draws, so the receiver's side has to be reproduced to get it
- the receive check looks at a fixed side of the board, so it only means anything on a correctly oriented mirror

### Why `SetDeckMirror` is off

Setting it marks the mulligan as finished, which turns `ShadowReconciler`'s return-to-deck path into draws that never happened. The two are simply mutually exclusive, and the mirror side is not the broken half, so once the server owns dealing `ShadowReconciler` becomes unnecessary and this can be turned on.

## What "done" means

The official servers are dead so there is nothing to diff against, but the check itself ships inside the client. It tests a received message against the receiver's board: is the action card there, is the attack legal, was the played card in hand (`OperateReceiveChecker`). That is Cygames' own definition of a desync, and on failure the message is discarded and `ConductError` is written to the log.

Success is five counters going to zero.

| | Metric |
| --- | --- |
| O1 | ConductError count |
| O2 | board diff between the two mirrors, including the random cursor |
| O3 | unresolved publish counts (3 of 5 on the current capture) |
| O4 | (opcode, timing) pairs not yet exercised, out of 683 |
| O5 | undrained vfx queues |

## Limits

- Values the server decides. `spin`, what to tell the peer about a card, and condition answers are parsed by the client and never written by it. The format is specified, what to put in them is not recorded anywhere, so that part is design rather than reproduction
- The engine is a reconstruction built from 607k decompiled lines by a compiler Cygames did not use. An IL diff narrows the difference but cannot reduce it to zero. Pin one engine build and ship its hash so two hosts cannot differ without noticing
- Guarded constructors skip their whole body on a null asset, so if any hides a state mutation the mirror skips it too. Chasing exceptions does not surface it

## Implementation

**Which side is the owner**: connect order does not decide it. The client rebuilds the battle socket per game, so game 2 onward is a reconnect race between two remote players, and getting it backwards swaps both the deck and the score. The sender of the room-creation message is the owner and the sender of the entry message is the visitor, so either one settles both (`RoomCreate` / `RoomEntry`).

**Mode flags**: without telling the engine this is a network battle it takes the solo-battle branch and draws from a local RNG neither client drew from (`GameMgr.IsNetworkBattle` and friends).

**Comparing the boards**: for each pair of the actor's turn end and the receiver's reply (a boundary from here on), the three board hashes each client sends are compared (`ConsistencyWatch`). On a mismatch it logs which third broke and nothing else, for now.

The shipped capture breaks at boundaries 9-11. It is itself a match that desynced without anyone noticing, and the play `ShadowCostFidelityTests` prices sits on the board after the break, so it is useful for spotting trouble but is not ground truth. The cemetery count looks like an independent signal, but the two turn ends land on different turns and cannot be compared as they are. The first hash already combines the cemetery, so a gap shows up there instead.

**Ending the match on a desync**: a closing message carrying `endType=2` runs the client's internal no-contest path and comes back as 900/901. The relay already reads that as a mutual no-contest. `OPENVERSE_DESYNC` switches between `warn` (default) and `stop`. Even on `stop` it takes two consecutive broken boundaries, since one can just be a late message.

**Detecting a disconnect**: a dropped line does not close a TCP socket, so the disconnect notice arrives minutes late or never. The last-received time is updated on every receive and 20 seconds of silence falls through to the disconnect result, which fits inside the 95 seconds the client itself waits before giving up.

**State carried between battles**: a room plays many battles, so mirror state has to be cleared per battle and released from every end path. Neither was true, so every game after the first was answering from the previous battle's board.

**Context isolation**: each mirror gets its own assembly load context (`EngineContext`). A shared pair still prints two plausible boards, so board output cannot detect the failure and the check has to be type identity.

**Measuring ConductError**: the client posts its own log to the server with `ConductError` and its reason in it. The server was already receiving it, buried inside a request body, so it is pulled out and logged with a running count.

The measured baseline, over six games against a remote friend: 30.

| Reason | Count |
| --- | --- |
| action card not found | 11 |
| the played card is not in hand | 10 |
| play target not found | 4 |
| skill selection check failed | 3 |
| no reason logged | 2 |

The cards in that log, `112821010`, `125811030` and `720831010`, are the same ones the relay could not state a cost for in that session, which closes the chain from the cause section on real data.

**The two mirrors**: each puts its own client's 40 cards on its own side and 40 dummies on the other (`MirrorPair`), matching the board that client actually has. The same message arrives as the client's own action on one and as the opponent's on the other, so getting it backwards would look like it works while being inverted. A test pins it.

**Reading values off the mirror**: card id, cost, attack, life, spellboost count, chant, tribe and class come off the card object (`ShadowMatch.Project`). Questions about hidden zones go to whichever side owns the zone. The cost is the value after the add, set and halve modifiers have been applied, which the receiver has no way to compute.

It is used only where the relay could not state a value, and accepted only when the card id at that index matches and the value is between zero and the base cost. Outside that the boards disagree, so it is discarded. The fixed-use and accelerate prices are not passed on even when they can be read. The receiver decides accelerate by comparing those two, so pinning them rejects every accelerate play.

**`spin`**: the receiver re-simulates the actor's action against forty dummies, so its random cursor lands short of the actor's. `spin` is the gap and the receiver discards that many random values to catch up, but it is forward-only, so a negative gap is never sent.

**One pair per room**: there was one set for the whole process, so two rooms playing at once answered each other's questions. They are keyed per room now and handed out from a pool (`MirrorPool`). Past the cap (`OPENVERSE_ENGINE_PAIRS`, default 2) a room gets none and relays without a mirror, which is safer than sharing a board.

**The pre-send check**: straight after the receiving board ingests, whether that client would drop the message is read and a refusal is logged. The message is still sent. The actor has already committed locally, so not sending would leave one side holding a board the other never gets, which is worse than the drop.

**Driving from the action**: instead of the receive path, the mirror is driven through the same entry the client uses for its own actions (`ShadowMatch.PlayByIntent`). That entry is the one the AI uses and has no battle-mode branch, so it goes through more readily than the receive path.

But turning it on moves `ShadowCostFidelityTests` from 2 to 6, counting only 13 of 16 spellboosts. So it is enabled explicitly through `OPENVERSE_ENGINE_INTENT` and off by default. A failure falls back to the receive path, so it is no worse than before, but it is not yet known to be better. That is the main open problem left.

Untested, but this path skips the pre-play board repair, and on success it also skips the actor's own receive handling. The step that moves a card from deck to hand is lost, so a card the wire put in hand can still sit in the mirror's deck where no spellboost reaches it. Calling the repair first would settle it, but measure before deciding.

### The copied client's viewer_id

Copying an install carries the cached viewer_id with it, so both clients claim the same one, and this is not only a seating problem. The client holds its own id as the value the server handed it, and the receiver discards any addressed value whose address is not its own. `spin` uses that shape too, so identical ids means every one of them is discarded.

Two fixes went in:

- seat off the source IP. The API records where a request came from and the Battle side matches it against the socket's peer, which is the one value the client cannot choose. Two players behind one NAT share an IP, so that case falls through to the viewer_id pin and then to connect order
- derive the id handed to the client from the seat. When both sockets claim the same id, the owner keeps it and the visitor is offset by one

### Entomb and reanimate

Card ids were only attached to cards that landed on the field, and entomb goes hand to cemetery, so it fell outside. With no card id stated the receiver fills one in from its own dummy deck, and the entombed follower shows up as a Goblin.

Cemetery and banish are visible to both players and stating a card id there leaks nothing, so the destination test now covers field, cemetery and banish. Arrival in hand still stays out since that would leak draws, and a hand-to-field play belongs to another path.

Reanimate looked suspect as a knock-on of this: a card that entered the cemetery unnamed is not in the index-to-card-id table, so reviving it later cannot state a card id either.

### What decides which card to state

Chevalier Magna (`119241030`) played as a 1pp crystal strips every ability off all the opponent's followers, provided a Commander card was fused into it beforehand (attack and life stay).

Only the played card is stated, so the ingredients stay dummies on the receiving side. A dummy carries no tribe, so it fails the fusion condition that requires a Commander and nothing is deposited. The effect then sees no fusion and never fires, the opponent's followers keep their abilities, and later attacks get refused.

The receive check has no fusion branch, so the fusion message passes it and the boards disagree with nothing erroring, which is why it went unnoticed.

Fusion ingredients go to their own zone and do not even emit a move record, so a destination test never reaches them. The destination test stays, and a test was added that states every index the message makes the receiver resolve. That covers any branch reading a hidden card's attributes, not just fusion. Note that only what the card data model holds can be sent to the peer, and fusion state is not in it.

### A spellboost count is not always a cost cut

The spellboost count on the wire is a general counter incremented when a spell is played. It lands on cards with no spellboost ability at all, which is normal, and across the master the skills that read it split into 100 that cut cost against 54 that spend it on damage, tokens, healing or stats.

Reading the two as one, the relay treated any card carrying a count as unpriceable and discarded cost deltas it had already recorded. In a live match Call to the Battlefield handed -1 to two cards and only one of them later caught an unrelated spellboost, so that one went out at base cost and was thrown away for lack of PP. The other card from the same message priced correctly, and the only difference was the spellboost.

Cards with no discount rule are now separated from cards whose discount step would not parse, and only the latter go unpriced. Two cards qualify.

The 54 that spend the count on something other than cost are not modelled at all. The actor ships concrete results so it usually does not matter, but a receiver counting its own spellboosts wrong gets the damage or the count wrong with it.

### The card id used as a placeholder

The client's own dummy is a Goblin with no effect (`100011010`). The relay and the mirrors used `100111010`, which is the real Water Fairy: it has a Last Word that adds a Fairy to hand, and one less life (1/1 against 1/2).

Every mirror booted with forty of those facing it, so a dummy dying grew the mirror's hand and nothing else's, and the relay reads its numbers off that board. As the marker for "cannot state this" it also collided with the real card, so an Elf playing Water Fairy had its card id blanked.

Three constants held the same value, which is what kept the mistake invisible. They are one now.

### A token's card id has to be stated

What tells the peer about a card cannot be handled headless and throws, so it was withheld from the mirrors. Deck cards resolve by index against the board's own forty, but a card created mid-match has an index past the deck and its card id exists nowhere else. Dropping it left the receiving side unable to place the card, and every later message naming that index died with it.

It now travels to the mirror under a key the client never reads, and any index the board does not hold is built through the engine's own token path, which needs no view and works headless. The shipped capture went from 2 dropped rows out of 72 to none.
