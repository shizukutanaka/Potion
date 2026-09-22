# Changelog

## Unreleased

### Removed (認証なしの `UseAuthorization` ミドルウェア)

- `app.UseAuthorization()` を削除 — `AddAuthentication`/`AddAuthorization` 登録も `[Authorize]` 属性も存在せず、何もしないデッドミドルウェアだった
- `src/Potion.Service/README.md` を実態へ更新 — `Scheduling/`・`Remediation/`・`Resources/`・`wwwroot/` を構成節に追加、エンドポイント一覧（`/health`・`/metrics`・`/collaboration`）を新設、`dotnet run` は simple.csproj 削除により `--project` 不要になった点と修復フラグを記載
- 検証: 0警告0エラー

### Fixed (オプションが設定バインドされていなかった問題 — 監視サービスの閾値を調整可能に)

- `AddOptions<T>()` 単独呼出し（5件）を `Configure<T>(GetSection(...))` へ変更 — Options クラスは `SectionName`/Enabled・閾値等を持つ設計だったが**設定が一切バインドされておらず**、コード既定値のみで動作し運用者の調整手段がなかった（RemediationPolicyOptions と同じ「バインド忘れ」クラス）
- 対象: `MemoryMonitorOptions`・`PerformanceOptimizerOptions`（`T.SectionName` 使用）＋ `Collaboration`/`EventCorrelation`/`Compliance`（サービス名セクション）
- 動作変化: セクション未定義時は従来どおり既定値。**`MemoryMonitor__MonitoringIntervalSeconds=60` 等の環境変数で再起動不要の調整が可能に**。`EventCorrelation__Enabled=true` で同サービスの起動を実機確認済み
- 検証: 0警告0エラー・起動実確認

### Removed (プロジェクトの死残滓2件)

- `src/Potion.Service/Potion.Service.simple.csproj` 削除 — 本物の csproj と同居する Worker SDK の雛形プロジェクト（Hosting/Logging のみ参照）で、ソリューション未所属・全く参照されない。このファイルの存在が `dotnet run`/`dotnet build` で `--project` 指定を必須にしていた（同ディレクトリの複数プロジェクト曖昧性）。削除で単純な `dotnet build` が通る
- `src/Potion.Service/ocelot.json` 削除 — Ocelot API ゲートウェイのルーティング設定だが、Ocelot パッケージも `AddOcelot`/`UseOcelot` 登録も存在せず**一度もロードされていない**死設定。内部ルートは不存在の `/api/health` を指す二重の死残滓
- 検証: ソリューションビルド 0警告0エラー・`src/Potion.Service` での `dotnet build` 単独実行成功を確認

### Added (RemediationScheduler のユニットテスト — 131→134)

- `Scheduling/RemediationSchedulerTests` 新規3件 — 予防修復スケジューラ（PR #13 で追加）の振る舞いを直接検証:
  - `ScheduleTaskAsync(null)` が `ArgumentNullException` を投げること
  - 期限超過タスクが即時 `IRemediationTaskExecutor` へディスパッチされ、`RemediationTaskDescriptor`（Name/Command/Enabled）へ正しく変換されること
  - 未来時刻タスクが指定時刻前に実行されないこと（遅延スケジューリングの確認）
- 検証: 0警告0エラー・134/134テスト

### Added (ヘルスプローブ `/health`)

- `AddHealthChecks` + `MapHealthChecks("/health")` を追加 — Dockerfile の `HEALTHCHECK` が不存在エンドポイントを指して削除済みだった経緯があり、稼働中サービスに死活監視用エンドポイントが存在しなかった。`GET /health` は 200 `Healthy` を返す（ASP.NET Core 標準機構・新規パッケージ不要）
- 検証: 0警告0エラー・起動後 `/health` 200、`/metrics`・`/collaboration` 併存確認
- `.gitignore` に `logs/`・`*.log` 追加 — Serilog 開発シンクがリポジトリ相対 `logs/` に出力するようになったため

