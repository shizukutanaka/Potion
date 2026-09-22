# Changelog

## Unreleased

### Fixed (PerformanceOptimizer の破壊的動作・検証欠落・クロスプラットフォーム破損)

- **ユーザープロセスを Kill していた**: `OptimizeProcessCountAsync` が複数起動の notepad/calc/mspaint を `Process.Kill` — 未保存データ損失の危険な破壊動作。重複起動の検出・報告のみに変更
- **任意プロセスの優先度を変更していた**: `OptimizeCpuUsageAsync` が累積 `TotalProcessorTime` 上位3プロセスを `BelowNormal` に降格 — 累積値は現在負荷と無関係で対象誤認、外部プロセスへの干渉自体が設計上のリスク。検出・報告のみに変更（サービス名でプロセスを探す死ブロックも除去）
- **`EmptyStandbyList.exe` 呼出しを削除** — Sysinternals 外部ツールで標準環境に存在せず確定失敗（代替の標準コマンド無し、GC 実行と検出報告を維持）
- **`ICommandValidator` 未経由でプロセス起動していた** — `netsh`/`powercfg` を直接 `_processRunner` 実行（#32 の allowlist 強化を迂回）。ctor に validator 注入し `EnsureCommandIsAllowed` を適用、実行体名を `netsh.exe`/`powercfg.exe` に修正（bare 名は allowlist 不一致）
- **WMI/netsh が非Windowsでも実行** — `GetMemoryInfo`（Win32_OperatingSystem）と `RunAdditionalOptimizationsAsync`（Win32_Battery/netsh/powercfg）に OS ガードなし → 非Windowsで毎サイクル例外。Windows ガード追加、メモリは GC 情報で代替
- `appsettings.json`/`appsettings.Production.json` の `RemediationPolicy.CommandAllowlist` に `netsh.exe` 追加（TCP autotuning 最適化アクションに必要 — 許可リスト変更のため注記）

### Fixed (修復実行経路の潜在バグ群 — FeatureFlags ON 時に確定失敗していた)

- `EventDrivenRemediationService` の既定トリガールールが**永久不発**: `Condition` が `"High CPU usage"` 等の本文を要求する一方、発行側メッセージは `"CPU usage at 87.3%"` 形式で不一致。Severity+Component のみで判定するよう Condition 撤廃（発行値 `cpu`/`memory`/`disk` に整合）
- 既定ルール `high_network` が `https://example.com` への Webhook POST — 発火した場合に外部 placeholder へ実送信する欠陥。ネットワークアラートの発生源は無く本質的に死ルールのため削除
- `PredictiveRemediationService` の `GetPreventiveCommand` とイベント駆動 `TaskName` が擬似コマンド名（`Optimize-CpuUsage`/`cpu-optimization` 等）→ CommandValidator allowlist で確定拒否。新設 `PreventiveRemediationCommands` で実ツールへマッピング: CPU→`powercfg.exe /energy /duration 60`（診断）、Memory→`sfc.exe /verifyonly`（整合性検証・読取のみ）、Disk→`cleanmgr.exe /verylowdisk`（実クリーンアップ）。未マップキーは実行せずスキップ
- `RemediationTask` に `Arguments` フィールド追加し `RemediationScheduler` 経由で Executor へ伝達（従来 `Command` に引数を含めると `FileName` 解決失敗 or allowlist 不一致の二択だった）
- `PredictiveRemediationService`/`EventDrivenRemediationService` のシャットダウン時 OCE を誤エラーログしないよう分離。イベント駆動サービスの無意味な1秒ポーリングループを停止シグナル待機に置換

### Tests

- `PreventiveRemediationCommands` 単体テスト新規（99→107）: 全既知キーが「空白を含まない .exe 実行体 + 分離された引数」を返すこと・大小文字不問・未既知キーは false を返すことを検証
- `RequestMetricsTracker` 単体テスト新規（90→99）: ローリング窓の RPS/平均レイテンシ/5xx エラー率算出と `/collaboration`・`/metrics` 計測除外を検証

### Fixed (異常検知ルーティングのケース不一致)

