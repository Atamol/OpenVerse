### ![ja](https://flagcdn.com/20x15/jp.png) [日本語版](../desync.md)

# Desync

## What the problem is

In PvP the two clients each compute the match independently, and the server only hands one client's messages to the other without computing the match itself. So when the result the acting side computed and the result the receiving side recomputed disagree, every board state after that point belongs to a different match, which is the desync.

- a card played and paid for never appears on the opponent's screen
- a follower is destroyed on one screen and survives on the other
- the attacking side sees the opponent's leader lose life while the opponent never sees the attack
- life or PP totals do not match

The game shows no error and a player only notices later, so watching for it is no use.

Below, the relay is this server, the actor is the client whose message it is, and the receiver is the client being handed that message.

## Cause

The client has no model of the opponent's deck. There is a method that installs one, but only the replay loader calls it, so during a match the opponent's deck comes back as 40 copies of a dummy card (`SetOppoDeck`).

Which card the opponent holds, and its cost, stats, counters and tribe, are values the original server computed the match to produce, and a relay that does not compute a match cannot write them.

So what is needed never reaches the wire:

- cost reductions (for example `NetworkSkill_cost_change.IsSend`) return false for as long as the affected card is in hand
- the actor's resolution record (`orderList`) is read nowhere in the client
- some of the fields the receiver accepts have no writer under any spelling

The receiver only installs a modifier when the value is stated, so staying quiet about a cost bills the base one. It subtracts its own PP, so it eventually refuses a whole play for lack of PP, and checks like whether an attack is legal fail after that. The log keeps `IsPlayCard PPover` and `ConductError`.

### Why practice never desyncs

Practice is built on a different battle type from network play and cannot reach the receive path at all (`BattleType.Practice`). It does no networking either, so it produces no output the relay could be modelled on.

Its operation record does hold both players' plays with the effective cost attached (`SingleBattleOperationRecorder`). That is ground truth for the cost the relay is guessing at, with no client modification and no second player.

## Plan

The client's own battle engine runs headless on the server, playing the same match alongside it. No screen, just a copy of the client that holds a board. The rest of this doc calls one a mirror, which is what the code calls it too (`MirrorPair` / `ShadowMirror`).

What the relay needs to know is what things look like in a given client's hands, so there is one mirror per client, and what to tell the peer is read off that client's mirror.

Why one is not enough:

- a second random stream advances only on its owner's effects and draws without touching the shared counter (`_stableRandomOnlySelf`), one per client by construction
- the receive check reads the opponent's board at a fixed side, so it only means anything on a correctly oriented mirror
- `spin` below comes from what the actor and the receiver each consumed, so both sides have to be reproduced to compute it

### Why `SetDeckMirror` is off

Setting it counts as the end of the mulligan, and `ShadowReconciler`'s path back into the deck then reads as draws that never happened. The two are simply exclusive: once the server deals, `ShadowReconciler` is unnecessary and this can go on.

## Target

The official server is gone, so there is no record to check against, but the check itself ships inside the client. It tests a received message against the receiver's board: whether the acting card is there, whether the attack is legal, whether the played card is in hand (`OperateReceiveChecker`). That is Cygames' own definition of a desync, and failing it drops the message and leaves a `ConductError` in the log.

Done is these five at zero.

- ConductErrors raised
- board diff between the two mirrors, random cursor included
- publish counts that cannot be resolved
- (opcode, timing) pairs never exercised
- unprocessed vfx queue

ConductErrors are measured by the client itself. It uploads its own log to the server with the reason attached, so the relay pulls those out and prints them with a running count.

## Implementation

### Which side is owner

Not the connection order. The client reopens its socket per match, so the second match onward is a reconnect race, and getting it wrong swaps both decks and both records. The sender of the room-create message is the owner and the enterer is the visitor, so either one settles both (`RoomCreate` / `RoomEntry`).

### Seating

A copied install brings its cached viewer_id along, so both clients announce the same value. The receive side drops any addressed value not addressed to itself, and `spin` has that shape, so an identical id means it is always dropped. Seating therefore comes from the source IP: the API records where a request came from and Battle matches it against the socket's origin, which is the one value the client cannot pick. Two people behind one NAT share an IP too, and that falls back to a viewer_id pin and then to connection order. When both sockets announce the same id, the visitor's is shifted by one on the way out.

### The mode flag

Without being told this is a network battle the engine takes the solo branch and draws local randoms neither client drew (`GameMgr.IsNetworkBattle` among others).

### The two mirrors

Each puts its own client's 40 cards on its own side and 40 dummies opposite (`MirrorPair`), the same shape the client actually holds. One message arrives at one as its own action and at the other as the opponent's, so getting them the wrong way round looks like it works and runs inverted.

The dummy's card id has to be the Goblin the client itself uses (`100011010`). A real card in that slot runs its Last Word on the mirror alone, and collides with the same value used as the "cannot state this" marker.

### Context isolation

Each mirror gets its own assembly load context (`EngineContext`). Sharing one still produces plausible boards on both sides, so board output cannot detect it and only type identity can.

