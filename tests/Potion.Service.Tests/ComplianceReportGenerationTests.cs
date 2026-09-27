using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// The enabled compliance path is driven end-to-end: StartAsync's timer fires
/// immediately, producing a real report file via the write-then-move save path.
/// Also pins the interval-validation guards and the HTTPS-endpoint GDPR check.
/// </summary>
public sealed class ComplianceReportGenerationTests
{
    private static ComplianceReportService CreateService(
        ComplianceOptions options,
        ISystemHealthMonitor? healthMonitor = null,
        IConfiguration? configuration = null)
    {
        var monitor = healthMonitor ?? Mock.Of<ISystemHealthMonitor>(m =>
            m.GetCurrentHealthAsync(It.IsAny<CancellationToken>()) ==
            Task.FromResult(Snapshot()));
        return new ComplianceReportService(
            NullLogger<ComplianceReportService>.Instance,
            Microsoft.Extensions.Options.Options.Create(options),
            monitor,
            configuration ?? new ConfigurationBuilder().Build());
    }

    private static SystemHealthSnapshot Snapshot() =>
        new(
            new SystemMetrics(
                new CpuMetrics(10, 0, 0, 4, 5),
                new MemoryMetrics(50, 0, 0, 0, 0, 0),
                new DiskMetrics(50, 0, 0, 0, 0),
                new NetworkMetrics(0, 0, 0),
                new WindowsEventMetrics(0, 0, 0, 0, DateTimeOffset.UtcNow),
                new ServiceMetrics(10, 8, 2, 0, Array.Empty<string>()),
                new SecurityMetrics(true, true, 0, true, DateTimeOffset.UtcNow),
                new SystemIntegrityMetrics(true, 0, 0, true, DateTimeOffset.UtcNow),
                new InventoryMetrics("m", "os", "v", "model", "serial"),
                new SecurityContextMetrics("u", false, false, false),
                new RuntimePerformanceMetrics(0, 0, 0, 0, 0),
                new ResourceMonitoringMetrics(0, 0, 0),
                new ResourcePressureMetrics(
                    PressureLevel.None, PressureLevel.None, PressureLevel.None, PressureLevel.None),
                new EventCorrelationMetrics(0, 0),
                new CompatibilityMetrics("8.0", true)),
            Array.Empty<SystemHealthAlert>());

    private static string UniqueReportDir() =>
        Path.Combine("reports", "compliance-test-" + Guid.NewGuid().ToString("N"));

    private static async Task<FileInfo> WaitForReportAsync(string reportDir)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var file = Directory.Exists(reportDir)
                ? new DirectoryInfo(reportDir).GetFiles("compliance_report_*.json")
                    .OrderByDescending(f => f.Name)
                    .FirstOrDefault()
                : null;
            if (file is not null && file.Length > 0)
            {
                return file;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"No compliance report materialized under {reportDir}");
    }

    [Fact]
    public void StartAsync_Disabled_DoesNotStartTimer()
    {
        using var service = CreateService(new ComplianceOptions { Enabled = false });

        var task = service.StartAsync(CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1194)]
    public async Task StartAsync_InvalidInterval_ThrowsConfigError(int hours)
    {
        using var service = CreateService(new ComplianceOptions
        {
            Enabled = true,
            ReportIntervalHours = hours,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Enabled_GeneratesReportWithStandardsAndHttpsCheck()
    {
        var reportDir = UniqueReportDir();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Https:Url"] = "https://localhost:5001",
            })
            .Build();
        using var service = CreateService(new ComplianceOptions
        {
            Enabled = true,
            Standards = new List<string> { "GDPR", "HIPAA" },
            ReportIntervalHours = 24,
            ReportDirectory = reportDir,
        }, configuration: config);

        var fullDir = Path.Combine(ServicePaths.Base, reportDir);
        try
        {
            await service.StartAsync(CancellationToken.None);
            var file = await WaitForReportAsync(fullDir);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName));
            var standards = doc.RootElement.GetProperty("Standards");
            Assert.True(standards.TryGetProperty("GDPR", out var gdpr));
            Assert.True(standards.TryGetProperty("HIPAA", out _));

            var encryptionCheck = gdpr.GetProperty("Checks").EnumerateArray()
                .First(c => c.GetProperty("CheckName").GetString() == "Data Encryption");
            Assert.True(encryptionCheck.GetProperty("Compliant").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(fullDir))
            {
                Directory.Delete(fullDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Enabled_NoHttpsEndpoint_GdprEncryptionCheckNonCompliant()
    {
        var reportDir = UniqueReportDir();
        using var service = CreateService(new ComplianceOptions
        {
            Enabled = true,
            Standards = new List<string> { "GDPR" },
            ReportIntervalHours = 24,
            ReportDirectory = reportDir,
        }); // no Kestrel endpoints configured

        var fullDir = Path.Combine(ServicePaths.Base, reportDir);
        try
        {
            await service.StartAsync(CancellationToken.None);
            var file = await WaitForReportAsync(fullDir);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName));
            var encryptionCheck = doc.RootElement
                .GetProperty("Standards").GetProperty("GDPR").GetProperty("Checks")
                .EnumerateArray()
                .First(c => c.GetProperty("CheckName").GetString() == "Data Encryption");
            Assert.False(encryptionCheck.GetProperty("Compliant").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("Standards")
                .GetProperty("GDPR").GetProperty("OverallCompliance").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(fullDir))
            {
                Directory.Delete(fullDir, recursive: true);
            }
        }
    }
}
