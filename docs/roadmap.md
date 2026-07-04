# OpenVerse 計画および進捗

現在地: Phase 1

## Phase 0: クライアント解析 (完了)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] Assembly-CSharpの逆コンパイル (Unity 2020.3 Mono，名前空間Wizard)
- [x] サーバー構成とドメイン (API/CDN/Node/DeckBuilder)
- [x] 暗号CryptAES (AES-256-CBC，API系とNode系)
- [x] シリアライズ (JSON -> MessagePack -> AES)
- [x] 認証 (Steam ticket + viewer_id) とエンドポイント一覧
- [x] Socket.IOのバージョン (URLはEIO=4だがv3 framing + v2バイナリの非標準混在)
- [x] APIボディの包み方 (`_createBodyMsgpack`: JSON -> MsgPack -> AES)

詳細は[protocol.md](protocol.md)にあります．

## Phase 1: 通信の土台と初回接続 (CURRENT)

![](https://progress-bar.xyz/66?width=500&title=Propgress:)

- [x] 骨組み (Common / Api / Battle)
- [x] 暗号ライブラリ`WireCrypto` (AES 2系統)
- [x] MessagePack統合
- [x] 通信コーデック`WireCodec` (リクエスト復号，レスポンス暗号化)
- [x] 受信機 (ヘッダとボディをログ，リクエストをJSONに復号，タイトルチェックにスタブ応答)
- [ ] リダイレクト (hosts + http化パッチ) で実クライアントを受信機に流す
- [ ] 実トラフィックで暗号とヘッダを確かめる
- [ ] 起動チェックに応答してタイトルを抜ける

## Phase 2: ホームまで

![](https://progress-bar.xyz/0?width=500&title=Propgress:)

- [ ] マスタデータ配信 (全カード所持で返す)
- [ ] ホーム画面に到達
- [ ] デッキ編集の保存

## Phase 3: ルームマッチ

![](https://progress-bar.xyz/0?width=500&title=Propgress:)

- [ ] 本編APIとルームのシーケンス解析 (`Wizard.RoomMatch`)
- [ ] ルームの作成と参加
- [ ] マッチ応答で`node_server_url`を返してバトルサーバーへ誘導

## Phase 4: バトル

![](https://progress-bar.xyz/0?width=500&title=Propgress:)

- [ ] 自前のSocket.IOフレーミング (v3 payload + v2バイナリ添付)
- [ ] operationプロトコル解析 (サーバー権威か，カード効果の所在)
- [ ] バトルエンジン (ターン進行，進化，カード効果．shadow_sim参考)

## Phase 5: 配布と運用

![](https://progress-bar.xyz/0?width=500&title=Propgress:)

- [ ] Docker化 (検討中)
- [ ] 接続手順のドキュメント
- [ ] 通し確認
