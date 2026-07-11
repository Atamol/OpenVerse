### [日本語版](../server-design.md)

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
- The client forces HTTPS, so use an http patch or a local CA (mkcert)

## Docker

- Direct `dotnet run` at first (no Docker needed with SQLite)
- Dockerize the whole thing once distribution or always-on hosting is needed