- `AnomalyDetector.HandleAdvancedAnomaly` の switch ケースが `cpu_usage_percent` 等の旧キー名で、実際に供給されるキー（`CpuUsage`/`MemoryUsage`/`DiskUsage`/`Bytes*PerSec`）と全不一致 → 全異常が Generic 経路へ落ちていた実バグ。実キーの小文字形に修正（`ToLower`→`ToLowerInvariant` も併せて）

### Fixed (ResourceMonitoringMetrics の値ミスマッチ修正)

- `CpuTimeSeconds` — `Environment.TickCount64`（システム稼働時間）が入っていた実バグを `Process.TotalProcessorTime`（プロセスCPU時間）に修正
- `IoOperationsPerSecond` — Linux で `/proc/self/io` の `syscr+syscw` 差分レートを実測（Windows はインスタンス名衝突のため安全側0）
- `GcCollectionCount` — gen0 のみだったものを gen0+gen1+gen2 の合計に修正

### Fixed (async void タイマーコールバック解消)

- `ComplianceReportService.GenerateComplianceReport` の `async void` を廃止 — 同期コールバックから `_ = GenerateComplianceReportAsync()` を火消し起動する形に変更（`async void` 特有の未観測例外・同期コンテキスト問題を解消。コードベース内の `async void` はこれで全滅）

### Fixed (監視サービスの既定無効化＋ServicePaths クロスプラットフォーム破損)

- `appsettings.json` に `EventCorrelation.Enabled`/`Compliance.Enabled` を `true` で追加 — 両サービスのオプション既定が false・設定セクション不在で常時自己無効化していた（相関ループ・アラート購読・コンプライアンスレポートが全て不発）
- `ServicePaths` の静的初期化で `SecurityIdentifier`（Windows専用）を構築 → 非Windowsで `TypeInitializationException` となりクラス全体が使用不可だった。ACL 強化を `OperatingSystem.IsWindows()` ガード内に遅延化
- `ServicePaths.Base` が `CommonApplicationData`（Unixでは `/usr/share`・root所有）直下作成で権限エラー → 候補ルートを順に試行（CommonApplicationData→LocalApplicationData→AppContext.BaseDirectory）

### Fixed (HTTP パフォーマンスの実測化)

- `RequestMetricsMiddleware` + `RequestMetricsTracker`（1分ローリング窓、スレッドセーフ）を新規追加し `RuntimePerformanceMetrics` の RequestsPerSecond/AverageLatencyMs/ErrorRate を実測化（固定0解消）。`/collaboration`（長時間接続）と `/metrics`（スクレイプ）は計測除外。登録は `UseStaticFiles` 後 — API/SignalR/health トラフィックのみ対象

### Fixed (WindowsEventMetrics / 復元ポイントの実測化)

- `WindowsEventMetrics` — `EventLogReader` の XPath クエリ（`TimeCreated[timediff <= 24h]`）で System/Security ログの直近24時間イベント数・エラー・Critical・最新時刻を実測（`System.Diagnostics.EventLog` 10.0.12 を明示追加 — 既に推移導入済みの同バージョン。走査上限5,000件。他OSは0）
- `SystemIntegrityMetrics.RestorePointAvailable` — `root\DEFAULT` の `SystemRestore` WMI クラスで復元ポイント有無を実測（他OSは false。IntegrityCheckPassed/ViolationCount/RepairedCount は安価な実測源が無いため従来値のまま）

### Fixed (EventCorrelationMetrics の実測化)

- `EventCorrelationStats` 共有カウンタを新設し、`EventCorrelationService` が稼働ルール数・累計検出相関数を記録、`SystemHealthMonitor` がスナップショットに反映（固定 0 解消）。`EventCorrelationService`/`SystemHealthMonitor` の ctor に DI 注入として追加

### Fixed (Windows 固有スタブの実測化)

- `SecurityMetrics` — `MSFT_MpComputerStatus`（Defender サービス/AV 有効・最終スキャン時刻）と `MSFT_MpThreat`（検出脅威数）を `root\Microsoft\Windows\Defender` から、ファイアウォールは `root\StandardCimv2` の `MSFT_NetFirewallProfile`（全プロファイル有効時のみ true）から、Secure Boot はレジストリ `SYSTEM\CurrentControlSet\Control\SecureBoot\State` から取得（`System.Management` 既参照・新規パッケージなし。他OSは false/0）
- `InventoryMetrics` — `Win32_ComputerSystem` の Manufacturer/Model と `Win32_BIOS` の SerialNumber を実測（他OSは空文字）
- `SecurityContextMetrics` — `IsElevated`/`CurrentUserIsAdmin` を `WindowsPrincipal.IsInRole(Administrator)` で実測（固定 false 解消、他OSは false）

