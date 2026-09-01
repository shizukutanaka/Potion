using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
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
    private readonly ICheckStateStore? checkState;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> lastInspections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> repairAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string CheckId, HistoryOutcome Outcome), DateTimeOffset> lastNotifications = new();
    private DateTimeOffset lastHistoryFailureNotifiedUtc;
    private bool checkStateLoaded;
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
        ILocalizer? localizer = null,
        ICheckStateStore? checkState = null)
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
        this.checkState = checkState;
    }

    public EngineState State => state;
    public DateTimeOffset? LastCycleCompletedUtc { get; private set; }
    public string StatusText => state switch
    {
        EngineState.Scanning => localizer.Get("Status.Scanning"),
        EngineState.Repairing => localizer.Get("Status.Repairing"),
        EngineState.Warning => localizer.Get("Status.Warning"),
        EngineState.Critical => localizer.Get("Status.Critical"),
        _ => localizer.Get("Status.Idle")
    };
    public string LastScanText => LastCycleCompletedUtc is { } completed
        ? localizer.Format(
            "Ui.Menu.LastScan",
            completed.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
        : localizer.Get("Ui.Menu.LastScanNever");

    public event EventHandler? StateChanged;
    public event EventHandler? CycleCompleted;

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
            LoadCheckState();
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
                catch (Exception ex) when (ex is not OperationCanceledException)
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
                OperationCanceledException? repairCancellation = null;
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
                else
                {
                    var since = clock.UtcNow.AddHours(-24);
                    var historyAttempts = await history.CountRepairAttemptsSinceAsync(finding.CheckId, since, ct);
                    var memoryAttempts = CountRecentRepairAttempts(finding.CheckId, since);
                    if (Math.Max(historyAttempts, memoryAttempts) >= settings.MaxRepairAttemptsPerDay)
                    {
                        outcome = HistoryOutcome.Skipped;
                        skipReason = localizer.Get("Skip.DailyLimit");
                    }
                    else
                    {
                        SetState(EngineState.Repairing);
                        try
                        {
                            try
                            {
                                repairOutcome = await repair.RepairAsync(finding, settings, ct);
                            }
                            catch (OperationCanceledException ex)
                            {
                                repairCancellation = ex;
                                repairOutcome = new RepairOutcome(
                                    false,
                                    localizer.Get("Repair.Aborted"),
                                    Array.Empty<CommandExecution>());
                            }
                            finally
                            {
                                RecordRepairAttempt(finding.CheckId, clock.UtcNow);
                            }

                            outcome = repairCancellation is not null
                                ? HistoryOutcome.RepairFailed
                                : repairOutcome.Success
                                    ? HistoryOutcome.Repaired
                                    : HistoryOutcome.RepairFailed;
                            if (repairCancellation is null && repairOutcome.Success)
                            {
                                try
                                {
                                    var verification = await check.InspectAsync(settings, ct);
                                    if (verification is not null &&
                                        verification.Status is HealthStatus.Warning or HealthStatus.Critical)
                                    {
                                        repairOutcome = repairOutcome with
                                        {
                                            Summary = $"{repairOutcome.Summary}\n{localizer.Format("Repair.Unverified", verification.Detail)}"
                                        };
                                        outcome = HistoryOutcome.RepairFailed;
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    log.Warn($"Unable to verify repair action {repair.DisplayName}; treating it as repaired.", ex);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            log.Error($"Repair action {repair.DisplayName} failed with an exception.", ex);
                            repairOutcome = new RepairOutcome(false, ex.Message, Array.Empty<CommandExecution>());
                            outcome = HistoryOutcome.RepairFailed;
                        }
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
                entries.Add(entry);
                var append = true;
                if (entry.Outcome is HistoryOutcome.Skipped or
                    HistoryOutcome.ManualActionRequired or
                    HistoryOutcome.Detected &&
                    settings.DuplicateSuppressionMinutes > 0)
                {
                    var previous = await history.FindLastAsync(entry.CheckId, ct);
                    append = previous is null ||
                        previous.Outcome != entry.Outcome ||
                        !string.Equals(previous.Detail, entry.Detail, StringComparison.Ordinal) ||
                        !string.Equals(previous.SkipReason, entry.SkipReason, StringComparison.Ordinal) ||
                        clock.UtcNow - previous.TimestampUtc >=
                        TimeSpan.FromMinutes(settings.DuplicateSuppressionMinutes);
                }

                if (!append)
                {
                    continue;
                }

                await history.AppendAsync(
                    entry,
                    repairCancellation is null ? ct : CancellationToken.None);
                if (history.LastAppendFailed)
                {
                    log.Error($"History entry for {entry.CheckId} could not be saved.");
                    if (repairCancellation is null)
                    {
                        NotifyHistoryFailure(settings);
                    }
                }
                if (repairCancellation is null &&
                    NotificationDecider.ShouldNotify(settings.Notifications, entry) &&
                    ShouldNotifyAfterCooldown(settings, entry))
                {
                    notifier.Notify(new Notification(entry.Title, BuildMessage(entry), entry.Status));
                }

                if (repairCancellation is not null)
                {
                    ExceptionDispatchInfo.Capture(repairCancellation).Throw();
                }
            }

            var finalState = entries.Any(e => e.Status == HealthStatus.Critical &&
                                              e.Outcome != HistoryOutcome.Repaired)
                ? EngineState.Critical
                : entries.Any(e => e.Status == HealthStatus.Warning)
                    ? EngineState.Warning
                    : EngineState.Idle;
            SetState(finalState);
            LastCycleCompletedUtc = clock.UtcNow;
            CycleCompleted?.Invoke(this, EventArgs.Empty);
            return new CycleResult(entries, finalState);
        }
        finally
        {
            SaveCheckState();
            cycleGate.Release();
        }
    }

    private void LoadCheckState()
    {
        if (checkState is null || checkStateLoaded)
        {
            return;
        }

        checkStateLoaded = true;
        try
        {
            foreach (var item in checkState.Load())
            {
                lastInspections[item.Key] = item.Value;
            }
        }
        catch (Exception ex)
        {
            log.Warn("Unable to load check state; continuing without persisted inspection times.", ex);
        }
    }

    private void SaveCheckState()
    {
        if (checkState is null)
        {
            return;
        }

        try
        {
            checkState.Save(lastInspections);
        }
        catch (Exception ex)
        {
            log.Warn("Unable to save check state.", ex);
        }
    }

    private bool ShouldNotifyAfterCooldown(TraySettings settings, HistoryEntry entry)
    {
        var key = (entry.CheckId, entry.Outcome);
        if (lastNotifications.TryGetValue(key, out var previous) &&
            clock.UtcNow - previous < TimeSpan.FromMinutes(settings.NotificationCooldownMinutes))
        {
            return false;
        }

        lastNotifications[key] = clock.UtcNow;
        return true;
    }

    private void NotifyHistoryFailure(TraySettings settings)
    {
        if (settings.Notifications == NotificationMode.None ||
            (settings.NotificationCooldownMinutes > 0 &&
             lastHistoryFailureNotifiedUtc != default &&
             clock.UtcNow - lastHistoryFailureNotifiedUtc <
             TimeSpan.FromMinutes(settings.NotificationCooldownMinutes)))
        {
            return;
        }

        notifier.Notify(new Notification(
            localizer.Get("Notify.HistoryUnavailable.Title"),
            localizer.Get("Notify.HistoryUnavailable.Message"),
            HealthStatus.Warning));
        lastHistoryFailureNotifiedUtc = clock.UtcNow;
    }

    private int CountRecentRepairAttempts(string checkId, DateTimeOffset sinceUtc)
    {
        if (!repairAttempts.TryGetValue(checkId, out var attempts))
        {
            return 0;
        }

        attempts.RemoveAll(timestamp => timestamp < sinceUtc);
        return attempts.Count;
    }

    private void RecordRepairAttempt(string checkId, DateTimeOffset timestamp)
    {
        if (!repairAttempts.TryGetValue(checkId, out var attempts))
        {
            attempts = new List<DateTimeOffset>();
            repairAttempts[checkId] = attempts;
        }

        attempts.Add(timestamp);
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
