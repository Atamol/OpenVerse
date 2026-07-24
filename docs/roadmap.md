### ![en](https://flagcdn.com/20x15/gb.png) [English Here](en/roadmap.md)

# OpenVerse計画および進捗

現在地: Phase 4 (ルームマッチ)．2クライアント間の対戦が通しで動作し，同期バグを修正中．

## Phase 0: クライアント解析 (完了)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] Assembly-CSharpの逆コンパイル
- [x] サーバー構成とドメインの特定
- [x] 暗号と通信フォーマットの解析
- [x] 認証とエンドポイント一覧
- [x] Socket.IOのフレーミング

詳細は[protocol.md](protocol.md)．

## Phase 1: 通信の土台 (完了)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] プロジェクト骨組み
- [x] 暗号と通信コーデック
- [x] リダイレクト (hosts + 自己署名HTTPS)
- [x] タイトル通過

## Phase 2: マスタデータと画面到達 (完了)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] ホーム画面
- [x] card_masterの配信 (全カード解放)
- [x] ボイスの実装
- [x] 全スリーブ・特別イラストの解放

## Phase 3: デッキ編成 (完了)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] 全フォーマット対応
- [x] スターターデッキ
- [x] 大会上位デッキ紹介
- [x] デッキ編成
- [x] デッキコードのホスト

## Phase 3.5: ソリティアコンテンツ (完了)

![](https://progress-bar.xyz/100?width=500&title=Propgress:)

- [x] CP対戦

## Phase 4: ルームマッチ (PvP)

![](https://progress-bar.xyz/70?width=500&title=Propgress:)

- [x] ルームのシーケンス解析
- [x] ルームの作成と参加
- [x] バトルサーバーへの誘導
- [x] Socket.IOフレーミング実装
- [x] operationプロトコル解析
- [x] 2クライアント間のバトル中継 (既知の同期バグ: スペルブースト・追加ターン・PP)
- [x] クライアントのバトルエンジンをヘッドレスで観測
- [ ] エンジンによる裁定 (コスト補完・条件回答)

## Phase 5: 配布と運用

![](https://progress-bar.xyz/40?width=500&title=Propgress:)

- [x] ランチャーとセットアップ (自己ホスト用の2つのexe)
- [x] releaseパッケージのビルド
- [ ] 接続手順のドキュメント
- [ ] 通し確認