### Added (IRemediationScheduler 実装 — 予防修復ループの完成)

- `Scheduling/RemediationScheduler.cs` 新規実装 — `PredictiveRemediationService` が依存する `IRemediationScheduler` の実装が存在しなかったため、予知修復がフラグ配下でも起動できなかった問題を解消:
  - `Channel<RemediationTask>` ベースの遅延実行キュー — `ScheduleTaskAsync` で受け取ったタスクを指定時刻（`task.Schedule`）まで待機し `IRemediationTaskExecutor` へディスパッチ
  - `EventDrivenRemediationService` と同じ ad-hoc `RemediationTaskDescriptor` 生成規約に準拠
  - `IRemediationScheduler` と `IHostedService` を同一シングルトンで提供（キュー状態の一元化）
- `PredictiveRemediationService` を FeatureFlags ブロックへ追加 — これで修復実行ティア全4サービスが `FeatureFlags:RepairExecutionEnabled` で起動可能に
- DI 回帰テスト更新 — フラグ ON で `PredictiveRemediationService`・`IRemediationScheduler`→`RemediationScheduler` の解決を検証
- 検証: 0警告0エラー・131/131テスト・フラグ ON 実起動で全5ホステッドサービス（スケジューラ含む）の起動ログ確認

||||||| parent of d913619 (chore: delete unreachable self-referencing service cluster (16 files))
### Removed (到達不能の自己参照クラスタ — 16ファイル・約7,000行)

- 厳密なアンカー解析（Startup・Program・テスト起点＋ライブサービスの推移参照）で到達不能確定の16ファイルを削除。クラスタ内部で相互参照するだけで、DI 登録・テスト・稼働中サービスのいずれからも消費されていなかった:
  `ZeroTrustSecurityService`/`ARVRMonitoringInterfaceService`/`AdvancedAlertSystem`/`ConfigurationManager`/`GarbageCollectionService`/`GitOpsService`/`MetricsCollector`/`MobileOptimizationService`/`PerformanceOptimizationService`/`ResourcePressureMonitor`/`RootCauseAnalysisService`/`SecurityAuditor`/`TelemetryIntegrityService`/`RemediationTaskCatalog`/`SecurityAuditOptions`/`TelemetryRetentionOptions`
- ライブ利用型の移設（HealthStatus と同規約）: `PressureLevel`・`AlertSeverity` enum → `SystemHealthMonitoring.cs`、`RemediationTaskDescriptor` record → `RemediationTaskExecutor.cs`
- `AutoRecoveryManager` の死パスを簡素化 — `GetService(typeof(ISecurityAuditor))`/`GetService(typeof(IConfigurationManager))` のオプショナル動的参照は実装も登録も存在せず常に null だった（削除対象型）。`CheckSecurityHealth` は `return true`、`ResetConfigurationAsync` は warning ログ付き `return false` に等価固定し、`GenerateDefaultConfiguration`・未使用 `_serviceProvider` 依存を除去
- 削除後検証: 0警告0エラー・131/131テスト — 実行時動作への影響なし（全て未到達パス）

||||||| parent of 475edd9 (chore: remove last consumer-less registration (hot-reload service) and dead flag keys)
### Removed (最後の消費者ゼロ登録 — ConfigurationHotReloadService + 死フラグキー)

