# Potion.Service

Windows 向け自己修復サービス。システムの健全性を監視し、検知した問題を自動で修復します。

## 構成

- `Program.cs` — Generic Host + WebHost のエントリポイント（Windows サービスとして動作）
- `Startup.cs` — DI 登録、OpenTelemetry、SignalR ハブ、ミドルウェア構成
- `Infrastructure/` — 監視・修復・メトリクス・レポート等の基盤サービス
- `Options/` — 構成オプション（`IOptionsMonitor` 経由）

## ビルド

```sh
dotnet build src/Potion.Service/Potion.Service.csproj
```

警告は `TreatWarningsAsErrors` によりビルドエラーとして扱われます。

## 実行

開発時は `dotnet run --project src/Potion.Service`。本番では Windows サービスとしてインストールして実行します。
