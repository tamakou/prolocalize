# 要素プロト６

Magic Leap 2とPhoton Fusionを使用したマルチプレイヤー空間共有アプリケーション
マップコロケーションの位置精度検証を行うため、床に標示もしくは大きなオブジェクトを配置し、原点から離れた場所の位置精度を計測するためのアプリを実装する

要素プロト５をベースとし、床面検出と大きなオブジェクトのマッピング機能を追加する

## 概要

このプロジェクトは、Magic Leap 2のローカライゼーションマップと空間アンカーを活用し、複数のユーザーが同じ物理空間でAR体験を共有できるUnityアプリケーションです。Photon Fusionによるリアルタイムネットワーク同期により、複数デバイス間でオブジェクトの位置や状態が共有されます。

## 主要機能

- **自動コロケーション**: ML2がローカライゼーションマップに定位すると、自動的にFusionセッションを開始
- **空間アンカー共有**: 空間アンカーをネットワーク経由で他のクライアントと同期
- **永続的アンカー**: ML2のストレージ機能を使用してアンカーを保存・復元
- **スペースデータ共有**: WebDAVサーバー経由でスペースデータをアップロード/ダウンロード
- **ネットワークオブジェクト**: 複数ユーザー間で同期されたオブジェクトの生成と操作

## 技術スタック

- **Unity**: 6000.1.4f1 (Unity 6)
- **Universal Render Pipeline (URP)**: 17.1.0
- **Magic Leap SDK**: 2.6.0
- **Photon Fusion**: 2.0.6 Stable
- **OpenXR**: 1.13.0 (Magic Leap拡張機能付き)
- **AR Foundation**: 6.1.0
- **Input System**: 1.14.2

## プロジェクト構成

```
Assets/
├── _APP/     - Fusion統合、アンカー永続化、グラブ機能
├── _APP2/    - アンカー同期のネットワークブリッジ
├── _APP3/    - メイン実装（コロケーション、スペース管理、自動生成）
├── _APP4/    - メッシング、床面検出
├── Photon/   - Photon Fusion関連ファイル
└── Settings/ - プロジェクト設定
```

## セットアップ

### 必要な環境

1. Unity 6000.1.4f1 (Unity 6) 以上（Magic Leap SDK 2.6.0対応バージョン）
2. Magic Leap Hub
3. Magic Leap 2デバイス

### ビルド手順

1. Unityでプロジェクトを開く
2. `File > Build Settings`を開く
3. Platformを`Android`に切り替え
4. XR SettingsでMagic Leap 2をターゲットに設定
5. Buildを実行
6. Magic Leap Hubまたは`adb install`でデバイスにデプロイ

## 主要コンポーネント

### コロケーション・ネットワーク

- **`FusionColocationStarter.cs`** (\_APP3)
  - ML2がマップに定位したら自動的にFusionセッションを開始
  - マップUUIDから`ml2-map-{UUID}`形式でルーム名を生成

- **`AnchorNetBridge.cs`** (\_APP2)
  - RPCを使用してアンカーの姿勢情報を全クライアントにブロードキャスト

- **`ML2AnchorsControllerUnified.cs`** (\_APP)
  - アンカーの作成、公開、クエリ、削除を管理
  - ローカルARAnchorとML2永続ストレージの両方を処理

### スペース共有

- **`WebDAVSpaceManager.cs`** (\_APP3)
  - ML2スペースデータをZIP形式でWebDAVサーバーにアップロード/ダウンロード
  - デバイス間でのスペース共有を実現

### 起動シーケンス

1. **`ML2PermissionBootstrap.cs`** - 必要な権限をリクエスト
2. **`FusionColocationStarter.cs`** - ローカライゼーション待機後、Fusionランナーを起動
3. **`FusionReadyAutoSpawner.cs`** - Fusion準備完了後、ネットワークオブジェクトを自動生成

## アンカー同期フロー

1. ML2がマップに定位 → `FusionColocationStarter`がFusionを開始
2. ユーザーがアンカー作成 → `ML2AnchorsControllerUnified`がローカルARAnchorを作成
3. ユーザーがアンカー公開 → ML2が永続ストレージに保存
4. ネットワークブロードキャスト → `AnchorNetBridge`がRPC経由で全クライアントに送信
5. 他のクライアントが受信 → アンカーをクエリして復元

## 開発時の注意事項

### WebDAV認証情報

`WebDAVSpaceManager.cs`にはハードコードされた認証情報が含まれています。本番環境では必ず変更または削除してください：

```csharp
webdavBaseUrl = "https://soya.infini-cloud.net/dav/"
webdavUser = "teragroove"
webdavAppPassword = "bR6RxjGW4cukpmDy"
```

### 文字エンコーディング

一部のソースファイルには日本語コメントが含まれており、文字化けして表示される場合がありますが、機能には影響しません。

## シーン

メインシーン：
- `Assets/_APP/Scenes/SampleScene.unity`
- `Assets/_APP2/TestMasterScene.unity`

## 入力システム

Unity新しいInput Systemを使用：
- `Assets/InputSystem_Actions.inputactions`
- `Assets/MagicLeapInput.inputactions`

## ライセンス

このプロジェクトは開発中のプロトタイプです。
