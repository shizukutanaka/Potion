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
    public async Task CriticalServiceHealthCheck_IgnoresDisabledStoppedServices()
    {
        var system = new FakeSystemInfoProvider
        {
            Services = new[]
            {
                new ServiceSnapshot("disabled", true, false, ServiceStartType.Disabled)
            }
        };

        Assert.Null(await new CriticalServiceHealthCheck(system).InspectAsync(new TraySettings(), default));
    }

    [Fact]
    public async Task ComponentStoreHealthCheck_RepairableIsCritical()
    {
        var runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/CheckHealth", "/English" },
            new ProcessRunResult(0, "The component store is repairable", "", TimeSpan.Zero, false));
        var finding = await new ComponentStoreHealthCheck(runner, new FakeLog())
            .InspectAsync(new TraySettings(), default);
        Assert.Equal(HealthStatus.Critical, finding!.Status);
    }

    [Fact]
    public async Task ComponentStoreHealthCheck_CleanAndTimeoutReturnNull()
    {
        var runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/CheckHealth", "/English" },
            new ProcessRunResult(0, "No component store corruption detected", "", TimeSpan.Zero, false));
        var log = new FakeLog();
        var check = new ComponentStoreHealthCheck(runner, log);
        Assert.Null(await check.InspectAsync(new TraySettings(), default));
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/CheckHealth", "/English" },
            new ProcessRunResult(-1, "The component store is repairable", "", TimeSpan.Zero, true));
        Assert.Null(await check.InspectAsync(new TraySettings(), default));
        Assert.NotEmpty(log.Warnings);
    }

    [Fact]
    public async Task ComponentStoreHealthCheck_FallsBackWhenEnglishOptionIsRejected()
    {
        var runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/CheckHealth", "/English" },
            new ProcessRunResult(87, "unrecognized option", "", TimeSpan.Zero, false));
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/CheckHealth" },
            new ProcessRunResult(0, "No component store corruption detected", "", TimeSpan.Zero, false));

        var finding = await new ComponentStoreHealthCheck(runner, new FakeLog())
            .InspectAsync(new TraySettings(), default);

        Assert.Null(finding);
        Assert.Equal(2, runner.Calls.Count);
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
    public async Task MemoryPressureHealthCheck_ListsTopMemoryProcesses()
    {
        var system = new FakeSystemInfoProvider
        {
            Memory = new MemorySnapshot(100, 5),
            TopMemoryProcesses = new[]
            {
                new ProcessMemorySnapshot("browser", 2L * 1024 * 1024 * 1024)
            }
        };

        var finding = await new MemoryPressureHealthCheck(system).InspectAsync(new TraySettings(), default);

        Assert.Contains("browser", finding!.Detail);
    }

    [Fact]
    public async Task MemoryPressureHealthCheck_WithoutTopProcessesKeepsExistingDetail()
    {
        var system = new FakeSystemInfoProvider
        {
            Memory = new MemorySnapshot(100, 5)
        };

        var finding = await new MemoryPressureHealthCheck(system).InspectAsync(new TraySettings(), default);

        Assert.Equal(
            new ResourceLocalizer().Format("Check.MemoryPressure.Detail", "5.0"),
            finding!.Detail);
    }

    [Fact]
    public async Task NetworkHealthCheck_DnsFailureIsCritical()
    {
        var system = new FakeSystemInfoProvider { CanResolveDns = false };
        var finding = await new NetworkHealthCheck(
            system,
            maxAttempts: 3,
            retryInterval: TimeSpan.Zero).InspectAsync(new TraySettings(), default);
        Assert.Equal(HealthStatus.Critical, finding!.Status);
    }

    [Fact]
    public async Task NetworkHealthCheck_TransientDnsFailureIsIgnored()
    {
        var system = new FakeSystemInfoProvider();
        system.DnsResults.Enqueue(false);
        system.DnsResults.Enqueue(true);

        var finding = await new NetworkHealthCheck(
            system,
            maxAttempts: 3,
            retryInterval: TimeSpan.Zero).InspectAsync(new TraySettings(), default);

        Assert.Null(finding);
        Assert.Equal(2, system.DnsCalls);
    }

    [Fact]
    public async Task NetworkHealthCheck_ThreeDnsFailuresAreCritical()
    {
        var system = new FakeSystemInfoProvider { CanResolveDns = false };

        var finding = await new NetworkHealthCheck(
            system,
            maxAttempts: 3,
            retryInterval: TimeSpan.Zero).InspectAsync(new TraySettings(), default);

        Assert.Equal(HealthStatus.Critical, finding!.Status);
        Assert.Equal(3, system.DnsCalls);
    }

    [Fact]
    public async Task NetworkHealthCheck_LosingNetworkDuringRetryIsIgnored()
    {
        var system = new FakeSystemInfoProvider { CanResolveDns = false };
        system.OnDnsCall = calls =>
        {
            if (calls == 1)
            {
                system.IsNetworkAvailable = false;
            }
        };

        var finding = await new NetworkHealthCheck(
            system,
            maxAttempts: 3,
            retryInterval: TimeSpan.Zero).InspectAsync(new TraySettings(), default);

        Assert.Null(finding);
        Assert.Equal(1, system.DnsCalls);
    }

    [Fact]
    public async Task NetworkHealthCheck_OfflineReturnsNoFinding()
    {
        var system = new FakeSystemInfoProvider { IsNetworkAvailable = false, CanResolveDns = false };

        Assert.Null(await new NetworkHealthCheck(system).InspectAsync(new TraySettings(), default));
        Assert.Equal(0, system.DnsCalls);
    }

    [Fact]
    public async Task DiskSpaceHealthCheck_LargeDriveWithLowPercentageButAmpleSpaceIsHealthy()
    {
        const long gibibyte = 1024L * 1024 * 1024;
        var system = new FakeSystemInfoProvider
        {
            Drives = new[] { new DriveSnapshot("C:", 4_000L * gibibyte, 600L * gibibyte) }
        };

        Assert.Null(await new DiskSpaceHealthCheck(system).InspectAsync(new TraySettings(), default));
    }

    [Fact]
    public async Task DiskSpaceHealthCheck_LowPercentageAndAbsoluteSpaceIsCritical()
    {
        const long gibibyte = 1024L * 1024 * 1024;
        var system = new FakeSystemInfoProvider
        {
            Drives = new[] { new DriveSnapshot("C:", 64L * gibibyte, 3L * gibibyte) }
        };

        var finding = await new DiskSpaceHealthCheck(system).InspectAsync(new TraySettings(), default);

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
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/StartComponentCleanup", "/English" },
            new ProcessRunResult(0, "", "", TimeSpan.Zero, false));
        var system = new FakeSystemInfoProvider { IsElevated = true };
        var outcome = await new DiskSpaceRepair(cleaner, runner, system).RepairAsync(
            new HealthFinding("disk-space", "ディスク", HealthStatus.Warning, ""), new TraySettings(), default);
        Assert.True(outcome.Success);
        Assert.Contains("132", outcome.Summary);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task DiskSpaceRepairUsesOneDayAgeForCriticalFindings()
    {
        var cleaner = new FakeTempFileCleaner();
        var repair = new DiskSpaceRepair(
            cleaner,
            new FakeProcessRunner(),
            new FakeSystemInfoProvider(),
            new ResourceLocalizer());

        await repair.RepairAsync(
            new HealthFinding("disk-space", "disk", HealthStatus.Critical, ""), new TraySettings(), default);
        Assert.Equal(TimeSpan.FromDays(1), cleaner.MinimumAge);
    }

    [Fact]
    public async Task DiskSpaceRepairUsesSevenDayAgeForWarningFindings()
    {
        var cleaner = new FakeTempFileCleaner();
        var repair = new DiskSpaceRepair(
            cleaner,
            new FakeProcessRunner(),
            new FakeSystemInfoProvider(),
            new ResourceLocalizer());

        await repair.RepairAsync(
            new HealthFinding("disk-space", "disk", HealthStatus.Warning, ""), new TraySettings(), default);
        Assert.Equal(TimeSpan.FromDays(7), cleaner.MinimumAge);
    }

    [Fact]
    public async Task DiskSpaceRepairIncludesLargeStorageConsumers()
    {
        var system = new FakeSystemInfoProvider
        {
            LargeStorageConsumers = new[]
            {
                new StorageConsumer("Storage.Downloads", 2L * 1024 * 1024 * 1024)
            }
        };
        var repair = new DiskSpaceRepair(
            new FakeTempFileCleaner(),
            new FakeProcessRunner(),
            system,
            new ResourceLocalizer());

        var outcome = await repair.RepairAsync(
            new HealthFinding("disk-space", "disk", HealthStatus.Warning, ""), new TraySettings
            {
                AllowComponentCleanup = false
            }, default);

        Assert.Contains(new ResourceLocalizer().Get("Storage.Downloads"), outcome.Summary);
        Assert.Contains("2.0", outcome.Summary);
    }

    [Fact]
    public async Task DiskSpaceRepairMarksTruncatedStorageConsumersAsLowerBounds()
    {
        var system = new FakeSystemInfoProvider
        {
            LargeStorageConsumers = new[]
            {
                new StorageConsumer("Storage.Downloads", 2L * 1024 * 1024 * 1024, true)
            }
        };
        var repair = new DiskSpaceRepair(
            new FakeTempFileCleaner(),
            new FakeProcessRunner(),
            system,
            new ResourceLocalizer());

        var outcome = await repair.RepairAsync(
            new HealthFinding("disk-space", "disk", HealthStatus.Warning, ""), new TraySettings
            {
                AllowComponentCleanup = false
            }, default);

        Assert.Contains("2.0 GB+", outcome.Summary);
    }

    [Fact]
    public async Task DiskSpaceRepairOmitsStorageConsumersWhenNoneAreReported()
    {
        var localizer = new ResourceLocalizer();
        var repair = new DiskSpaceRepair(
            new FakeTempFileCleaner(),
            new FakeProcessRunner(),
            new FakeSystemInfoProvider(),
            localizer);

        var outcome = await repair.RepairAsync(
            new HealthFinding("disk-space", "disk", HealthStatus.Warning, ""), new TraySettings
            {
                AllowComponentCleanup = false
            }, default);

        Assert.Equal(localizer.Get("Repair.DiskSpace.NoneSummary"), outcome.Summary);
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
    public async Task ServiceRestartRepair_StartsOnlyAutomaticStoppedServices()
    {
        var runner = new FakeProcessRunner();
        runner.Respond("sc.exe", new[] { "start", "automatic" },
            new ProcessRunResult(0, "", "", TimeSpan.Zero, false));
        var system = new FakeSystemInfoProvider
        {
            Services = new[]
            {
                new ServiceSnapshot("automatic", true, false, ServiceStartType.Automatic),
                new ServiceSnapshot("disabled", true, false, ServiceStartType.Disabled),
                new ServiceSnapshot("manual", true, false, ServiceStartType.Manual)
            }
        };

        var outcome = await new ServiceRestartRepair(runner, system).RepairAsync(
            new HealthFinding("critical-services", "services", HealthStatus.Critical, ""), new TraySettings(), default);

        Assert.True(outcome.Success);
        Assert.Single(runner.Calls);
        Assert.Equal("automatic", runner.Calls[0].Arguments[1]);
    }

    [Fact]
    public async Task ComponentStoreRepair_OnlyRunsSfcAfterRestoreSucceeds()
    {
        var runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/RestoreHealth", "/English" },
            new ProcessRunResult(0, "", "", TimeSpan.Zero, false));
        runner.Respond("sfc.exe", new[] { "/scannow" },
            new ProcessRunResult(0, "", "", TimeSpan.Zero, false));
        var outcome = await new ComponentStoreRepair(runner).RepairAsync(
            new HealthFinding("component-store", "ストア", HealthStatus.Critical, ""), new TraySettings(), default);
        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.Commands.Count);

        runner = new FakeProcessRunner();
        runner.Respond("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/RestoreHealth", "/English" },
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
        FakeLog? log = null,
        ICheckStateStore? checkState = null) =>
        new(new[] { check }, repairs ?? Array.Empty<IRepairAction>(), history, settings, notifier, system, clock, log ?? new FakeLog(), checkState: checkState);

    private static HistoryEntry History(string id, string checkId, HistoryOutcome outcome, DateTimeOffset timestamp) =>
        new(id, timestamp, checkId, checkId, HealthStatus.Critical, outcome, "old detail", null, null, TimeSpan.Zero, Array.Empty<CommandExecution>());

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
    public async Task CanceledCycleDoesNotPublishCompletion()
    {
        var completed = 0;
        var check = new DelegateHealthCheck("x", (_, _) =>
            throw new OperationCanceledException());
        var engine = Engine(check, new FakeHistoryStore(), new FakeSettingsStore(),
            new RecordingNotifier(), new FakeSystemInfoProvider(), new FakeClock());
        engine.CycleCompleted += (_, _) => completed++;

        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.RunCycleAsync(default));

        Assert.Null(engine.LastCycleCompletedUtc);
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task CanceledRepairIsRecordedAsAborted()
    {
        var history = new FakeHistoryStore();
        var repair = new DelegateRepair("x", false, (_, _, _) =>
            throw new OperationCanceledException());
        var engine = Engine(
            new DelegateHealthCheck("x", (_, _) => Task.FromResult<HealthFinding?>(Finding())),
            history,
            new FakeSettingsStore(),
            new RecordingNotifier(),
            new FakeSystemInfoProvider(),
            new FakeClock(),
            new[] { repair });

        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.RunCycleAsync(default));

        var entry = Assert.Single(history.Entries);
        Assert.Equal(HistoryOutcome.RepairFailed, entry.Outcome);
        Assert.Equal(new ResourceLocalizer().Get("Repair.Aborted"), entry.RepairSummary);
    }

    [Fact]
    public async Task CanceledRepairDoesNotNotify()
    {
        var notifier = new RecordingNotifier();
        var repair = new DelegateRepair("x", false, (_, _, _) =>
            throw new OperationCanceledException());
        var engine = Engine(
            new DelegateHealthCheck("x", (_, _) => Task.FromResult<HealthFinding?>(Finding())),
            new FakeHistoryStore(),
            new FakeSettingsStore(),
            notifier,
            new FakeSystemInfoProvider(),
            new FakeClock(),
            new[] { repair });

        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.RunCycleAsync(default));

        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task IneffectiveRepairStopsAfterConsecutiveFailuresAndAddsAdvice()
    {
        var clock = new FakeClock();
        var history = new FakeHistoryStore();
        history.Entries.AddRange(new[]
        {
            History("old-failure", "disk-space", HistoryOutcome.RepairFailed, clock.UtcNow.AddHours(-1)),
            History("older-failure", "disk-space", HistoryOutcome.RepairFailed, clock.UtcNow.AddHours(-2))
        });
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings { MaxRepairAttemptsPerDay = 2 }
        };
        var repairCalls = 0;
        var engine = Engine(
            new DelegateHealthCheck("disk-space", (_, _) =>
                Task.FromResult<HealthFinding?>(Finding("disk-space"))),
            history,
            settings,
            new RecordingNotifier(),
            new FakeSystemInfoProvider(),
            clock,
            new[] { new DelegateRepair("disk-space", false, (_, _, _) =>
            {
                repairCalls++;
                return Task.FromResult(new RepairOutcome(true, "unused", Array.Empty<CommandExecution>()));
            }) });

        var entry = (await engine.RunCycleAsync(default)).Entries.Single();

        Assert.Equal(0, repairCalls);
        Assert.Equal(HistoryOutcome.ManualActionRequired, entry.Outcome);
        Assert.Equal(new ResourceLocalizer().Get("Skip.RepairIneffective"), entry.SkipReason);
        Assert.Contains(new ResourceLocalizer().Get("Advice.disk-space"), entry.Detail);
    }

    [Fact]
    public async Task RepairedHistoryBreaksConsecutiveFailureCount()
    {
        var clock = new FakeClock();
        var history = new FakeHistoryStore();
        history.Entries.AddRange(new[]
        {
            History("failure", "disk-space", HistoryOutcome.RepairFailed, clock.UtcNow.AddHours(-2)),
            History("repaired", "disk-space", HistoryOutcome.Repaired, clock.UtcNow.AddHours(-1))
        });
        history.Attempts = 2;
        var repairCalls = 0;
        var engine = Engine(
            new SequenceHealthCheck("disk-space", Finding("disk-space"), null),
            history,
            new FakeSettingsStore { Settings = new TraySettings { MaxRepairAttemptsPerDay = 3 } },
            new RecordingNotifier(),
            new FakeSystemInfoProvider(),
            clock,
            new[] { new DelegateRepair("disk-space", false, (_, _, _) =>
            {
                repairCalls++;
                return Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()));
            }) });

        await engine.RunCycleAsync(default);

        Assert.Equal(1, repairCalls);
    }

    [Fact]
    public async Task FailuresOlderThanSevenDaysDoNotStopRepair()
    {
        var clock = new FakeClock();
        var history = new FakeHistoryStore();
        history.Entries.AddRange(new[]
        {
            History("old-failure", "disk-space", HistoryOutcome.RepairFailed, clock.UtcNow.AddDays(-8)),
            History("older-failure", "disk-space", HistoryOutcome.RepairFailed, clock.UtcNow.AddDays(-9))
        });
        var repairCalls = 0;
        var engine = Engine(
            new SequenceHealthCheck("disk-space", Finding("disk-space"), null),
            history,
            new FakeSettingsStore { Settings = new TraySettings { MaxRepairAttemptsPerDay = 2 } },
            new RecordingNotifier(),
            new FakeSystemInfoProvider(),
            clock,
            new[] { new DelegateRepair("disk-space", false, (_, _, _) =>
            {
                repairCalls++;
                return Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()));
            }) });

        await engine.RunCycleAsync(default);

        Assert.Equal(1, repairCalls);
    }

    [Fact]
    public async Task SuccessfulRepairIsRecordedAndNotified()
    {
        var history = new FakeHistoryStore();
        var notifier = new RecordingNotifier();
        var repair = new DelegateRepair("x", false, (_, _, _) =>
            Task.FromResult(new RepairOutcome(true, "完了", Array.Empty<CommandExecution>())));
        var check = new SequenceHealthCheck("x", Finding(), null);
        var engine = Engine(check,
            history, new FakeSettingsStore(), notifier, new FakeSystemInfoProvider(), new FakeClock(), new[] { repair });
        var result = await engine.RunCycleAsync(default);
        Assert.Equal(HistoryOutcome.Repaired, result.Entries.Single().Outcome);
        Assert.Single(notifier.Notifications);
        Assert.Equal(2, check.Calls);
    }

    [Fact]
    public async Task SuccessfulRepairWithHealthyVerificationIsRepaired()
    {
        var check = new SequenceHealthCheck("x", Finding(), null);
        var engine = Engine(check, new FakeHistoryStore(), new FakeSettingsStore(), new RecordingNotifier(),
            new FakeSystemInfoProvider(), new FakeClock(),
            new[] { new DelegateRepair("x", false, (_, _, _) =>
                Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()))) });

        var result = await engine.RunCycleAsync(default);

        Assert.Equal(HistoryOutcome.Repaired, result.Entries.Single().Outcome);
        Assert.Equal(2, check.Calls);
    }

    [Fact]
    public async Task FailedVerificationMarksRepairFailedWithRemainingDetail()
    {
        var check = new SequenceHealthCheck(
            "x",
            Finding("x", HealthStatus.Critical),
            new HealthFinding("x", "x", HealthStatus.Critical, "still present"));
        var engine = Engine(check, new FakeHistoryStore(), new FakeSettingsStore(), new RecordingNotifier(),
            new FakeSystemInfoProvider(), new FakeClock(),
            new[] { new DelegateRepair("x", false, (_, _, _) =>
                Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()))) });

        var result = await engine.RunCycleAsync(default);

        var entry = result.Entries.Single();
        Assert.Equal(HistoryOutcome.RepairFailed, entry.Outcome);
        Assert.Contains("still present", entry.RepairSummary);
        Assert.Equal(2, check.Calls);
    }

    [Fact]
    public async Task VerificationExceptionKeepsRepairSuccessful()
    {
        var calls = 0;
        var check = new DelegateHealthCheck("x", (_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult<HealthFinding?>(Finding());
            }

            throw new InvalidOperationException("verification failed");
        });
        var log = new FakeLog();
        var engine = Engine(check, new FakeHistoryStore(), new FakeSettingsStore(), new RecordingNotifier(),
            new FakeSystemInfoProvider(), new FakeClock(),
            new[] { new DelegateRepair("x", false, (_, _, _) =>
                Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()))) }, log);

        var result = await engine.RunCycleAsync(default);

        Assert.Equal(HistoryOutcome.Repaired, result.Entries.Single().Outcome);
        Assert.NotEmpty(log.Warnings);
    }

    [Fact]
    public async Task DuplicateSkippedEntryIsNotAppended()
    {
        var settings = new FakeSettingsStore { Settings = new TraySettings { AutoRepairEnabled = false } };
        var history = new FakeHistoryStore();
        var check = new SequenceHealthCheck("x", Finding(), Finding());
        var engine = Engine(check, history, settings, new RecordingNotifier(),
            new FakeSystemInfoProvider(), new FakeClock(),
            new[] { new DelegateRepair("x", false, (_, _, _) =>
                Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()))) });

        var first = await engine.RunCycleAsync(default);
        var second = await engine.RunCycleAsync(default);

        Assert.Single(history.Entries);
        Assert.Single(first.Entries);
        Assert.Single(second.Entries);
    }

    [Fact]
    public async Task HistoryAppendFailureNotifiesUser()
    {
        var history = new FakeHistoryStore { FailAppend = true };
        var notifier = new RecordingNotifier();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings
            {
                Notifications = NotificationMode.RepairsOnly,
                AutoRepairEnabled = false
            }
        };
        var engine = Engine(new DelegateHealthCheck("x", (_, _) =>
                Task.FromResult<HealthFinding?>(Finding(status: HealthStatus.Warning))),
            history, settings, notifier, new FakeSystemInfoProvider(), new FakeClock(),
            new[] { new DelegateRepair("x", false, (_, _, _) =>
                Task.FromResult(new RepairOutcome(true, "unused", Array.Empty<CommandExecution>()))) });

        await engine.RunCycleAsync(default);

        var notification = Assert.Single(notifier.Notifications);
        Assert.Equal(new ResourceLocalizer().Get("Notify.HistoryUnavailable.Title"), notification.Title);
    }

    [Fact]
    public async Task HistoryAppendFailureDoesNotNotifyWhenNotificationsDisabled()
    {
        var history = new FakeHistoryStore { FailAppend = true };
        var notifier = new RecordingNotifier();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings { Notifications = NotificationMode.None, AutoRepairEnabled = false }
        };
        var engine = Engine(new DelegateHealthCheck("x", (_, _) =>
                Task.FromResult<HealthFinding?>(Finding())),
            history, settings, notifier, new FakeSystemInfoProvider(), new FakeClock());

        await engine.RunCycleAsync(default);

        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public async Task RepairedEntriesAreAlwaysAppended()
    {
        var history = new FakeHistoryStore();
        var check = new SequenceHealthCheck("x", Finding(), null, Finding(), null);
        var engine = Engine(check, history, new FakeSettingsStore(), new RecordingNotifier(),
            new FakeSystemInfoProvider(), new FakeClock(),
            new[] { new DelegateRepair("x", false, (_, _, _) =>
                Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()))) });

        await engine.RunCycleAsync(default);
        await engine.RunCycleAsync(default);

        Assert.Equal(2, history.Entries.Count);
    }

    [Fact]
    public async Task NotificationCooldownSuppressesThenAllowsNotification()
    {
        var clock = new FakeClock();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings
            {
                Notifications = NotificationMode.All,
                DuplicateSuppressionMinutes = 0,
                NotificationCooldownMinutes = 60,
                AutoRepairEnabled = false
            }
        };
        var notifier = new RecordingNotifier();
        var check = new SequenceHealthCheck("x", Finding(), Finding(), Finding());
        var engine = Engine(check, new FakeHistoryStore(), settings, notifier, new FakeSystemInfoProvider(), clock,
            new[] { new DelegateRepair("x", false, (_, _, _) =>
                Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()))) });

        await engine.RunCycleAsync(default);
        await engine.RunCycleAsync(default);
        Assert.Single(notifier.Notifications);
        clock.UtcNow = clock.UtcNow.AddMinutes(61);
        await engine.RunCycleAsync(default);
        Assert.Equal(2, notifier.Notifications.Count);
    }

    [Fact]
    public async Task NotificationOutcomeChangeBypassesCooldown()
    {
        var clock = new FakeClock();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings { Notifications = NotificationMode.All, AutoRepairEnabled = false }
        };
        var notifier = new RecordingNotifier();
        var check = new SequenceHealthCheck("x", Finding(), Finding(), null);
        var repair = new DelegateRepair("x", false, (_, _, _) =>
            Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>())));
        var engine = Engine(check, new FakeHistoryStore(), settings, notifier, new FakeSystemInfoProvider(), clock,
            new[] { repair });

        await engine.RunCycleAsync(default);
        settings.Settings.AutoRepairEnabled = true;
        await engine.RunCycleAsync(default);

        Assert.Equal(2, notifier.Notifications.Count);
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
    public async Task InMemoryRepairAttemptLimitStopsWhenHistoryCannotBeWritten()
    {
        var clock = new FakeClock();
        var history = new FakeHistoryStore { FailAppend = true };
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings { MaxRepairAttemptsPerDay = 2 }
        };
        var repairCalls = 0;
        var check = new SequenceHealthCheck(
            "x",
            Finding(), null,
            Finding(), null,
            Finding());
        var repair = new DelegateRepair("x", false, (_, _, _) =>
        {
            repairCalls++;
            return Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()));
        });
        var engine = Engine(check, history, settings, new RecordingNotifier(),
            new FakeSystemInfoProvider(), clock, new[] { repair });

        await engine.RunCycleAsync(default);
        await engine.RunCycleAsync(default);
        var limited = await engine.RunCycleAsync(default);
        Assert.Equal(2, repairCalls);
        Assert.Equal(HistoryOutcome.Skipped, limited.Entries.Single().Outcome);
    }

    [Fact]
    public async Task InMemoryRepairAttemptLimitExpiresAfter24Hours()
    {
        var clock = new FakeClock();
        var history = new FakeHistoryStore { FailAppend = true };
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings { MaxRepairAttemptsPerDay = 1 }
        };
        var repairCalls = 0;
        var check = new SequenceHealthCheck("x", Finding(), null, Finding(), Finding(), null);
        var repair = new DelegateRepair("x", false, (_, _, _) =>
        {
            repairCalls++;
            return Task.FromResult(new RepairOutcome(true, "done", Array.Empty<CommandExecution>()));
        });
        var engine = Engine(check, history, settings, new RecordingNotifier(),
            new FakeSystemInfoProvider(), clock, new[] { repair });

        await engine.RunCycleAsync(default);
        await engine.RunCycleAsync(default);
        Assert.Equal(1, repairCalls);
        clock.UtcNow = clock.UtcNow.AddHours(24).AddSeconds(1);
        await engine.RunCycleAsync(default);

        Assert.Equal(2, repairCalls);
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
    public async Task CanceledInspectionIsRetriedOnNextCycle()
    {
        var clock = new FakeClock();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings { CheckIntervalMinutes = new() { ["x"] = 10 } }
        };
        var calls = 0;
        var check = new DelegateHealthCheck("x", (_, _) =>
        {
            calls++;
            return calls == 1
                ? Task.FromException<HealthFinding?>(new OperationCanceledException())
                : Task.FromResult<HealthFinding?>(null);
        });
        var engine = Engine(check, new FakeHistoryStore(), settings, new RecordingNotifier(),
            new FakeSystemInfoProvider(), clock);

        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.RunCycleAsync(default));
        await engine.RunCycleAsync(default);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedInspectionIsNotRetriedWithinInterval()
    {
        var clock = new FakeClock();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings { CheckIntervalMinutes = new() { ["x"] = 10 } }
        };
        var calls = 0;
        var check = new DelegateHealthCheck("x", (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("bad");
        });
        var engine = Engine(check, new FakeHistoryStore(), settings, new RecordingNotifier(),
            new FakeSystemInfoProvider(), clock);

        await engine.RunCycleAsync(default);
        await engine.RunCycleAsync(default);

        Assert.Equal(1, calls);
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
    public async Task PersistedCheckStateHonorsComponentStoreInterval()
    {
        var clock = new FakeClock();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings
            {
                CheckIntervalMinutes = new() { ["component-store"] = 720 }
            }
        };
        var state = new FakeCheckStateStore();
        state.State["component-store"] = clock.UtcNow.AddHours(-1);
        var calls = 0;
        var check = new DelegateHealthCheck("component-store", (_, _) =>
        {
            calls++;
            return Task.FromResult<HealthFinding?>(Finding("component-store"));
        });
        var engine = Engine(check, new FakeHistoryStore(), settings, new RecordingNotifier(),
            new FakeSystemInfoProvider(), clock, checkState: state);

        var result = await engine.RunCycleAsync(default);

        Assert.Empty(result.Entries);
        Assert.Equal(0, calls);
        Assert.Equal(1, state.SaveCount);
    }

    [Fact]
    public async Task LongIntervalCheckGetsInitialGracePeriod()
    {
        var clock = new FakeClock();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings
            {
                CheckIntervalMinutes = new() { ["component-store"] = 720 }
            }
        };
        var state = new FakeCheckStateStore();
        var calls = 0;
        var check = new DelegateHealthCheck("component-store", (_, _) =>
        {
            calls++;
            return Task.FromResult<HealthFinding?>(null);
        });
        var engine = Engine(check, new FakeHistoryStore(), settings, new RecordingNotifier(),
            new FakeSystemInfoProvider(), clock, checkState: state);

        await engine.RunCycleAsync(default);
        Assert.Equal(0, calls);

        clock.UtcNow = clock.UtcNow.AddMinutes(30);
        await engine.RunCycleAsync(default);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PersistedLongIntervalCheckDoesNotGetInitialGraceSeed()
    {
        var clock = new FakeClock();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings
            {
                CheckIntervalMinutes = new() { ["component-store"] = 720 }
            }
        };
        var state = new FakeCheckStateStore();
        state.State["component-store"] = clock.UtcNow.AddHours(-1);
        var calls = 0;
        var check = new DelegateHealthCheck("component-store", (_, _) =>
        {
            calls++;
            return Task.FromResult<HealthFinding?>(null);
        });
        var engine = Engine(check, new FakeHistoryStore(), settings, new RecordingNotifier(),
            new FakeSystemInfoProvider(), clock, checkState: state);

        await engine.RunCycleAsync(default);
        clock.UtcNow = clock.UtcNow.AddMinutes(30);
        await engine.RunCycleAsync(default);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ShortIntervalCheckRunsOnFirstCycle()
    {
        var clock = new FakeClock();
        var settings = new FakeSettingsStore
        {
            Settings = new TraySettings
            {
                CheckIntervalMinutes = new() { ["x"] = 15 }
            }
        };
        var calls = 0;
        var check = new DelegateHealthCheck("x", (_, _) =>
        {
            calls++;
            return Task.FromResult<HealthFinding?>(null);
        });
        var engine = Engine(check, new FakeHistoryStore(), settings, new RecordingNotifier(),
            new FakeSystemInfoProvider(), clock, checkState: new FakeCheckStateStore());

        await engine.RunCycleAsync(default);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CheckStateIsSavedAfterCycle()
    {
        var clock = new FakeClock();
        var state = new FakeCheckStateStore();
        var check = new DelegateHealthCheck("x", (_, _) =>
            Task.FromResult<HealthFinding?>(Finding("x")));
        var engine = Engine(check, new FakeHistoryStore(), new FakeSettingsStore(),
            new RecordingNotifier(), new FakeSystemInfoProvider(), clock, checkState: state);

        await engine.RunCycleAsync(default);

        Assert.Equal(1, state.SaveCount);
        Assert.Equal(clock.UtcNow, state.SavedState!["x"]);
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
        Assert.True(second.AlreadyRunning);
        check.Continue.SetResult(true);
        var firstResult = await first;
        Assert.False(firstResult.AlreadyRunning);
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
    public void NormalizeWithChangesReportsCriticalThresholdAdjustment()
    {
        var settings = new TraySettings
        {
            DiskWarnPercent = 15,
            DiskCriticalPercent = 30
        };

        var changes = settings.NormalizeWithChanges();

        Assert.Equal(new[] { "Ui.Settings.DiskCritical" }, changes);
        Assert.Equal(15, settings.DiskCriticalPercent);
    }

    [Fact]
    public void NormalizeWithChangesReturnsEmptyForValuesInRange()
    {
        var changes = new TraySettings().NormalizeWithChanges();

        Assert.Empty(changes);
    }

    [Fact]
    public void NormalizeWithChangesReportsServiceWhitespaceRemoval()
    {
        var settings = new TraySettings
        {
            MonitoredServices = new() { "Dnscache", "  " }
        };

        var changes = settings.NormalizeWithChanges();

        Assert.Equal(new[] { "Ui.Settings.Services" }, changes);
        Assert.Equal(new[] { "Dnscache" }, settings.MonitoredServices);
    }

    [Fact]
    public void NormalizeWithChangesReportsInvalidCulture()
    {
        var settings = new TraySettings { UiCulture = "not-a-culture-###" };

        var changes = settings.NormalizeWithChanges();

        Assert.Equal(new[] { "Ui.Settings.Language" }, changes);
        Assert.Equal(string.Empty, settings.UiCulture);
    }

    [Fact]
    public void NormalizeWithChangesReportsInvalidDnsProbeHost()
    {
        var settings = new TraySettings { DnsProbeHost = "https://example.com/" };

        var changes = settings.NormalizeWithChanges();

        Assert.Equal(new[] { "Ui.Settings.DnsProbeHost" }, changes);
        Assert.Equal("www.msftconnecttest.com", settings.DnsProbeHost);
    }

    [Fact]
    public void NormalizeWithChangesKeepsValidDnsProbeHost()
    {
        var settings = new TraySettings { DnsProbeHost = "example.com" };

        var changes = settings.NormalizeWithChanges();

        Assert.Empty(changes);
        Assert.Equal("example.com", settings.DnsProbeHost);
    }

    [Fact]
    public void TrayPathsExposeDataAndNestedLogDirectories()
    {
        Assert.NotEmpty(TrayPaths.DataDirectory);
        Assert.NotEmpty(TrayPaths.LogDirectory);
        Assert.StartsWith(
            Path.GetFullPath(TrayPaths.DataDirectory) + Path.DirectorySeparatorChar,
            Path.GetFullPath(TrayPaths.LogDirectory),
            StringComparison.OrdinalIgnoreCase);
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
    public void JsonSettingsStoreSaveFailureIsReportedAndClearedAfterSuccessfulSave()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        Directory.CreateDirectory(path);
        var store = new JsonSettingsStore(path, new FakeLog());

        store.Save(new TraySettings());
        Assert.True(store.LastSaveFailed);

        Directory.Delete(path);
        store.Save(new TraySettings());
        Assert.False(store.LastSaveFailed);
    }

    [Fact]
    public void FileTrayLogPrunesExpiredDailyFiles()
    {
        using var directory = new TempDirectory();
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock { UtcNow = now };
        var oldPath = Path.Combine(directory.Path, $"tray-{now.AddDays(-40):yyyyMMdd}.log");
        var currentPath = Path.Combine(directory.Path, $"tray-{now:yyyyMMdd}.log");
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(currentPath, "current");

        var log = new FileTrayLog(directory.Path, clock);
        log.Info("test");

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(currentPath));
    }

    [Fact]
    public async Task JsonlHistoryStoreReadsNewestSkipsCorruptAndCountsAttempts()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "history.jsonl");
        var log = new FakeLog();
        using var store = new JsonlHistoryStore(path, log, maxEntries: 100, retentionDays: 90);
        await store.AppendAsync(Entry("a", HistoryOutcome.Repaired), default);
        await store.AppendAsync(Entry("b", HistoryOutcome.RepairFailed), default);
        await File.AppendAllTextAsync(path, "{broken}\n");
        var recent = await store.ReadRecentAsync(10, default);
        Assert.Equal(2, recent.Count);
        Assert.Single(log.Warnings);
        Assert.Contains(path, log.Warnings[0]);
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
        Assert.NotEmpty(log.Errors);
    }

    [Fact]
    public void JsonSettingsStoreMovesBrokenJsonToInvalidBackup()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        const string broken = "{ this is not json }";
        Directory.CreateDirectory(directory.Path);
        File.WriteAllText(path, broken);
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero) };

        var settings = new JsonSettingsStore(path, new FakeLog(), clock).Load();

        Assert.True(settings.AutoRepairEnabled);
        Assert.False(File.Exists(path));
        var backups = Directory.GetFiles(directory.Path, "settings.invalid-*.json");
        var backup = Assert.Single(backups);
        Assert.Equal(broken, File.ReadAllText(backup));
    }

    [Fact]
    public void JsonSettingsStoreDoesNotCreateInvalidBackupWhenFileIsMissing()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        Directory.CreateDirectory(directory.Path);

        var settings = new JsonSettingsStore(path, new FakeLog()).Load();

        Assert.True(settings.AutoRepairEnabled);
        Assert.Empty(Directory.GetFiles(directory.Path, "settings.invalid-*.json"));
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
    [InlineData("cleanmgr.exe")]
    public void AllowListRejectsUnsafeNames(string name) =>
        Assert.False(CommandAllowList.IsAllowed(name));

    [Fact]
    public async Task RunnerRejectsUnsafeNameBeforeStartingProcess()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SystemProcessRunner().RunAsync("powershell.exe", Array.Empty<string>(), TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public void NormalizeRemovesNullCharactersBeforeOutputTail()
    {
        var output = "\0s\0f\0c\r\n\t  ";

        Assert.Equal("sfc\r\n\t", SystemProcessRunner.Normalize(output));
        Assert.Equal("sfc\r\n\t", SystemProcessRunner.OutputTail(output));
    }

    [Fact]
    public void NormalizeReturnsEmptyForNullOrEmpty()
    {
        Assert.Equal(string.Empty, SystemProcessRunner.Normalize(null!));
        Assert.Equal(string.Empty, SystemProcessRunner.Normalize(string.Empty));
    }
}

