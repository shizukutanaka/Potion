using Potion.Tray.Core.Resources;

namespace Potion.Tray.Core.Repairs;

public sealed class DiskSpaceRepair : IRepairAction
{
    private readonly ITempFileCleaner cleaner;
    private readonly IProcessRunner processRunner;
    private readonly ISystemInfoProvider system;
    private readonly ILocalizer localizer;

    public DiskSpaceRepair(ITempFileCleaner cleaner, IProcessRunner processRunner, ISystemInfoProvider system, ILocalizer localizer)
    {
        this.cleaner = cleaner;
        this.processRunner = processRunner;
        this.system = system;
        this.localizer = localizer;
    }
    public DiskSpaceRepair(ITempFileCleaner cleaner, IProcessRunner processRunner, ISystemInfoProvider system)
        : this(cleaner, processRunner, system, new ResourceLocalizer()) { }

    public string CheckId => "disk-space";
    public string DisplayName => localizer.Get("Repair.DiskSpace.Title");
    public bool RequiresAdministrator => false;

    public async Task<RepairOutcome> RepairAsync(HealthFinding finding, TraySettings settings, CancellationToken ct)
    {
        var minimumAge = finding.Status == HealthStatus.Critical
            ? TimeSpan.FromDays(1)
            : TimeSpan.FromDays(7);
        var cleanup = await cleaner.CleanAsync(minimumAge, ct);
        ProcessRunResult? componentCleanup = null;
        var cleanupArgs = new[] { "/Online", "/Cleanup-Image", "/StartComponentCleanup", "/English" };
        if (settings.AllowComponentCleanup && system.IsElevated)
        {
            componentCleanup = await processRunner.RunAsync(
                "DISM.exe",
                cleanupArgs,
                TimeSpan.FromMinutes(30),
                ct);
        }

        var actions = new List<string>();
        if (cleanup.FilesDeleted > 0)
        {
            actions.Add(localizer.Format(
                "Repair.DiskSpace.FilesSummary",
                cleanup.FilesDeleted,
                ByteFormatter.Gigabytes(cleanup.BytesFreed, localizer)));
        }

        if (componentCleanup is { ExitCode: 0 })
        {
            actions.Add(localizer.Get("Repair.DiskSpace.ComponentSummary"));
        }

        var summary = actions.Count == 0
            ? localizer.Get("Repair.DiskSpace.NoneSummary")
            : string.Join(localizer.Get("Format.ListSeparator"), actions);
        return new RepairOutcome(
            cleanup.FilesDeleted > 0 || componentCleanup is { ExitCode: 0 },
            summary,
            componentCleanup is null
                ? Array.Empty<CommandExecution>()
                : new[] { CommandExecutionFactory.Create("DISM.exe", cleanupArgs, componentCleanup) });
    }

}

public sealed class ServiceRestartRepair : IRepairAction
{
    private readonly IProcessRunner processRunner;
    private readonly ISystemInfoProvider system;
    private readonly ILocalizer localizer;

    public ServiceRestartRepair(IProcessRunner processRunner, ISystemInfoProvider system, ILocalizer localizer)
    {
        this.processRunner = processRunner;
        this.system = system;
        this.localizer = localizer;
    }
    public ServiceRestartRepair(IProcessRunner processRunner, ISystemInfoProvider system)
        : this(processRunner, system, new ResourceLocalizer()) { }

    public string CheckId => "critical-services";
    public string DisplayName => localizer.Get("Repair.ServiceRestart.Title");
    public bool RequiresAdministrator => true;

