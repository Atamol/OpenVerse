![](https://progress-bar.xyz/82?width=100&title=Propgress:)

### [![Discord](https://img.shields.io/badge/Discord-Join-5865F2?logo=discord&logoColor=white)](https://discord.gg/VMjWKegucJ)

### [日本語版](README.md)

# OpenVerse

An emulation server project for Shadowverse (Steam version), whose service ended 2026-07-01 11:00.
It keeps deck editing, CP battles, and room matches playable after shutdown.

Language: English (this file). The source is Japanese: see [`README.md`](README.md).

## What it does

Run your own server and get:
- Deck editing
- All cards free
- CP battles
- Room match

Out of scope for now:
- Puzzle
- Ranked
- Gacha
- Payments

## Progress

Room match is in progress. Phase-by-phase breakdown in the [roadmap](docs/en/roadmap.md).

![](https://progress-bar.xyz/82?width=500&title=Propgress:)

- [x] Project design
- [x] Client analysis (crypto, wire format, endpoints)
- [x] Home and other UI
- [x] All cards, voices, sleeves unlocked
- [x] Deck editing
- [x] vs CP
- [ ] Room match

## How it works

OpenVerse stands in for the servers the Steam client talked to over HTTP and Socket.IO:
- API server (HTTP): login, master data, deck info, room match
- Battle server (Socket.IO): online battle
- Static server: game assets

Request and response bodies are JSON wrapped with MessagePack and encrypted with AES. Details in [protocol.md](docs/en/protocol.md).
Point the client at your own server by rewriting the host in `hosts`, then use an http patch or a local certificate.

## Architecture

Runs on Windows and Linux.

- `src/OpenVerse.Common`: shared crypto, wire codec, types
- `src/OpenVerse.Api`: HTTP API server
- `src/OpenVerse.Battle`: Socket.IO battle server

## Running

For owners of the Steam client, on Windows. You need card_master from your own client (played before the end of service).

1. Download `openverse-setup.exe` and `openverse-launcher.exe` from [Releases](https://github.com/Atamol/OpenVerse/releases)
2. Run `openverse-setup.exe` and set up before connecting to OpenVerse (first time only)
3. Run `openverse-launcher.exe`, grant admin, and it starts the server (every time)
4. Launch Shadowverse from Steam (closing the game restores hosts)

Game data is not bundled, so step 2 produces it from your own client. To build it yourself, run `build-release.ps1` for a full `release/`.

## Legal

- This project is unofficial and unaffiliated with Cygames.
- Obtain your own legally acquired copy of the client.
- OpenVerse ships only original server source and protocol notes derived from observed traffic. It contains no decompiled client code and no game assets.

## License

TBD
