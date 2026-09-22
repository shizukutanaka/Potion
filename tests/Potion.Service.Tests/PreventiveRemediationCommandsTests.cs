using Potion.Service.Remediation;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// Guards the regression where preventive/event-driven remediation emitted pseudo-command
/// names ("Optimize-CpuUsage") that the CommandValidator allowlist always rejected.
/// Every mapped key must resolve to a bare executable plus a separate arguments string.
/// </summary>
public sealed class PreventiveRemediationCommandsTests
{
    [Theory]
    [InlineData("CpuUsage")]
    [InlineData("MemoryUsage")]
    [InlineData("DiskUsage")]
    [InlineData("cpu-optimization")]
    [InlineData("memory-cleanup")]
    [InlineData("disk-cleanup")]
    public void KnownKeys_ResolveToBareExecutableAndArguments(string key)
    {
        Assert.True(PreventiveRemediationCommands.TryResolve(key, out var command, out var arguments));

        // FileName must be a bare executable — whitespace would break Process.Start.
        Assert.DoesNotContain(' ', command);
        Assert.EndsWith(".exe", command);
        Assert.NotNull(arguments);
    }

    [Fact]
    public void KeyMatching_IsCaseInsensitive()
    {
        Assert.True(PreventiveRemediationCommands.TryResolve("cpuusage", out _, out _));
    }

    [Fact]
    public void UnknownKey_ReturnsFalse()
    {
        Assert.False(PreventiveRemediationCommands.TryResolve("NetworkUsage", out var command, out _));
        Assert.Empty(command);
    }
}
