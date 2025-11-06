using System;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface IDomainValidator
{
    bool IsValidDomain(string domain);
}

public sealed class DomainValidator : IDomainValidator
{
    private readonly ILogger<DomainValidator> _logger;

    public DomainValidator(ILogger<DomainValidator> logger)
    {
        _logger = logger;
    }

    public bool IsValidDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        if (!NetworkSecurityGuard.TryNormalizeHost(domain, out var normalizedDomain, out var isDnsName) || !isDnsName)
        {
            return false;
        }

        if (normalizedDomain.Length > 253)
        {
            return false;
        }

        if (!NetworkSecurityGuard.HasValidDomainStructure(normalizedDomain))
        {
            return false;
        }

        if (NetworkSecurityGuard.IsHostRestricted(normalizedDomain, isDnsName))
        {
            return false;
        }

        return true;
    }
}
