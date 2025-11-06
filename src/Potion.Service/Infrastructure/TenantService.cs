using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Potion.Service.Infrastructure;

public class TenantOptions
{
    public bool MultiTenantEnabled { get; set; } = false;
    public string TenantConfigPath { get; set; } = "config/tenants.json";
    public bool EnableTenantIsolation { get; set; } = true;
}

public class TenantService
{
    private readonly ILogger<TenantService> _logger;
    private readonly TenantOptions _options;
    private readonly ConcurrentDictionary<string, Tenant> _tenants = new();

    public TenantService(
        ILogger<TenantService> logger,
        IOptions<TenantOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        LoadTenants();
    }

    private void LoadTenants()
    {
        try
        {
            var configPath = Path.Combine(ServicePaths.Base, _options.TenantConfigPath);
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var tenantList = JsonSerializer.Deserialize<List<Tenant>>(json);
                if (tenantList != null)
                {
                    foreach (var tenant in tenantList)
                    {
                        _tenants[tenant.Id] = tenant;
                    }
                }
                _logger.LogInformation("Loaded {Count} tenants from configuration", _tenants.Count);
            }
            else
            {
                _logger.LogInformation("No tenant configuration file found, creating default tenant");
                CreateDefaultTenant();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load tenant configuration");
            CreateDefaultTenant();
        }
    }

    private void CreateDefaultTenant()
    {
        var defaultTenant = new Tenant
        {
            Id = "default",
            Name = "Default Tenant",
            ApiKey = GenerateApiKey(),
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true,
            Quotas = new TenantQuotas
            {
                MaxRequestsPerHour = 1000,
                MaxStorageGB = 10,
                MaxConcurrentTasks = 5
            }
        };

        _tenants[defaultTenant.Id] = defaultTenant;
        SaveTenants();
    }

    public Tenant? GetTenant(string tenantId)
    {
        return _tenants.TryGetValue(tenantId, out var tenant) ? tenant : null;
    }

    public Tenant? GetTenantByApiKey(string apiKey)
    {
        return _tenants.Values.FirstOrDefault(t => t.ApiKey == apiKey && t.IsActive);
    }

    public bool ValidateTenantAccess(string tenantId, string? apiKey = null)
    {
        var tenant = GetTenant(tenantId);
        if (tenant == null || !tenant.IsActive)
            return false;

        if (!string.IsNullOrEmpty(apiKey) && tenant.ApiKey != apiKey)
            return false;

        return true;
    }

    public bool CheckTenantQuota(string tenantId, string resourceType, int requestedAmount = 1)
    {
        var tenant = GetTenant(tenantId);
        if (tenant == null) return false;

        return resourceType switch
        {
            "requests" => tenant.RequestsThisHour < tenant.Quotas.MaxRequestsPerHour,
            "storage" => tenant.StorageUsedGB < tenant.Quotas.MaxStorageGB,
            "tasks" => tenant.ActiveTasks < tenant.Quotas.MaxConcurrentTasks,
            _ => true
        };
    }

    public void UpdateTenantUsage(string tenantId, string resourceType, int amount = 1)
    {
        var tenant = GetTenant(tenantId);
        if (tenant == null) return;

        switch (resourceType)
        {
            case "requests":
                tenant.RequestsThisHour += amount;
                break;
            case "storage":
                tenant.StorageUsedGB += amount;
                break;
            case "tasks":
                tenant.ActiveTasks += amount;
                break;
        }

        SaveTenants();
    }

    public Tenant CreateTenant(string name, TenantQuotas quotas)
    {
        var tenant = new Tenant
        {
            Id = GenerateTenantId(),
            Name = name,
            ApiKey = GenerateApiKey(),
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true,
            Quotas = quotas,
            RequestsThisHour = 0,
            StorageUsedGB = 0,
            ActiveTasks = 0
        };

        _tenants[tenant.Id] = tenant;
        SaveTenants();
        _logger.LogInformation("Created new tenant: {TenantId} - {Name}", tenant.Id, tenant.Name);

        return tenant;
    }

    private void SaveTenants()
    {
        try
        {
            var configPath = Path.Combine(ServicePaths.Base, _options.TenantConfigPath);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            var tenantList = _tenants.Values.ToList();
            var json = JsonSerializer.Serialize(tenantList, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save tenant configuration");
        }
    }

    private static string GenerateTenantId()
    {
        return Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("/", "").Replace("+", "").Substring(0, 32);
    }

    public IEnumerable<Tenant> GetAllTenants()
    {
        return _tenants.Values;
    }
}

public class Tenant
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public TenantQuotas Quotas { get; set; } = new();

    // Runtime usage tracking
    public int RequestsThisHour { get; set; }
    public double StorageUsedGB { get; set; }
    public int ActiveTasks { get; set; }
}

public class TenantQuotas
{
    public int MaxRequestsPerHour { get; set; } = 1000;
    public double MaxStorageGB { get; set; } = 10;
    public int MaxConcurrentTasks { get; set; } = 5;
}
