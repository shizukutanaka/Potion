using Potion.Tray.Core;

namespace Potion.Tray.Core.Tests;

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessRunResult> responses = new(StringComparer.Ordinal);
    public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = new();

    public void Respond(string fileName, IReadOnlyList<string> arguments, ProcessRunResult result) =>
        responses[Key(fileName, arguments)] = result;

    public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((fileName, arguments.ToList()));
        return Task.FromResult(responses.TryGetValue(Key(fileName, arguments), out var result)
            ? result
            : new ProcessRunResult(0, string.Empty, string.Empty, TimeSpan.Zero, false));
    }

    private static string Key(string fileName, IReadOnlyList<string> arguments) =>
        fileName + "\0" + string.Join("\0", arguments);
}

internal sealed class FakeSystemInfoProvider : ISystemInfoProvider
{
    public bool IsElevated { get; set; }
    public IReadOnlyList<DriveSnapshot> Drives { get; set; } = Array.Empty<DriveSnapshot>();
    public MemorySnapshot Memory { get; set; } = new(100, 100);
    public IReadOnlyList<ServiceSnapshot> Services { get; set; } = Array.Empty<ServiceSnapshot>();
    public bool RebootPending { get; set; }
    public bool CanResolveDns { get; set; } = true;
    public bool IsNetworkAvailable { get; set; } = true;
    public int DnsCalls { get; private set; }

    public IReadOnlyList<DriveSnapshot> GetFixedDrives() => Drives;
    public MemorySnapshot GetMemory() => Memory;
    public IReadOnlyList<ServiceSnapshot> GetServices(IReadOnlyList<string> names) => Services;
    public bool IsRebootPending() => RebootPending;
    public Task<bool> CanResolveDnsAsync(string host, CancellationToken ct)
    {
        DnsCalls++;
        return Task.FromResult(CanResolveDns);
    }
}

internal sealed class FakeClock : ITrayClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class RecordingNotifier : INotifier
{
    public List<Notification> Notifications { get; } = new();
    public void Notify(Notification notification) => Notifications.Add(notification);
}

internal sealed class FakeTempFileCleaner : ITempFileCleaner
{
    public TempCleanupResult Result { get; set; } = new(0, 0);
    public Task<TempCleanupResult> CleanAsync(CancellationToken ct) => Task.FromResult(Result);
}

internal sealed class FakeLog : ITrayLog
{
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public void Info(string message) { }
    public void Warn(string message, Exception? exception = null) => Warnings.Add(message);
    public void Error(string message, Exception? exception = null) => Errors.Add(message);
}

internal sealed class FakeSettingsStore : ISettingsStore
{
    public TraySettings Settings { get; set; } = new();
    public int LoadCount { get; private set; }
    public TraySettings Load()
    {
        LoadCount++;
        return Settings;
    }

    public void Save(TraySettings settings) => Settings = settings;
}

internal sealed class FakeHistoryStore : IHistoryStore
{
    public List<HistoryEntry> Entries { get; } = new();
    public int Attempts { get; set; }
    public Task AppendAsync(HistoryEntry entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HistoryEntry>> ReadRecentAsync(int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HistoryEntry>>(Entries.OrderByDescending(e => e.TimestampUtc).Take(max).ToList());

    public Task<HistoryEntry?> FindLastAsync(string checkId, CancellationToken ct) =>
        Task.FromResult(Entries
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry.CheckId.Equals(checkId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.entry.TimestampUtc)
            .ThenByDescending(item => item.index)
            .Select(item => item.entry)
            .FirstOrDefault());

    public Task<int> CountRepairAttemptsSinceAsync(string checkId, DateTimeOffset sinceUtc, CancellationToken ct) =>
        Task.FromResult(Attempts);
}

internal sealed class FakeCheckStateStore : ICheckStateStore
{
    public Dictionary<string, DateTimeOffset> State { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int SaveCount { get; private set; }
    public IReadOnlyDictionary<string, DateTimeOffset>? SavedState { get; private set; }

    public IReadOnlyDictionary<string, DateTimeOffset> Load() => State;

    public void Save(IReadOnlyDictionary<string, DateTimeOffset> lastInspections)
    {
        SaveCount++;
        SavedState = new Dictionary<string, DateTimeOffset>(lastInspections, StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class SequenceHealthCheck : IHealthCheck
{
    private readonly Queue<HealthFinding?> findings;

    public SequenceHealthCheck(string id, params HealthFinding?[] findings)
    {
        Id = id;
        this.findings = new Queue<HealthFinding?>(findings);
    }

    public int Calls { get; private set; }
    public string Id { get; }
    public string DisplayName => Id;

    public Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(findings.Count > 0 ? findings.Dequeue() : null);
    }
}

internal sealed class DelegateHealthCheck : IHealthCheck
{
    private readonly Func<TraySettings, CancellationToken, Task<HealthFinding?>> inspect;
    public DelegateHealthCheck(string id, Func<TraySettings, CancellationToken, Task<HealthFinding?>> inspect)
    {
        Id = id;
        this.inspect = inspect;
    }

    public string Id { get; }
    public string DisplayName => Id;
    public Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct) => inspect(settings, ct);
}

internal sealed class DelegateRepair : IRepairAction
{
    private readonly Func<HealthFinding, TraySettings, CancellationToken, Task<RepairOutcome>> repair;
    public DelegateRepair(string checkId, bool requiresAdministrator, Func<HealthFinding, TraySettings, CancellationToken, Task<RepairOutcome>> repair)
    {
        CheckId = checkId;
        RequiresAdministrator = requiresAdministrator;
        this.repair = repair;
    }

    public string CheckId { get; }
    public string DisplayName => CheckId;
    public bool RequiresAdministrator { get; }
    public Task<RepairOutcome> RepairAsync(HealthFinding finding, TraySettings settings, CancellationToken ct) =>
        repair(finding, settings, ct);
}

internal sealed class BlockingHealthCheck : IHealthCheck
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Id => "blocking";
    public string DisplayName => Id;
    public async Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct)
    {
        Started.SetResult(true);
        await Continue.Task.WaitAsync(ct);
        return null;
    }
}
