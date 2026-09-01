using System.Globalization;

namespace Potion.Tray.Core;

public enum HealthStatus { Healthy, Warning, Critical }

public sealed record HealthFinding(
    string CheckId,
    string Title,
    HealthStatus Status,
    string Detail,
    IReadOnlyDictionary<string, string>? Metrics = null);

public interface IHealthCheck
{
    string Id { get; }
    string DisplayName { get; }
    Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct);
}

public sealed record CommandExecution(
    string FileName,
    string Arguments,
    int ExitCode,
    TimeSpan Duration,
    string StdOutTail,
    string StdErrTail);

public sealed record RepairOutcome(bool Success, string Summary, IReadOnlyList<CommandExecution> Commands);

public interface IRepairAction
{
    string CheckId { get; }
    string DisplayName { get; }
    bool RequiresAdministrator { get; }
    Task<RepairOutcome> RepairAsync(HealthFinding finding, TraySettings settings, CancellationToken ct);
}

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct);
}

public sealed record DriveSnapshot(string Name, long TotalBytes, long FreeBytes);
public sealed record MemorySnapshot(long TotalBytes, long AvailableBytes);
public sealed record ProcessMemorySnapshot(string Name, long WorkingSetBytes);
public enum ServiceStartType { Unknown, Boot, System, Automatic, Manual, Disabled }
public sealed record ServiceSnapshot(
    string Name,
    bool Exists,
    bool IsRunning,
    ServiceStartType StartType = ServiceStartType.Unknown);

internal static class ServicePolicy
{
    public static bool ShouldMonitor(ServiceSnapshot service) =>
        service.StartType is ServiceStartType.Automatic or
            ServiceStartType.Boot or
            ServiceStartType.System or
            ServiceStartType.Unknown;
}

public interface ISystemInfoProvider
{
    bool IsElevated { get; }
    IReadOnlyList<DriveSnapshot> GetFixedDrives();
    MemorySnapshot GetMemory();
    IReadOnlyList<ProcessMemorySnapshot> GetTopMemoryProcesses(int count);
    IReadOnlyList<ServiceSnapshot> GetServices(IReadOnlyList<string> names);
    bool IsRebootPending();
    bool IsNetworkAvailable { get; }
    Task<bool> CanResolveDnsAsync(string host, CancellationToken ct);
}

public interface ITrayClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemTrayClock : ITrayClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface ITrayLog
{
    void Info(string message);
    void Warn(string message, Exception? exception = null);
    void Error(string message, Exception? exception = null);
}

public interface ITempFileCleaner
{
    Task<TempCleanupResult> CleanAsync(TimeSpan minimumAge, CancellationToken ct);
}

public sealed record TempCleanupResult(int FilesDeleted, long BytesFreed);

public enum NotificationMode { All, RepairsOnly, FailuresOnly, None }

public sealed class TraySettings
{
    public bool AutoRepairEnabled { get; set; } = true;
    public NotificationMode Notifications { get; set; } = NotificationMode.RepairsOnly;
    public int ScanIntervalMinutes { get; set; } = 5;
    public bool RunAtWindowsStartup { get; set; } = true;
    public bool DryRun { get; set; }
    public int DiskWarnPercent { get; set; } = 15;
    public int DiskCriticalPercent { get; set; } = 7;
    public int DiskWarnFreeGb { get; set; } = 20;
    public int DiskCriticalFreeGb { get; set; } = 8;
    public int MemoryWarnPercent { get; set; } = 10;
    public int MaxRepairAttemptsPerDay { get; set; } = 3;
    public int HistoryMaxEntries { get; set; } = 1000;
    public int HistoryRetentionDays { get; set; } = 90;
    public int DuplicateSuppressionMinutes { get; set; } = 60;
    public int NotificationCooldownMinutes { get; set; } = 60;
    public bool AllowComponentCleanup { get; set; } = true;
    public string UiCulture { get; set; } = string.Empty;
    public string DnsProbeHost { get; set; } = "www.msftconnecttest.com";
    public List<string> MonitoredServices { get; set; } = new()
    {
        "Winmgmt", "EventLog", "Dhcp", "Dnscache", "BITS", "wuauserv", "Schedule"
    };
    public Dictionary<string, bool> ChecksEnabled { get; set; } = new();
    public Dictionary<string, int> CheckIntervalMinutes { get; set; } = new()
    {
        ["component-store"] = 720
    };

    public TraySettings Clone()
    {
        return new TraySettings
        {
            AutoRepairEnabled = AutoRepairEnabled,
            Notifications = Notifications,
            ScanIntervalMinutes = ScanIntervalMinutes,
            RunAtWindowsStartup = RunAtWindowsStartup,
            DryRun = DryRun,
            DiskWarnPercent = DiskWarnPercent,
            DiskCriticalPercent = DiskCriticalPercent,
            DiskWarnFreeGb = DiskWarnFreeGb,
            DiskCriticalFreeGb = DiskCriticalFreeGb,
            MemoryWarnPercent = MemoryWarnPercent,
            MaxRepairAttemptsPerDay = MaxRepairAttemptsPerDay,
            HistoryMaxEntries = HistoryMaxEntries,
            HistoryRetentionDays = HistoryRetentionDays,
            DuplicateSuppressionMinutes = DuplicateSuppressionMinutes,
            NotificationCooldownMinutes = NotificationCooldownMinutes,
            AllowComponentCleanup = AllowComponentCleanup,
            UiCulture = UiCulture,
            DnsProbeHost = DnsProbeHost,
            MonitoredServices = new List<string>(MonitoredServices ?? new()),
            ChecksEnabled = new Dictionary<string, bool>(ChecksEnabled ?? new()),
            CheckIntervalMinutes = new Dictionary<string, int>(CheckIntervalMinutes ?? new())
        };
    }