### Fixed (HealthAlert が一切発火しなかったバグ)

- `SystemHealthMonitor` — `HealthAlert` イベントは宣言のみで発火箇所ゼロ、スナップショットも常に空アラートだった。`GetCurrentHealthAsync` で cpu/memory/disk の圧レベルを評価し ≥High で `SystemHealthAlert` を発行・イベント発火（Critical→Critical/High→Warning）。発火はコンポーネント×レベルで重複抑制し15分クールダウン、圧が下がれば解除（`EventCorrelationService`・`EventDrivenRemediationService` の購読が実際に機能するようになる）

### Fixed (残存スタブメトリクスの実測化)

- `ServiceMetrics` — Windows 上で `Win32_Service`（既参照の `System.Management`、新規パッケージなし）からサービス総数/稼働/停止を実測。`Stopped` かつ `Auto` 開始は失敗サービスとして件名を返す（他OSは 0）
- `ResourcePressureMetrics` — 常に `None` だった cpu/memory/disk 圧を使用率から導出（≥70 Medium / ≥85 High / ≥95 Critical）
- `RuntimePerformanceMetrics` — `threadCount`（全OS）と `handleCount`（Windows）を `Process` 実測

### Fixed (ヘルスメトリクスが固定値0を返していたバグ)

- `SystemHealthMonitor` — `CpuUsage`/`DiskUsage`/`BytesReceivedPerSec`/`BytesSentPerSec`/`ActiveConnections` が常に 0.0 だった。`SystemMetricsSampler` を追加し実測化:
  - CPU: Windows は `PerformanceCounter`(`Processor\% Processor Time\_Total`)、Linux は `/proc/stat` 差分、その他OSは 0 フォールバック
  - Disk: `DriveInfo` でシステムドライブの使用%・空き・総容量を実測（read/write レートは Windows カウンタ、その他OSは 0）
  - Network: `NetworkInterface` のインターフェースカウンタ差分で送受信レートを算出＋`IPGlobalProperties` でアクティブ TCP 数を実測
- 確認済み実値: Disk 74.6%・Network ~77KB/s・activeConnections=7（従来は全て 0）

### Fixed (機能的に死んでいた監視サービス2件を実接続)

- `AnomalyDetector` — 3分毎の分析タイマーは動いていたが `RecordMetric` の呼出元がゼロで、空の履歴を永遠に解析していた。`ISystemHealthMonitor.GetCurrentMetricsAsync()` から各周期メトリクスを投入するよう接続
- `EventCorrelationService` — 相関ルール（cpu_usage/memory_usage 等）は存在するが `RecordEvent` の呼出元がゼロ。`HealthAlert` イベントを購読して `health.alert` イベントを記録＋相関周期で現在メトリクスをルールのイベント型名（`cpu_usage` 等）へマッピングして投入。`StopAsync` で購読解除

### Fixed (dependabot.yml の構文破損)

- `.github/dependabot.yml` — `automerge`/`with`/`key`/`restore-keys` 等の Dependabot に存在しないキー（CI キャッシュ設定の混入）を除去し、本来の `nuget` エコシステム定義を追加。従来はバリデーション不備で NuGet 更新 PR が一切発行されない構成だった

### Removed (ビルド不能なインストーラ・ツール群と恒常失敗の検証スクリプト)

- `setup/` 削除 — WiX 定義 `Potion.wxs` が存在しない `Potion.Service.Installer.dll`（カスタムアクション DLL のプロジェクト自体が無い）と `License.rtf`/`Dialog.bmp`/`Banner.bmp`（ファイル未同梱）を必須参照するためビルド不可。`install.cmd` はその MSI を実行、`PotionSetupUI.cs` は存在しない `Potion.ConfigTool.exe` を生成する手順を持つ
- `tools/Potion.ConfigTool.cs` 削除 — プロジェクトファイルなし・`Potion.Service.Options`（内部型）参照でコンパイル不能な孤立ファイル（tools/ は空に）
- `scripts/validate-system.sh` 削除 — 削除済みコントローラの `/api/health/system/comprehensive` 等15件の不存在エンドポイントを叩くため恒常失敗
- いずれも CI・他スクリプト・ドキュメントから参照なし。残存スクリプト（`build-release.ps1`/`deploy-windows.ps1`/`package-installer.ps1`/`deploy.sh`）は sc.exe/MSI 非依存の実用経路

