using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

public class SystemHealthBoundaryTests
{
    [Theory]
    [InlineData(96.0, PressureLevel.Critical)]
    [InlineData(95.0, PressureLevel.Critical)]
    [InlineData(94.9, PressureLevel.High)]
    [InlineData(85.0, PressureLevel.High)]
    [InlineData(84.9, PressureLevel.Medium)]
    [InlineData(70.0, PressureLevel.Medium)]
    [InlineData(69.9, PressureLevel.None)]
    [InlineData(0.0, PressureLevel.None)]
    public void ToPressure_MapsThresholds(double percent, PressureLevel expected)
    {
        Assert.Equal(expected, SystemHealthMonitor.ToPressure(percent));
    }

    [Theory]
    [InlineData("/metrics", false)]
    [InlineData("/collaboration", false)]
    [InlineData("/collaboration/negotiate", false)]
    [InlineData("/api/health", true)]
    [InlineData("/health", true)]
    [InlineData("/index.html", true)]
    public void ShouldTrack_ExcludesLongLivedEndpoints(string path, bool expected)
    {
        Assert.Equal(expected, RequestMetricsTracker.ShouldTrack(new PathString(path)));
    }

    [Fact]
    public void ReadSysfs_ReadsAndTrimsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sysfs-{Guid.NewGuid()}");
        File.WriteAllText(path, "  Intel  \n");
        try
        {
            Assert.Equal("Intel", SystemMetricsSampler.ReadSysfs(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadSysfs_MissingFileReturnsEmpty()
    {
        Assert.Equal(string.Empty, SystemMetricsSampler.ReadSysfs("/nonexistent/path/file"));
    }
}
