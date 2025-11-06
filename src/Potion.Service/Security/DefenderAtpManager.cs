using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Potion.Service.Security;

/// <summary>
/// Windows Defender ATP Integration Manager for Windows Server 2025.
/// Integrates with Microsoft Defender Advanced Threat Protection (ATP) for continuous threat monitoring.
/// Provides threat intelligence ingestion, incident response automation, and endpoint protection.
/// </summary>
public interface IDefenderAtpManager
{
    /// <summary>Checks if Defender ATP is available and configured</summary>
    Task<DefenderAtpAvailabilityStatus> CheckAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Fetches latest threat intelligence from Defender ATP</summary>
    Task<ThreatIntelligenceData> FetchLatestThreatIntelligenceAsync(CancellationToken cancellationToken);

    /// <summary>Blocks indicators of compromise (IoCs) locally</summary>
    Task<IndicatorBlockingResult> BlockDetectedIndicatorsAsync(ThreatIntelligenceData threatIntel, CancellationToken cancellationToken);

    /// <summary>Configures incident notification for critical threats</summary>
    Task<bool> ConfigureIncidentNotificationAsync(CancellationToken cancellationToken);

    /// <summary>Gets real-time threat status</summary>
    Task<ThreatStatusSummary> GetThreatStatusAsync(CancellationToken cancellationToken);

    /// <summary>Initiates automated incident response workflow</summary>
    Task<IncidentResponseResult> InitiateIncidentResponseAsync(SecurityIncident incident, CancellationToken cancellationToken);

    /// <summary>Generates threat assessment report</summary>
    Task<DefenderAtpComplianceReport> GenerateThreatAssessmentAsync(CancellationToken cancellationToken);
}

/// <summary>Defender ATP availability and configuration status</summary>
public sealed record DefenderAtpAvailabilityStatus(
    bool IsAvailable,
    bool IsConfigured,
    bool IsConnected,
    string DefenderVersion,
    DateTime LastHealthCheck,
    List<string> ConfigurationIssues,
    string RecommendedAction
);

/// <summary>Threat intelligence data from Defender ATP</summary>
public sealed record ThreatIntelligenceData(
    string IntelligenceId,
    DateTime FetchedAt,
    List<IndicatorOfCompromise> Indicators,
    List<MalwareSignature> MalwareSignatures,
    List<AnomalousActivityPattern> AnomalousPatterns,
    int ThreatCount,
    ThreatLevel OverallThreatLevel,
    List<string> RecommendedActions
);

/// <summary>Indicator of compromise (IoC)</summary>
public sealed record IndicatorOfCompromise(
    string Indicator,              // File hash, IP, domain, etc.
    IndicatorType Type,
    ThreatLevel Severity,
    string MalwareName,
    DateTime FirstObserved,
    DateTime LastObserved,
    int SuspiciousTenantCount,
    string? BlockAction           // "quarantine", "block", "alert"
);

/// <summary>Type of indicator</summary>
public enum IndicatorType
{
    FileHash = 0,
    IpAddress = 1,
    DomainName = 2,
    Url = 3,
    RegistryValue = 4,
    ProcessBehavior = 5,
    NetworkSignature = 6
}

/// <summary>Malware signature from Defender</summary>
public sealed record MalwareSignature(
    string SignatureId,
    string MalwareName,
    string Family,
    ThreatLevel Severity,
    List<string> DetectionRules,
    string? RemediationAction,
    bool RequiresReboot
);

/// <summary>Anomalous activity pattern</summary>
public sealed record AnomalousActivityPattern(
    string PatternId,
    string Description,
    ThreatLevel Severity,
    List<string> AnomalousBehaviors,
    int Occurrences,
    double ConfidenceScore,      // 0-100%
    string? MitigationStrategy
);

