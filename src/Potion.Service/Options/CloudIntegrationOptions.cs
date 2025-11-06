using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

public class CloudIntegrationOptions
{
    public bool Enabled { get; set; } = false;

    [Required]
    public List<CloudProviderOptions> Providers { get; set; } = new();

    public int CheckIntervalMinutes { get; set; } = 10;

    public bool AlertOnAnomalies { get; set; } = true;

    public bool AutoRemediationEnabled { get; set; } = false;
}

public class CloudProviderOptions
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = false;

    // AWS specific
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string? Region { get; set; } = "us-east-1";

    // Azure specific
    public string? SubscriptionId { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    // GCP specific
    public string? ProjectId { get; set; }
    public string? ServiceAccountKeyPath { get; set; }

    // Monitoring flags
    public bool MonitorEC2 { get; set; } = true;
    public bool MonitorRDS { get; set; } = true;
    public bool MonitorLambda { get; set; } = false;
    public bool MonitorVM { get; set; } = true;
    public bool MonitorFunctions { get; set; } = false;
    public bool MonitorCompute { get; set; } = true;
    public bool MonitorCloudFunctions { get; set; } = false;
    public bool MonitorPods { get; set; } = true;
    public bool MonitorServices { get; set; } = true;
    public bool MonitorDeployments { get; set; } = true;
}
