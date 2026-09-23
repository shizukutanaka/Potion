using System;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

public class SystemMetricsSamplerParseTests
{
    [Fact]
    public void Systemctl_Parse_CountsSubStates()
    {
        var output = string.Join('\n',
            "cron.service              loaded active running Cron daemon",
            "ssh.service               loaded active running OpenSSH server",
            "bluetooth.service         loaded active exited  Bluetooth service",
            "badunit.service           loaded failed failed  Crashed unit",
            "malformed");

        var (total, running, stopped, failed, failedNames) =
            SystemMetricsSampler.ParseSystemctlServiceLines(output);

        Assert.Equal(4, total);
        Assert.Equal(2, running);
        Assert.Equal(2, stopped);
        Assert.Equal(1, failed);
        Assert.Equal(new[] { "badunit.service" }, failedNames);
    }

    [Fact]
    public void Systemctl_Parse_EmptyOutputYieldsZeros()
    {
        var result = SystemMetricsSampler.ParseSystemctlServiceLines(string.Empty);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.FailedNames);
    }

    [Fact]
    public void Launchctl_Parse_NumericPidIsRunning_ExitCodeIsFailed()
    {
        var output = "PID\tStatus\tLabel\n" +
            "12345\t0\tcom.apple.running\n" +
            "-\t0\tcom.apple.idle\n" +
            "-\t78\tcom.apple.crashed\n";

        var (total, running, stopped, failed, failedNames) =
            SystemMetricsSampler.ParseLaunchctlServiceLines(output);

        Assert.Equal(3, total);
        Assert.Equal(1, running);
        Assert.Equal(2, stopped);
        Assert.Equal(1, failed);
        Assert.Equal(new[] { "com.apple.crashed" }, failedNames);
    }

    [Fact]
    public void Launchctl_Parse_HeaderRowIsSkipped()
    {
        var output = "PID\tStatus\tLabel\n";
        var result = SystemMetricsSampler.ParseLaunchctlServiceLines(output);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public void Journal_Parse_CountsSeverityMarkersAndSecurityUnits()
    {
        var output = string.Join('\n',
            "2026-09-22T07:30:00+0000 host kernel[0]: normal message",
            "2026-09-22T07:31:00+0000 host sshd[12]: authentication error",
            "2026-09-22T07:32:00+0000 host systemd[1]: unit failed to start",
            "2026-09-22T07:33:00+0000 host kernel[0]: oom-killer invoked",
            "2026-09-22T07:34:00+0000 host sudo[55]: session opened",
            "short-line");

        var (total, errors, security, critical, lastAt) =
            SystemMetricsSampler.ParseJournalLines(output);

        Assert.Equal(5, total);
        Assert.Equal(1, critical);   // oom-killer
        Assert.Equal(2, errors);     // error + failed
        Assert.Equal(2, security);   // sshd + sudo
        Assert.Equal(
            new DateTimeOffset(2026, 9, 22, 7, 34, 0, TimeSpan.Zero), lastAt);
    }

    [Fact]
    public void Journal_Parse_NoEventsKeepsMinValueTimestamp()
    {
        var result = SystemMetricsSampler.ParseJournalLines(string.Empty);
        Assert.Equal(0, result.Total);
        Assert.Equal(DateTimeOffset.MinValue, result.LastAt);
    }
}
