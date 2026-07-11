### [English Here](en/server-design.md)

# OpenVerseサーバー設計

## 構成

| サーバー | フレームワーク | 役割 |
| --- | --- | --- |
| API | C# (ASP.NET Core / Kestrel) | ログイン，マスタデータ，デッキ，ルーム管理 |
| バトル | C# (自前Socket.IO) | PvPのリアルタイム通信 |
| CDN | 静的サーバー (nginx / caddy) | アセット配信 |
| DB | SQLite (必要ならPostgreSQL) | デッキ，所持カード，セッション |

- APIとバトルは同居/分離を選べる (共通ライブラリを共有)
- バトルサーバーが要るのはPvPだけ．ソリティア (CP対戦・ストーリー・クエスト) はエンジンもAIもクライアント側で完結する

## APIハンドラ

`Program.cs`が各ハンドラに振り分けます．

- `card_master`: 全カードを所持させて配信
- `DeckHandler`: デッキ編成，大会上位デッキ紹介，スターター
- `PracticeHandler`: CP対戦のセットアップと結果記録
- `RoomHandler`: ルーム管理 (Phase 4)
- `DeckCodeHandler`: デッキコードのセルフホスト
- load/index等はスタブに動的差し込み (所持カード・スリーブ・背景ID等)

カード名/効果はクライアントのSystemTextが持つので，card_masterはテキストIDだけ返します．

## 言語

JpnとEngの2言語をHTTPヘッダ`LANGUAGE`で切り替えます．

- 大半のテキストはクライアントのSystemTextにあり，サーバーはIDだけを返す
- `LOCALE`/`REGION_CODE`は表示言語と無関係なので見なくて良い
- CDNはパスの言語トークンを見て`stubs/<lang>/`を返す
- サーバーが言語別に持つのはメール・ミッション・投票等の一部だけ (Phase 5 i18nテーブル)

## ネットワーク

各マシンでhostsの`utoongaize.shadowverse.jp`をサーバーに向けます．手段はホストとなる人が選べます:

- VPN (ポート開放が不要で身内向き)
- 固定ホスト (ポート開放やDDNSが必要)
- HTTPS固定なので，http化パッチかローカルCA (mkcert) を使う

## Docker

- 最初は`dotnet run`で直接動かす (SQLiteならDocker不要)
- 配布や常時稼働が要る段階でDocker化することを視野に入れている
