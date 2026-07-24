### ![ja](https://flagcdn.com/20x15/jp.png) [日本語版](../server-design.md)

# OpenVerse server design

## Layout

| Server | Framework | Role |
| --- | --- | --- |
| API | C# (ASP.NET Core / Kestrel) | login, master data, decks, room management |
| Battle | C# (custom Socket.IO) | PvP real-time |
| CDN | static server (nginx / caddy) | asset delivery |
| DB | SQLite (PostgreSQL if needed) | decks, owned cards, sessions |

- API and battle can share one process or split later (they share a common library)
- A battle server is only needed for PvP. Solitaire (CP battle, story, quest) runs entirely client-side, engine and AI included

## API handlers

`Program.cs` dispatches to each handler.

- `card_master`: serves every card as owned
- `DeckHandler`: deck editing, deck introduction, starters
- `PracticeHandler`: CP battle setup and result recording
- `RoomHandler`: room management (Phase 4)
- `DeckCodeHandler`: self-hosted deck codes
- load/index etc. are stubs with dynamic splices (owned cards, sleeves, background ids)

Card names/effects live in the client's SystemText, so card_master only returns text ids.

## Language

Jpn and Eng, switched by the HTTP `LANGUAGE` header.

- Most text lives in the client's SystemText, so the server just returns ids
- `LOCALE`/`REGION_CODE` are unrelated to display language and can be ignored
- The CDN reads the language token in the path and serves `stubs/<lang>/`
- The server only holds per-language text for a few things (mail, missions, votes, etc.), in Phase 5 via an i18n table

## Network

Point `utoongaize.shadowverse.jp` at the server via hosts on each machine. Whoever hosts picks the method:

- VPN (no port forwarding, good for private groups)
- Fixed host (needs port forwarding or DDNS)
- The endpoint is HTTPS, but the client does not validate the cert, so a self-signed one works as-is (no client patch or mkcert, the launcher generates it)

## Battle engine

The PvP relay passes a client's messages straight to the peer, but the values the original server used to fill in (costs, condition answers) go missing. So the client's own battle engine runs headless alongside the match, replays the same game, and observes those missing values. It only observes for now. Switching it to adjudication is gated behind the `OPENVERSE_ENGINE_ROLE` env var and enabled in stages.

## Distribution

- Ship two exes, setup and launcher (setup needs no elevation, launcher runs as admin to rewrite hosts and start the server)
- Self-hosting also works via `dotnet run` (SQLite, so no DB server)
- Dockerize if always-on hosting is needed
