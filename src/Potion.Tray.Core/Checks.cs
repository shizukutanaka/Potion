using System.Globalization;
using Potion.Tray.Core.Resources;

namespace Potion.Tray.Core.Checks;

public sealed class DiskSpaceHealthCheck : IHealthCheck
{
    private readonly ISystemInfoProvider system;
    private readonly ILocalizer localizer;

    public DiskSpaceHealthCheck(ISystemInfoProvider system, ILocalizer localizer)
    {
        this.system = system;
        this.localizer = localizer;
    }
    public DiskSpaceHealthCheck(ISystemInfoProvider system) : this(system, new ResourceLocalizer()) { }
    public string Id => "disk-space";
    public string DisplayName => localizer.Get("Check.DiskSpace.Title");

    public Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct)
    {
        var drives = system.GetFixedDrives();
        var findings = drives
            .Where(d => d.TotalBytes > 0)
            .Select(d =>
            {
                var percent = d.FreeBytes * 100d / d.TotalBytes;
                var freeGb = d.FreeBytes / (1024d * 1024d * 1024d);
                var status = percent < settings.DiskCriticalPercent &&
                             freeGb < settings.DiskCriticalFreeGb
                    ? HealthStatus.Critical
                    : percent < settings.DiskWarnPercent &&
                      freeGb < settings.DiskWarnFreeGb
                        ? HealthStatus.Warning
                        : HealthStatus.Healthy;
                return (Drive: d, Percent: percent, Status: status);
            })
            .Where(x => x.Status != HealthStatus.Healthy)
            .ToList();
        if (findings.Count == 0)
        {
            return Task.FromResult<HealthFinding?>(null);
        }

        var worst = findings.Max(x => x.Status);
        var details = findings.Select(x =>
            localizer.Format(
                "Check.DiskSpace.Detail",
                x.Drive.Name,
                ByteFormatter.Gigabytes(x.Drive.FreeBytes, localizer),
                ByteFormatter.Gigabytes(x.Drive.TotalBytes, localizer),
                x.Percent.ToString("0.0", CultureInfo.CurrentUICulture)));
        var metrics = findings.ToDictionary(
            x => x.Drive.Name,
            x => $"{x.Percent.ToString("0.0", CultureInfo.InvariantCulture)}%",
            StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<HealthFinding?>(new HealthFinding(
            Id,
            DisplayName,
            worst,
            string.Join(localizer.Get("Format.ListSeparator"), details),
            metrics));
    }

}

public sealed class CriticalServiceHealthCheck : IHealthCheck
{
    private readonly ISystemInfoProvider system;
    private readonly ILocalizer localizer;

    public CriticalServiceHealthCheck(ISystemInfoProvider system, ILocalizer localizer)
    {
        this.system = system;
        this.localizer = localizer;
    }
    public CriticalServiceHealthCheck(ISystemInfoProvider system) : this(system, new ResourceLocalizer()) { }
    public string Id => "critical-services";
    public string DisplayName => localizer.Get("Check.CriticalServices.Title");

    public Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct)
    {
        var stopped = system.GetServices(settings.MonitoredServices)
            .Where(s => s.Exists && !s.IsRunning && ServicePolicy.ShouldMonitor(s))
            .Select(s => s.Name)
            .ToList();
        return Task.FromResult<HealthFinding?>(stopped.Count == 0
            ? null
            : new HealthFinding(
                Id,
                DisplayName,
                HealthStatus.Critical,
                localizer.Format("Check.CriticalServices.Detail", string.Join(localizer.Get("Format.ListSeparator"), stopped)),
                new Dictionary<string, string> { ["services"] = string.Join(localizer.Get("Format.ListSeparator"), stopped) }));
    }
}

public sealed class ComponentStoreHealthCheck : IHealthCheck
{
    private readonly IProcessRunner processRunner;
    private readonly ITrayLog log;
    private readonly ILocalizer localizer;

    public ComponentStoreHealthCheck(IProcessRunner processRunner, ITrayLog log, ILocalizer localizer)
    {
        this.processRunner = processRunner;
        this.log = log;
        this.localizer = localizer;
    }
    public ComponentStoreHealthCheck(IProcessRunner processRunner, ITrayLog log)
        : this(processRunner, log, new ResourceLocalizer()) { }

    public string Id => "component-store";
    public string DisplayName => localizer.Get("Check.ComponentStore.Title");

