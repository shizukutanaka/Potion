using Potion.Tray.Core;
using Potion.Tray.Core.Checks;
using Potion.Tray.Core.Repairs;
using Potion.Tray.Core.Resources;
using Xunit;

namespace Potion.Tray.Core.Tests;

public class HealthCheckTests
{
    [Fact]
    public async Task DiskSpaceHealthCheck_HealthyWarningCriticalAndWorst()
    {
        var system = new FakeSystemInfoProvider
        {
            Drives = new[]
            {
                new DriveSnapshot("C:", 1000, 200),
                new DriveSnapshot("D:", 1000, 100),
                new DriveSnapshot("E:", 1000, 50)
            }
        };
        var finding = await new DiskSpaceHealthCheck(system).InspectAsync(new TraySettings(), default);
        Assert.NotNull(finding);
        Assert.Equal(HealthStatus.Critical, finding.Status);
        Assert.Contains("D:", finding.Detail);
        Assert.Contains("E:", finding.Detail);
        Assert.Equal("10.0%", finding.Metrics!["D:"]);
    }

    [Fact]
    public async Task DiskSpaceHealthCheck_HealthyReturnsNull()
    {
        var system = new FakeSystemInfoProvider
        {
            Drives = new[] { new DriveSnapshot("C:", 1000, 900) }
        };
        Assert.Null(await new DiskSpaceHealthCheck(system).InspectAsync(new TraySettings(), default));
    }

    [Fact]
    public async Task CriticalServiceHealthCheck_ListsStoppedAndIgnoresMissing()
    {
        var system = new FakeSystemInfoProvider
        {
            Services = new[]
            {
                new ServiceSnapshot("wuauserv", true, false),
                new ServiceSnapshot("missing", false, false),
                new ServiceSnapshot("Dnscache", true, true)
            }
        };
        var finding = await new CriticalServiceHealthCheck(system).InspectAsync(new TraySettings(), default);
        Assert.NotNull(finding);
        Assert.Equal(new ResourceLocalizer().Format("Check.CriticalServices.Detail", "wuauserv"), finding.Detail);
    }

    [Fact]
    public async Task CriticalServiceHealthCheck_AllRunningReturnsNull()
    {
        var system = new FakeSystemInfoProvider
        {
            Services = new[] { new ServiceSnapshot("Dnscache", true, true) }
        };
        Assert.Null(await new CriticalServiceHealthCheck(system).InspectAsync(new TraySettings(), default));
    }

    [Fact]
    public async Task ComponentStoreHealthCheck_RepairableIsCritical()
    {
        var runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/CheckHealth" },
            new ProcessRunResult(0, "The component store is repairable", "", TimeSpan.Zero, false));
        var finding = await new ComponentStoreHealthCheck(runner, new FakeLog())
            .InspectAsync(new TraySettings(), default);
        Assert.Equal(HealthStatus.Critical, finding!.Status);
    }

    [Fact]
    public async Task ComponentStoreHealthCheck_CleanAndTimeoutReturnNull()
    {
        var runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/CheckHealth" },
            new ProcessRunResult(0, "No component store corruption detected", "", TimeSpan.Zero, false));
        var log = new FakeLog();
        var check = new ComponentStoreHealthCheck(runner, log);
        Assert.Null(await check.InspectAsync(new TraySettings(), default));
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/CheckHealth" },
            new ProcessRunResult(-1, "The component store is repairable", "", TimeSpan.Zero, true));
        Assert.Null(await check.InspectAsync(new TraySettings(), default));
        Assert.NotEmpty(log.Warnings);
    }

    [Fact]
    public async Task MemoryAndRebootChecksUseThresholds()
    {
        var system = new FakeSystemInfoProvider
        {
            Memory = new MemorySnapshot(100, 5),
            RebootPending = true
        };
        var settings = new TraySettings { MemoryWarnPercent = 10 };
        Assert.Equal(HealthStatus.Warning, (await new MemoryPressureHealthCheck(system).InspectAsync(settings, default))!.Status);
        Assert.NotNull(await new PendingRebootHealthCheck(system).InspectAsync(settings, default));
    }

    [Fact]
    public async Task NetworkHealthCheck_DnsFailureIsCritical()
    {
        var system = new FakeSystemInfoProvider { CanResolveDns = false };
        var finding = await new NetworkHealthCheck(system).InspectAsync(new TraySettings(), default);
        Assert.Equal(HealthStatus.Critical, finding!.Status);
    }
}

