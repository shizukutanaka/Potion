using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

public sealed class SystemPreflightChecker
{
    private readonly ILogger<SystemPreflightChecker> _logger;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _optionsMonitor;
    private readonly ICommandGuard _commandGuard;
    private readonly IOptionsMonitor<TelemetryRetentionOptions> _retentionOptions;
    private readonly ITelemetryRetentionSnapshotStore _snapshotStore;
    private readonly IOptionsMonitor<RemoteManagementConfig> _remoteManagementOptions;
    private readonly IBillingService _billingService;

    public SystemPreflightChecker(
        ILogger<SystemPreflightChecker> logger,
        IOptionsMonitor<RemediationPolicyOptions> optionsMonitor,
        ICommandGuard commandGuard,
        IOptionsMonitor<TelemetryRetentionOptions> retentionOptions,
        ITelemetryRetentionSnapshotStore snapshotStore,
        IOptionsMonitor<RemoteManagementConfig> remoteManagementOptions,
        IBillingService billingService)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _commandGuard = commandGuard;
        _retentionOptions = retentionOptions;
        _snapshotStore = snapshotStore;
        _remoteManagementOptions = remoteManagementOptions;
        _billingService = billingService;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = _optionsMonitor.CurrentValue;

        await ValidateSystemResourcesAsync();
        await ValidateTelemetryRetentionAsync(cancellationToken);
        ValidateRemoteManagementConfiguration(_remoteManagementOptions.CurrentValue);
        await ValidateBillingStatusAsync(cancellationToken);

        ValidateDirectories();
        ValidatePolicy(options, cancellationToken);

