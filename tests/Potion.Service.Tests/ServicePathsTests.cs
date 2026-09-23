using System;
using System.IO;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

public sealed class ServicePathsTests
{
    [Fact]
    public void Base_IsUnderPotionDirectory()
    {
        var path = ServicePaths.Base;

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith("Potion", path);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Subdirectories_ExistAfterAccess()
    {
        foreach (var path in new[]
        {
            ServicePaths.Logs,
            ServicePaths.State,
            ServicePaths.Reports,
        })
        {
            Assert.True(Directory.Exists(path), $"expected {path} to be created");
            Assert.StartsWith(ServicePaths.Base, path);
        }
    }

    [Fact]
    public void Ensure_IsIdempotent()
    {
        // Repeated access must not throw — the directory already exists.
        var first = ServicePaths.Logs;
        var second = ServicePaths.Logs;

        Assert.Equal(first, second);
    }

    [Fact]
    public void ConfigurationFile_IsInsideBaseAndHasExpectedName()
    {
        var path = ServicePaths.ConfigurationFile;

        Assert.StartsWith(ServicePaths.Base, path);
        Assert.Equal("appsettings.json", Path.GetFileName(path));
    }
}