### One pair per room

Mirrors are held per room and rented from a pool (`MirrorPool`). One pair per process means two concurrent rooms answer each other's questions. A room past the cap (`OPENVERSE_ENGINE_PAIRS`, default 2) is relayed without a mirror, which is safer than sharing a board.

### Carry-over between matches

A room holds several matches, so mirror state is cleared per match and released on every exit path. Miss either and the second match onward answers from the previous match's board.

### Reading values off the mirror

Card id, cost, attack, life, spellboost count, chant, tribe and class come off the mirror's own card objects (`ShadowMatch.Project`). Questions about a hidden zone go to whichever side owns that zone. Cost is the value after the add, set and halve modifiers, which the receiver has no way to compute.

It is used only where the relay could not produce the value. It is accepted when the card at that index has a matching id and the value is between zero and the base cost. Outside that the boards disagree, so it is discarded. The only cards left unpriced are those with a discount whose step will not parse, and a card merely carrying a spellboost count is not one of them.

The fixed-use and accelerate prices are readable but not sent. The receiver decides accelerate by comparing the two, so pinning them rejects every accelerate.

### Which card to state

A card id is stated when its destination is the field, the cemetery or banish. All three are visible to both players, so nothing leaks. Arrival in hand stays out because that would leak draws.

Destination alone is not enough. Fusion ingredients go to their own zone and do not even emit a move record, so a destination test never reaches them. Ingredients left as dummies carry no tribe, so they fail a fusion condition that requires a Commander, and a card like Chevalier Magna (`119241030`) reads as unfused. The receive check has no fusion branch, so the boards disagree with nothing erroring.

Alongside the destination test, every index the message makes the receiver resolve is stated as well, which covers any branch reading a hidden card's attributes.

Only what the card data model holds can be sent, and fusion state is not in it. A card that entered the cemetery unnamed is not in the index-to-card-id table, so reviving it later cannot state a card id either.

### A token's card id

A card created mid-match has an index past the deck and its card id exists nowhere else. Dropping it leaves the receiving side unable to place the card, and every later message naming that index dies with it. What tells the peer about a card cannot be handled headless and throws, so it travels to the mirror under a key the client never reads, and any index the board does not hold is built through the engine's own token path, which needs no view.

### `spin`

A field the wire already has, for closing a gap between the two random cursors.

Both sides keep random effects in agreement by drawing from the same random stream at the same position. The receiver recomputes the actor's action against 40 dummies rather than the real deck, so it draws fewer times and its cursor stops short. Every random effect after that resolves differently on the two sides.

`spin` is the size of that gap. The receiver burns exactly that many draws to catch up. It is forward-only, so a negative gap is not sent.

It is computed but not sent by default (`OPENVERSE_SPIN=1` to send it). The value comes from the difference between the mirrors and those are adrift themselves. Measured runs ask for wildly oversized gaps, and burning that many wrecks the hand it was meant to line up.

### Board comparison

For each pair of an actor's turn end and the receiver's answer (a boundary below), the three board hashes each client sends are compared (`ConsistencyWatch`). A mismatch only logs which of the three broke.

Cemetery counts look like an independent signal but the two turn ends fall in different turns, so they cannot be compared as they stand. The first hash folds the cemetery in, so the drift shows up there.

### Ending a match on desync

`endType=2` on the finish message takes the client's own no-contest path and returns 900/901, which the relay reads as a mutual no-contest. `OPENVERSE_DESYNC` switches between `warn` (default) and `stop`. Even on `stop`, one boundary can just be late, so a match ends only after two consecutive ones break.

### The pre-send refusal check

Right after a message is run against the receiver's board, whether that client would drop it is read and logged. The send is not held back. The actor has already committed locally, so not sending leaves the board on one side only, which is worse than being dropped.

### Driving by intent

The mirror is driven through the same entry the client uses for its own actions rather than the receive path (`ShadowMatch.PlayByIntent`). It is the entry the AI uses and has no battle-type branch, so it passes more readily than the receive path. Enabled with `OPENVERSE_ENGINE_INTENT`, off by default.

## Bugs

- the mirrors' boards drift from the match. That is why `spin` produces wildly wrong values, and the mirrors' PP cannot be trusted either
- driving by intent loses cost fidelity. It is off by default to avoid that
- a guarded constructor skips its whole body on a null asset, so a state mutation inside one is skipped on the mirror as well. Chasing the exception does not surface it

## Not implemented

- abilities that spend the spellboost count on something other than cost
- the design of the values the server used to decide (`spin`, what to tell the peer about a card, the answers to conditions). The client only reads them, so the shapes are known but nothing records what belongs inside

## Structural limits

The engine is a rebuild of the client and the difference from the original cannot be taken to zero. The build is pinned and its hash published so two hosts cannot drift apart unnoticed.

The shipped capture breaks partway through, and the plays `ShadowCostFidelityTests` checks sit on the board after that break. It is useful for spotting trouble but is not ground truth.