### Removed (消費者ゼロの i18n スタック全体 — サービス+resx+パッケージ+ツール)

- `InternationalizationService` — DI 登録済みだが消費者ゼロ（Startup の登録以外、本番・ダッシュボード・API のどこからも参照されない）。登録・`UseRequestLocalization`・`AddLocalization`/`AddMemoryCache`・46言語の `supportedCultures` ブロックを削除（`IMemoryCache` の唯一の利用者も同サービス）
- `Resources/` 配下の resx 48ファイル削除 — `ControllerStrings.*.resx` は削除済みコントローラ向けの文言で、上記サービス経由でしか読まれない
- `Microsoft.Extensions.Localization` パッケージ除去 — 唯一の利用者が同サービス（ライブラリ削除: 唯一消費者の消失に伴う。将来 i18n が必要になれば resx ごと git 履歴から復元可能）
- `InternationalizationServiceTests`・`tools/TranslationManager.cs`/`.csx` 削除 — resx を検査するためだけのツール群
- テスト総数 41→29

### Fixed (README の実態整合)

- 機能一覧の `argument sanitization, SQL-injection guards, rate limiting` を除去 — 全て削除済みのテスト専用コードだった（実効セキュリティは `CommandValidator` 許可リスト + シェルレス起動）
- テスト数 `134/134` → `41/41`（削除分は全て死コード専用テスト）
- `src/Potion.Service/README.md` に `/api/health*` エンドポイント一覧を追記

### Removed (テスト専用サービス第2弾 — 約2,300行)

- `AdvancedCacheService`・`DatabaseOptimizationService`・`ErrorHandler`(+`IErrorHandler`/`LogLevel`/`ErrorType`/`ErrorSeverity`/`ErrorRecoveryAction` 等の付属型) — いずれも DI 未登録・本番参照ゼロで、対応するテストファイルだけが参照していた
- 上記専用テスト3件を削除: `AdvancedCacheServiceTests`・`DatabaseOptimizationServiceTests`・`ErrorHandlerTests`
- `Microsoft.Data.SqlClient` 7.0.3 を csproj から除去 — 唯一の利用者が `DatabaseOptimizationService` だった（ライブラリ削除: 唯一消費者の消失に伴う。将来 SQL アクセスが必要になれば再追加で復元可能）
- テスト総数 90→41。残存テストは全て live コードを対象

### Removed (テスト専用バリデータクラスタ — 本番未接続、約2,900行)

- `CommandGuard`/`ICommandGuard`（76行）— 本番で一度もインスタンス化・DI登録されていないファサード。実際の許可リスト強制は `ICommandValidator`/`CommandValidator` が担う（PR #32 で executor に配線済み）
- 同ファサード専用依存も消費者ゼロのため削除: `UrlValidator`（144行）・`DomainValidator`（49行）・`ArgumentSanitizer`（121行）・`RateLimiter`（103行）・`NetworkSecurityGuard`（485行）
- 上記だけを対象とするテスト資産も削除: `CommandGuardTests.cs`（423行）・`IntegrationTests.cs`（264行・全件 CommandGuard+ProcessRunner）・`PerformanceTests.cs` の CommandGuard 系4テスト（ProcessRunner/Memory 系3テストは存続）・`TestHelpers.CreateCommandGuard`/`ForwardingLogger`
- `tests/Potion.Service.Benchmarks/` プロジェクトごと削除 — 全ベンチマークが本クラスタ専用、かつ sln 未登録の孤児プロジェクトだった（CI の `Test-Path` ガードによりジョブは自動スキップ）
- テスト総数 139→90。セキュリティ上の実効経路（`CommandValidator` 許可リスト + `UseShellExecute=false` 直接起動）は変更なし・既存テストで保護継続

