using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

/// <summary>
/// Configuration options for Windows repair functionality
/// </summary>
public sealed class WindowsRepairOptions
{
    public bool Enabled { get; set; } = true;

    public SystemFileCheckOptions? SystemFileCheck { get; set; } = new();

    public DiskCheckOptions? DiskCheck { get; set; } = new();

    public DismRepairOptions? DismRepair { get; set; } = new();

    public ComponentCleanupOptions? ComponentCleanup { get; set; } = new();

    public StartupOptimizationOptions? StartupOptimization { get; set; } = new();
}

public sealed class SystemFileCheckOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, 10080)]
    public int RunEveryMinutes { get; set; } = 10080; // Weekly
}

public sealed class DiskCheckOptions
{
    public bool Enabled { get; set; } = false;

    [StringLength(1)]
    [RegularExpression(@"[A-Z]", ErrorMessage = "Drive letter must be a single uppercase letter")]
    public string? DriveLetter { get; set; } = "C";

    public bool RepairErrors { get; set; } = true;

    [Range(1, 10080)]
    public int RunEveryMinutes { get; set; } = 43200; // Monthly
}

public sealed class DismRepairOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, 10080)]
    public int RunEveryMinutes { get; set; } = 10080; // Weekly
}

public sealed class ComponentCleanupOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, 10080)]
    public int RunEveryMinutes { get; set; } = 1440; // Daily
}

public sealed class StartupOptimizationOptions
{
    public bool Enabled { get; set; } = true;

    [Range(1, 10080)]
    public int RunEveryMinutes { get; set; } = 10080; // Weekly
}

/// <summary>
/// Extended remediation policy options with Windows repair integration
/// </summary>
public partial class RemediationPolicyOptions
{
    public bool Enabled { get; set; } = true;

    public MaintenanceWindow? MaintenanceWindow { get; set; }

    public WindowsRepairOptions? Repairs { get; set; } = new();
}

public sealed class MaintenanceWindow
{
    [StringLength(64)]
    public string Tag { get; set; } = "default";

    [RegularExpression(@"^([01]?\d|2[0-3]):[0-5]\d$", ErrorMessage = "StartTime must be in HH:mm format.")]
    public string StartTime { get; set; } = "02:00";

    [RegularExpression(@"^([01]?\d|2[0-3]):[0-5]\d$", ErrorMessage = "EndTime must be in HH:mm format.")]
    public string EndTime { get; set; } = "06:00";

    public List<DayOfWeek> Days { get; set; } = new()
    {
        DayOfWeek.Sunday,
        DayOfWeek.Saturday
    };
}
