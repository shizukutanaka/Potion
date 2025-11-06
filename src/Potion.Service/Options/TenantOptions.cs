using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

public class TenantOptions
{
    public bool MultiTenantEnabled { get; set; } = false;

    public string TenantConfigPath { get; set; } = "config/tenants.json";

    public bool EnableTenantIsolation { get; set; } = true;
}