    public async Task<RepairOutcome> RepairAsync(HealthFinding finding, TraySettings settings, CancellationToken ct)
    {
        var snapshots = system.GetServices(settings.MonitoredServices)
            .Where(s => s.Exists && !s.IsRunning && ServicePolicy.ShouldMonitor(s))
            .ToList();
        var commands = new List<CommandExecution>();
        var successful = new List<string>();
        var failed = new List<string>();
        foreach (var service in snapshots)
        {
            var args = new[] { "start", service.Name };
            var result = await processRunner.RunAsync("sc.exe", args, TimeSpan.FromMinutes(2), ct);
            commands.Add(CommandExecutionFactory.Create("sc.exe", args, result));
            if (result.ExitCode == 0 || result.ExitCode == 1056)
            {
                successful.Add(service.Name);
            }
            else
            {
                failed.Add(service.Name);
            }
        }

        var summary = localizer.Format(
            "Repair.ServiceRestart.Summary",
            successful.Count == 0 ? localizer.Get("Ui.History.Detail.None") : string.Join(localizer.Get("Format.ListSeparator"), successful),
            failed.Count == 0 ? localizer.Get("Ui.History.Detail.None") : string.Join(localizer.Get("Format.ListSeparator"), failed));
        return new RepairOutcome(failed.Count == 0, summary, commands);
    }

}

public sealed class ComponentStoreRepair : IRepairAction
{
    private readonly IProcessRunner processRunner;
    private readonly ILocalizer localizer;

    public ComponentStoreRepair(IProcessRunner processRunner, ILocalizer localizer)
    {
        this.processRunner = processRunner;
        this.localizer = localizer;
    }
    public ComponentStoreRepair(IProcessRunner processRunner) : this(processRunner, new ResourceLocalizer()) { }
    public string CheckId => "component-store";
    public string DisplayName => localizer.Get("Repair.ComponentStore.Title");
    public bool RequiresAdministrator => true;

    public async Task<RepairOutcome> RepairAsync(HealthFinding finding, TraySettings settings, CancellationToken ct)
    {
        var commands = new List<CommandExecution>();
        var restoreArgs = new[] { "/Online", "/Cleanup-Image", "/RestoreHealth", "/English" };
        var restore = await processRunner.RunAsync("DISM.exe", restoreArgs, TimeSpan.FromMinutes(60), ct);
        commands.Add(CommandExecutionFactory.Create("DISM.exe", restoreArgs, restore));
        if (restore.ExitCode != 0)
        {
            return new RepairOutcome(false, localizer.Get("Repair.ComponentStore.RestoreFailed"), commands);
        }

        var sfcArgs = new[] { "/scannow" };
        var sfc = await processRunner.RunAsync("sfc.exe", sfcArgs, TimeSpan.FromMinutes(60), ct);
        commands.Add(CommandExecutionFactory.Create("sfc.exe", sfcArgs, sfc));
        return new RepairOutcome(
            sfc.ExitCode == 0,
            sfc.ExitCode == 0
                ? localizer.Get("Repair.ComponentStore.Success")
                : localizer.Get("Repair.ComponentStore.SfcFailed"),
            commands);
    }

}

public sealed class DnsFlushRepair : IRepairAction
{
    private readonly IProcessRunner processRunner;
    private readonly ISystemInfoProvider system;
    private readonly ILocalizer localizer;

    public DnsFlushRepair(IProcessRunner processRunner, ISystemInfoProvider system, ILocalizer localizer)
    {
        this.processRunner = processRunner;
        this.system = system;
        this.localizer = localizer;
    }
    public DnsFlushRepair(IProcessRunner processRunner, ISystemInfoProvider system)
        : this(processRunner, system, new ResourceLocalizer()) { }

    public string CheckId => "network";
    public string DisplayName => localizer.Get("Repair.DnsFlush.Title");
    public bool RequiresAdministrator => false;

    public async Task<RepairOutcome> RepairAsync(HealthFinding finding, TraySettings settings, CancellationToken ct)
    {
        var args = new[] { "/flushdns" };
        var result = await processRunner.RunAsync("ipconfig.exe", args, TimeSpan.FromMinutes(2), ct);
        var success = result.ExitCode == 0 && await system.CanResolveDnsAsync(settings.DnsProbeHost, ct);
        return new RepairOutcome(
            success,
            success
                ? localizer.Get("Repair.DnsFlush.Success")
                : localizer.Get("Repair.DnsFlush.Failed"),
            new[] { CommandExecutionFactory.Create("ipconfig.exe", args, result) });
    }
}