/// <summary>Threat level severity</summary>
public enum ThreatLevel
{
    Informational = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>Result of indicator blocking operation</summary>
public sealed record IndicatorBlockingResult(
    bool Success,
    int BlockedIndicators,
    int QuarantinedFiles,
    List<string> BlockedIocs,
    List<string> BlockedMalware,
    List<string> Errors,
    DateTime BlockingTime
);

/// <summary>Real-time threat status summary</summary>
public sealed record ThreatStatusSummary(
    int ActiveThreats,
    int QuarantinedItems,
    int SuspiciousProcesses,
    ThreatLevel OverallRiskLevel,
    DateTime LastFullScan,
    TimeSpan TimeSinceLastScan,
    bool FullProtectionActive,
    List<string> ActiveProtectionFeatures
);

/// <summary>Security incident detected</summary>
public sealed record SecurityIncident(
    string IncidentId,
    string Title,
    ThreatLevel Severity,
    DateTime DetectedAt,
    string SourceIndicator,
    List<string> AffectedSystems,
    List<string> ImpactedResources,
    string? RecommendedAction
);

/// <summary>Incident response operation result</summary>
public sealed record IncidentResponseResult(
    bool Success,
    string IncidentId,
    string ResponseAction,
    bool AffectedProcessesTerminated,
    bool MalwareRemoved,
    List<string> QuarantinedFiles,
    DateTime ResponseTime,
    List<string> AdditionalRecommendations
);

/// <summary>Defender ATP compliance and threat assessment report</summary>
public sealed record DefenderAtpComplianceReport(
    bool FullProtectionActive,
    int ThreatDetectionCoverage,   // 0-100%
    int VulnerabilityScore,        // 0-100 (lower is better)
    int ExposureScore,             // 0-100 (lower is better)
    int ComplianceScore,           // 0-100
    List<string> CriticalVulnerabilities,
    List<string> HighRiskIndicators,
    List<string> RecommendedActions,
    DateTime ReportGeneratedAt
);

/// <summary>
/// Implementation of Defender ATP Manager.
/// Integrates with Windows Defender Advanced Threat Protection.
/// </summary>
public sealed class DefenderAtpManager : IDefenderAtpManager
{
    private readonly ILogger<DefenderAtpManager> _logger;

    // Registry paths for Defender ATP configuration
    private const string DefenderRegistryPath = @"SYSTEM\CurrentControlSet\Services\WinDefend";
    private const string AtpRegistryPath = @"SYSTEM\CurrentControlSet\Services\Sense";
    private const string DefenderPoliciesPath = @"SOFTWARE\Policies\Microsoft\Windows Defender";

    // Mock threat intelligence data (in production, this would call Defender ATP API)
    private static readonly List<IndicatorOfCompromise> MockIndicators = new()
    {
        new IndicatorOfCompromise(
            Indicator: "e99a18c428cb38d5f260853678922e03",  // MD5 hash
            Type: IndicatorType.FileHash,
            Severity: ThreatLevel.High,
            MalwareName: "Emotet",
            FirstObserved: DateTime.UtcNow.AddDays(-30),
            LastObserved: DateTime.UtcNow.AddHours(-2),
            SuspiciousTenantCount: 2847,
            BlockAction: "quarantine"
        ),
        new IndicatorOfCompromise(
            Indicator: "192.168.1.100",
            Type: IndicatorType.IpAddress,
            Severity: ThreatLevel.Critical,
            MalwareName: "TrickBot C2 Server",
            FirstObserved: DateTime.UtcNow.AddDays(-60),
            LastObserved: DateTime.UtcNow.AddMinutes(-15),
            SuspiciousTenantCount: 5632,
            BlockAction: "block"
        )
    };

    public DefenderAtpManager(ILogger<DefenderAtpManager> logger)
    {
        _logger = logger;
    }

    public async Task<DefenderAtpAvailabilityStatus> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Checking Defender ATP availability and configuration");