### Fixed (ナビゲーション全体が初回クリックで全滅するバグ — UI変更: バグ修正のみ、見た目・レイアウト変更なし)

- `showSection()` のナビハイライトが2重に壊れていた:
  - active 解除ループが不存在の `.dashboard-nav li` を指定（実体は `.side-nav-link`）→ 前のセクションの active が消えず複数同時ハイライト
  - セレクタ `[onclick="showSection('X')"]` の完全一致に対し、実 HTML は `onclick="showSection('X'); return false;"` → `querySelector` が `null` を返し `.classList` で **TypeError。init 内の `showSection('overview')` も例外となり初期化が中断**（`initializeCharts` 以降未到達）
  - `*=`（部分一致）セレクタに修正 — `switchTab`/`filterAlerts`/`setTimeFilter` は onclick が完全一致のため実害なしと確認済み
- 副次的に dead コード削除: `.dashboard-nav li` へのリスナ登録（マッチゼロ＋二重発火の原因）、`.dropdown` 系一式（`setupDropdowns`/`toggleDropdown`/`closeAllDropdowns`/`dropdownStates` — `.dropdown` 要素が HTML・JS のどこにも存在しない完全な死サブシステム）

### Fixed (ダッシュボード ID 不整合 — アラート一覧が常に非表示・更新ごとに例外)

- `updateAlertsBadge` が存在しない `#alerts-count` を参照 — `updateAlertsDisplay` 内で毎回 NPE となり **アラート一覧が例外で表示されなかった**。実在する `#alerts-count-side`（サイドナビバッジ）に修正
- `updateResourceChart` が存在しない `#resource-chart` canvas を参照 — `loadPerformanceData` の更新サイクル毎に NPE（catch されてコンソール汚染）。実チャートは `renderResourceTrendsChart`（`#resource-trends-chart`）が別途描画するため、**死んだ重複メソッドごと削除**（UI変更: バグ修正のみ、見た目・レイアウト変更なし）

### Fixed (ダッシュボードUIの死ハンドラ3件 — UI変更: バグ修正のみ、見た目・レイアウト変更なし)

- `openPerformanceDrawer`・`toggleAdvancedSearch`・`applyAdvancedFilters` — HTML の `onclick`/`onchange` から参照されるがグローバル関数が未定義で、押下のたびに `ReferenceError` が発生し何も動作しなかった（メソッド本体は `dashboard` オブジェクト上に実装済み・対象 DOM も存在 — ラッパー欠落のみ）
- 他グローバル関数と同型の `window.dashboard` 委譲ラッパーを3件追加
- 検証: `node --check` パス・対象要素4点の存在確認

### Fixed (残留プレースホルダ・死シードキー)

- `CONTRIBUTING.md` のクローン URL `yourusername` プレースホルダ → `shizukutanaka` 実URL（README で前回同型修正済みの漏れ）
- `DependencyInjectionTests` の in-memory 設定から `RemediationPolicy:Enabled` を除去 — `RemediationPolicyOptions` に `Enabled` プロパティは存在せず、バインドされない死シードキーだった

### Security (修復コマンドのアローリスト強制 — CommandValidator 接続)

- `CommandValidator`/`ICommandValidator` は実装済みだったが**どこからも呼ばれていなかった** — 修復パイプラインは `descriptor.Option.Command` を検証なしでそのまま `ProcessRunner` へ渡していた（フラグ有効化時、設定した任意コマンドが無検査で実行される状態）
- `RemediationTaskExecutor.ExecuteAsync` でプロセス生成直前に `EnsureCommandIsAllowed(option.Command)` を強制（最終ホップで防御 — 上流からのバイパス経路を塞ぐ）。`ICommandValidator`→`CommandValidator` をフラグブロックに登録
- 検証: 0警告0エラー・関連テスト 11/11・新規「ブロック済みコマンドは spawn 前に失敗」テスト追加

### Added (RemediationTaskExecutor ユニットテスト — 134→139)

- `Remediation/RemediationTaskExecutorTests` 新規5件 — 修復実行の最終段（実プロセス実行への橋渡し）の振る舞いを `IProcessRunner` モックで検証:
  - null descriptor → `ArgumentNullException`
  - `ProcessStartInfo` が Option から正しく構築されること（FileName=Command・Arguments・UseShellExecute=false・出力リダイレクト・CreateNoWindow・TimeoutSeconds→timeout）
  - `AllowedExitCodes` 空のとき 0 以外は失敗扱い（警告ログ）／指定コード（3010 等）は成功扱い
  - runner 例外の伝播
