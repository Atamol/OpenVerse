### ![en](https://flagcdn.com/20x15/gb.png) [English Here](en/server-design.md)

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

- VPN (ポート開放が不要)
- 固定ホスト (ポート開放やDDNSが必要)

APIとCDNはHTTPS固定ですが，クライアントは証明書を検証しないので自己署名証明書がそのまま通ります．クライアント改変もmkcertも要らず，証明書はランチャーが生成します．

## バトルエンジン

PvPの中継はクライアントの送信をそのまま相手へ渡すため，元サーバーが補完していた値 (コスト・条件回答・`spin`) が抜けます．そこでクライアントのバトルエンジンをヘッドレスで並走させ，同じ試合を再生して抜けた値を読みます．

エンジンは部屋ごとに2基持ちます．クライアントは受信メッセージを表示するのではなく再シミュレートするので，各クライアントの視界を個別にモデル化しないと`spin`も受信検査も出せません．

信用の度合いは`release/server/engine.txt`で段階的に上げます (`Observe` → `AdviseCost` → `AnswerBlanks`)．既定は`AnswerBlanks`です．`AdviseCost`以下では条件への回答が注入されず，リーダーに付いたPP増加などが受け手側だけ発動しません．原因の調査結果と残る限界は[desync.md](desync.md)を参照してください．

## 配布

- ランチャーとセットアップの2つのexeで配ります．setupは昇格不要で，launcherが管理者権限でhosts書き換えとサーバー起動を行います
- 自己ホストは`dotnet run`でも動きます．SQLiteなのでDBサーバーは要りません