public class HistoryFormattingTests
{
    [Fact]
    public void SingleLineCollapsesHistoryWhitespace()
    {
        Assert.Equal("one two three", HistoryText.SingleLine("  one\r\n two\t\tthree  "));
    }

    [Fact]
    public void SingleLineReturnsEmptyForNullOrEmpty()
    {
        Assert.Equal(string.Empty, HistoryText.SingleLine(null));
        Assert.Equal(string.Empty, HistoryText.SingleLine(string.Empty));
    }

    [Fact]
    public void HistorySearchMatchesFieldsIgnoringCase()
    {
        var entry = Entry("Disk space warning", "Free space is low", "Cleanup completed", null);

        Assert.True(HistorySearch.Matches(entry, "CLEANUP"));
        Assert.True(HistorySearch.Matches(entry, "  disk  "));
    }

    [Fact]
    public void HistorySearchRejectsNonMatchingTerms()
    {
        var entry = Entry("Disk space warning", "Free space is low", null, "Manual action");

        Assert.False(HistorySearch.Matches(entry, "network"));
        Assert.True(HistorySearch.Matches(entry, " \t"));
    }

    [Fact]
    public void DurationFormatterUsesSecondsForShortDurations()
    {
        var localizer = new ResourceLocalizer();

        Assert.Equal("1.2 s", DurationFormatter.Format(TimeSpan.FromSeconds(1.23), localizer));
    }