- `IConfigurationHotReloadService`/`ConfigurationHotReloadService` の登録と実装（241行）を削除 — API 全6メンバ（`GetFeatureFlagAsync`/`SetFeatureFlagAsync`/`IsFeatureEnabledAsync`/`GetAllFeatureFlagsAsync`/`GetConfigurationSnapshotAsync`/`OnConfigurationChanged`）に呼出元ゼロ。30秒タイマーで設定変更を監視していたが、イベント購読者も存在しない「動いているが誰も見ていない」最後の死登録
- `FeatureFlags` の死キー11件を削除 — 実消費は `RepairExecutionEnabled` のみ（`AdvancedCorrelationAnalysis`/`MachineLearningIntegration`/`RealTimeMonitoring`/`PredictiveMaintenance`/`AutoScaling`/`MultiTenant`/`CloudIntegration`/`APIGateway`/`SecurityHardening`/`PerformanceMonitoring`/`ComplianceReporting` は唯一の消費者であるホットリロードサービス削除で全て未バインド化）
- `appsettings.simple.json` を削除 — `appsettings.{Environment}.json` の環境命名規約にも `AddJsonFile` にも合致しない未ロード設定残滓（内部の `Potion:` セクションも未バインド）
- 検証: 0警告0エラー・131/131テスト

||||||| parent of b980600 (fix: wire Serilog so configured sinks/enrichers actually run)
### Fixed (Serilog の実配線 — 宣言のみだった構造化ログの有効化)

- `Program.cs` に `.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration))` を追加 — Serilog パッケージと `Serilog` 設定セクションは存在したが `UseSerilog` が未呼出で、全てのログは Serilog を経由していなかった（wwwroot と同型の「設定済み・未配信」）。Console/File/EventLog シンク・エンリッチャー・`ServiceVersion` プロパティが全て初めて動作
- パッケージ変更（追加6件＋升級4件 — appsettings が宣言する機能を実際に動作させるために必要。Serilog 3.x 系では `Sinks.EventLog`/`Sinks.Console 6.x` と非互換のため **Serilog 4.3.1 系で一式整合**）:
  - 升級: `Serilog` 3.1.1→4.3.1、`Sinks.File` 5.0.0→6.0.0、`Extensions.Logging` 8.0.0→9.0.2
  - 追加: `Sinks.Console` 6.0.0、`Sinks.EventLog` 4.0.0、`Extensions.Hosting` 9.0.0（`UseSerilog`）、`Settings.Configuration` 9.0.0（`ReadFrom.Configuration`）、`Enrichers.Environment`/`Process`/`Thread`（宣言済み `WithMachineName`/`WithProcessId`/`WithThreadId`）
- 設定内の実バグ修正:
  - `otedama-.log` → `potion-.log`（全3環境 — 別プロジェクト名の残滓）
  - `%ProgramData%` → `C:/ProgramData/Potion/logs/`（環境変数プレースホルダは Serilog 設定で展開されず、リテラルディレクトリが作られていた）
  - 死 `Override` キー削除: `Potion.Service.Controllers`（コントローラ不存在）、`Potion.Service.Infrastructure.Security`/`Performance`（名字空間不存在）
  - `ServiceVersion` `2.0.0`/`2.0.0-dev` → `1.0.0`/`1.0.0-dev`（csproj 無指定＝実アセンブリバージョンと一致させた）
- 開発環境のログパスはリポジトリ相対 `logs/dev-potion-.log`（非 Windows でも書込み可能）
- 検証: 0警告0エラー・131/131テスト・起動時に Serilog テンプレート出力＋ファイルシンクによる `dev-potion-*.log` 実生成を確認

### Added (修復実行ティアの FeatureFlags ゲート付き有効化)

- 修復実行系サービスを `FeatureFlags:RepairExecutionEnabled`（既定 OFF）で登録可能に — 運用者が設定変更のみで製品中核の自律修復ループを起動できるようになった（コード変更不要）。フラグ ON で以下が起動:
  - `AutoRecoveryManager`（ヘルスチェック失敗時の自律復旧アクション実行）
  - `PerformanceOptimizer`（メモリ・パフォーマンス最適化）
  - `EventDrivenRemediationService`（イベント駆動修復トリガー）
  - 依存登録: `IProcessRunner`→`ProcessRunner`、`IRemediationTaskExecutor`→`RemediationTaskExecutor`、`RemediationPolicyOptions` バインド
