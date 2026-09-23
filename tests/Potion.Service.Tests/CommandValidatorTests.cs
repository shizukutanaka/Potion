using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Xunit;

namespace Potion.Service.Tests;

public class CommandValidatorTests
{
    private static CommandValidator CreateValidator(IEnumerable<string> allowlist)
    {
        var logger = new Mock<ILogger<CommandValidator>>();
        var options = new Mock<IOptionsMonitor<RemediationPolicyOptions>>();
        options.Setup(o => o.CurrentValue)
            .Returns(new RemediationPolicyOptions { CommandAllowlist = allowlist.ToList() });
        return new CommandValidator(logger.Object, options.Object);
    }

    [Fact]
    public void EnsureCommandIsAllowed_AllowsBareExecutableNameOnAllowlist()
    {
        var validator = CreateValidator(new[] { "sfc.exe" });
        Assert.Equal("sfc.exe /verifyonly", validator.EnsureCommandIsAllowed("sfc.exe /verifyonly"));
    }

    [Fact]
    public void EnsureCommandIsAllowed_RejectsExecutableNotOnAllowlist()
    {
        var validator = CreateValidator(new[] { "sfc.exe" });
        Assert.Throws<InvalidOperationException>(() => validator.EnsureCommandIsAllowed("calc.exe"));
    }

    [Fact]
    public void EnsureCommandIsAllowed_RejectsEmptyAllowlistByDefault()
    {
        var validator = CreateValidator(Array.Empty<string>());
        Assert.Throws<InvalidOperationException>(() => validator.EnsureCommandIsAllowed("sfc.exe"));
    }

    [Theory]
    [InlineData(@"C:\evil\sfc.exe /verifyonly")]
    [InlineData(@"D:\tmp\net.exe user hacker P@ss /add")]
    [InlineData(@"../tools/sfc.exe")]
    [InlineData(@"/tmp/fake/sfc.exe")]
    public void EnsureCommandIsAllowed_RejectsPathQualifiedBinaryMatchingBareNameEntry(string command)
    {
        // A bare-name allowlist entry trusts PATH resolution; a binary at an
        // arbitrary path that merely shares the name must not be blessed.
        var validator = CreateValidator(new[] { "sfc.exe", "net.exe" });
        Assert.Throws<InvalidOperationException>(() => validator.EnsureCommandIsAllowed(command));
    }

    [Fact]
    public void EnsureCommandIsAllowed_AllowsPathQualifiedEntryToMatchItsOwnPath()
    {
        var validator = CreateValidator(new[] { @"C:\Windows\System32\sfc.exe" });
        Assert.Equal(@"C:\Windows\System32\sfc.exe /verifyonly",
            validator.EnsureCommandIsAllowed(@"C:\Windows\System32\sfc.exe /verifyonly"));
    }

    [Fact]
    public void EnsureCommandIsAllowed_AllowsExactFullCommandEntry()
    {
        var validator = CreateValidator(new[] { "wevtutil.exe cl System" });
        Assert.Equal("wevtutil.exe cl System", validator.EnsureCommandIsAllowed("wevtutil.exe cl System"));
    }

    [Fact]
    public void EnsureCommandIsAllowed_RejectsNullOrEmptyCommand()
    {
        var validator = CreateValidator(new[] { "sfc.exe" });
        Assert.Throws<ArgumentException>(() => validator.EnsureCommandIsAllowed("  "));
    }

    [Fact]
    public void CommandsAreAllowlisted_RejectsPathedCommandMatchingBareNameEntry()
    {
        // Config-time validation must enforce the same rule as the runtime
        // validator, or a pathed task passes startup checks but dies at execution.
        var options = new RemediationPolicyOptions
        {
            CommandAllowlist = new List<string> { "sfc.exe" },
            Tasks = new List<RemediationTaskOption>
            {
                new() { Name = "evil", Command = @"D:\tmp\sfc.exe", Enabled = true }
            }
        };
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(
            () => RemediationPolicyOptionsValidators.CommandsAreAllowlisted(options));
    }

    [Fact]
    public void CommandsAreAllowlisted_AllowsBareNameAndPathedEntries()
    {
        var options = new RemediationPolicyOptions
        {
            CommandAllowlist = new List<string> { "sfc.exe", @"C:\Windows\System32\chkdsk.exe" },
            Tasks = new List<RemediationTaskOption>
            {
                new() { Name = "a", Command = "sfc.exe", Enabled = true },
                new() { Name = "b", Command = @"C:\Windows\System32\chkdsk.exe", Enabled = true }
            }
        };
        Assert.True(RemediationPolicyOptionsValidators.CommandsAreAllowlisted(options));
    }
}
