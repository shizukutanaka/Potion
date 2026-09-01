using System.Diagnostics;
using Potion.Tray.Core.Resources;

namespace Potion.Tray.Core;

public sealed class SelfHealingEngine
{
    private readonly IReadOnlyList<IHealthCheck> checks;
    private readonly IReadOnlyDictionary<string, IRepairAction> repairs;
    private readonly IHistoryStore history;
    private readonly ISettingsStore settingsStore;
    private readonly INotifier notifier;
    private readonly ISystemInfoProvider system;
    private readonly ITrayClock clock;
    private readonly ITrayLog log;
    private readonly ILocalizer localizer;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> lastInspections = new(StringComparer.OrdinalIgnoreCase);
    private EngineState state = EngineState.Idle;

    public SelfHealingEngine(
        IReadOnlyList<IHealthCheck> checks,
        IReadOnlyList<IRepairAction> repairs,
        IHistoryStore history,
        ISettingsStore settings,
        INotifier notifier,
        ISystemInfoProvider system,
        ITrayClock clock,
        ITrayLog log,
        ILocalizer? localizer = null)
    {
        this.checks = checks;
        this.repairs = repairs.ToDictionary(r => r.CheckId, StringComparer.OrdinalIgnoreCase);
        this.history = history;
        settingsStore = settings;
        this.notifier = notifier;
        this.system = system;
        this.clock = clock;
        this.log = log;
        this.localizer = localizer ?? new ResourceLocalizer();
    }

    public EngineState State => state;
    public string StatusText => state switch
    {
        EngineState.Scanning => localizer.Get("Status.Scanning"),
        EngineState.Repairing => localizer.Get("Status.Repairing"),
        EngineState.Warning => localizer.Get("Status.Warning"),
        EngineState.Critical => localizer.Get("Status.Critical"),
        _ => localizer.Get("Status.Idle")
    };

    public event EventHandler? StateChanged;

    public async Task<CycleResult> RunCycleAsync(CancellationToken ct)
    {
        if (!await cycleGate.WaitAsync(0, ct))
        {
            return new CycleResult(Array.Empty<HistoryEntry>(), State);
        }

        try
        {
            var settings = settingsStore.Load();
            settings.Normalize();
            SetState(EngineState.Scanning);
            var entries = new List<HistoryEntry>();
            foreach (var check in checks)
            {
                ct.ThrowIfCancellationRequested();
                if (!settings.IsCheckEnabled(check.Id) || IsWithinInterval(check.Id, settings))
                {
                    continue;
                }

                lastInspections[check.Id] = clock.UtcNow;
                HealthFinding? finding;
                try
                {
                    finding = await check.InspectAsync(settings, ct);
                }
                catch (Exception ex)
                {
                    log.Error($"Health check {check.DisplayName} failed with an exception.", ex);
                    continue;
                }

                if (finding is null)
                {
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                RepairOutcome? repairOutcome = null;
                string? skipReason = null;
                HistoryOutcome outcome;
                if (!repairs.TryGetValue(finding.CheckId, out var repair))
                {
                    outcome = HistoryOutcome.ManualActionRequired;
                }
                else if (!settings.AutoRepairEnabled)
                {
                    outcome = HistoryOutcome.Skipped;
                    skipReason = localizer.Get("Skip.AutoRepairDisabled");
                }
                else if (settings.DryRun)
                {
                    outcome = HistoryOutcome.Skipped;
                    skipReason = localizer.Get("Skip.DryRun");
                }
                else if (repair.RequiresAdministrator && !system.IsElevated)
                {
                    outcome = HistoryOutcome.Skipped;
                    skipReason = localizer.Get("Skip.AdminRequired");
                }
                else if (await history.CountRepairAttemptsSinceAsync(
                             finding.CheckId,
                             clock.UtcNow.AddHours(-24),
                             ct) >= settings.MaxRepairAttemptsPerDay)
                {
                    outcome = HistoryOutcome.Skipped;
                    skipReason = localizer.Get("Skip.DailyLimit");
                }
                else
                {
                    SetState(EngineState.Repairing);
                    try
                    {
                        repairOutcome = await repair.RepairAsync(finding, settings, ct);
                        outcome = repairOutcome.Success ? HistoryOutcome.Repaired : HistoryOutcome.RepairFailed;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Repair action {repair.DisplayName} failed with an exception.", ex);
                        repairOutcome = new RepairOutcome(false, ex.Message, Array.Empty<CommandExecution>());
                        outcome = HistoryOutcome.RepairFailed;
                    }
                }

                var entry = new HistoryEntry(
                    Guid.NewGuid().ToString("N"),
                    clock.UtcNow,
                    finding.CheckId,
                    finding.Title,
                    finding.Status,
                    outcome,
                    finding.Detail,
                    repairOutcome?.Summary,
                    skipReason,
                    Stopwatch.GetElapsedTime(started),
                    repairOutcome?.Commands ?? Array.Empty<CommandExecution>());
                await history.AppendAsync(entry, ct);
                entries.Add(entry);
                if (NotificationDecider.ShouldNotify(settings.Notifications, entry))
                {
                    notifier.Notify(new Notification(entry.Title, BuildMessage(entry), entry.Status));
                }
            }

            var finalState = entries.Any(e => e.Status == HealthStatus.Critical &&
                                              e.Outcome != HistoryOutcome.Repaired)
                ? EngineState.Critical
                : entries.Any(e => e.Status == HealthStatus.Warning)
                    ? EngineState.Warning
                    : EngineState.Idle;
            SetState(finalState);
            return new CycleResult(entries, finalState);
        }
        finally
        {
            cycleGate.Release();
        }
    }

    private bool IsWithinInterval(string checkId, TraySettings settings)
    {
        if (!lastInspections.TryGetValue(checkId, out var last) ||
            !settings.CheckIntervalMinutes.TryGetValue(checkId, out var minutes))
        {
            return false;
        }

        return clock.UtcNow - last < TimeSpan.FromMinutes(minutes);
    }

    private static string BuildMessage(HistoryEntry entry)
    {
        var suffix = entry.RepairSummary ?? entry.SkipReason ?? entry.Detail;
        return suffix is null || string.Equals(suffix, entry.Detail, StringComparison.Ordinal)
            ? entry.Detail
            : $"{entry.Detail}\n{suffix}";
    }

    private void SetState(EngineState value)
    {
        if (state == value)
        {
            return;
        }

        state = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
