using System.Threading;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface ITelemetryRetentionMetrics
{
    TelemetryRetentionMetricsSnapshot? LastSnapshot { get; }

    void Initialize(TelemetryRetentionMetricsSnapshot snapshot);

    void RecordSweep(TelemetryRetentionMetricsSnapshot snapshot);
}

public sealed record TelemetryRetentionMetricsSnapshot(
    DateTimeOffset SweepStartedUtc,
    DateTimeOffset SweepCompletedUtc,
    DateTimeOffset RetentionCutoffUtc,
    int DeletedCount,
    int FailedCount,
    bool LimitReached,
    int RetentionDays,
    int CleanupIntervalHours,
    int MaxDeletionsPerSweep,
    int AttemptedCount = 0,
    int SkippedCount = 0,
    long BytesFreed = 0);

public sealed class TelemetryRetentionMetrics : ITelemetryRetentionMetrics
{
    private readonly ILogger<TelemetryRetentionMetrics> _logger;
    private TelemetryRetentionMetricsSnapshot? _lastSnapshot;

    public TelemetryRetentionMetrics(ILogger<TelemetryRetentionMetrics> logger)
    {
        _logger = logger;
    }

    public TelemetryRetentionMetricsSnapshot? LastSnapshot => Volatile.Read(ref _lastSnapshot);

    public void Initialize(TelemetryRetentionMetricsSnapshot snapshot)
    {
        Volatile.Write(ref _lastSnapshot, snapshot);
    }

    public void RecordSweep(TelemetryRetentionMetricsSnapshot snapshot)
    {
        _logger.LogInformation(
            "Telemetry retention sweep summary: StartedUtc={SweepStartedUtc}, CompletedUtc={SweepCompletedUtc}, CutoffUtc={RetentionCutoffUtc}, Deleted={DeletedCount}, Failed={FailedCount}, LimitReached={LimitReached}, Attempted={AttemptedCount}, Skipped={SkippedCount}, BytesFreed={BytesFreed}, RetentionDays={RetentionDays}, CleanupIntervalHours={CleanupIntervalHours}, MaxDeletionsPerSweep={MaxDeletionsPerSweep}",
            snapshot.SweepStartedUtc,
            snapshot.SweepCompletedUtc,
            snapshot.RetentionCutoffUtc,
            snapshot.DeletedCount,
            snapshot.FailedCount,
            snapshot.LimitReached,
            snapshot.AttemptedCount,
            snapshot.SkippedCount,
            snapshot.BytesFreed,
            snapshot.RetentionDays,
            snapshot.CleanupIntervalHours,
            snapshot.MaxDeletionsPerSweep);

        Volatile.Write(ref _lastSnapshot, snapshot);
    }
}
