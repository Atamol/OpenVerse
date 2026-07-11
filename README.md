![](https://progress-bar.xyz/82?width=100&title=Propgress:)

### [![Discord](https://img.shields.io/badge/Discord-Join-5865F2?logo=discord&logoColor=white)](https://discord.gg/VMjWKegucJ)

### 🇬🇧 [English Here](README.en.md)

# OpenVerse

2026年7月1日 (水) 11:00にサービスが終了した，Shadowverse (Steam版) のエミュレーションサーバープロジェクト．  
サービスが終了した後もデッキ編成，CPとの対戦，ルームマッチのプレイを可能にします．

## できること

ユーザーが自前でサーバーを立てることで，以下を実現します:
- デッキの編成
- 全カードの無償化
- CP戦
- ルームマッチ

なお，
- パズル
- ランクマッチ
- ガチャ
- 課金

などその他の機能は，現時点では視野に入れていません．

## 進捗

現在はルームマッチの実装途中です．フェーズごとの進捗は[roadmap](docs/roadmap.md)から確認できます．

![](https://progress-bar.xyz/82?width=500&title=Propgress:)

- [x] プロジェクトの設計
- [x] クライアントの解析 (暗号，通信フォーマット，エンドポイント)
- [x] ホームなどのUI
- [x] 全カード，ボイス，スリーブの解放
- [x] デッキ編成
- [x] 対CP
- [ ] ルームマッチ

## 仕組み

Steamクライアントで行われていた，HTTPおよびSocket.IOでサーバーと通信する仕組みを，OpenVerseで代行します:
- APIサーバー (HTTP): ログイン，マスタデータ，デッキ情報，ルームマッチ
- バトルサーバー (Socket.IO): オンライン対戦
- 静的サーバー: ゲームアセット

リクエストとレスポンスはJSONをMessagePackで包み，AESで暗号化します．詳しくは[protocol.md](docs/protocol.md)を参照してください．  
クライアントを自前のサーバーに向けるには，hostsでホストを書き換え，http化パッチかローカル証明書を使います．

## 構成

Windows，Linuxで動作します．

- `src/OpenVerse.Common`: 共通の暗号，通信コーデック，型
- `src/OpenVerse.Api`: HTTP APIサーバー
- `src/OpenVerse.Battle`: Socket.IOバトルサーバー

## 起動

Steam版クライアントを持っていること (サービス終了前にプレイしたことがあり，card_masterが手元にあること) が前提で，Windows向けです．

1. [Releases](https://github.com/Atamol/OpenVerse/releases)から`openverse-setup.exe`，`openverse-launcher.exe`をダウンロードする
2. `openverse-setup.exe`を実行し，OpenVerseに接続する前にセットアップを行う (初回のみ)
3. `openverse-launcher.exe`を実行し，管理者権限を許可してサーバーを立ち上げる (毎回)
4. Steamからシャドバを起動する (ゲームを閉じるとhostsは元に戻る)

ゲームデータは同梱していないため，2で各自のクライアントから用意する仕組みです．自分でビルドする場合は，`build-release.ps1`で`release/`に一式を出力できます．

## 法的事項

- 本プロジェクトは非公式であり，Cygamesとは無関係です
- クライアントはユーザーが正規の手段で入手してください
- OpenVerseは，当プロジェクトで用意したサーバーのソースコードと，観測した通信から取ったプロトコルメモだけを配布します．逆コンパイルコードやゲームのアセットは含みません

## ライセンス

TBD