- 検証: 0警告0エラー・新規5/5・合計139件

### Removed (消費者ゼロの死ファイル2件 — 1,099行)＋ 生型の移設

- `Infrastructure/SecureLogService.cs`（617行）削除 — `ISecureLogService`/`SecureLogService`/`LogEntry`/`LogSecurityReport`/`LogAccessControl` 全て参照ゼロ（DI 登録・サービス消費・テスト利用なし）。ファイル内の `LogLevel` enum は `ErrorHandler.HandleError` の公開API型（内部で MEL `LogLevel` へ序数キャスト）のため **`ErrorHandler.cs` へ移設**
- `Infrastructure/AutomaticDiagnostics.cs`（482行）削除 — `IAutomaticDiagnostics`/`AutomaticDiagnostics` は DI 未登録・消費者ゼロ。ただし `DiagnosticReport`/`DiagnosticCheck`/`DiagnosticRecommendation`/`DiagnosticSeverity` は `ResiliencePipelines.CreateDiagnosticPipeline`（`ResiliencePipeline<DiagnosticReport>`、Startup 登録済み）が使用のため **同ファイルへ移設**
- 検証: 0警告0エラー・ErrorHandler/DI テスト 32/32

### Removed (起動不能の compose 派生2ファイル)

- `docker-compose.enterprise.yml`（201行）削除 — 参照する `./nginx.conf`・`./prometheus.yml`（ルート）・`./fluentd.conf`・`./redis-data`・`./postgres-data` 等が全て不存在で起動不能。`potion/enterprise:latest` イメージもビルド経路なし。postgres/redis/elasticsearch はアプリ未使用の死依存
- `docker-compose.monitoring.yml`（220行）削除 — 同様に `./scripts/init.sql`・`./monitoring/grafana/*`・`./monitoring/loki-config.yml`・`./monitoring/tempo-config.yml`・`./monitoring/k6` 等の不存在参照が半数超。ベースの `docker-compose.yml`（前項で修復済みの最小構成）が代替
- 残置: `k8s/`・`helm/`・`kubernetes-enterprise.yml` は「存在しないイメージを前提とした将来デプロイ用ひな形」で種類が異なるため保持（Windows 専用サービスの k8s 運用は別途判断要）
- 検証: 削除対象の参照不存在を全件確認済み

### Fixed (docker-compose.yml を実際に起動できる最小構成へ修復)

- 従来ファイルは参照先欠落で**半分のサービスが起動不能**だった:
  - `./scripts/init.sql`、`./monitoring/loki-config.yml`、`./monitoring/tempo-config.yml`、`./monitoring/grafana/{dashboards,provisioning}` が全て不存在
  - `postgres`/`redis` はアプリ側に DB/Redis 利用コードが一切ない死依存（平文パスワードも混入）
- 最小構成へ修正: `potion-service`（`ASPNETCORE_URLS=http://+:80`、HTTP のみで証明書不要）+ `prometheus` + `alertmanager`（存在する設定のみ）
- `monitoring/prometheus.yml` 修正: 不存在の `rule_files: alerts.yml` で Prometheus 自体が起動失敗していた + スクレイプ先が不存在パス `/api/health/metrics/custom` → 実在する `/metrics` へ。クラスタ前提の k8s ジョブは compose 文脈で無意味なため削除
- 検証: YAML パース確認済み（docker 非搭載のため実起動は未検証 — 参照パスは全て実在を確認）
- 残件: `docker-compose.enterprise.yml`/`docker-compose.monitoring.yml`/`kubernetes-enterprise.yml` も同様に不存在参照多数 — 別サイクルで対応予定

### Removed (死設定 `TelemetryRetention` セクション)

