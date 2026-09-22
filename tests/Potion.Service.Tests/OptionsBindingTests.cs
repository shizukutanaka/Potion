using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Potion.Service.Hubs;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Guards the regression where "MaxConcurrency": "env:POTION_MAX_CONCURRENCY:default:4"
/// shipped as a literal string — no provider understands env: interpolation, so binding
/// threw InvalidOperationException the first time the flag-gated options were read.
/// Every configured options class must bind cleanly against the real appsettings files
/// (which are copied to the test output directory).
/// </summary>
public sealed class OptionsBindingTests
{
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    [InlineData("appsettings.Development.json")]
    public void AllConfiguredOptions_BindCleanly(string overlayFile)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, overlayFile), optional: true)
            .Build();

        // The same sections Startup.ConfigureServices binds — each must parse without throwing.
        // Sections absent from config yield null (options fall back to their defaults).
        var collaboration = configuration.GetSection("Collaboration").Get<CollaborationOptions>();
        var memoryMonitor = configuration.GetSection(MemoryMonitorOptions.SectionName).Get<MemoryMonitorOptions>();
        var performanceOptimizer = configuration.GetSection(PerformanceOptimizerOptions.SectionName).Get<PerformanceOptimizerOptions>();
        var eventCorrelation = configuration.GetSection("EventCorrelation").Get<EventCorrelationOptions>();
        var compliance = configuration.GetSection("Compliance").Get<ComplianceOptions>();
        var remediationPolicy = configuration.GetSection("RemediationPolicy").Get<RemediationPolicyOptions>();

        Assert.NotNull(eventCorrelation);
        Assert.NotNull(compliance);
        Assert.NotNull(remediationPolicy);
        Assert.InRange(remediationPolicy!.MaxConcurrency, 1, 8);
    }

    [Fact]
    public void BaseConfig_CommandAllowlist_IsNonEmpty()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();

        var policy = configuration.GetSection("RemediationPolicy").Get<RemediationPolicyOptions>();

        Assert.NotNull(policy);
        Assert.NotEmpty(policy!.CommandAllowlist);
    }
}
