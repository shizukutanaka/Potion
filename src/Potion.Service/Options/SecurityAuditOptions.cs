using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

public sealed class SecurityAuditOptions
{
    private const int MinimumAuditIntervalHours = 1;
    private const int MaximumAuditIntervalHours = 168;

    public bool Enabled { get; set; } = true;

    [Range(MinimumAuditIntervalHours, MaximumAuditIntervalHours)]
    public int AuditIntervalHours { get; set; } = 24;

    public bool LogSecurityEvents { get; set; } = true;

    public bool AlertOnCriticalIssues { get; set; } = true;

    [Range(0, 365)]
    public int MaxLogRetentionDays { get; set; } = 14;

    [EmailAddress]
    [StringLength(256)]
    public string? SendReportsTo { get; set; }
}
