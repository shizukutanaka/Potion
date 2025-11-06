using System;
using Microsoft.Extensions.Options;
using Potion.Service.Infrastructure;

namespace Potion.Service.Options;

public sealed class RemoteManagementConfigValidator : IValidateOptions<RemoteManagementConfig>
{
    public ValidateOptionsResult Validate(string? name, RemoteManagementConfig options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.ServerEndpoint))
        {
            return ValidateOptionsResult.Fail("RemoteManagement.ServerEndpoint must be provided when remote management is enabled.");
        }

        if (!Uri.TryCreate(options.ServerEndpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            return ValidateOptionsResult.Fail("RemoteManagement.ServerEndpoint must be a valid HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return ValidateOptionsResult.Fail("RemoteManagement.ApiKey must be provided when remote management is enabled.");
        }

        if (options.ApiKey.Length < 32)
        {
            return ValidateOptionsResult.Fail("RemoteManagement.ApiKey must be at least 32 characters long.");
        }

        if (string.IsNullOrWhiteSpace(options.MachineId))
        {
            return ValidateOptionsResult.Fail("RemoteManagement.MachineId must be specified when remote management is enabled.");
        }

        if (options.HeartbeatInterval < TimeSpan.FromMinutes(1))
        {
            return ValidateOptionsResult.Fail("RemoteManagement.HeartbeatInterval must be one minute or greater.");
        }

        if (options.LogSyncInterval < TimeSpan.FromMinutes(5))
        {
            return ValidateOptionsResult.Fail("RemoteManagement.LogSyncInterval must be five minutes or greater.");
        }

        return ValidateOptionsResult.Success;
    }
}
