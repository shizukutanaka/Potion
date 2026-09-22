# Potion.Service

Windows 向け自己修復サービス。システムの健全性を監視し、検知した問題を自動で修復します。

## 構成

- `Program.cs` — Generic Host + WebHost のエントリポイント（Windows サービスとして動作）。Serilog を `appsettings` から配線し、DI 登録の起動時検証（`ValidateOnBuild`）を有効化
- `Startup.cs` — DI 登録、OpenTelemetry（metrics + tracing）、SignalR ハブ、ミドルウェア構成
- `Infrastructure/` — 監視・修復・メトリクス・レポート等の基盤サービス
- `Scheduling/` — 修復タスクのスケジューリング（`RemediationScheduler`、イベント駆動修復）
- `Remediation/` — 修復タスクの実行（`RemediationTaskExecutor`）
- `Options/` — 構成オプション（`IOptions<T>` でバインド、環境変数で上書き可能）
- `Resources/` — ローカライズ文字列リソース（30+ 言語の `.resx`）
- `wwwroot/` — 静的ダッシュボード（`UseStaticFiles` で配信）

## エンドポイント

- `/health` — 死活監視プローブ（200 `Healthy`）
- `/metrics` — Prometheus スクレイプ用メトリクス
- `/collaboration` — SignalR ハブ

## ビルド

```sh
dotnet build
```

警告は `TreatWarningsAsErrors` によりビルドエラーとして扱われます。

## 実行

開発時は `dotnet run`（このディレクトリで実行可）。本番では Windows サービスとしてインストールして実行します。修復実行系サービスは `FeatureFlags:RepairExecutionEnabled`（既定 OFF）で有効化します。