    public async Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct)
    {
        ProcessRunResult result;
        try
        {
            result = await processRunner.RunAsync(
                "DISM.exe",
                new[] { "/Online", "/Cleanup-Image", "/CheckHealth", "/English" },
                TimeSpan.FromMinutes(5),
                ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Warn("Unable to inspect the component store.", ex);
            return null;
        }

        var output = $"{result.StandardOutput}\n{result.StandardError}";
        if (result.TimedOut)
        {
            log.Warn("Component store inspection timed out.");
            return null;
        }

        var finding = Interpret(output);
        if (finding is not null)
        {
            return finding;
        }

        if (!ContainsNoCorruption(output) && result.ExitCode != 0)
        {
            try
            {
                result = await processRunner.RunAsync(
                    "DISM.exe",
                    new[] { "/Online", "/Cleanup-Image", "/CheckHealth" },
                    TimeSpan.FromMinutes(5),
                    ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.Warn("Unable to inspect the component store without English output.", ex);
                return null;
            }

            output = $"{result.StandardOutput}\n{result.StandardError}";
            if (result.TimedOut)
            {
                log.Warn("Component store inspection fallback timed out.");
                return null;
            }

            finding = Interpret(output);
            if (finding is not null)
            {
                return finding;
            }
        }

        if (ContainsNoCorruption(output))
        {
            return null;
        }

        log.Warn($"Unable to interpret component store inspection output (exit code: {result.ExitCode}).");
        return null;
    }

    private HealthFinding? Interpret(string output) =>
        ContainsRepairable(output)
            ? new HealthFinding(
                Id,
                DisplayName,
                HealthStatus.Critical,
                localizer.Get("Check.ComponentStore.Detail"))
            : null;

    private static bool ContainsRepairable(string output) =>
        output.Contains("repairable", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("修復可能", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNoCorruption(string output) =>
        output.Contains("no component store corruption", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("コンポーネント ストアの破損は検出されませんでした", StringComparison.OrdinalIgnoreCase);
}

public sealed class MemoryPressureHealthCheck : IHealthCheck
{
    private readonly ISystemInfoProvider system;
    private readonly ILocalizer localizer;
    private readonly ITrayLog? log;

    public MemoryPressureHealthCheck(ISystemInfoProvider system, ILocalizer localizer, ITrayLog? log = null)
    {
        this.system = system;
        this.localizer = localizer;
        this.log = log;
    }
    public MemoryPressureHealthCheck(ISystemInfoProvider system) : this(system, new ResourceLocalizer()) { }
    public string Id => "memory-pressure";
    public string DisplayName => localizer.Get("Check.MemoryPressure.Title");

    public Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct)
    {
        var memory = system.GetMemory();
        if (memory.TotalBytes <= 0)
        {
            return Task.FromResult<HealthFinding?>(null);
        }

        var percent = memory.AvailableBytes * 100d / memory.TotalBytes;
        if (percent >= settings.MemoryWarnPercent)
        {
            return Task.FromResult<HealthFinding?>(null);
        }

        var detail = localizer.Format(
            "Check.MemoryPressure.Detail",
            percent.ToString("0.0", CultureInfo.CurrentUICulture));
        try
        {
            var processes = system.GetTopMemoryProcesses(3);
            if (processes.Count > 0)
            {
                var consumers = processes.Select(process =>
                    $"{process.Name} ({ByteFormatter.Gigabytes(process.WorkingSetBytes, localizer)})");
                detail += Environment.NewLine + localizer.Format(
                    "Check.MemoryPressure.TopProcesses",
                    string.Join(localizer.Get("Format.ListSeparator"), consumers));
            }
        }
        catch (Exception ex)
        {
            log?.Warn("Unable to inspect top memory-consuming processes.", ex);
        }

        return Task.FromResult<HealthFinding?>(new HealthFinding(
            Id,
            DisplayName,
            HealthStatus.Warning,
            detail,
            new Dictionary<string, string>
            {
                ["available"] = percent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            }));
    }
}

public sealed class PendingRebootHealthCheck : IHealthCheck
{
    private readonly ISystemInfoProvider system;
    private readonly ILocalizer localizer;

    public PendingRebootHealthCheck(ISystemInfoProvider system, ILocalizer localizer)
    {
        this.system = system;
        this.localizer = localizer;
    }
    public PendingRebootHealthCheck(ISystemInfoProvider system) : this(system, new ResourceLocalizer()) { }
    public string Id => "pending-reboot";
    public string DisplayName => localizer.Get("Check.PendingReboot.Title");

    public Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct) =>
        Task.FromResult<HealthFinding?>(system.IsRebootPending()
            ? new HealthFinding(Id, DisplayName, HealthStatus.Warning, localizer.Get("Check.PendingReboot.Detail"))
            : null);
}

public sealed class NetworkHealthCheck : IHealthCheck
{
    private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(2);
    private readonly ISystemInfoProvider system;
    private readonly ILocalizer localizer;
    private readonly int maxAttempts;
    private readonly TimeSpan retryInterval;

    public NetworkHealthCheck(
        ISystemInfoProvider system,
        ILocalizer localizer,
        int maxAttempts = 3,
        TimeSpan? retryInterval = null)
    {
        this.system = system;
        this.localizer = localizer;
        this.maxAttempts = Math.Max(1, maxAttempts);
        this.retryInterval = retryInterval is null
            ? DefaultRetryInterval
            : retryInterval.Value < TimeSpan.Zero
                ? TimeSpan.Zero
                : retryInterval.Value;
    }
    public NetworkHealthCheck(
        ISystemInfoProvider system,
        int maxAttempts = 3,
        TimeSpan? retryInterval = null)
        : this(system, new ResourceLocalizer(), maxAttempts, retryInterval) { }
    public string Id => "network";
    public string DisplayName => localizer.Get("Check.Network.Title");

    public async Task<HealthFinding?> InspectAsync(TraySettings settings, CancellationToken ct)
    {
        if (!system.IsNetworkAvailable)
        {
            return null;
        }

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!system.IsNetworkAvailable)
            {
                return null;
            }

            if (await system.CanResolveDnsAsync(settings.DnsProbeHost, ct))
            {
                return null;
            }

            if (attempt + 1 < maxAttempts)
            {
                await Task.Delay(retryInterval, ct);
            }
        }

        return new HealthFinding(Id, DisplayName, HealthStatus.Critical, localizer.Get("Check.Network.Detail"));
    }
}