- `PredictiveRemediationService` は `IRemediationScheduler` の実装が存在しないため登録対象外（実装追加まで温存）
- 有効化方法: `appsettings.json` で `FeatureFlags:RepairExecutionEnabled: true`、または環境変数 `FeatureFlags__RepairExecutionEnabled=true`
- DI 回帰テストを2件追加（フラグ OFF で未登録・フラグ ON で全登録が `ValidateOnBuild` 下で解決可能を検証）— テスト総数 129→131
- 検証: 0警告0エラー・131/131テスト・フラグ ON での起動実確認（全修復サービスの起動ログと自律修復アクション実行を確認）

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

### Fixed (ビルド警告ゼロ化・フレイキーテスト修正)

- MSBuild 警告を全件解消（ソリューション再ビルドで警告 0 件を確認）
  - `Potion.Service.csproj`: 存在しない `MinimumRecommendedRules.ruleset` への `<CodeAnalysisRuleSet>` 参照を削除（NU パッケージ版アナライザと二重構成になっていたため）
  - 実行時未使用の `Microsoft.CodeAnalysis.NetAnalyzers` 8.0.0 と `Microsoft.SourceLink.GitHub` 8.0.0 の PackageReference を削除（CI でのソースリンク生成対象外・依存削減）
  - `ResiliencePipelines.CreateRemediationPipeline` から未使用の `enableChaos` パラメータを削除（Chaos 実装削除の残滓。唯一の呼び出し元 `Startup.cs` はロガーのみ渡していたため外部影響なし）
- テストプロジェクトの警告も整理: `<Nullable>annotations</Nullable>` 追加（CS8632 解消）＋ `<NoWarn>CA1416;CS1998</NoWarn>`（製品が Windows 専用のため CA1416 は想定内、async テスト骨格の CS1998 も設計意図どおり）
- `PerformanceTests`: コールドスタート時に Compiled 正規表現/JIT 初期化コストが性能アサーション（0.1ms/回 等）を超過し偶発失敗していたフレイキーを、計測前ウォームアップ（正規表現ベースの3テスト）で根本原因から修正

### Removed (デッドテスト資産の削除)

- 廃止API向けの除外テスト6ファイル（計 ~1,900行・89テスト相当）を削除
  - `LocalizationTests.cs`: `HealthController`/`LocalizedString` が実装から削除済み（`InternationalizationService` は `InternationalizationServiceTests.cs` が継続カバー）
  - `BillingTests.cs`/`PerformanceOptimizerTests.cs`/`SystemHealthMonitorTests.cs`/`RemediationSchedulerTests.cs`/`SecurityAndPerformanceTests.cs`: 対象サービスが全て DI 未登録の到達不能コードであり、削除済み・改名済みメンバを参照しコンパイル不能。`SecurityAndPerformanceTests` の `CommandGuardTests`/`PerformanceTests`/`IntegrationTests` は同名クラスとして現行のコンパイル済みテストが網羅済み
  - 判断基準: DI 登録がないサービス = 実行パスに到達しないため、テスト復元は維持コストのみ増大する無駄（利用者が実際に使わない機能）
- 不存在 `TestData` ディレクトリへの `<None Update>` エントリを csproj から除去

### Removed (製品側デッドコードの削除)

- DI 登録・テスト参照のいずれからも到達不能なサービス群 **81 ファイル・約 37,700 行** を削除
  - 到達性解析（DI 登録型＋テスト参照型からの推移閉包）で算出したデッド集合を `git rm` し、全削除後に `dotnet build` が 0 警告・0 エラー・テスト 126/126 成功を維持することを検証済み
  - 削除対象: 未登録サービス群（Billing/Performance/SystemHealthMonitor/RemediationScheduler/SecurityAuditor/Backup 等）、未使用の `MachineLearning/`/`PersonalPC/`/`Remediation/`/`Scheduling/`/`Security/`/`Storage/`/`Updates/`/`Compliance/`/`Configuration/` ディレクトリ全体、孤立 Options 4 件
  - 判断基準: 到達不能コードは「利用者が使わない機能」であり維持コストのみ発生するため、リファクタリング条件（重複削除・保守性向上）に基づき削除