- `appsettings.json` の `TelemetryRetention` セクションを削除 — `TelemetryRetentionOptions` は #14 で削除済み、`Configure` バインドもなく**どのコードも読まない死設定**だった（同型監査の最後の残件）。`TelemetryRetentionService` 自体は DI 未登録で `AutoRecoveryManager` の文字列スイッチにのみ残存
- テストの in-memory 設定からも同キー除去（参照消滅の後片付け）
- README を実態へ更新: エンドポイント一覧に `/health`・`/api/health*` を追加、`TelemetryRetention` を節リストから削除し監視オプション各セクション（環境変数調整可能）を記載、テスト数を 134 へ
- 検証: JSON パース確認・DI テスト 5/5・0警告0エラー

### Removed (未使用 NuGet パッケージ15件 — ライブラリ削除の説明)

- 製品 csproj から14件: 全て**ソース内参照ゼロ**を確認済み（`using`/型利用なし・NuGet restore のみに存在し PublishTrimmed の肥大化要因）
  - `Microsoft.ML`/`Microsoft.ML.TimeSeries`/`Microsoft.ML.FastTree` — "ML-based" と称する AnomalyDetector は統計的手法（平均・分散・トレンド）で ML.NET は一切未使用
  - `System.IdentityModel.Tokens.Jwt`/`Microsoft.IdentityModel.Tokens` — 認証コード不存在で未使用
  - `NuGet.Versioning`/`MathNet.Numerics`/`System.Reactive`/`System.Security.Cryptography.Pkcs`/`System.Security.Principal.Windows`/`System.Diagnostics.TraceSource`/`System.Diagnostics.EventLog`/`Polly.Extensions`/`Polly.Testing`（製品 csproj に誤配置のテスト用パッケージ）
  - `System.Diagnostics.EventLog`/`System.Security.Principal.Windows` は `Serilog.Sinks.EventLog`/`System.Management` が推移的に供給するため直接参照の削除のみ
- ベンチマーク csproj から1件: `System.Security.Cryptography.Pkcs` 9.0.13 ピン — 製品側の Pkcs 8.0.1 明示参照との NU1605 衝突回避用だったが、製品側の参照自体を削除したため不要化
- 検証: ソリューション 0警告0エラー・DI/Scheduler テスト 8/8

### Fixed (性能テストのフレイク根本修正 — `CommandGuard_UrlValidation_Performance`)

- 閾値 `0.01ms/検証` の平均値アサートが CI の GC・スケジューリング外れ値で断続失敗していた（本セッションでも再現）。絶対速度の厳密保証ではなく退行検出が目的のため、**5ラウンドの中央値比較**へ変更し外れ値耐性を持たせつつ閾値は 0.03ms（約3倍の猶予でも10倍超の退行は捕捉）
- 検証: 同テスト連続3回パス・ビルド 0警告0エラー

### Added (ダッシュボード API `/api/health*` — 配信のみだった監視 UI の実稼働化)

- `wwwroot` ダッシュボードが参照する4エンドポイントを `ISystemHealthMonitor` スナップショットから実装 — これまで全て404で UI は表示されるがデータゼロだった（Serilog・wwwroot と同型の「配線済み宣言・未実装」クラス）。**API 追加（UI変更の説明）**: ダッシュボードが fetch する経路を既存モニターデータで満たす最小 API で、UI 側の変更はない
  - `GET /api/health` → `{metrics, alerts}`（スナップショット全体、camelCase でダッシュボード期待値に一致）
  - `GET /api/health/metrics` → `SystemMetrics`（cpu/memory/disk/services/security/windowsEvents）
  - `GET /api/health/security` → `{defenderStatus, firewallStatus, realTimeProtection, securityCount, securityAlerts}`
  - `GET /api/health/security/summary` → `{securityScore}`（Defender/Firewall 状態と脅威数から算出）
- 併せて潜伏バグ修正: csproj `PublishTrimmed=true` が `JsonSerializerIsReflectionEnabledByDefault=false` を暗黙設定し、Minimal API の複雑型シリアライズが `NoMetadataForType` で**実行時クラッシュ**していた → 同フラグを `true` で明示（AOT ではなくトリムのみのためリフレクション有効が正）。この潜伏バグは `SecureLogService`/`ComplianceReportService` の JSON エクスポート経路にも波及していた
- 検証: 0警告0エラー・全4エンドポイントが実 JSON を返却（macOS では Windows 専用メトリクスが 0、memory は実値）・134/134 相当（既知フレイク1件は再実行でパス）

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
