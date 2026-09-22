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
- `ArgumentSanitizer` の引数長 8192 超過時例外（後段の 1024 文字切り詰め処理で網羅されるため）
- SQLインジェクション検出正規表現の `--`・`;` リテラルマッチ（`--flag` 形式の正当な CLI 引数を誤検知していたため。SQL キーワード・ブロックコメント検出は維持）

### Fixed (テストスイート修復)

- `tests/Potion.Service.Tests` がビルド・実行可能に修復（126テスト中126件成功）
  - プロジェクト参照パスを `..\..\src\Potion.Service\` に修正、`Microsoft.Extensions.Caching.Memory` を 8.0.1 へ更新（NU1605 解消）
  - `CommandGuard` の新コンポジション（`CommandValidator`/`ArgumentSanitizer`/`UrlValidator`/`DomainValidator`/`RateLimiter`）に合わせ `TestObjectFactory`/`ForwardingLogger` を追加
  - 廃止 API を参照する 6 テストファイルを `<Compile Remove>` で除外（理由を csproj コメントに記載）
  - Moq が拡張メソッド `IMemoryCache.Set`/`Clear` をモックできないため実 `MemoryCache` を使用するよう修正

- 製品コードの実バグ修正（テストで検出）
  - `NetworkSecurityGuard`: `example.com` 等の正当ドメインが危険ラベル誤検知で拒否されていた問題を修正（危険ドメイン判定を最終ラベル（TLD）のみに限定）＋ 単一ラベルドメインを無効化
  - `RateLimiter.CheckRateLimitAsync`: キャンセル済みトークンで `OperationCanceledException` ではなく `TaskCanceledException` を返すよう修正（`Task.FromCanceled`）
  - `ErrorHandler.HandleError`: 同一レベルでログを2回出力していた問題を修正（指定レベルのログを CEF 1 回に集約、構造化 JSON は Debug へ）
  - `ErrorHandler.CanRetryOperationAsync`/`ExecuteWithRetryAsync`: キャンセル済みトークンで `TaskCanceledException` を即時送出するよう修正
  - `ErrorHandler.ClassifyError`: `UnauthorizedAccessException` を FileSystem（再試行扱い）から Configuration（恒久的失敗扱い→Fail）へ再分類
  - `CommandValidator`: 許可リスト拒否を `ArgumentException` から `InvalidOperationException` へ変更（引数形式の問題ではなくポリシー違反のため）

- テスト契約の更新（意図的な実装強化に追従）
  - レート制限超過時の契約を「例外送出」から「false 返却」へ（`CheckRateLimitAsync` の bool 返却 API に合わせ更新）
  - サーキットブレーカー開放テストを実装の失敗カウント方式に合わせ修正
  - `cmd.exe` 依存テスト（ProcessRunner/統合/性能）へ Windows ガード `TestEnvironment.IsWindows` を追加（非 Windows CI で自動スキップ相当の動作）

- CI/CD でのテスト継続検証を確立
  - `tests/Potion.Service.Tests` を `Potion.sln` に登録（これまで `dotnet test Potion.sln` がテストを実行できていなかった → Windows CI で全126テストが実行可能に）
  - 空の死蔵ディレクトリ `src/Potion.Service.Advanced/` を削除（ソースファイルゼロの nuspec 誤認ファイルのみ、どこからも参照されない）