### Added (ベンチマーク基盤の実用化)

- `tests/Potion.Service.Benchmarks/` に最小 csproj + エントリポイントを新規作成し、CI の `Run benchmarks` ステップ（`dotnet run --project`）が実際に実行可能に
  - `BenchmarkDotNet` 0.14.0 を追加（ライブラリ追加の理由: CI にベンチマークステップが既存だが csproj 不在で未実行だったため設計どおりの有効化。性能リグレッションをセキュリティホットパスで検出する標準的手法）
  - ベンチマーク対象を現行APIに全面書き換え: `CommandGuard.EnsureCommandIsAllowed`/`SanitizeArguments`/`IsValidUrl`（全コマンド実行時に通るセキュリティホットパス）＋ `RateLimiter.CheckRateLimitAsync`。旧対象（SystemHealthMonitor/廃止CommandGuard API/TelemetryRetentionService）は全て廃止・削除済みAPIだった
  - `SimpleJob` のランタイムモニカー未指定化（`Net80` 固定は .NET 9 環境で実行不可のため、ホストランタイム自動解決で環境非依存に）
  - `System.Security.Cryptography.Pkcs` 9.0.13 をローカル参照（製品の明示 8.0.1 と SqlClient 7.0.3 推移要件 >=9.0.13 の不整合を解消）
  - 検証: `--filter "*EnsureCommandIsAllowed*" --job dry` で実計測成功（約29.5ns/op・168B割当）
- `.gitignore` に `BenchmarkDotNet.Artifacts/` を追加（実行成果物の誤コミット防止）

### Removed (死設定の削除)

- appsettings 監査で未バインド設定セクションを削除（運用者が変更しても何も起きない silent no-op の解消）
  - `appsettings.json`: `LogCompression`/`Backup`/`Report`/`PerformanceOptimizer`/`SystemDiagnostics`/`Security`/`CorrelationAnalysis`/`MachineLearning`/`NetworkSecurity`/`HealthCheck`/`Observability`/`Metrics`/`CsrfProtection`/`ZeroTrust` の14セクション（`GetSection`/`Configure`/`AddOptions`/`Bind` のいずれにも到達しないことを確認）
  - `appsettings.Development.json`: `CorrelationAnalysis`/`MachineLearning`/`Security`/`Metrics`
  - `appsettings.Production.json`: `Security`/`NetworkSecurity`/`CorrelationAnalysis`/`MachineLearning`/`HealthCheck`/`Metrics`/`PerformanceOptimizer`/`Compliance`
  - 維持したセクション: `Serilog`/`AllowedHosts`/`Kestrel`（フレームワーク消費）、`RemediationPolicy`/`TelemetryRetention`/`FeatureFlags`（`GetSection` で実バインド確認済み）
  - `Options/*.cs` の `SectionName` 定数は未使用だがクラス自体は存続コードが利用するため温存
- ルート `appsettings.Personal.json` を削除 — csproj はプロジェクトディレクトリ内の `appsettings*.json` のみコピーするため、リポジトリルートのこのファイルは一切ロードされない設定残滓

### Fixed (ランタイム監査で検出した実バグ)

- `CollaborationService` を DI 登録（`services.AddSingleton<CollaborationService>()` + `AddOptions<CollaborationOptions>()`）— `/collaboration` にマップされた `CollaborationHub` が生成時に同クラスを注入するが未登録だったため、SignalR 接続のたびに DI 解決失敗していた実バグを修正
- ランタイム監査の補足所見（要判断事項として報告）: `AutoRecoveryManager`/`MemoryMonitor`/`PerformanceOptimizer`/`PredictiveRemediationService` 等の `BackgroundService`/`IHostedService` 実装が存在するが `AddHostedService` 登録がゼロのため、自己修復監視ループは現状起動しない設計。また `wwwroot/`（index.html/styles.css/dashboard.js）は `UseStaticFiles` 未呼び出しで配信されない