public class RepairTests
{
    [Fact]
    public async Task DiskSpaceRepairCleansFilesAndOptionallyRunsDism()
    {
        var cleaner = new FakeTempFileCleaner { Result = new TempCleanupResult(132, 1_500_000_000) };
        var runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/StartComponentCleanup" },
            new ProcessRunResult(0, "", "", TimeSpan.Zero, false));
        var system = new FakeSystemInfoProvider { IsElevated = true };
        var outcome = await new DiskSpaceRepair(cleaner, runner, system).RepairAsync(
            new HealthFinding("disk-space", "ディスク", HealthStatus.Warning, ""), new TraySettings(), default);
        Assert.True(outcome.Success);
        Assert.Contains("132", outcome.Summary);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task DnsFlushRepair_UsesFlushDnsAndChecksAgain()
    {
        var runner = new FakeProcessRunner();
        var system = new FakeSystemInfoProvider { CanResolveDns = true };
        var outcome = await new DnsFlushRepair(runner, system).RepairAsync(
            new HealthFinding("network", "ネットワーク", HealthStatus.Critical, "失敗"), new TraySettings(), default);
        Assert.True(outcome.Success);
        Assert.Equal("ipconfig.exe", runner.Calls[0].FileName);
        Assert.Equal("/flushdns", runner.Calls[0].Arguments[0]);
        Assert.Equal(1, system.DnsCalls);
    }

    [Fact]
    public async Task ServiceRestartRepair_Handles1056AndFailures()
    {
        var runner = new FakeProcessRunner();
        runner.Respond("sc.exe", new[] { "start", "a" }, new ProcessRunResult(1056, "", "", TimeSpan.Zero, false));
        runner.Respond("sc.exe", new[] { "start", "b" }, new ProcessRunResult(1, "", "", TimeSpan.Zero, false));
        var system = new FakeSystemInfoProvider
        {
            Services = new[] { new ServiceSnapshot("a", true, false), new ServiceSnapshot("b", true, false) }
        };
        var outcome = await new ServiceRestartRepair(runner, system).RepairAsync(
            new HealthFinding("critical-services", "サービス", HealthStatus.Critical, ""), new TraySettings(), default);
        Assert.False(outcome.Success);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains("a", outcome.Summary);
        Assert.Contains("b", outcome.Summary);
    }

    [Fact]
    public async Task ComponentStoreRepair_OnlyRunsSfcAfterRestoreSucceeds()
    {
        var runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/RestoreHealth" },
            new ProcessRunResult(0, "", "", TimeSpan.Zero, false));
        runner.Respond("sfc.exe", new[] { "/scannow" },
            new ProcessRunResult(0, "", "", TimeSpan.Zero, false));
        var outcome = await new ComponentStoreRepair(runner).RepairAsync(
            new HealthFinding("component-store", "ストア", HealthStatus.Critical, ""), new TraySettings(), default);
        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.Commands.Count);

        runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/RestoreHealth" },
            new ProcessRunResult(1, "", "", TimeSpan.Zero, false));
        await new ComponentStoreRepair(runner).RepairAsync(
            new HealthFinding("component-store", "ストア", HealthStatus.Critical, ""), new TraySettings(), default);
        Assert.Single(runner.Calls);
    }
}

public class EngineTests
{
    private static HealthFinding Finding(string id = "x", HealthStatus status = HealthStatus.Critical) =>
        new(id, "テスト点検", status, "問題");

    private static SelfHealingEngine Engine(
        IHealthCheck check,
        FakeHistoryStore history,
        FakeSettingsStore settings,
        RecordingNotifier notifier,
        FakeSystemInfoProvider system,
        FakeClock clock,
        IRepairAction[]? repairs = null,
        FakeLog? log = null) =>
        new(new[] { check }, repairs ?? Array.Empty<IRepairAction>(), history, settings, notifier, system, clock, log ?? new FakeLog());

