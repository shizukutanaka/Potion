using System;
using System.IO;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Path contract tests: every accessor must return a path under the resolved base
/// (which guarantees the directory exists on disk) and task-derived file names must
/// be sanitized so hostile task names cannot escape the state directories.
/// </summary>
public sealed class ServicePathsTests
{
    [Fact]
    public void WellKnownDirectories_ExistOnDisk()
    {
        Assert.True(Directory.Exists(ServicePaths.Logs));
        Assert.True(Directory.Exists(ServicePaths.State));
        Assert.True(Directory.Exists(ServicePaths.Telemetry));
        Assert.True(Directory.Exists(ServicePaths.Playbooks));
        Assert.True(Directory.Exists(ServicePaths.Certificates));
        Assert.True(Directory.Exists(ServicePaths.Security));
        Assert.True(Directory.Exists(ServicePaths.Backups));
        Assert.True(Directory.Exists(ServicePaths.Reports));
        Assert.True(Directory.Exists(ServicePaths.ConfigBackups));
    }

    [Fact]
    public void WellKnownPaths_StayUnderBase()
    {
        var prefix = ServicePaths.Base + Path.DirectorySeparatorChar;

        Assert.StartsWith(prefix, ServicePaths.Logs + Path.DirectorySeparatorChar);
        Assert.StartsWith(prefix, ServicePaths.State + Path.DirectorySeparatorChar);
        Assert.StartsWith(prefix, ServicePaths.Telemetry + Path.DirectorySeparatorChar);
        Assert.StartsWith(ServicePaths.Base, ServicePaths.ConfigurationFile);
        Assert.Equal(ServicePaths.Base, ServicePaths.BaseDirectory);
    }

    [Fact]
    public void TelemetryFilePath_SanitizedNameAndUtcFormat()
    {
        var stamp = new DateTimeOffset(2026, 3, 1, 12, 34, 56, TimeSpan.Zero);
        var path = ServicePaths.GetTelemetryFilePath("cpu-cleanup", stamp);

        Assert.Equal(Path.Combine(ServicePaths.Telemetry, "cpu-cleanup_20260301T123456Z.json"), path);
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void TelemetryFilePath_InvalidCharsStripped()
    {
        var path = ServicePaths.GetTelemetryFilePath("bad/name:with*chars", DateTimeOffset.UnixEpoch);
        var fileName = Path.GetFileName(path)!;

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            Assert.DoesNotContain(c, fileName);
        }
        var expected = string.Concat(
            "bad/name:with*chars".Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith(expected + "_", fileName);
    }

    [Fact]
    public void TelemetryFilePath_AllInvalidName_FallsBackToHex()
    {
        var invalid = string.Concat(Path.GetInvalidFileNameChars());
        if (invalid.Length == 0)
        {
            return; // platform has no invalid file-name chars to strip
        }
        var path = ServicePaths.GetTelemetryFilePath(invalid, DateTimeOffset.UnixEpoch);
        var fileName = Path.GetFileNameWithoutExtension(path)!;

        // random-hex fallback: "XXXXXXXXXXXX_yyyyMMddTHHmmssZ"
        var namePart = fileName[..fileName.LastIndexOf('_')];
        Assert.Equal(12, namePart.Length);
        Assert.Matches("^[0-9A-F]{12}$", namePart);
    }

    [Fact]
    public void TaskStatePath_LongName_TruncatedTo64()
    {
        var longName = new string('x', 200);
        var path = ServicePaths.GetTaskStatePath(longName);

        Assert.Equal(64 + ".json".Length, Path.GetFileName(path).Length);
        Assert.StartsWith(Path.Combine(ServicePaths.State, new string('x', 64)), path);
    }

    [Fact]
    public void DigestPath_ChangesExtensionToSha256()
    {
        var path = ServicePaths.GetTelemetryDigestPath("/tmp/report_20260101T000000Z.json");

        Assert.Equal("/tmp/report_20260101T000000Z.sha256", path);
    }

    [Fact]
    public void SnapshotAndAuditPaths_AreUnderExpectedDirectories()
    {
        Assert.Equal(Path.Combine(ServicePaths.State, "telemetry-retention.json"),
            ServicePaths.GetTelemetryRetentionSnapshotPath());
        Assert.Equal(Path.Combine(ServicePaths.Security, "latest-audit.json"),
            ServicePaths.GetSecurityAuditReportPath());
        Assert.Equal(Path.Combine(ServicePaths.Base, "config", "appsettings.json"),
            ServicePaths.ConfigurationFile);
    }
}
