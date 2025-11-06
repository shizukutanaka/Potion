using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

public class CollaborationOptions
{
    public bool Enabled { get; set; } = false;

    public int MaxConcurrentUsers { get; set; } = 50;

    public bool EnableRealTimeAlerts { get; set; } = true;
}
