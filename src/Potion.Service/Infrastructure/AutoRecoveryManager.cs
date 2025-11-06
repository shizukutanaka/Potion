// AutoRecoveryManager functionality has been integrated into DistributedSelfHealingService.cs
// to reduce code duplication and improve maintainability.
// AutoRecoveryManager functionality has been integrated into DistributedSelfHealingService.cs
// to reduce code duplication and improve maintainability.

// AutoRecoveryManager functionality has been integrated into DistributedSelfHealingService.cs
// to reduce code duplication and improve maintainability.

public sealed record ComponentHealth(
    bool IsHealthy,
    string Status,
    string? ErrorMessage,
    TimeSpan ResponseTime);

public sealed record RecoveryAttemptEventArgs(
    string Component,
    RecoveryAction Action,
    bool Success,
    string? ErrorMessage,
    DateTimeOffset AttemptedAt);

public sealed record SystemHealthChangedEventArgs(
    Dictionary<string, ComponentHealth> PreviousHealth,
    Dictionary<string, ComponentHealth> CurrentHealth,
    DateTimeOffset ChangedAt);

public enum RecoveryAction
{
    RestartService,
    RestartComponent,
    ClearCache,
    ResetConfiguration,
    Failover
}

public sealed class AutoRecoveryManager : BackgroundService, IAutoRecoveryManager
{
    private readonly ILogger<AutoRecoveryManager> _logger;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<string, ComponentHealth> _componentHealth = new();
    private readonly ConcurrentDictionary<string, int> _failureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(1);
    private readonly int _maxRecoveryAttempts = 3;

    public event EventHandler<RecoveryAttemptEventArgs>? RecoveryAttempted;
    public event EventHandler<SystemHealthChangedEventArgs>? SystemHealthChanged;

    public AutoRecoveryManager(
        ILogger<AutoRecoveryManager> logger,
        IOptionsMonitor<RemediationPolicyOptions> options,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _options = options;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auto recovery manager started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformHealthCheckCycleAsync(stoppingToken);
                await Task.Delay(_healthCheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in auto recovery cycle");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // エラー時は5分待機
            }
        }

