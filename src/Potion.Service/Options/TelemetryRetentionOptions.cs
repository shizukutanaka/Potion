using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

public sealed class TelemetryRetentionOptions
{
    private const int MaxRetentionDays = 365;
    public const int MaxCleanupIntervalHours = 168;
    public const int MaxDeletionsPerSweepLimit = 10_000;

    public bool Enabled { get; set; } = true;

    [Range(1, MaxRetentionDays)]
    public int RetentionDays { get; set; } = 30;

    [Range(1, MaxCleanupIntervalHours)]
    public int CleanupIntervalHours { get; set; } = 12;

    [Range(1, MaxDeletionsPerSweepLimit)]
    public int MaxDeletionsPerSweep { get; set; } = 500;
}