    public void Normalize()
    {
        ScanIntervalMinutes = Math.Clamp(ScanIntervalMinutes, 1, 1440);
        DiskWarnPercent = Math.Clamp(DiskWarnPercent, 1, 90);
        DiskCriticalPercent = Math.Clamp(DiskCriticalPercent, 1, DiskWarnPercent);
        DiskWarnFreeGb = Math.Clamp(DiskWarnFreeGb, 1, 4096);
        DiskCriticalFreeGb = Math.Clamp(DiskCriticalFreeGb, 1, DiskWarnFreeGb);
        MemoryWarnPercent = Math.Clamp(MemoryWarnPercent, 1, 90);
        MaxRepairAttemptsPerDay = Math.Clamp(MaxRepairAttemptsPerDay, 1, 50);
        HistoryMaxEntries = Math.Clamp(HistoryMaxEntries, 50, 20000);
        HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 1, 3650);
        DuplicateSuppressionMinutes = Math.Clamp(DuplicateSuppressionMinutes, 0, 1440);
        NotificationCooldownMinutes = Math.Clamp(NotificationCooldownMinutes, 0, 1440);
        UiCulture ??= string.Empty;
        UiCulture = UiCulture.Trim();
        if (UiCulture.Length > 0)
        {
            try
            {
                _ = CultureInfo.GetCultureInfo(UiCulture);
            }
            catch (CultureNotFoundException)
            {
                UiCulture = string.Empty;
            }
        }
        DnsProbeHost = string.IsNullOrWhiteSpace(DnsProbeHost)
            ? "www.msftconnecttest.com"
            : DnsProbeHost.Trim();
        MonitoredServices = (MonitoredServices ?? new())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (MonitoredServices.Count == 0)
        {
            MonitoredServices = new TraySettings().MonitoredServices;
        }
        ChecksEnabled ??= new();
        CheckIntervalMinutes = (CheckIntervalMinutes ?? new())
            .Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .ToDictionary(
                p => p.Key.Trim(),
                p => Math.Clamp(p.Value, 1, 10080),
                StringComparer.OrdinalIgnoreCase);
    }

    public bool IsCheckEnabled(string checkId) =>
        !ChecksEnabled.TryGetValue(checkId, out var enabled) || enabled;
}

public interface ISettingsStore
{
    TraySettings Load();
    void Save(TraySettings settings);
}

public enum HistoryOutcome { Detected, Repaired, RepairFailed, Skipped, ManualActionRequired }

public sealed record HistoryEntry(
    string Id,
    DateTimeOffset TimestampUtc,
    string CheckId,
    string Title,
    HealthStatus Status,
    HistoryOutcome Outcome,
    string Detail,
    string? RepairSummary,
    string? SkipReason,
    TimeSpan Duration,
    IReadOnlyList<CommandExecution> Commands);

public interface IHistoryStore
{
    Task AppendAsync(HistoryEntry entry, CancellationToken ct);
    Task<IReadOnlyList<HistoryEntry>> ReadRecentAsync(int max, CancellationToken ct);
    Task<HistoryEntry?> FindLastAsync(string checkId, CancellationToken ct);
    Task<int> CountRepairAttemptsSinceAsync(string checkId, DateTimeOffset sinceUtc, CancellationToken ct);
}

public interface ICheckStateStore
{
    IReadOnlyDictionary<string, DateTimeOffset> Load();
    void Save(IReadOnlyDictionary<string, DateTimeOffset> lastInspections);
}

public enum EngineState { Idle, Scanning, Repairing, Warning, Critical }

public sealed record CycleResult(IReadOnlyList<HistoryEntry> Entries, EngineState State);

public sealed record Notification(string Title, string Message, HealthStatus Severity);

public interface INotifier
{
    void Notify(Notification notification);
}

public static class NotificationDecider
{
    public static bool ShouldNotify(NotificationMode mode, HistoryEntry entry)
    {
        return mode switch
        {
            NotificationMode.None => false,
            NotificationMode.All => true,
            NotificationMode.RepairsOnly => entry.Outcome is
                HistoryOutcome.Repaired or HistoryOutcome.RepairFailed or HistoryOutcome.ManualActionRequired,
            NotificationMode.FailuresOnly => entry.Outcome is
                HistoryOutcome.RepairFailed or HistoryOutcome.ManualActionRequired ||
                entry.Outcome == HistoryOutcome.Skipped && entry.Status == HealthStatus.Critical,
            _ => false
        };
    }
}
