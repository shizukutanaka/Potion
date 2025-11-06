using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

public interface ICommandGuard : IDisposable
{
    string EnsureCommandIsAllowed(string command);
    string SanitizeArguments(string arguments);
    bool IsValidUrl(string url);
    bool IsValidDomain(string domain);
    Task<bool> CheckRateLimitAsync(string operation, CancellationToken cancellationToken);
    IReadOnlyCollection<string> GetCurrentAllowlist();
}

public sealed class CommandGuard : ICommandGuard, IDisposable
{
    private readonly ICommandValidator _commandValidator;
    private readonly IArgumentSanitizer _argumentSanitizer;
    private readonly IUrlValidator _urlValidator;
    private readonly IDomainValidator _domainValidator;
    private readonly IRateLimiter _rateLimiter;

    public CommandGuard(
        ICommandValidator commandValidator,
        IArgumentSanitizer argumentSanitizer,
        IUrlValidator urlValidator,
        IDomainValidator domainValidator,
        IRateLimiter rateLimiter)
    {
        _commandValidator = commandValidator;
        _argumentSanitizer = argumentSanitizer;
        _urlValidator = urlValidator;
        _domainValidator = domainValidator;
        _rateLimiter = rateLimiter;
    }

    public string EnsureCommandIsAllowed(string command)
    {
        return _commandValidator.EnsureCommandIsAllowed(command);
    }

    public string SanitizeArguments(string arguments)
    {
        return _argumentSanitizer.SanitizeArguments(arguments);
    }

    public bool IsValidUrl(string url)
    {
        return _urlValidator.IsValidUrl(url);
    }

    public bool IsValidDomain(string domain)
    {
        return _domainValidator.IsValidDomain(domain);
    }

    public Task<bool> CheckRateLimitAsync(string operation, CancellationToken cancellationToken)
    {
        return _rateLimiter.CheckRateLimitAsync(operation, cancellationToken);
    }

    public IReadOnlyCollection<string> GetCurrentAllowlist()
    {
        return _commandValidator.GetCurrentAllowlist();
    }

    public void Dispose()
    {
        if (_rateLimiter is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