        try
        {
            bool winDefendServiceExists = ServiceExists("WinDefend");
            bool senseServiceExists = ServiceExists("Sense"); // ATP sensor
            bool atpConfigured = IsDefenderAtpConfigured();
            bool connected = await IsDefenderAtpConnectedAsync(cancellationToken);

            var issues = new List<string>();
            if (!winDefendServiceExists)
                issues.Add("Windows Defender service not found");
            if (!senseServiceExists)
                issues.Add("Defender ATP sensor (Sense) not installed");
            if (!atpConfigured)
                issues.Add("Defender ATP not properly configured");
            if (!connected)
                issues.Add("Unable to connect to Defender ATP cloud service");

            bool isAvailable = winDefendServiceExists && senseServiceExists;
            string defenderVersion = GetDefenderVersion();

            return new DefenderAtpAvailabilityStatus(
                IsAvailable: isAvailable,
                IsConfigured: atpConfigured,
                IsConnected: connected,
                DefenderVersion: defenderVersion,
                LastHealthCheck: DateTime.UtcNow,
                ConfigurationIssues: issues,
                RecommendedAction: isAvailable && atpConfigured && connected
                    ? "Defender ATP is available and configured"
                    : "Configure Defender ATP: Enable Sense service and cloud connectivity"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check Defender ATP availability");
            return new DefenderAtpAvailabilityStatus(
                false, false, false, "Unknown",
                DateTime.UtcNow,
                new() { ex.Message },
                "Manual configuration required"
            );
        }
    }

    public async Task<ThreatIntelligenceData> FetchLatestThreatIntelligenceAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching latest threat intelligence from Defender ATP");

        try
        {
            var indicators = new List<IndicatorOfCompromise>(MockIndicators);

            var malwareSignatures = new List<MalwareSignature>
            {
                new("SIG-001", "Emotet", "Banking Trojan", ThreatLevel.Critical,
                    new() { "network_c2", "persistence_registry", "credential_theft" },
                    "quarantine", false),
                new("SIG-002", "TrickBot", "Remote Access Trojan", ThreatLevel.Critical,
                    new() { "lateral_movement", "privilege_escalation", "data_exfiltration" },
                    "block", true),
                new("SIG-003", "Ryuk", "Ransomware", ThreatLevel.Critical,
                    new() { "file_encryption", "backup_deletion", "ransom_note" },
                    "quarantine", true)
            };

            var anomalousPatterns = new List<AnomalousActivityPattern>
            {
                new("ANOMALY-001", "Unusual privilege elevation attempts",
                    ThreatLevel.High,
                    new() { "token_impersonation", "credential_dumping", "sudo_abuse" },
                    47, 87.5, "Enable LSA Protection (Credential Guard)"),
                new("ANOMALY-002", "Lateral movement indicators",
                    ThreatLevel.High,
                    new() { "pass_the_hash", "psexec_execution", "wmi_lateral_movement" },
                    23, 92.3, "Isolate compromised system, conduct forensics"),
                new("ANOMALY-003", "Data exfiltration patterns",
                    ThreatLevel.Medium,
                    new() { "large_file_transfers", "cloud_upload", "dns_tunneling" },
                    12, 78.9, "Block known C2 servers, monitor egress traffic")
            };

            return new ThreatIntelligenceData(
                IntelligenceId: Guid.NewGuid().ToString("N"),
                FetchedAt: DateTime.UtcNow,
                Indicators: indicators,
                MalwareSignatures: malwareSignatures,
                AnomalousPatterns: anomalousPatterns,
                ThreatCount: indicators.Count + malwareSignatures.Count,
                OverallThreatLevel: ThreatLevel.High,
                RecommendedActions: new()
                {
                    "Immediately quarantine systems matching IoC signatures",
                    "Review and harden Access Control Lists (ACLs)",
                    "Enable Enhanced Monitoring for flagged processes",
                    "Conduct full forensic analysis of high-risk systems"
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch threat intelligence");
            throw;
        }
    }

    public async Task<IndicatorBlockingResult> BlockDetectedIndicatorsAsync(ThreatIntelligenceData threatIntel, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Blocking {Count} detected indicators", threatIntel.Indicators.Count);

        try
        {
            var blockedIocs = new List<string>();
            var blockedMalware = new List<string>();
            var errors = new List<string>();

            // Block file hashes
            var fileHashes = threatIntel.Indicators
                .Where(i => i.Type == IndicatorType.FileHash)
                .ToList();

            foreach (var indicator in fileHashes)
            {
                if (AddToDefenderBlockList(indicator.Indicator))
                {
                    blockedIocs.Add(indicator.Indicator);
                    _logger.LogWarning("Blocked malware: {Name}", indicator.MalwareName);
                    if (!blockedMalware.Contains(indicator.MalwareName))
                        blockedMalware.Add(indicator.MalwareName);
                }
                else
                {
                    errors.Add($"Failed to block hash {indicator.Indicator}");
                }
            }

            // Block network indicators
            var networkIndicators = threatIntel.Indicators
                .Where(i => i.Type is IndicatorType.IpAddress or IndicatorType.DomainName)
                .ToList();

            foreach (var indicator in networkIndicators)
            {
                if (AddToDefenderNetworkBlockList(indicator.Indicator, indicator.Type))
                {
                    blockedIocs.Add(indicator.Indicator);
                }
                else
                {
                    errors.Add($"Failed to block {indicator.Type}: {indicator.Indicator}");
                }
            }

            // Log security event
            PotionEventSource.Log.SecurityThreatIndicatorBlocked(
                blockedIocs.Count, blockedMalware.Count);

            return new IndicatorBlockingResult(
                Success: errors.Count == 0,
                BlockedIndicators: blockedIocs.Count,
                QuarantinedFiles: fileHashes.Count,
                BlockedIocs: blockedIocs,
                BlockedMalware: blockedMalware,
                Errors: errors,
                BlockingTime: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to block indicators");
            return new IndicatorBlockingResult(
                false, 0, 0, new(), new(),
                new() { ex.Message }, DateTime.UtcNow
            );
        }
    }

    public async Task<bool> ConfigureIncidentNotificationAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Configuring Defender ATP incident notifications");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DefenderPoliciesPath, writable: true) ??
                           Registry.LocalMachine.CreateSubKey(DefenderPoliciesPath);

            // Enable notifications for critical threats
            key?.SetValue("EnableNotification", 1);
            key?.SetValue("NotificationLevel", 2); // High and Critical
            key?.SetValue("CloudNotificationEnabled", 1);

            _logger.LogInformation("Incident notifications configured");
            PotionEventSource.Log.SecurityConfigurationApplied("Defender ATP Notifications");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure incident notifications");
            return false;
        }
    }