### Fixed (起動検証で検出した実バグ群) — 初のランタイム起動成功

- DI ビルド時検証を有効化（`DefaultServiceProviderFactory` + `ValidateOnBuild`/`ValidateScopes`）— 登録サービスの依存解決失敗を起動時に fail-fast で検出する再発防止
- `services.AddHttpClient()` を追加 — 検証により `ServiceMeshService` が未登録の `IHttpClientFactory` を注入している潜伏バグを発見・修正
- `app.UseStaticFiles()` を追加 — `wwwroot/` のダッシュボード（index.html/styles.css/dashboard.js）が配信されるよう有効化
- Kestrel 証明書設定の実バグ修正 — ベース/開発設定の `Https` エンドポイントは証明書 `Path` が空で全環境で起動失敗していたため HTTP のみに限定。本番設定の `Password` は `${VAR:?}` シェル構文で .NET が展開しない無効プレースホルダだったため除去（本番では環境変数 `Kestrel__Endpoints__Https__Certificate__Password` で注入する方式に）
- **初のランタイム起動検証成功**（macOS・Development 環境）: `dotnet run` で `Now listening on: http://localhost:5000` / `Application started` まで到達し、実 HTTP レスポンスを確認 — `index.html`/`swagger`/`metrics`/`/collaboration/negotiate` が全て 200（SignalR Hub の DI 修正が実行時にも検証済み）

### Added (自己修復監視ループの有効化)

- 観測・レポート専用の BackgroundService を `AddHostedService` で有効化 — これまで自己修復監視ループは一度も起動していなかった（`AddHostedService` 登録ゼロ）
  - `MemoryMonitor`（メモリ統計・GC/ワーキングセット最適化）、`AnomalyDetector`（MLベース異常検知）、`EventCorrelationService`（イベント相関）、`ComplianceReportService`（コンプライアンスレポート）
  - 依存 `ISystemHealthMonitor`→`SystemHealthMonitor` と各 `IOptions`/`IOptionsMonitor` を登録
- 起動検証: MemoryMonitor/AnomalyDetector が起動ログを出力し、EventCorrelation/Compliance は各オプションの `Enabled=false` で適切に待機 — 観測ループのみ動作
- 修復実行系（`AutoRecoveryManager`/`PerformanceOptimizer`/`PredictiveRemediationService`/`EventDrivenRemediationService`/`RemediationTaskExecutor`）は OS 修復を自律実行するため未登録のまま温存 — 有効化は明示的な製品判断が必要

### Removed (デッドAPI面の整理 — ライブラリ削除を伴うため説明)

- コントローラがリポジトリ内に1つも存在しないため、MVC/Swagger/APIバージョニングの設定一式を削除 — `AddControllers`/`AddEndpointsApiExplorer`/`AddSwaggerGen`/`AddApiVersioning`/`UseSwagger`/`UseSwaggerUI`/`MapControllers` は全て無効構成であり、起動時の「No action descriptors found」警告の根本原因だった
- パッケージ削除（ライブラリ削除の説明）: `Swashbuckle.AspNetCore` 8.1.4 と `Microsoft.AspNetCore.Mvc.Versioning` 5.1.0 — エンドポイント0件の API 面のみに使用されており未使用化。将来コントローラ追加時は csproj + Startup に再追加するだけで復元可能
- 維持: SignalR Hub（`/collaboration`）と Prometheus `/metrics` は存続し全て 200 を確認。`swagger` エンドポイントは空の API を文書化していただけのため廃止

