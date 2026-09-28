using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Readiness check: proves the monitoring pipeline can sample metrics end-to-end.
/// Reports Degraded when the worst of CPU/memory/disk is saturated so orchestrators
/// can drain traffic before the process wedges; a sampling exception surfaces as
/// Unhealthy via HealthCheckService's default handling.
/// </summary>
public sealed class SystemReadinessCheck : IHealthCheck
{
    private const double SaturatedPercent = 95.0;
    private readonly ISystemHealthMonitor _monitor;

    public SystemReadinessCheck(ISystemHealthMonitor monitor) => _monitor = monitor;

    // Fully qualified: ServiceSupportTypes.HealthCheckResult in this namespace
    // shadows the ASP.NET type.
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = await _monitor.GetCurrentHealthAsync(cancellationToken);
        var metrics = snapshot.Metrics;
        var worst = Math.Max(Math.Max(metrics.Cpu.UsagePercent, metrics.Memory.UsedPercent), metrics.Disk.UsedPercent);
        return worst >= SaturatedPercent
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded(
                $"resource saturation: worst metric at {worst:F1}%")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(
                $"worst resource usage {worst:F1}%");
    }
}