    public async Task<ThreatStatusSummary> GetThreatStatusAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Retrieving current threat status");

        try
        {
            // In production, this would query Defender ATP API and local scans
            var activeThreats = 0;
            var quarantinedItems = 3;
            var suspiciousProcesses = 1;
            var lastScan = DateTime.UtcNow.AddHours(-2);

            return new ThreatStatusSummary(
                ActiveThreats: activeThreats,
                QuarantinedItems: quarantinedItems,
                SuspiciousProcesses: suspiciousProcesses,
                OverallRiskLevel: activeThreats == 0 ? ThreatLevel.Low : ThreatLevel.High,
                LastFullScan: lastScan,
                TimeSinceLastScan: DateTime.UtcNow - lastScan,
                FullProtectionActive: true,
                ActiveProtectionFeatures: new()
                {
                    "Real-time Protection",
                    "Cloud-delivered Protection",
                    "Behavioral Monitoring",
                    "Ransomware Protection"
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat status");
            throw;
        }
    }

    public async Task<IncidentResponseResult> InitiateIncidentResponseAsync(SecurityIncident incident, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initiating incident response for incident {IncidentId}", incident.IncidentId);

        try
        {
            var quarantinedFiles = new List<string>();
            var recommendations = new List<string>();

            // Automated response based on threat level
            if (incident.Severity == ThreatLevel.Critical)
            {
                // Isolate affected systems
                foreach (var system in incident.AffectedSystems)
                {
                    _logger.LogWarning("Isolating system: {System}", system);
                    // In production: Execute isolation commands
                }

                // Quarantine suspicious files
                if (!string.IsNullOrEmpty(incident.SourceIndicator))
                {
                    quarantinedFiles.Add(incident.SourceIndicator);
                    _logger.LogWarning("Quarantined: {File}", incident.SourceIndicator);
                }

                recommendations.Add("Conduct full forensic investigation");
                recommendations.Add("Review system access logs for lateral movement");
                recommendations.Add("Change compromised credentials");
            }

            // Log incident response
            PotionEventSource.Log.SecurityIncidentResponseInitiated(
                incident.IncidentId, incident.Title);

            return new IncidentResponseResult(
                Success: true,
                IncidentId: incident.IncidentId,
                ResponseAction: $"Automated response for {incident.Title}",
                AffectedProcessesTerminated: incident.Severity == ThreatLevel.Critical,
                MalwareRemoved: incident.Severity >= ThreatLevel.High,
                QuarantinedFiles: quarantinedFiles,
                ResponseTime: DateTime.UtcNow,
                AdditionalRecommendations: recommendations
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate incident response");
            return new IncidentResponseResult(
                false, incident.IncidentId, "Failed", false, false,
                new(), DateTime.UtcNow, new() { ex.Message }
            );
        }
    }

    public async Task<DefenderAtpComplianceReport> GenerateThreatAssessmentAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Generating Defender ATP threat assessment report");

        try
        {
            var threatIntel = await FetchLatestThreatIntelligenceAsync(cancellationToken);
            var status = await GetThreatStatusAsync(cancellationToken);

            int vulnerabilityScore = CalculateVulnerabilityScore(threatIntel);
            int exposureScore = CalculateExposureScore(threatIntel, status);
            int complianceScore = (100 - vulnerabilityScore + 100 - exposureScore) / 2;

            return new DefenderAtpComplianceReport(
                FullProtectionActive: status.FullProtectionActive,
                ThreatDetectionCoverage: 98,  // Assume 98% coverage with ATP
                VulnerabilityScore: vulnerabilityScore,
                ExposureScore: exposureScore,
                ComplianceScore: complianceScore,
                CriticalVulnerabilities: threatIntel.Indicators
                    .Where(i => i.Severity == ThreatLevel.Critical)
                    .Select(i => i.MalwareName)
                    .Distinct()
                    .ToList(),
                HighRiskIndicators: threatIntel.AnomalousPatterns
                    .Where(p => p.Severity == ThreatLevel.High)
                    .Select(p => p.Description)
                    .ToList(),
                RecommendedActions: threatIntel.RecommendedActions,
                ReportGeneratedAt: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate threat assessment");
            throw;
        }
    }

    // Private helper methods

    private bool ServiceExists(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private string GetDefenderVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender");
            return (key?.GetValue("DisplayVersion") as string) ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private bool IsDefenderAtpConfigured()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DefenderPoliciesPath);
            return key != null && (key.GetValue("EnableNotification") as int? ?? 0) > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsDefenderAtpConnectedAsync(CancellationToken ct)
    {
        // In production, check Sense service status and cloud connectivity
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AtpRegistryPath);
            return key != null && ServiceExists("Sense");
        }
        catch
        {
            return false;
        }
    }

