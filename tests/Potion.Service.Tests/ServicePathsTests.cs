using System.IO;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Path contract tests: every accessor must return a path under the resolved base
/// (which guarantees the state directory exists on disk).
/// </summary>
public sealed class ServicePathsTests
{
    [Fact]
    public void StateDirectory_ExistsOnDisk()
    {
        Assert.True(Directory.Exists(ServicePaths.State));
    }

    [Fact]
    public void WellKnownPaths_StayUnderBase()
    {
        var prefix = ServicePaths.Base + Path.DirectorySeparatorChar;

        Assert.StartsWith(prefix, ServicePaths.State + Path.DirectorySeparatorChar);
        Assert.StartsWith(ServicePaths.Base, ServicePaths.ConfigurationFile);
        Assert.Equal(Path.Combine(ServicePaths.Base, "config", "appsettings.json"),
            ServicePaths.ConfigurationFile);
    }
}