        _logger.LogInformation("System preflight check completed successfully");

    }

    private async Task ValidateSystemResourcesAsync()
    {
        _logger.LogDebug("Validating system resource availability");

        var memoryInfo = GetMemoryUsage();
        var diskInfo = GetDiskUsage();

        _logger.LogInformation(
            "System resources: Memory={UsedPercent:P1}, Disk={DiskUsedPercent:P1}",
            memoryInfo.UsedPercent,
            diskInfo.UsedPercent);

        // メモリ使用率が90%を超えた場合警告
        if (memoryInfo.UsedPercent > 0.9)
        {
            _logger.LogWarning("High memory usage detected: {UsedPercent:P1}", memoryInfo.UsedPercent);
        }

        // ディスク使用率が95%を超えた場合警告
        if (diskInfo.UsedPercent > 0.95)
        {
            _logger.LogWarning("High disk usage detected: {UsedPercent:P1}", diskInfo.UsedPercent);
        }

        // 利用可能なメモリが512MB未満の場合警告
        if (memoryInfo.AvailableBytes < 512 * 1024 * 1024)
        {
            _logger.LogWarning("Low available memory: {AvailableMB:F1} MB", memoryInfo.AvailableBytes / (1024.0 * 1024.0));
        }

        // 利用可能なディスク容量が1GB未満の場合警告
        if (diskInfo.AvailableBytes < 1024 * 1024 * 1024)
        {
            _logger.LogWarning("Low available disk space: {AvailableGB:F1} GB", diskInfo.AvailableBytes / (1024.0 * 1024.0 * 1024.0));
        }
    }

    private (long TotalBytes, long AvailableBytes, double UsedPercent) GetMemoryUsage()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT TotalPhysicalMemory, AvailableBytes FROM CIM_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var totalMemory = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                var availableMemory = Convert.ToInt64(obj["AvailableBytes"] ?? obj["FreePhysicalMemory"]);
                var usedPercent = 1.0 - (availableMemory / (double)totalMemory);

                return (totalMemory, availableMemory, usedPercent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get memory usage information");
        }

        return (0, 0, 0);
    }

    private (long TotalBytes, long AvailableBytes, double UsedPercent) GetDiskUsage()
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(ServicePaths.Base));
            var totalSpace = driveInfo.TotalSize;
            var availableSpace = driveInfo.AvailableFreeSpace;
            var usedPercent = 1.0 - (availableSpace / (double)totalSpace);

            return (totalSpace, availableSpace, usedPercent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get disk usage information");
            return (0, 0, 0);
        }
    }

    private void ValidateDirectories()
    {
        _logger.LogDebug("Validating service directory structure");
        var directories = new[]
        {
            ServicePaths.Base,
            ServicePaths.Logs,
            ServicePaths.State,
            ServicePaths.Telemetry,
            ServicePaths.Playbooks,
            ServicePaths.Certificates
        };

        foreach (var directory in directories)
        {
            try
            {
                var testFile = Path.Combine(directory, ".healthcheck");
                File.WriteAllText(testFile, DateTimeOffset.UtcNow.ToString("O"));
                File.Delete(testFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Preflight directory validation failed for {Directory}", directory);
                throw;
            }
        }
    }

    private async Task ValidateTelemetryRetentionAsync(CancellationToken cancellationToken)
    {
        var retention = _retentionOptions.CurrentValue;
        ValidateTelemetryRetentionOptions(retention);

        if (!retention.Enabled)
        {
            _logger.LogDebug("Telemetry retention disabled; skipping snapshot verification");
            await _snapshotStore.CleanupQuarantinedSnapshotsAsync(cancellationToken);
            return;
        }

        var snapshot = await _snapshotStore.LoadAsync(cancellationToken);
        if (snapshot is null)
        {
            _logger.LogWarning("Telemetry retention snapshot not found; initial cleanup sweep may not have completed yet");
            return;
        }

        var age = DateTimeOffset.UtcNow - snapshot.SweepCompletedUtc;
        var expectedInterval = TimeSpan.FromHours(Math.Clamp(retention.CleanupIntervalHours, 1, TelemetryRetentionOptions.MaxCleanupIntervalHours));
        if (age > expectedInterval * 2)
        {
            _logger.LogWarning(
                "Telemetry retention sweep appears stale. Last completion at {LastCompletedUtc}, expected within {ExpectedHours} hours",
                snapshot.SweepCompletedUtc,
                retention.CleanupIntervalHours);
        }

        if (snapshot.RetentionDays != retention.RetentionDays || snapshot.CleanupIntervalHours != retention.CleanupIntervalHours)
        {
            _logger.LogInformation(
                "Telemetry retention snapshot is based on previous configuration (RetentionDays={SnapshotRetention}, CleanupIntervalHours={SnapshotInterval}); a future sweep will refresh metadata",
                snapshot.RetentionDays,
                snapshot.CleanupIntervalHours);
        }
    }

    private void ValidateTelemetryRetentionOptions(TelemetryRetentionOptions retention)
    {
        if (!retention.Enabled)
        {
            return;
        }

        if (retention.RetentionDays < 7)
        {
            _logger.LogWarning("Telemetry retention window is configured for less than 7 days ({RetentionDays}); consider increasing to preserve diagnostics", retention.RetentionDays);
        }

        var retentionWindowHours = retention.RetentionDays * 24;
        if (retention.CleanupIntervalHours > retentionWindowHours)
        {
            throw new InvalidOperationException("Cleanup interval must be shorter than the telemetry retention window.");
        }

        if (retention.MaxDeletionsPerSweep > TelemetryRetentionOptions.MaxDeletionsPerSweepLimit)
        {
            throw new InvalidOperationException(
                $"MaxDeletionsPerSweep must not exceed {TelemetryRetentionOptions.MaxDeletionsPerSweepLimit}.");
        }

        if (retention.MaxDeletionsPerSweep == TelemetryRetentionOptions.MaxDeletionsPerSweepLimit)
        {
            _logger.LogWarning("MaxDeletionsPerSweep is set to the safety ceiling ({Limit}); confirm this is intentional to avoid extended cleanup cycles.", TelemetryRetentionOptions.MaxDeletionsPerSweepLimit);
        }
        else if (retention.MaxDeletionsPerSweep >= TelemetryRetentionOptions.MaxDeletionsPerSweepLimit * 0.9)
        {
            _logger.LogInformation("MaxDeletionsPerSweep is configured near the upper safety ceiling ({Value}); evaluate storage I/O capacity before increasing further.", retention.MaxDeletionsPerSweep);
        }
    }

    private void ValidateRemoteManagementConfiguration(RemoteManagementConfig config)
    {
        if (!config.Enabled)
        {
            _logger.LogDebug("Remote management disabled; skipping configuration validation");
            return;
        }

        if (!Uri.TryCreate(config.ServerEndpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("Remote management endpoint must be a valid absolute URI.");
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Remote management endpoint must use HTTPS.");
        }

        if (endpoint.IsLoopback)
        {
            throw new InvalidOperationException("Remote management endpoint must not point to loopback or localhost when enabled.");
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey) || config.ApiKey.Length < 32)
        {
            throw new InvalidOperationException("Remote management API key must be at least 32 characters long.");
        }

        if (string.IsNullOrWhiteSpace(config.MachineId))
        {
            throw new InvalidOperationException("Remote management MachineId must be specified.");
        }

        if (config.HeartbeatInterval < TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("Remote management heartbeat interval must be at least one minute.");
        }

        if (config.LogSyncInterval < TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Remote management log sync interval must be at least five minutes.");
        }

        _logger.LogInformation("Remote management configuration verified for endpoint {Endpoint}", endpoint);
    }

    private void ValidatePolicy(RemediationPolicyOptions options, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Validating remediation policy with {TaskCount} tasks", options.Tasks.Count);
        if (options.Tasks.Count == 0)
        {
            throw new InvalidOperationException("At least one remediation task must be configured.");
        }

        if (options.CommandAllowlist.Count == 0)
        {
            throw new InvalidOperationException("Command allow list must contain at least one entry.");
        }

        EnsureNoDuplicateAllowlistEntries(options.CommandAllowlist);
        var maintenanceWindowTags = ValidateMaintenanceWindows(options);

        foreach (var task in options.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!task.Enabled)
            {
                continue;
            }

            if (task.MaintenanceWindowTag is { Length: > 0 } tag && !maintenanceWindowTags.Contains(tag))
            {
                throw new InvalidOperationException($"Task '{task.Name}' references unknown maintenance window tag '{tag}'.");
            }

            try
            {
                var resolvedCommand = _commandGuard.EnsureCommandIsAllowed(task.Command);
                RejectReparsePoints(resolvedCommand);
                _logger.LogDebug("Validated task {TaskName} command {Command}", task.Name, task.Command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Preflight command validation failed for task {TaskName}", task.Name);
                throw;
            }

            if (task.RequiresElevation && !OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Elevation-only tasks require Windows.");
            }
        }
    }

    private static void EnsureNoDuplicateAllowlistEntries(IEnumerable<string> entries)
    {
        var duplicates = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .GroupBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException($"Duplicate entries detected in command allow list: {string.Join(", ", duplicates)}");
        }
    }

    private void RejectReparsePoints(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Command path '{path}' must not be a reparse point.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to inspect command path {Path} for reparse attributes", path);
            throw;
        }
    }

    private HashSet<string> ValidateMaintenanceWindows(RemediationPolicyOptions options)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dailyWindows = new Dictionary<DayOfWeek, List<MaintenanceWindowInterval>>();

        foreach (var window in options.MaintenanceWindows)
        {
            if (!tags.Add(window.Tag))
            {
                throw new InvalidOperationException($"Duplicate maintenance window tag detected: '{window.Tag}'.");
            }

            if (!TimeSpan.TryParse(window.StartTime, out var start))
            {
                throw new InvalidOperationException($"Maintenance window '{window.Tag}' has invalid start time '{window.StartTime}'.");
            }

            if (!TimeSpan.TryParse(window.EndTime, out var end))
            {
                throw new InvalidOperationException($"Maintenance window '{window.Tag}' has invalid end time '{window.EndTime}'.");
            }

            if (start == end)
            {
                throw new InvalidOperationException($"Maintenance window '{window.Tag}' has zero duration and is therefore invalid.");
            }

            var days = window.DaysOfWeek.Any() ? window.DaysOfWeek : Enum.GetValues<DayOfWeek>();
            foreach (var day in days)
            {
                if (start < end)
                {
                    RegisterInterval(day, start, end, window.Tag, dailyWindows);
                }
                else
                {
                    RegisterInterval(day, start, TimeSpan.FromDays(1), window.Tag, dailyWindows);
                    var nextDay = (DayOfWeek)(((int)day + 1) % 7);
                    RegisterInterval(nextDay, TimeSpan.Zero, end, window.Tag, dailyWindows);
                }
            }
        }

        return tags;
    }

    private void RegisterInterval(DayOfWeek day, TimeSpan start, TimeSpan end, string tag, Dictionary<DayOfWeek, List<MaintenanceWindowInterval>> dailyWindows)
    {
        if (!dailyWindows.TryGetValue(day, out var intervals))
        {
            intervals = new List<MaintenanceWindowInterval>();
            dailyWindows[day] = intervals;
        }

        foreach (var interval in intervals)
        {
            if (start < interval.End && end > interval.Start)
            {
                throw new InvalidOperationException($"Maintenance window '{tag}' overlaps with '{interval.Tag}' on {day}.");
            }
        }

        intervals.Add(new MaintenanceWindowInterval(start, end, tag));
    }

    private async Task ValidateBillingStatusAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("課金状態チェックを実行します");

        var billingStatus = _billingService.GetCurrentStatus();

        _logger.LogInformation(
            "課金状態: ライセンス={Status}, 課金タイプ={BillingType}, 価格={Price}ドル ({MonthlyPrice}ドル/月, {OneTimePrice}ドル/買い切り), 最終チェック={LastChecked}",
            billingStatus.IsLicensed ? "有効" : "無効",
            billingStatus.CurrentBillingType,
            billingStatus.CurrentPrice,
            billingStatus.MonthlyPrice,
            billingStatus.OneTimePrice,
            billingStatus.LastChecked);

        if (!billingStatus.IsLicensed)
        {
            // デバッグモードの場合は警告のみ
            var options = _optionsMonitor.CurrentValue;
            if (options.DebugMode)
            {
                _logger.LogWarning("デバッグモードのため、ライセンスチェックをスキップします");
                return;
            }

            // 猶予期間内の場合は警告のみ
            if (billingStatus.LastChecked != DateTimeOffset.MinValue)
            {
                var gracePeriodEnd = billingStatus.LastChecked.AddDays(7); // デフォルト猶予期間
                if (DateTimeOffset.UtcNow <= gracePeriodEnd)
                {
                    var remainingHours = (gracePeriodEnd - DateTimeOffset.UtcNow).TotalHours;
                    _logger.LogWarning(
                        "ライセンスが無効です。課金タイプ: {BillingType}, 価格: {Price}ドル。猶予期間終了まであと{Hours:F0}時間です",
                        billingStatus.CurrentBillingType,
                        billingStatus.CurrentPrice,
                        remainingHours);
                    return;
                }
            }

            // 猶予期間終了後はエラーとしてサービス開始を拒否
            throw new InvalidOperationException(
                $"ライセンスが無効です。サービスを開始するには有効なライセンスが必要です。課金タイプ: {billingStatus.CurrentBillingType}, 価格: {billingStatus.CurrentPrice}ドル（月額: {billingStatus.MonthlyPrice}ドル、買い切り: {billingStatus.OneTimePrice}ドル）");
        }

        _logger.LogInformation("課金チェックが正常に完了しました");
    }

    private sealed record MaintenanceWindowInterval(TimeSpan Start, TimeSpan End, string Tag);
}