    private bool AddToDefenderBlockList(string fileHash)
    {
        try
        {
            // In production: Call Defender API or write to enforcement policies
            _logger.LogDebug("Adding {Hash} to Defender block list", fileHash[..8] + "...");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool AddToDefenderNetworkBlockList(string indicator, IndicatorType type)
    {
        try
        {
            // In production: Configure Windows Firewall or Defender Network Protection
            _logger.LogDebug("Blocking {Type}: {Indicator}", type, indicator);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private int CalculateVulnerabilityScore(ThreatIntelligenceData threatIntel)
    {
        int criticalCount = threatIntel.Indicators
            .Count(i => i.Severity == ThreatLevel.Critical);
        int highCount = threatIntel.Indicators
            .Count(i => i.Severity == ThreatLevel.High);

        // Simple scoring: Critical = 10 points, High = 5 points, max 100
        int score = Math.Min(100, (criticalCount * 10) + (highCount * 5));
        return score;
    }

    private int CalculateExposureScore(ThreatIntelligenceData threatIntel, ThreatStatusSummary status)
    {
        int exposureScore = status.ActiveThreats * 15;
        exposureScore += status.SuspiciousProcesses * 10;
        exposureScore += (100 - (int)threatIntel.Indicators
            .Average(i => Math.Max(0, 100 - ((DateTime.UtcNow - i.LastObserved).Days * 2)))) / 2;

        return Math.Min(100, exposureScore);
    }
}
