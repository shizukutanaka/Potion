using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// ヘルスチェック結果
/// </summary>
public sealed class HealthCheckResult
{
    public HealthCheckResult(bool isHealthy, IReadOnlyDictionary<string, ComponentHealth> components, DateTimeOffset checkedAt)
    {
        IsHealthy = isHealthy;
        ComponentHealth = components;
        CheckedAt = checkedAt;
    }

    public bool IsHealthy { get; }

    public IReadOnlyDictionary<string, ComponentHealth> ComponentHealth { get; }

    public DateTimeOffset CheckedAt { get; }
}

/// <summary>
/// コマンドバリデータ
/// </summary>
public interface ICommandValidator
{
    string EnsureCommandIsAllowed(string command);

    void EnsureArgumentsAreAllowed(string command, string? arguments);

    IReadOnlyCollection<string> GetCurrentAllowlist();
}

public sealed class CommandValidator : ICommandValidator
{
    private const int MaxCommandLength = 260;

    private readonly ILogger<CommandValidator> _logger;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _optionsMonitor;

    public CommandValidator(ILogger<CommandValidator> logger, IOptionsMonitor<RemediationPolicyOptions> optionsMonitor)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
    }

    public string EnsureCommandIsAllowed(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command must not be null, empty, or whitespace.", nameof(command));
        }

        if (command.Length > MaxCommandLength)
        {
            throw new ArgumentException($"Command exceeds the maximum length of {MaxCommandLength} characters.", nameof(command));
        }

        var allowlist = GetCurrentAllowlist();
        if (allowlist.Count == 0)
        {
            // Deny by default: an empty allowlist means the security control was
            // never configured, not that every command is trusted.
            _logger.LogWarning("Blocked command because the remediation allowlist is empty: {Command}", command);
            throw new InvalidOperationException("Command allowlist is empty; commands are denied by default.");
        }

        var fileName = command.Split(' ', '\t')[0];
        var executableName = System.IO.Path.GetFileName(fileName);
        // An explicit path must match a path-qualified allowlist entry (or the full
        // command). Bare-name entries trust PATH resolution and must not bless a
        // binary at an arbitrary location that shares the name (e.g. D:\tmp\net.exe).
        var hasExplicitPath = fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0;
        var isAllowed = allowlist.Any(allowed =>
            string.Equals(allowed, command, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(allowed, fileName, StringComparison.OrdinalIgnoreCase) ||
            (!hasExplicitPath && string.Equals(allowed, executableName, StringComparison.OrdinalIgnoreCase)));

        if (!isAllowed)
        {
            _logger.LogWarning("Blocked command that is not in the allowlist: {Command}", command);
            throw new InvalidOperationException("Command is not allowed by the remediation policy allowlist.");
        }

        return command;
    }

    public void EnsureArgumentsAreAllowed(string command, string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return;
        }

        // Arguments land verbatim on the spawned command line; a double-quote or
        // control character can break quoting and smuggle extra arguments into
        // an allowlisted binary (e.g. `net.exe` -> net user /add).
        if (arguments.Length > 2048)
        {
            throw new ArgumentException("Arguments exceed the maximum length of 2048 characters.", nameof(arguments));
        }
        if (arguments.IndexOf('"') >= 0 || arguments.Any(char.IsControl))
        {
            _logger.LogWarning("Blocked arguments containing quote/control characters for command {Command}", command);
            throw new InvalidOperationException("Arguments contain characters that could alter the spawned command line.");
        }

        var argumentAllowlist = _optionsMonitor.CurrentValue.CommandArgumentAllowlist;
        var fileName = command.Split(' ', '\t')[0];
        var executableName = System.IO.Path.GetFileName(fileName);
        var entry = argumentAllowlist.FirstOrDefault(kv =>
            string.Equals(kv.Key, command, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kv.Key, fileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kv.Key, executableName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(entry.Key) &&
            !entry.Value.Any(a => string.Equals(a, arguments, StringComparison.Ordinal)))
        {
            _logger.LogWarning("Blocked arguments not in the allowlist for command {Command}: {Arguments}", command, arguments);
            throw new InvalidOperationException("Arguments are not allowed by the command argument allowlist.");
        }
    }

    public IReadOnlyCollection<string> GetCurrentAllowlist()
    {
        return _optionsMonitor.CurrentValue.CommandAllowlist;
    }
}


/// <summary>
/// 修復タスクスケジューラ
/// </summary>
public interface IRemediationScheduler
{
    Task ScheduleTaskAsync(RemediationTask task, CancellationToken cancellationToken = default);
}
