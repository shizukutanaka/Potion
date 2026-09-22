# Changelog

## Unreleased

### Fixed

- `src/Potion.Service` がコンパイルエラー 0 件でビルドできるように修正（340+ 件のコンパイルエラーを解消）
  - `Program.cs` を新規作成（エントリポイント CS5001 を解消）
  - Polly 8 API へ移行（`OnTimeout` / `OnOpened` / `OnRejected`、Timeout は `args.Timeout`）
  - `ResiliencePipelines` の Chaos Engineering ブロックを削除（対応パッケージ `Polly.Simmy` が nuget.org に存在しないため。`Polly.Contrib.Simmy` は旧 Polly API 向けで非互換）
  - バルクヘッドを `Polly.RateLimiting` + `ConcurrencyLimiter`（PermitLimit 4 / QueueLimit 10）で実装
  - ML.NET 5.0 の API 差異を修正（`DetectIidSpike` transform、`RandomizedPca`）
  - 重複型の解消（`RemediationTaskDescriptor` → `Potion.Service.Remediation` に統一、`UserSession` → `Potion.Service.Hubs`）
  - nullable 参照型の警告（CS8600/8602/8603/8604）を `!`・`?? string.Empty`・シグネチャの nullable 化で解消
  - 不足メンバーの追加（`ProcessExecutionResult.Success/Output/Error`、`LogErrorStatistics.CriticalErrorCount`、`NuGetAuditReport.CriticalVulnerabilities`、各 Options クラスの欠損プロパティ等）
  - `System.Data.SqlClient` → `Microsoft.Data.SqlClient`、`Microsoft.VisualBasic.Devices.ComputerInfo` → WMI、`GCSettings.IsServerGC` 代入（読み取り専用）の削除、`ReadAsync` → `ReadExactlyAsync` 等の API 修正

### Added

- `Polly.RateLimiting` 8.8.0 パッケージ参照（バルクヘッド実装に必要）
- `Microsoft.Extensions.Hosting.WindowsServices` 8.0.1（`UseWindowsService` に必要。本製品は Windows サービスとして動作するため）
- `src/Potion.Service/README.md`（csproj の `<None Include>` が参照）
- `src/Potion.Service/Properties/AssemblyInfo.cs`（`SupportedOSPlatform("windows")` — CA1416 抑止）
- `src/Potion.Service/Infrastructure/ServiceSupportTypes.cs`、`SystemHealthMonitoring.cs`

### Changed

- `.gitignore` に `bin/` `obj/` を追加し、誤って追跡されていた生成物 66 ファイルを Git 管理から除外

### Removed

- Chaos Engineering レジリエンス設定（上記パッケージ非存在のため）
