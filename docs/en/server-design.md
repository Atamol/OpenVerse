# OpenVerse server design

## Servers

| Server | Language/tech | Role |
| --- | --- | --- |
| API | C# (ASP.NET Core / Kestrel) | login, master data, decks, room management. HTTP + AES + MsgPack |
| Battle | C# (custom Socket.IO) | real-time. server-authoritative operation list |
| CDN | static server (nginx / caddy) | asset delivery |
| DB | SQLite, PostgreSQL if needed | decks, owned cards, sessions |

- API and battle can share one process or split later. They share a common library (crypto / types / MsgPack / DB)
- The main hurdle is the custom Socket.IO framing, matching BestHTTP's actual frames 1:1 (polling `[type][length][0xFF]`, websocket upgrade, `{_placeholder,num}` plus a leading `0x04` raw binary attachment, ping fixed at 2000/5000ms)

## Language

The client supports 9 languages (Jpn, Eng, Kor, Chs, Cht, Fre, Ita, Ger, Spa). OpenVerse handles just Jpn and Eng.

- Determined by the HTTP `LANGUAGE` header. Unsupported values fall back to Jpn
- The `LOCALE` header is hard-coded to `"Jpn"` client-side and can be ignored. `REGION_CODE` is the Steam store country and unrelated to display language, also ignored
- CDN paths carry a language token (for example `/dl/Manifest/<Ver>/Jpn/Windows/manifest_assetmanifest`), so pull it out of the URL and serve `stubs/<lang>/manifest/<name>`
- Text language and sound/movie language are independent, but OpenVerse does not handle sound so only `LANGUAGE` (text) is read

### What needs localization

The client's `SystemText` (a local JSON dictionary) covers error messages, maintenance banners, and most card name/effect text. The server just returns IDs.

Server-side text is only needed for:

- Mail `message`
- Mission / Achievement `name`
- Login bonus campaign `name`
- Vote family (`vote_name`, `title_text`, `tweet_text`)
- Quest / event band label `name`
- Boss rush `name` / `skill_text`

None of these touch room match or deck editing. They land in Phase 5 (mail / announcements) via an i18n table (`table_i18n(id, lang, ...)`).

### Cost

Code side is around 20 lines (read header, extract the language token from the path, switch stub folder).

The real work is getting EN assets (`master_cardnametextmaster.unity3d` and friends). The official CDN is dead, so the source is an EN player's local cache (`AppData/LocalLow/Cygames/Shadowverse/a/`).

## Network

Whoever runs the server picks the reach method, pointing `utoongaize.shadowverse.jp` at the server via hosts on each machine.

- VPN (no port forwarding, well-suited for casual private matches)
- Fixed host (needs port forwarding or DDNS)
- The client forces HTTPS, so either patch it to http (`SetScemeMode(Https) -> Http`) or set up a local CA (mkcert). For a private group, a patched client plus one hosts line is enough

## Docker

- Direct `dotnet run` at first (no Docker needed if the DB is SQLite)
  - Compose just the DB when moving to Postgres
- Dockerize the whole thing once always-on hosting or distribution becomes necessary
