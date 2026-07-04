# OpenVerseサーバー設計

## サーバー構成

| サーバー | 言語・技術 | 役割 |
| --- | --- | --- |
| API | C# (ASP.NET Core / Kestrel) | ログイン，マスタデータ，デッキ，ルーム管理．HTTP + AES + MsgPack |
| バトル | C# (自前Socket.IO) | リアルタイム通信．サーバー権威のoperation列 |
| CDN | 静的サーバー (nginx / caddy) | アセットの配信 |
| DB | SQLite，必要ならPostgreSQL | デッキ，所持カード，セッション |

- APIとバトルは1プロセス同居と分離を後から選べ，共通ライブラリ (暗号 / 型 / MsgPack / DB) を共有します
- 山場は自前のSocket.IOフレーミングで，BestHTTPの実際のフレームに1:1で合わせます (pollingの`[type][長さ][0xFF]`，websocket昇格，`{_placeholder,num}`と先頭`0x04`の生バイナリ添付，Pingは2000/5000ms固定)

## 言語

クライアントは9言語 (Jpn, Eng, Kor, Chs, Cht, Fre, Ita, Ger, Spa) を持ちますが，OpenVerseではJpnとEngの2つを扱います．

- HTTPヘッダ`LANGUAGE`で判定します．未対応の値はJpnに寄せます
- 同時に届く`LOCALE`はクライアント側で常に`"Jpn"`固定なので無視．`REGION_CODE`はSteamのストア国コードで表示言語とは無関係なので同じく無視します
- CDNのパスに言語トークンが入る (例: `/dl/Manifest/<Ver>/Jpn/Windows/manifest_assetmanifest`) ので，そこから抜き取って`stubs/<lang>/manifest/<name>`を返します
- テキスト言語と音声/動画言語は独立ですが，OpenVerseでは音声を扱わないので`LANGUAGE` (テキスト) だけ見ます

### ローカライズが必要な範囲

クライアントの`SystemText` (ローカルJSON辞書) がエラー文言・メンテナンス表示・カード名/効果の大半を持っており，サーバーはIDを返せば済みます．

サーバー側で言語別テキストを持つ必要があるのは以下です:

- Mailの`message`
- Mission / Achievementの`name`
- Login bonusキャンペーン`name`
- Vote系 (`vote_name`, `title_text`, `tweet_text`)
- Quest / event帯ラベル`name`
- Boss rushの`name` / `skill_text`

いずれもルームマッチとデッキ編集では触れません．Phase 5のメール/アナウンス実装時にi18nテーブル (`table_i18n(id, lang, ...)`) で持ちます．

### 実装コスト

コード側は20行程度です (ヘッダ読取 + パスから言語トークン抽出 + stubフォルダ分岐)．

手間はENアセット (`master_cardnametextmaster.unity3d`など) の入手にかかっています．公式CDNは死んでいるため，EN版を長くプレイした人のローカルキャッシュ (`AppData/LocalLow/Cygames/Shadowverse/a/`) を確保する必要があります．

## ネットワーク

接続手順は，サーバーの運用者が選べ，各マシンでhostsの`utoongaize.shadowverse.jp`をサーバーに向けます．
- VPN (ポート開放が不要で，カジュアルな身内対戦に向いている)
- 固定ホスト (ポート開放やDDNSが必要)
- クライアントはHTTPS固定なので，http化パッチ (`SetScemeMode(Https) -> Http`) かローカルCA (mkcert) を使います．身内対戦等であれば，配布はパッチ済みクライアントとhosts一行で済みます

## Docker

- 最初は`dotnet run`で直接動かします (DBがSQLiteならDockerは不要です)
  - Postgresにする場合，DBのみcomposeにします
- 配布や常時稼働などが必要になった段階で全体をDocker化する予定です