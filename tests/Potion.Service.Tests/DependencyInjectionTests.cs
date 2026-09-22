using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Potion.Service;
using Potion.Service.Hubs;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Regression coverage for the class of bugs where a DI registration cannot
/// be resolved (CollaborationService missing registration, ServiceMeshService
/// injecting an unregistered IHttpClientFactory). Building the service
/// collection with ValidateOnBuild catches unresolvable registrations at
/// provider construction, mirroring Program.cs startup validation.
/// </summary>
public sealed class DependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RemediationPolicy:Enabled"] = "false",
                ["TelemetryRetention:Enabled"] = "false",
                ["FeatureFlags:test"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // Framework services the generic host supplies at runtime
        services.AddSingleton(new Mock<IHostApplicationLifetime>().Object);
        new Startup(configuration).ConfigureServices(services);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void AllRegistrations_ResolveAtBuildTime()
    {
        using var provider = BuildProvider(); // throws if any registration is unresolvable
        Assert.NotNull(provider);
    }

    [Fact]
    public void MonitoringLoop_IsWiredAsHostedServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        new Startup(configuration).ConfigureServices(services);

        var hosted = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToHashSet();

        Assert.Contains(typeof(MemoryMonitor), hosted);
        Assert.Contains(typeof(AnomalyDetector), hosted);
        Assert.Contains(typeof(EventCorrelationService), hosted);
        Assert.Contains(typeof(ComplianceReportService), hosted);
    }

    [Fact]
    public void CollaborationHubDependency_IsRegistered()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        new Startup(configuration).ConfigureServices(services);

        Assert.Contains(services, d => d.ServiceType == typeof(CollaborationService));
        Assert.Contains(services, d => d.ServiceType == typeof(ISystemHealthMonitor));
    }
}
