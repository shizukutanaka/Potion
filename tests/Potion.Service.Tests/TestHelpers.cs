using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Infrastructure;
using Potion.Service.Options;

namespace Potion.Service.Tests;

/// <summary>
/// ILogger&lt;T&gt; shim that forwards every call to a shared inner ILogger,
/// so tests can keep verifying logging through a single logger mock while
/// the composed validators each receive a correctly typed logger.
/// </summary>
public sealed class ForwardingLogger<T> : ILogger<T>
{
    private readonly ILogger _inner;

    public ForwardingLogger(ILogger inner)
    {
        _inner = inner;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>Shared factories matching the service's current public constructors.</summary>
public static class TestObjectFactory
{
    /// <summary>
    /// CommandGuard now composes focused validators; this wires the same graph
    /// the DI container builds, with all validator loggers forwarding to one mock.
    /// </summary>
    public static CommandGuard CreateCommandGuard(ILogger logger, IOptionsMonitor<RemediationPolicyOptions> optionsMonitor)
    {
        return new CommandGuard(
            new CommandValidator(new ForwardingLogger<CommandValidator>(logger), optionsMonitor),
            new ArgumentSanitizer(new ForwardingLogger<ArgumentSanitizer>(logger)),
            new UrlValidator(new ForwardingLogger<UrlValidator>(logger)),
            new DomainValidator(new ForwardingLogger<DomainValidator>(logger)),
            new RateLimiter(new ForwardingLogger<RateLimiter>(logger)));
    }
}

/// <summary>
/// OS-dependent test gates. The product is a Windows service and several tests
/// exercise cmd.exe/process semantics that do not exist on other platforms.
/// </summary>
public static class TestEnvironment
{
    public static bool IsWindows => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
        System.Runtime.InteropServices.OSPlatform.Windows);
}