### Removed (到達不能サービスの最終整理)

- 到達性解析（DI登録型＋テスト参照型を起点に型参照の推移閉包）で到達不能と確定した18ファイルを削除 — `AdvancedAuthenticationService`/`DeviceTrustService`/`NotificationService`/`RateLimitingService`/`RemoteManagementClient`/`SecureCommunicator`/`SecurityPolicyEngine`/`TenantService`/`UserBehaviorAnalyzer`/`ZeroTrustSecurityService`（全て DI 未登録）と、削除済みサービスの孤立 Options 8件（Backup/Billing/CloudIntegration/LogCompression/RemoteManagementConfigValidator/Report/SystemDiagnostics/WindowsRepair）
- `ZeroTrustSecurityService` は削除済み依存（DeviceTrust/UserBehavior/SecurityPolicyEngine）に依存しており非修復系のため同様に削除
- 削除後検証: `dotnet build` 0警告0エラー・126/126テスト — 実行時動作への影響なし

### Removed (消費者ゼロの死登録とその実装 — 15登録・10ファイル)

- 「DI 登録済みだが消費者がゼロ」＝見た目は稼働、実際は何もしない誤認登録15件を削除（ctor 注入・`GetService` 動的解決・修復実行系からの参照も全て監査し消費者ゼロを確認）:
  `ISelfHealingCollectionsService`/`IPerformanceOptimizationService`/`IReactiveEventSystem`/`IFunctionalErrorHandlingService`/`IObservabilityService`/`IMetricsCollectionService`/`IFeatureFlagService`/`IChaosEngineeringService`/`IServiceMeshService`/`IAnomalyDetectionService`/`IAuditTrailService`/`IKubernetesOperatorService`/`IKubernetesHealthService`/`IGitOpsService`/`IIacService`/`IPerformanceAnalyticsService`
- 登録削除により到達不能となった実装ファイル10件を削除: `AnomalyDetectionService`/`AuditTrailService`/`ChaosEngineeringService`/`KubernetesService`/`ObservabilityService`/`PerformanceAnalyticsService`/`ReactiveEventSystem`/`SelfHealingCollectionsService`/`AutomatedRemediationOrchestrator`/`DefenderAtpManager`
- `IConfigurationHotReloadService`（ConfigurationManagementService が消費）と `ISystemHealthMonitor`（監視ループが消費）は存続
- 削除後検証: 0警告0エラー・126/126テスト・起動＋`/collaboration` 200 維持

### Removed (live ファイル内の死メンバ整理)

- `ServiceMeshService.cs` を削除 — 前サイクルの登録削除で13型が死型化。唯一の生存型 `HealthStatus` enum は `ServiceSupportTypes.cs`（利用者 `HealthCheckResult` の所在）へ移行
- `ConfigurationManagementService.cs` 末尾の死ブロック削除 — `IFeatureFlagService`/`FeatureFlagInfo`/`FeatureFlagService`（消費者ゼロ、登録削除済み）
- `ServiceSupportTypes.cs` の `IFunctionalErrorHandlingService`/`FunctionalErrorHandlingService` を削除（同上）
- 削除後検証: 0警告0エラー・126/126テスト

### Added (DI 回帰テスト)

- `DependencyInjectionTests` 新規追加（3テスト）— 本セッションで繰り返し発生した DI 破損クラス（未登録依存の注入）の回帰防止:
  - `AllRegistrations_ResolveAtBuildTime`: `Startup.ConfigureServices` の全登録を `ValidateOnBuild` 付きで構築し解決不能を検出（ホスト提供の `IHostApplicationLifetime` はモック注入）
  - `MonitoringLoop_IsWiredAsHostedServices`: 監視ループ4サービスが `IHostedService` として登録済みであることを検証
  - `CollaborationHubDependency_IsRegistered`: Hub 依存の登録存在を検証
- テスト総数 126→129
