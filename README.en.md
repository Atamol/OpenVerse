![](https://progress-bar.xyz/14?width=100&title=Propgress:)

# OpenVerse

A preservation project for the original Shadowverse (Steam version), whose service ended on 2026-07-01 11:00.
The goal is to keep deck editing and room matches playable after shutdown.

Language: English (this file). The source is Japanese: see [`README.md`](README.md).

## What it does

Run your own server and get:
- Deck editing
- All cards free
- Room match

Out of scope for now, mostly for cost reasons:
- Ranked
- Gacha
- Payments

## Progress

Early development. Phase-by-phase breakdown in the [roadmap](docs/en/roadmap.md).

![](https://progress-bar.xyz/14?width=500&title=Propgress:)

- [x] Project design
- [x] Client analysis (crypto, wire format, endpoints)
- [ ] Real client past the title screen
- [ ] Home (custom UI)
- [ ] Deck editing
- [ ] Room match
- [ ] Battle

## How it works

OpenVerse stands in for the servers the Steam client used to talk to over HTTP and Socket.IO:
- API server (HTTP): login, master data, decks, room matching
- Battle server (Socket.IO): online battle
- Static server: game assets

Request and response bodies are JSON, packed with MessagePack and encrypted with AES. Full details in [protocol.md](docs/en/protocol.md).
Point the client at your server by rewriting the host in `hosts`, then either patch the client to use http or install a local certificate.

## Architecture

Runs on Windows and Linux.

- `src/OpenVerse.Common`: shared crypto, wire codec, types
- `src/OpenVerse.Api`: HTTP API server
- `src/OpenVerse.Battle`: Socket.IO battle server

## Running

Not yet.

## Legal

- This project is unofficial and unaffiliated with Cygames.
- Obtain your own legally acquired copy of the client.
- OpenVerse ships only original server source and protocol notes derived from observed traffic. It contains no decompiled client code and no game assets.

## License

TBD
