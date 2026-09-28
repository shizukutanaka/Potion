using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Potion.Service.Options;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// RemediationPolicyOptionsValidators are the fluent validators Startup registers via
/// ValidateDataAnnotations-adjacent checks; they must reject duplicate names/tags,
/// unallowlisted commands (with the same bare-name-vs-path rule as CommandValidator),
/// and malformed maintenance windows before the scheduler ever runs.
/// </summary>
public sealed class RemediationPolicyValidatorsTests
{
    private static RemediationTaskOption Task(string name, string command, bool enabled = true) =>
        new() { Name = name, Command = command, Enabled = enabled };

    [Fact]
    public void UniqueNames_EmptyTaskSet_IsValid()
    {
        Assert.True(RemediationPolicyOptionsValidators.HasUniqueTaskNames(new RemediationPolicyOptions()));
    }

    [Fact]
    public void UniqueNames_DistinctNames_IsValid()
    {
        var options = new RemediationPolicyOptions
        {
            Tasks = { Task("a", "sfc.exe"), Task("b", "dism.exe") }
        };

        Assert.True(RemediationPolicyOptionsValidators.HasUniqueTaskNames(options));
    }

    [Fact]
    public void UniqueNames_DuplicateIgnoringCase_ThrowsWithNames()
    {
        var options = new RemediationPolicyOptions
        {
            Tasks = { Task("Cleanup", "sfc.exe"), Task("CLEANUP", "dism.exe"), Task("other", "chkdsk.exe") }
        };

        var ex = Assert.Throws<ValidationException>(
            () => RemediationPolicyOptionsValidators.HasUniqueTaskNames(options));
        Assert.Contains("cleanup", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allowlist_EmptyAllowlist_IsInvalid()
    {
        var options = new RemediationPolicyOptions
        {
            Tasks = { Task("a", "sfc.exe") }
        };

        Assert.False(RemediationPolicyOptionsValidators.CommandsAreAllowlisted(options));
    }

    [Fact]
    public void Allowlist_BareNameMatch_IsValid()
    {
        var options = new RemediationPolicyOptions
        {
            CommandAllowlist = { "sfc.exe" },
            Tasks = { Task("a", "sfc.exe") }
        };

        Assert.True(RemediationPolicyOptionsValidators.CommandsAreAllowlisted(options));
    }

    [Fact]
    public void Allowlist_DisabledTask_Skipped()
    {
        var options = new RemediationPolicyOptions
        {
            CommandAllowlist = { "sfc.exe" },
            Tasks = { Task("a", "sfc.exe"), Task("off", "evil.exe", enabled: false) }
        };

        Assert.True(RemediationPolicyOptionsValidators.CommandsAreAllowlisted(options));
    }

    [Fact]
    public void Allowlist_PathQualifiedCommand_NotBlessedByBareNameEntry()
    {
        // Same bypass rule as CommandValidator: "evil.exe" in the allowlist must
        // not bless "C:\tools\evil.exe" — path-qualified commands need a
        // path-qualified entry.
        var options = new RemediationPolicyOptions
        {
            CommandAllowlist = { "evil.exe" },
            Tasks = { Task("a", "C:\\tools\\evil.exe") }
        };

        var ex = Assert.Throws<ValidationException>(
            () => RemediationPolicyOptionsValidators.CommandsAreAllowlisted(options));
        Assert.Contains("evil.exe", ex.Message);
    }

    [Fact]
    public void Allowlist_MissingCommand_ThrowsListingTaskAndCommand()
    {
        var options = new RemediationPolicyOptions
        {
            CommandAllowlist = { "sfc.exe" },
            Tasks = { Task("cleanup", "notallowed.exe") }
        };

        var ex = Assert.Throws<ValidationException>(
            () => RemediationPolicyOptionsValidators.CommandsAreAllowlisted(options));
        Assert.Contains("cleanup", ex.Message);
        Assert.Contains("notallowed.exe", ex.Message);
    }

    [Fact]
    public void MaintenanceWindows_None_IsValid()
    {
        Assert.True(RemediationPolicyOptionsValidators.MaintenanceWindowsAreValid(new RemediationPolicyOptions()));
    }

    [Fact]
    public void MaintenanceWindows_Valid_IsValid()
    {
        var options = new RemediationPolicyOptions
        {
            MaintenanceWindows =
            {
                new MaintenanceWindowOption { Tag = "nightly", StartTime = "01:00", EndTime = "03:00" }
            }
        };

        Assert.True(RemediationPolicyOptionsValidators.MaintenanceWindowsAreValid(options));
    }

    [Fact]
    public void MaintenanceWindows_DuplicateTag_Throws()
    {
        var options = new RemediationPolicyOptions
        {
            MaintenanceWindows =
            {
                new MaintenanceWindowOption { Tag = "Nightly" },
                new MaintenanceWindowOption { Tag = "nightly" }
            }
        };

        var ex = Assert.Throws<ValidationException>(
            () => RemediationPolicyOptionsValidators.MaintenanceWindowsAreValid(options));
        Assert.Contains("nightly", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaintenanceWindows_UnparseableTime_Throws()
    {
        var options = new RemediationPolicyOptions
        {
            MaintenanceWindows =
            {
                new MaintenanceWindowOption { Tag = "bad", StartTime = "not-a-time" }
            }
        };

        var ex = Assert.Throws<ValidationException>(
            () => RemediationPolicyOptionsValidators.MaintenanceWindowsAreValid(options));
        Assert.Contains("bad", ex.Message);
    }

    [Fact]
    public void MaintenanceWindows_EmptyDaysOfWeek_Throws()
    {
        var options = new RemediationPolicyOptions
        {
            MaintenanceWindows =
            {
                new MaintenanceWindowOption
                {
                    Tag = "empty",
                    DaysOfWeek = new List<DayOfWeek>()
                }
            }
        };

        var ex = Assert.Throws<ValidationException>(
            () => RemediationPolicyOptionsValidators.MaintenanceWindowsAreValid(options));
        Assert.Contains("empty", ex.Message);
        Assert.Contains("day of week", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArgumentsAreSafe_AcceptsBenignArguments()
    {
        var options = new RemediationPolicyOptions
        {
            Tasks = new List<RemediationTaskOption>
            {
                new() { Name = "a", Command = "sfc.exe", Arguments = "/scannow", Enabled = true }
            }
        };
        Assert.True(RemediationPolicyOptionsValidators.ArgumentsAreSafe(options));
    }

    [Fact]
    public void ArgumentsAreSafe_IgnoresDisabledTasks()
    {
        var options = new RemediationPolicyOptions
        {
            Tasks = new List<RemediationTaskOption>
            {
                new() { Name = "a", Command = "net.exe", Arguments = "user \" /add", Enabled = false }
            }
        };
        Assert.True(RemediationPolicyOptionsValidators.ArgumentsAreSafe(options));
    }

    [Fact]
    public void ArgumentsAreSafe_RejectsQuoteSmugglingAndControlChars()
    {
        var options = new RemediationPolicyOptions
        {
            Tasks = new List<RemediationTaskOption>
            {
                new() { Name = "a", Command = "net.exe", Arguments = "user \" /add", Enabled = true },
                new() { Name = "b", Command = "sfc.exe", Arguments = "/scannow\nx", Enabled = true }
            }
        };
        var ex = Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(
            () => RemediationPolicyOptionsValidators.ArgumentsAreSafe(options));
        Assert.Contains("a", ex.Message);
        Assert.Contains("b", ex.Message);
    }

    [Fact]
    public void ArgumentsAreSafe_RejectsOversizedArguments()
    {
        var options = new RemediationPolicyOptions
        {
            Tasks = new List<RemediationTaskOption>
            {
                new() { Name = "a", Command = "sfc.exe", Arguments = new string('a', 2049), Enabled = true }
            }
        };
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(
            () => RemediationPolicyOptionsValidators.ArgumentsAreSafe(options));
    }

    [Fact]
    public void ArgumentsAreAllowlisted_AcceptsMatchingArguments()
    {
        var options = new RemediationPolicyOptions
        {
            CommandArgumentAllowlist = new Dictionary<string, List<string>>
            {
                ["sfc.exe"] = new() { "/scannow" }
            },
            Tasks = new List<RemediationTaskOption>
            {
                new() { Name = "a", Command = "sfc.exe", Arguments = "/scannow", Enabled = true }
            }
        };
        Assert.True(RemediationPolicyOptionsValidators.ArgumentsAreAllowlisted(options));
    }

    [Fact]
    public void ArgumentsAreAllowlisted_UnlistedCommandIsUnrestricted()
    {
        var options = new RemediationPolicyOptions
        {
            CommandArgumentAllowlist = new Dictionary<string, List<string>>
            {
                ["sfc.exe"] = new() { "/scannow" }
            },
            Tasks = new List<RemediationTaskOption>
            {
                new() { Name = "a", Command = "chkdsk.exe", Arguments = "/f /r", Enabled = true }
            }
        };
        Assert.True(RemediationPolicyOptionsValidators.ArgumentsAreAllowlisted(options));
    }

    [Fact]
    public void ArgumentsAreAllowlisted_RejectsUnlistedArguments()
    {
        var options = new RemediationPolicyOptions
        {
            CommandArgumentAllowlist = new Dictionary<string, List<string>>
            {
                ["net.exe"] = new() { "user /domain" }
            },
            Tasks = new List<RemediationTaskOption>
            {
                new() { Name = "a", Command = "net.exe", Arguments = "user badguy /add", Enabled = true }
            }
        };
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(
            () => RemediationPolicyOptionsValidators.ArgumentsAreAllowlisted(options));
    }
}