    [Fact]
    public void DurationFormatterUsesMinutesForLongDurationsAndClampsNegative()
    {
        var localizer = new ResourceLocalizer();

        Assert.Equal("2.0 min", DurationFormatter.Format(TimeSpan.FromMinutes(2), localizer));
        Assert.Equal("0.0 s", DurationFormatter.Format(TimeSpan.FromSeconds(-1), localizer));
    }

    [Fact]
    public void CommandSummaryIncludesMultipleCommandsAndEmptyInput()
    {
        var commands = new[]
        {
            new CommandExecution("sfc.exe", "/scannow", 0, TimeSpan.Zero, string.Empty, string.Empty),
            new CommandExecution("sc.exe", string.Empty, 5, TimeSpan.Zero, string.Empty, string.Empty)
        };

        Assert.Equal("sfc.exe /scannow -> 0 ; sc.exe -> 5", HistoryText.CommandSummary(commands));
        Assert.Equal(string.Empty, HistoryText.CommandSummary(Array.Empty<CommandExecution>()));
    }

    private static HistoryEntry Entry(
        string title,
        string detail,
        string? repairSummary,
        string? skipReason) =>
        new(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            "test",
            title,
            HealthStatus.Warning,
            HistoryOutcome.Detected,
            detail,
            repairSummary,
            skipReason,
            TimeSpan.Zero,
            Array.Empty<CommandExecution>());
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
