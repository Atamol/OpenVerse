# OpenVerse roadmap and progress

A self-hosted server for Shadowverse (Steam, service ended), for a small group. The goal is full room-match reproduction with every card unlocked, built in C#. No central server, whoever wants to play self-hosts.

Current position: late Phase 1. The receiver answers the title checks. Next up is connecting a real client to verify the crypto.

## Phase 0: Client analysis (done)

- [x] Assembly-CSharp decompile (Unity 2020.3 Mono, namespace Wizard)
- [x] Server layout and domains (API/CDN/Node/DeckBuilder)
- [x] Crypto CryptAES (AES-256-CBC, API and Node variants)
- [x] Serialization (JSON -> MessagePack -> AES)
- [x] Auth (Steam ticket + viewer_id) and endpoint list
- [x] Socket.IO version (URL says EIO=4 but v3 framing + v2 binary, a non-standard mix)
- [x] API body wrapping (`_createBodyMsgpack`: JSON -> MsgPack -> AES)

Details in [protocol.md](protocol.md).

## Phase 1: Wire foundation and first connection (here)

- [x] Chose C# (rationale in [server-design.md](server-design.md))
- [x] Solution skeleton (Common / Api / Battle)
- [x] Crypto library `WireCrypto` (both AES variants)
- [x] MessagePack integration
- [x] Wire codec `WireCodec` (request decode, response encode)
- [x] Capture receiver (logs headers and body, decodes requests to JSON, stub responses for the title checks)
- [ ] Redirect (hosts + http patch) to route a real client into the receiver (in prep)
- [ ] Verify crypto and headers against real traffic
- [ ] Answer the startup checks and clear the title

## Phase 2: Master data and reaching a screen

- [ ] Master data delivery (return every card as owned)
- [ ] Reach the home screen or the original UI

## Phase 3: Deck editing

- [ ] Deck CRUD API (`deck/info`, `deck/create`, `deck/edit`, `deck/delete`)
- [ ] All formats
- [ ] Card restrictions
- [ ] Editing UI (native or custom via the original UI)

## Phase 4: Room match

- [ ] Analyze the main API and room sequence (`Wizard.RoomMatch`)
- [ ] Create and join a room
- [ ] Return `node_server_url` in the match response to steer to the battle server

## Phase 5: Battle (hardest)

- [ ] Custom Socket.IO framing (v3 payload + v2 binary attachments)
- [ ] Operation protocol analysis (server-authoritative or not, where card effects live)
- [ ] Battle engine (turns, evolve, card effects. reference shadow_sim)

## Phase 6: Distribution and ops

- [ ] Dockerize (so `docker compose up` brings it up)
- [ ] Document reach options (private groups via Tailscale, fixed host via port forwarding)
- [ ] Full run-through with friends
