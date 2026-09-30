using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using Microsoft.Extensions.Configuration;
using Potion.Service.Hubs;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Runs the same ValidateOnStart rules Startup.ConfigureServices applies against the
/// config each deployment environment would actually boot with (base + env overlay,
/// merged by the configuration binder). An overlay that drifts from base can silently
/// break startup validation — the merged result must pass every shipped validator.
/// </summary>
public sealed class ConfigOverlayValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Container")]
    public void MergedConfig_PassesStartupValidators(string? environment)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        if (environment is not null)
        {
            builder.AddJsonFile(Path.Combine(AppContext.BaseDirectory, $"appsettings.{environment}.json"), optional: false);
        }
        var configuration = builder.Build();

        // Startup.cs: CollaborationOptions — MaxConcurrentUsers > 0
        var collaboration = configuration.GetSection("Collaboration").Get<CollaborationOptions>() ?? new CollaborationOptions();
        Assert.True(collaboration.MaxConcurrentUsers > 0, "Collaboration:MaxConcurrentUsers must be positive");

        // Startup.cs: MemoryMonitorOptions / PerformanceOptimizerOptions — DataAnnotations
        var memoryMonitor = configuration.GetSection(MemoryMonitorOptions.SectionName).Get<MemoryMonitorOptions>() ?? new MemoryMonitorOptions();
        Validator.ValidateObject(memoryMonitor, new ValidationContext(memoryMonitor), validateAllProperties: true);
        var performanceOptimizer = configuration.GetSection(PerformanceOptimizerOptions.SectionName).Get<PerformanceOptimizerOptions>() ?? new PerformanceOptimizerOptions();
        Validator.ValidateObject(performanceOptimizer, new ValidationContext(performanceOptimizer), validateAllProperties: true);

        // Startup.cs: EventCorrelationOptions — positive knobs when enabled
        var eventCorrelation = configuration.GetSection("EventCorrelation").Get<EventCorrelationOptions>() ?? new EventCorrelationOptions();
        Assert.True(!eventCorrelation.Enabled || eventCorrelation.CorrelationWindowMinutes > 0, "EventCorrelation:CorrelationWindowMinutes must be positive when enabled");
        Assert.True(!eventCorrelation.Enabled || eventCorrelation.MaxEventsToCorrelate > 0, "EventCorrelation:MaxEventsToCorrelate must be positive when enabled");

        // Startup.cs: ComplianceOptions — ReportIntervalHours 1-1193 when enabled
        var compliance = configuration.GetSection("Compliance").Get<ComplianceOptions>() ?? new ComplianceOptions();
        Assert.True(!compliance.Enabled || compliance.ReportIntervalHours is >= 1 and <= 1193, "Compliance:ReportIntervalHours must be 1-1193 hours when enabled");

        // Startup.cs: RemediationPolicyOptions — DataAnnotations + all policy validators.
        // Validators run even though the registration is flag-gated: enabling
        // FeatureFlags:RepairExecutionEnabled later must not surface a broken policy.
        var policy = configuration.GetSection("RemediationPolicy").Get<RemediationPolicyOptions>() ?? new RemediationPolicyOptions();
        Validator.ValidateObject(policy, new ValidationContext(policy), validateAllProperties: true);
        Assert.True(RemediationPolicyOptionsValidators.HasUniqueTaskNames(policy), "Remediation policy contains duplicate task names");
        Assert.True(RemediationPolicyOptionsValidators.CommandsAreAllowlisted(policy), "Remediation policy references commands outside the allowlist");
        Assert.True(RemediationPolicyOptionsValidators.ArgumentsAreSafe(policy), "Remediation policy contains unsafe task arguments");
        Assert.True(RemediationPolicyOptionsValidators.ArgumentsAreAllowlisted(policy), "Remediation policy uses arguments outside the command argument allowlist");
        Assert.True(RemediationPolicyOptionsValidators.MaintenanceWindowsAreValid(policy), "Remediation policy contains invalid maintenance windows");
    }
}
