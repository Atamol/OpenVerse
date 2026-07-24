### ![ja](https://flagcdn.com/20x15/jp.png) [日本語版](../roadmap.md)

# OpenVerse roadmap and progress

Current position: Phase 4 (room match). PvP runs end to end between two clients, sync bugs still being fixed.

## Phase 0: Client analysis (done)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] Assembly-CSharp decompile
- [x] Server layout and domains
- [x] Crypto and wire format analysis
- [x] Auth and endpoint list
- [x] Socket.IO framing

Details in [protocol.md](protocol.md).

## Phase 1: Wire foundation (done)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] Solution skeleton
- [x] Crypto and wire codec
- [x] Redirect (hosts + self-signed HTTPS)
- [x] Clear the title

## Phase 2: Master data and reaching a screen (done)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] Home screen
- [x] card_master delivery (all cards unlocked)
- [x] Voices
- [x] All sleeves and special illustrations

## Phase 3: Deck editing (done)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] All formats
- [x] Starter decks
- [x] Deck introduction
- [x] Deck editing
- [x] Self-hosted deck codes

## Phase 3.5: Solitaire content (done)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] CP battle

## Phase 4: Room match (PvP)

![](https://progress-bar.xyz/70?width=500&title=Propgress:)

- [x] Room sequence analysis
- [x] Create and join a room
- [x] Steer to the battle server
- [x] Socket.IO framing
- [x] Operation protocol analysis
- [x] Relay battles between two clients (known sync bugs: spellboost, extra turns, PP)
- [x] Run the client's battle engine headless as an observer
- [ ] Engine adjudication (fill cost/condition blanks)

## Phase 5: Distribution and ops

![](https://progress-bar.xyz/40?width=500&title=Propgress:)

- [x] Launcher and setup (two exes for self-hosting)
- [x] Release package build
- [ ] Connection docs
- [ ] Full run-through