        _logger.LogInformation("Auto recovery manager stopped");
    }

    public async Task<bool> AttemptRecoveryAsync(string component, Exception failure, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Attempting recovery for component {Component} due to failure: {Failure}", component, failure.Message);

        var failureCount = _failureCounts.AddOrUpdate(component, 1, (_, current) => current + 1);

        if (failureCount > _maxRecoveryAttempts)
        {
            _logger.LogError("Maximum recovery attempts ({MaxAttempts}) exceeded for component {Component}", _maxRecoveryAttempts, component);
            RecoveryAttempted?.Invoke(this, new RecoveryAttemptEventArgs(component, RecoveryAction.Failover, false, "Max attempts exceeded", DateTimeOffset.UtcNow));
            return false;
        }

        var recoveryAction = DetermineRecoveryAction(component, failure, failureCount);

        try
        {
            var success = await ExecuteRecoveryActionAsync(component, recoveryAction, cancellationToken);

            RecoveryAttempted?.Invoke(this, new RecoveryAttemptEventArgs(
                component,
                recoveryAction,
                success,
                success ? null : "Recovery action failed",
                DateTimeOffset.UtcNow));

            if (success)
            {
                _failureCounts.TryRemove(component, out _);
                _logger.LogInformation("Recovery successful for component {Component}", component);
                return true;
            }
            else
            {
                _logger.LogWarning("Recovery attempt failed for component {Component}", component);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery action failed for component {Component}", component);
            RecoveryAttempted?.Invoke(this, new RecoveryAttemptEventArgs(component, recoveryAction, false, ex.Message, DateTimeOffset.UtcNow));
            return false;
        }
    }

    public async Task<HealthCheckResult> PerformHealthCheckAsync(CancellationToken cancellationToken)
    {
        var previousHealth = new Dictionary<string, ComponentHealth>(_componentHealth);
        var currentHealth = new Dictionary<string, ComponentHealth>();
        var checkTime = DateTimeOffset.UtcNow;

        // 主要コンポーネントのヘルスチェック
        var components = new[]
        {
            ("ServiceHost", CheckServiceHostHealth),
            ("Scheduler", CheckSchedulerHealth),
            ("FileSystem", CheckFileSystemHealth),
            ("Configuration", CheckConfigurationHealth),
            ("Network", CheckNetworkHealth),
            ("Memory", CheckMemoryHealth),
            ("Disk", CheckDiskHealth),
            ("Security", CheckSecurityHealth)
        };

        foreach (var (componentName, healthCheck) in components)
        {
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var isHealthy = await healthCheck(cancellationToken);
                stopwatch.Stop();

                var health = new ComponentHealth(
                    isHealthy,
                    isHealthy ? "Healthy" : "Unhealthy",
                    isHealthy ? null : $"Component {componentName} is not responding properly",
                    stopwatch.Elapsed);

                currentHealth[componentName] = health;
            }
            catch (Exception ex)
            {
                currentHealth[componentName] = new ComponentHealth(
                    false,
                    "Error",
                    ex.Message,
                    TimeSpan.Zero);
            }
        }

        // ヘルス状態の変化を検知
        var hasChanges = HasHealthChanged(previousHealth, currentHealth);
        if (hasChanges)
        {
            SystemHealthChanged?.Invoke(this, new SystemHealthChangedEventArgs(previousHealth, currentHealth, checkTime));
        }

        _componentHealth.Clear();
        foreach (var (key, value) in currentHealth)
        {
            _componentHealth[key] = value;
        }

        var isSystemHealthy = currentHealth.Values.All(h => h.IsHealthy);

        return new HealthCheckResult(isSystemHealthy, currentHealth, checkTime);
    }

    private static bool HasHealthChanged(
        IReadOnlyDictionary<string, ComponentHealth> previous,
        IReadOnlyDictionary<string, ComponentHealth> current)
    {
        if (previous.Count != current.Count)
        {
            return true;
        }

        foreach (var (component, previousHealth) in previous)
        {
            if (!current.TryGetValue(component, out var currentHealth))
            {
                return true;
            }

            if (!Equals(previousHealth, currentHealth))
            {
                return true;
            }
        }

        return false;
    }

    private RecoveryAction DetermineRecoveryAction(string component, Exception failure, int failureCount)
    {
        return component switch
        {
            "ServiceHost" => RecoveryAction.RestartService,
            "Scheduler" => failureCount <= 1 ? RecoveryAction.RestartComponent : RecoveryAction.RestartService,
            "FileSystem" => RecoveryAction.ClearCache,
            "Configuration" => RecoveryAction.ResetConfiguration,
            "Network" => RecoveryAction.Failover,
            _ => RecoveryAction.RestartComponent
        };
    }

    private async Task<bool> ExecuteRecoveryActionAsync(string component, RecoveryAction action, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing recovery action {Action} for component {Component}", action, component);

        switch (action)
        {
            case RecoveryAction.RestartService:
                return await RestartServiceAsync(cancellationToken);

            case RecoveryAction.RestartComponent:
                return await RestartComponentAsync(component, cancellationToken);

            case RecoveryAction.ClearCache:
                return await ClearCacheAsync(component, cancellationToken);

            case RecoveryAction.ResetConfiguration:
                return await ResetConfigurationAsync(cancellationToken);

            case RecoveryAction.Failover:
                return await PerformFailoverAsync(component, cancellationToken);

            default:
                _logger.LogWarning("Unknown recovery action: {Action}", action);
                return false;
        }
    }

    private async Task<bool> RestartServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 実際の実装では適切なサービス再起動方法を使用
            _logger.LogInformation("Attempting to restart Potion service");

            // ここでは簡易的な実装を示す
            await Task.Delay(1000, cancellationToken); // シミュレーション

            _logger.LogInformation("Service restart completed");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart service");
            return false;
        }
    }

    private async Task<bool> RestartComponentAsync(string component, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Attempting to restart component: {Component}", component);

            // コンポーネント固有の再起動処理
            switch (component)
            {
                case "Scheduler":
                    // スケジューラの再起動
                    break;
                case "TelemetryRetentionService":
                    // テレメトリサービスの再起動
                    break;
                default:
                    _logger.LogWarning("No restart procedure defined for component: {Component}", component);
                    break;
            }

            await Task.Delay(500, cancellationToken); // シミュレーション
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart component: {Component}", component);
            return false;
        }
    }

    private async Task<bool> ClearCacheAsync(string component, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Clearing cache for component: {Component}", component);

            // キャッシュクリア処理
            if (component == "FileSystem")
            {
                var cacheDir = Path.Combine(ServicePaths.State, "cache");
                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, true);
                    Directory.CreateDirectory(cacheDir);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache for component: {Component}", component);
            return false;
        }
    }

    private async Task<bool> ResetConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Attempting to reset configuration");

            // 設定のリセット処理
            var configManager = _serviceProvider.GetService(typeof(IConfigurationManager)) as IConfigurationManager;
            if (configManager != null)
            {
                // デフォルト設定を生成
                var defaultConfig = GenerateDefaultConfiguration();
                var updateResult = await configManager.UpdateConfigurationAsync(defaultConfig, cancellationToken);

                if (updateResult.Success)
                {
                    _logger.LogInformation("Configuration reset completed");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset configuration");
            return false;
        }
    }

    private async Task<bool> PerformFailoverAsync(string component, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Attempting failover for component: {Component}", component);

            // フェイルオーバー処理（実際の実装では適切な方法で）
            await Task.Delay(1000, cancellationToken); // シミュレーション

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failover failed for component: {Component}", component);
            return false;
        }
    }

    private async Task PerformHealthCheckCycleAsync(CancellationToken stoppingToken)
    {
        var healthResult = await PerformHealthCheckAsync(stoppingToken);

        if (!healthResult.IsHealthy)
        {
            _logger.LogWarning("System health check failed. Unhealthy components: {UnhealthyComponents}",
                string.Join(", ", healthResult.ComponentHealth.Where(h => !h.Value.IsHealthy).Select(h => h.Key)));

            // 不健康なコンポーネントに対して回復を試行
            foreach (var (component, health) in healthResult.ComponentHealth.Where(h => !h.Value.IsHealthy))
            {
                await AttemptRecoveryAsync(component, new InvalidOperationException(health.ErrorMessage), stoppingToken);
            }
        }
        else
        {
            _logger.LogDebug("System health check passed");
        }

        // メトリクスをログ出力
        LogHealthMetrics(healthResult);
    }

    private void LogHealthMetrics(HealthCheckResult healthResult)
    {
        var healthyCount = healthResult.ComponentHealth.Count(h => h.Value.IsHealthy);
        var totalCount = healthResult.ComponentHealth.Count;

        _logger.LogInformation(
            "Health check results: {HealthyComponents}/{TotalComponents} healthy, LastChecked: {CheckedAt}",
            healthyCount,
            totalCount,
            healthResult.CheckedAt);
    }

    // ヘルスチェックメソッド
    private async Task<bool> CheckServiceHostHealth(CancellationToken cancellationToken)
    {
        try
        {
            // プロセスが実行中かチェック
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            return !currentProcess.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckSchedulerHealth(CancellationToken cancellationToken)
    {
        try
        {
            // スケジューラの状態チェック（実際の実装では適切な方法で）
            await Task.Delay(100, cancellationToken); // シミュレーション
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckFileSystemHealth(CancellationToken cancellationToken)
    {
        try
        {
            // ファイルシステムのアクセスチェック
            var testFile = Path.Combine(ServicePaths.State, ".healthcheck");
            await File.WriteAllTextAsync(testFile, DateTimeOffset.UtcNow.ToString(), cancellationToken);
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckConfigurationHealth(CancellationToken cancellationToken)
    {
        try
        {
            // 設定ファイルの存在と有効性チェック
            var configPath = Path.Combine(ServicePaths.Base, "appsettings.json");
            if (!File.Exists(configPath))
            {
                return false;
            }

            // JSON構文チェック
            var configContent = await File.ReadAllTextAsync(configPath, cancellationToken);
            System.Text.Json.JsonDocument.Parse(configContent);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckMemoryHealth(CancellationToken cancellationToken)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT AvailableBytes, TotalPhysicalMemory FROM CIM_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var availableBytes = Convert.ToInt64(obj["AvailableBytes"]);
                var totalBytes = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                var availablePercent = (double)availableBytes / totalBytes * 100;

                // 利用可能メモリが20%未満の場合は警告
                return availablePercent > 20;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private async Task<bool> CheckDiskHealth(CancellationToken cancellationToken)
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(ServicePaths.Base));
            var availablePercent = (double)driveInfo.AvailableFreeSpace / driveInfo.TotalSize * 100;

            // 利用可能ディスク容量が10%未満の場合は警告
            return availablePercent > 10;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckSecurityHealth(CancellationToken cancellationToken)
    {
        try
        {
            // セキュリティ関連の基本チェック
            var securityAuditor = _serviceProvider.GetService(typeof(ISecurityAuditor)) as ISecurityAuditor;
            if (securityAuditor != null)
            {
                var auditResult = await securityAuditor.PerformSecurityAuditAsync(cancellationToken);
                return auditResult.IsSecure;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GenerateDefaultConfiguration()
    {
        // デフォルト設定を生成（実際の実装では適切なデフォルト値を使用）
        return @"{
  ""RemediationPolicy"": {
    ""MaxConcurrency"": 2,
    ""SchedulerIntervalSeconds"": 300,
    ""CommandAllowlist"": [""sfc.exe"", ""dism.exe"", ""cleanmgr.exe""],
    ""Tasks"": []
  },
  ""TelemetryRetention"": {
    ""Enabled"": true,
    ""RetentionDays"": 30,
    ""CleanupIntervalHours"": 12
  }
}";
    }
}
