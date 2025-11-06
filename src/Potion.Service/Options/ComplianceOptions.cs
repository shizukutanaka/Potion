using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

public class ComplianceOptions
{
    public bool Enabled { get; set; } = false;

    public List<string> Standards { get; set; } = new() { "GDPR", "HIPAA", "PCI-DSS" };

    public int ReportIntervalHours { get; set; } = 24;

    public string ReportDirectory { get; set; } = "reports/compliance";
}