    [Fact]
    public async Task NoFindingProducesNoHistory()
    {
        var history = new FakeHistoryStore();
        var engine = Engine(new DelegateHealthCheck("x", (_, _) => Task.FromResult<HealthFinding?>(null)),
            history, new FakeSettingsStore(), new RecordingNotifier(), new FakeSystemInfoProvider(), new FakeClock());
        var result = await engine.RunCycleAsync(default);
        Assert.Empty(result.Entries);
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task SuccessfulRepairIsRecordedAndNotified()
    {
        var history = new FakeHistoryStore();
        var notifier = new RecordingNotifier();
        var repair = new DelegateRepair("x", false, (_, _, _) =>
            Task.FromResult(new RepairOutcome(true, "完了", Array.Empty<CommandExecution>())));
        var engine = Engine(new DelegateHealthCheck("x", (_, _) => Task.FromResult<HealthFinding?>(Finding())),
            history, new FakeSettingsStore(), notifier, new FakeSystemInfoProvider(), new FakeClock(), new[] { repair });
        var result = await engine.RunCycleAsync(default);
        Assert.Equal(HistoryOutcome.Repaired, result.Entries.Single().Outcome);
        Assert.Single(notifier.Notifications);
    }

    [Fact]
    public async Task DisabledAdminLimitDryRunAndMissingRepairAreHandled()
    {
        var settings = new FakeSettingsStore { Settings = new TraySettings { AutoRepairEnabled = false } };
        var history = new FakeHistoryStore();
        var engine = Engine(new DelegateHealthCheck("x", (_, _) => Task.FromResult<HealthFinding?>(Finding())),
            history, settings, new RecordingNotifier(), new FakeSystemInfoProvider(), new FakeClock(),
            new[] { new DelegateRepair("x", true, (_, _, _) => throw new InvalidOperationException()) });
        Assert.Equal(new ResourceLocalizer().Get("Skip.AutoRepairDisabled"), (await engine.RunCycleAsync(default)).Entries.Single().SkipReason);

        settings.Settings = new TraySettings { DryRun = true };
        Assert.Equal(new ResourceLocalizer().Get("Skip.DryRun"), (await engine.RunCycleAsync(default)).Entries.Single().SkipReason);

        settings.Settings = new TraySettings();
        var nonElevated = new FakeSystemInfoProvider { IsElevated = false };
        engine = Engine(new DelegateHealthCheck("x", (_, _) => Task.FromResult<HealthFinding?>(Finding())),
            history = new FakeHistoryStore(), settings, new RecordingNotifier(), nonElevated, new FakeClock(),
            new[] { new DelegateRepair("x", true, (_, _, _) => throw new InvalidOperationException()) });
        Assert.Equal(new ResourceLocalizer().Get("Skip.AdminRequired"), (await engine.RunCycleAsync(default)).Entries.Single().SkipReason);

        settings.Settings = new TraySettings { MaxRepairAttemptsPerDay = 1 };
        history.Attempts = 1;
        nonElevated.IsElevated = true;
        Assert.Equal(new ResourceLocalizer().Get("Skip.DailyLimit"), (await engine.RunCycleAsync(default)).Entries.Single().SkipReason);

        engine = Engine(new DelegateHealthCheck("missing", (_, _) => Task.FromResult<HealthFinding?>(Finding("missing"))),
            new FakeHistoryStore(), new FakeSettingsStore(), new RecordingNotifier(), new FakeSystemInfoProvider(), new FakeClock());
        Assert.Equal(HistoryOutcome.ManualActionRequired, (await engine.RunCycleAsync(default)).Entries.Single().Outcome);
    }

    [Fact]
    public async Task InspectionExceptionDoesNotStopNextCheck()
    {
        var first = new DelegateHealthCheck("first", (_, _) => throw new InvalidOperationException("bad"));
        var second = new DelegateHealthCheck("second", (_, _) => Task.FromResult<HealthFinding?>(Finding("second")));
        var history = new FakeHistoryStore();
        var engine = new SelfHealingEngine(new[] { first, second }, Array.Empty<IRepairAction>(),
            history, new FakeSettingsStore(), new RecordingNotifier(), new FakeSystemInfoProvider(), new FakeClock(), new FakeLog());
        await engine.RunCycleAsync(default);
        Assert.Single(history.Entries);
        Assert.Equal("second", history.Entries[0].CheckId);
    }

    [Fact]
    public async Task CheckIntervalSkipsUntilElapsed()
    {
        var clock = new FakeClock();
        var settings = new FakeSettingsStore { Settings = new TraySettings { CheckIntervalMinutes = new() { ["x"] = 10 } } };
        var calls = 0;
        var check = new DelegateHealthCheck("x", (_, _) =>
        {
            calls++;
            return Task.FromResult<HealthFinding?>(null);
        });
        var engine = Engine(check, new FakeHistoryStore(), settings, new RecordingNotifier(), new FakeSystemInfoProvider(), clock);
        await engine.RunCycleAsync(default);
        await engine.RunCycleAsync(default);
        Assert.Equal(1, calls);
        clock.UtcNow = clock.UtcNow.AddMinutes(11);
        await engine.RunCycleAsync(default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentCycleReturnsEmptyResult()
    {
        var check = new BlockingHealthCheck();
        var engine = Engine(check, new FakeHistoryStore(), new FakeSettingsStore(), new RecordingNotifier(), new FakeSystemInfoProvider(), new FakeClock());
        var first = engine.RunCycleAsync(default);
        await check.Started.Task;
        var second = await engine.RunCycleAsync(default);
        Assert.Empty(second.Entries);
        check.Continue.SetResult(true);
        await first;
    }
}

public class SettingsAndNotificationTests
{
    [Fact]
    public void NormalizeClampsAndRepairsDefaults()
    {
        var settings = new TraySettings
        {
            ScanIntervalMinutes = -1,
            DiskWarnPercent = 10,
            DiskCriticalPercent = 90,
            MonitoredServices = new() { "", "  ", "Dnscache", "dnscache" }
        };
        settings.Normalize();
        Assert.Equal(1, settings.ScanIntervalMinutes);
        Assert.Equal(10, settings.DiskCriticalPercent);
        Assert.Single(settings.MonitoredServices);
    }

    [Fact]
    public void NotificationDeciderCoversModes()
    {
        var repaired = Entry(HistoryOutcome.Repaired, HealthStatus.Warning);
        var failed = Entry(HistoryOutcome.RepairFailed, HealthStatus.Warning);
        var manual = Entry(HistoryOutcome.ManualActionRequired, HealthStatus.Critical);
        var skippedCritical = Entry(HistoryOutcome.Skipped, HealthStatus.Critical);
        var skippedWarning = Entry(HistoryOutcome.Skipped, HealthStatus.Warning);
        Assert.True(NotificationDecider.ShouldNotify(NotificationMode.All, skippedWarning));
        Assert.False(NotificationDecider.ShouldNotify(NotificationMode.None, repaired));
        Assert.True(NotificationDecider.ShouldNotify(NotificationMode.RepairsOnly, repaired));
        Assert.True(NotificationDecider.ShouldNotify(NotificationMode.RepairsOnly, manual));
        Assert.False(NotificationDecider.ShouldNotify(NotificationMode.RepairsOnly, skippedWarning));
        Assert.True(NotificationDecider.ShouldNotify(NotificationMode.FailuresOnly, failed));
        Assert.True(NotificationDecider.ShouldNotify(NotificationMode.FailuresOnly, skippedCritical));
        Assert.False(NotificationDecider.ShouldNotify(NotificationMode.FailuresOnly, skippedWarning));
    }

    public static IEnumerable<object[]> NotificationCases()
    {
        foreach (var mode in Enum.GetValues<NotificationMode>())
        {
            foreach (var outcome in Enum.GetValues<HistoryOutcome>())
            {
                foreach (var status in new[] { HealthStatus.Healthy, HealthStatus.Warning, HealthStatus.Critical })
                {
                    var expected = mode switch
                    {
                        NotificationMode.None => false,
                        NotificationMode.All => true,
                        NotificationMode.RepairsOnly => outcome is
                            HistoryOutcome.Repaired or HistoryOutcome.RepairFailed or HistoryOutcome.ManualActionRequired,
                        NotificationMode.FailuresOnly => outcome is
                            HistoryOutcome.RepairFailed or HistoryOutcome.ManualActionRequired ||
                            outcome == HistoryOutcome.Skipped && status == HealthStatus.Critical,
                        _ => false
                    };
                    yield return new object[] { mode, outcome, status, expected };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(NotificationCases))]
    public void NotificationDeciderCoversAllModesAndOutcomes(
        NotificationMode mode, HistoryOutcome outcome, HealthStatus status, bool expected) =>
        Assert.Equal(expected, NotificationDecider.ShouldNotify(mode, Entry(outcome, status)));

    private static HistoryEntry Entry(HistoryOutcome outcome, HealthStatus status) =>
        new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "x", "x", status, outcome, "x", null, null, TimeSpan.Zero, Array.Empty<CommandExecution>());
}

public class StoreTests
{
    [Fact]
    public async Task JsonlHistoryStoreReadsNewestSkipsCorruptAndCountsAttempts()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "history.jsonl");
        using var store = new JsonlHistoryStore(path, maxEntries: 100, retentionDays: 90);
        await store.AppendAsync(Entry("a", HistoryOutcome.Repaired), default);
        await store.AppendAsync(Entry("b", HistoryOutcome.RepairFailed), default);
        await File.AppendAllTextAsync(path, "{broken}\n");
        var recent = await store.ReadRecentAsync(10, default);
        Assert.Equal(2, recent.Count);
        Assert.Equal(2, await store.CountRepairAttemptsSinceAsync("a", DateTimeOffset.UtcNow.AddDays(-1), default) +
                         await store.CountRepairAttemptsSinceAsync("b", DateTimeOffset.UtcNow.AddDays(-1), default));
    }

    [Fact]
    public async Task JsonlHistoryStorePrunesCountAndRetention()
    {
        using var directory = new TempDirectory();
        using var store = new JsonlHistoryStore(Path.Combine(directory.Path, "history.jsonl"), maxEntries: 2, retentionDays: 90, compactionThreshold: 1);
        foreach (var id in Enumerable.Range(1, 22))
        {
            await store.AppendAsync(Entry(id.ToString(), HistoryOutcome.Detected), default);
        }
        Assert.Equal(2, (await store.ReadRecentAsync(10, default)).Count);
    }

    [Fact]
    public async Task JsonlHistoryStorePrunesExpiredEntries()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "history.jsonl");
        using var store = new JsonlHistoryStore(path, maxEntries: 100, retentionDays: 90, compactionThreshold: 1);
        var old = Entry("old", HistoryOutcome.Detected) with { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-91) };
        await store.AppendAsync(old, default);
        await store.AppendAsync(Entry("new", HistoryOutcome.Detected), default);
        var entries = await store.ReadRecentAsync(10, default);
        Assert.Single(entries);
        Assert.Equal("new", entries[0].Id);
    }

    [Fact]
    public async Task JsonlHistoryStoreUsesInjectedClockForRetention()
    {
        using var directory = new TempDirectory();
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock { UtcNow = now };
        using var store = new JsonlHistoryStore(
            Path.Combine(directory.Path, "history.jsonl"),
            maxEntries: 100,
            retentionDays: 90,
            compactionThreshold: 1,
            clock: clock);
        await store.AppendAsync(
            Entry("old", HistoryOutcome.Detected) with { TimestampUtc = now.AddDays(-91) },
            default);
        await store.AppendAsync(
            Entry("new", HistoryOutcome.Detected) with { TimestampUtc = now },
            default);

        var entries = await store.ReadRecentAsync(10, default);
        Assert.Single(entries);
        Assert.Equal("new", entries[0].Id);
    }

    [Fact]
    public async Task JsonlHistoryStoreSupportsParallelAppends()
    {
        using var directory = new TempDirectory();
        using var store = new JsonlHistoryStore(Path.Combine(directory.Path, "history.jsonl"), maxEntries: 100, retentionDays: 90);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => store.AppendAsync(Entry(i.ToString(), HistoryOutcome.Detected), default)));
        Assert.Equal(50, (await store.ReadRecentAsync(100, default)).Count);
    }

    [Fact]
    public void JsonSettingsStoreDefaultsRoundTripAndBrokenJson()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var log = new FakeLog();
        var store = new JsonSettingsStore(path, log);
        Assert.True(store.Load().AutoRepairEnabled);
        var settings = new TraySettings { AutoRepairEnabled = false, DiskCriticalPercent = 20, DiskWarnPercent = 10 };
        store.Save(settings);
        var loaded = store.Load();
        Assert.False(loaded.AutoRepairEnabled);
        Assert.Equal(10, loaded.DiskCriticalPercent);
        File.WriteAllText(path, "{broken}");
        Assert.True(store.Load().AutoRepairEnabled);
        Assert.NotEmpty(log.Warnings);
    }

    private static HistoryEntry Entry(string id, HistoryOutcome outcome) =>
        new(id, DateTimeOffset.UtcNow, id, id, HealthStatus.Warning, outcome, id, null, null, TimeSpan.Zero, Array.Empty<CommandExecution>());
}

public class ProcessRunnerTests
{
    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("..\\evil.exe")]
    [InlineData("cmd.exe /c ...")]
    public void AllowListRejectsUnsafeNames(string name) =>
        Assert.False(CommandAllowList.IsAllowed(name));

    [Fact]
    public async Task RunnerRejectsUnsafeNameBeforeStartingProcess()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SystemProcessRunner().RunAsync("powershell.exe", Array.Empty<string>(), TimeSpan.FromSeconds(1), default));
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "potion-tests-" + Guid.NewGuid().ToString("N"));
    public string Path { get; }
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
