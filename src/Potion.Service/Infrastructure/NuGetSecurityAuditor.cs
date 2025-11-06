using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Potion.Service.Infrastructure;

/// <summary>
/// NuGetパッケージのセキュリティ監査サービス
/// 依存関係のセキュリティ脆弱性を自動的にチェック
/// </summary>
public interface INuGetSecurityAuditor
{
    Task<SecurityAuditReport> AuditDependenciesAsync();
    Task<IEnumerable<PackageVulnerability>> CheckPackageVulnerabilitiesAsync(string packageId, string version);
    Task<SecurityUpdateRecommendation> GetUpdateRecommendationsAsync();
}

/// <summary>
/// セキュリティ監査レポート
/// </summary>
public class SecurityAuditReport
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public int TotalPackages { get; set; }
    public int VulnerablePackages { get; set; }
    public int HighSeverityVulnerabilities { get; set; }
    public int MediumSeverityVulnerabilities { get; set; }
    public int LowSeverityVulnerabilities { get; set; }
    public List<PackageVulnerability> Vulnerabilities { get; set; } = new();
    public bool IsSecure { get; set; }
    public string OverallRisk { get; set; } = string.Empty;
}

/// <summary>
/// パッケージ脆弱性情報
/// </summary>
public class PackageVulnerability
{
    public string PackageId { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string VulnerabilityId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public VulnerabilitySeverity Severity { get; set; }
    public string CveId { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
    public string FixedInVersion { get; set; } = string.Empty;
    public List<string> AffectedVersions { get; set; } = new();
}

/// <summary>
/// 脆弱性の重大度
/// </summary>
public enum VulnerabilitySeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// セキュリティ更新推奨
/// </summary>
public class SecurityUpdateRecommendation
{
    public List<PackageUpdateRecommendation> PackageUpdates { get; set; } = new();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public int TotalUpdatesAvailable { get; set; }
}

/// <summary>
/// パッケージ更新推奨
/// </summary>
public class PackageUpdateRecommendation
{
    public string PackageId { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string RecommendedVersion { get; set; } = string.Empty;
    public VulnerabilitySeverity MaxSeverity { get; set; }
    public int VulnerabilityCount { get; set; }
}

/// <summary>
/// NuGetセキュリティ監査サービス実装
/// </summary>
public class NuGetSecurityAuditor : INuGetSecurityAuditor
{
    private readonly ILogger<NuGetSecurityAuditor> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _nuGetApiBaseUrl = "https://api.nuget.org/v3";
    private readonly string _vulnerabilityApiUrl = "https://api.nuget.org/v3/vulnerabilities";

    public NuGetSecurityAuditor(ILogger<NuGetSecurityAuditor> logger, HttpClient httpClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Potion-Security-Auditor/1.0");
    }

    public async Task<SecurityAuditReport> AuditDependenciesAsync()
    {
        _logger.LogInformation("Starting NuGet dependency security audit");

        var report = new SecurityAuditReport();

        try
        {
            // プロジェクトファイルから依存関係を抽出
            var dependencies = await ExtractProjectDependenciesAsync();

            report.TotalPackages = dependencies.Count;

            // 各パッケージの脆弱性をチェック
            foreach (var dependency in dependencies)
            {
                var vulnerabilities = await CheckPackageVulnerabilitiesAsync(dependency.PackageId, dependency.Version);

                foreach (var vulnerability in vulnerabilities)
                {
                    report.Vulnerabilities.Add(vulnerability);

                    switch (vulnerability.Severity)
                    {
                        case VulnerabilitySeverity.Critical:
                        case VulnerabilitySeverity.High:
                            report.HighSeverityVulnerabilities++;
                            break;
                        case VulnerabilitySeverity.Medium:
                            report.MediumSeverityVulnerabilities++;
                            break;
                        case VulnerabilitySeverity.Low:
                            report.LowSeverityVulnerabilities++;
                            break;
                    }
                }
            }

            report.VulnerablePackages = report.Vulnerabilities
                .GroupBy(v => v.PackageId)
                .Count();

            report.IsSecure = report.HighSeverityVulnerabilities == 0 && report.CriticalVulnerabilities == 0;

            // 全体的なリスク評価
            if (report.HighSeverityVulnerabilities > 0 || report.CriticalVulnerabilities > 0)
            {
                report.OverallRisk = "High";
            }
            else if (report.MediumSeverityVulnerabilities > 5)
            {
                report.OverallRisk = "Medium";
            }
            else if (report.LowSeverityVulnerabilities > 0)
            {
                report.OverallRisk = "Low";
            }
            else
            {
                report.OverallRisk = "None";
            }

            _logger.LogInformation("Security audit completed. Found {VulnerabilityCount} vulnerabilities in {PackageCount} packages",
                report.Vulnerabilities.Count, report.TotalPackages);

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during security audit");
            report.IsSecure = false;
            report.OverallRisk = "Error";
            return report;
        }
    }

    public async Task<IEnumerable<PackageVulnerability>> CheckPackageVulnerabilitiesAsync(string packageId, string version)
    {
        var vulnerabilities = new List<PackageVulnerability>();

        try
        {
            // NuGet脆弱性APIから情報を取得
            var url = $"{_vulnerabilityApiUrl}/{packageId.ToLowerInvariant()}.json";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("No vulnerability data found for package {PackageId}", packageId);
                return vulnerabilities;
            }

            var jsonContent = await response.Content.ReadAsStringAsync();
            var vulnerabilityData = JsonSerializer.Deserialize<VulnerabilityApiResponse>(jsonContent);

            if (vulnerabilityData?.Vulnerabilities == null)
            {
                return vulnerabilities;
            }

            var currentVersion = NuGetVersion.Parse(version);

            foreach (var vulnerability in vulnerabilityData.Vulnerabilities)
            {
                // 現在のバージョンが影響を受けるかチェック
                if (IsVersionAffected(currentVersion, vulnerability.Versions))
                {
                    vulnerabilities.Add(new PackageVulnerability
                    {
                        PackageId = packageId,
                        CurrentVersion = version,
                        VulnerabilityId = vulnerability.Id,
                        Title = vulnerability.Title,
                        Description = vulnerability.Description,
                        Severity = MapSeverity(vulnerability.Severity),
                        CveId = vulnerability.CveId,
                        PublishedDate = vulnerability.PublishedDate,
                        FixedInVersion = GetFixedInVersion(vulnerability.Versions),
                        AffectedVersions = vulnerability.Versions.Where(v => v.IsAffected).Select(v => v.VersionRange).ToList()
                    });
                }
            }

            return vulnerabilities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking vulnerabilities for package {PackageId}", packageId);
            return vulnerabilities;
        }
    }

    public async Task<SecurityUpdateRecommendation> GetUpdateRecommendationsAsync()
    {
        _logger.LogInformation("Generating security update recommendations");

        var recommendation = new SecurityUpdateRecommendation();

        try
        {
            var auditReport = await AuditDependenciesAsync();

            var packageUpdates = new List<PackageUpdateRecommendation>();

            foreach (var vulnerabilityGroup in auditReport.Vulnerabilities.GroupBy(v => v.PackageId))
            {
                var packageId = vulnerabilityGroup.Key;
                var maxSeverity = vulnerabilityGroup.Max(v => v.Severity);

                // 最新バージョンの取得（簡易実装）
                var latestVersion = await GetLatestPackageVersionAsync(packageId);

                if (!string.IsNullOrEmpty(latestVersion))
                {
                    packageUpdates.Add(new PackageUpdateRecommendation
                    {
                        PackageId = packageId,
                        CurrentVersion = vulnerabilityGroup.First().CurrentVersion,
                        RecommendedVersion = latestVersion,
                        MaxSeverity = maxSeverity,
                        VulnerabilityCount = vulnerabilityGroup.Count()
                    });
                }
            }

            recommendation.PackageUpdates = packageUpdates;
            recommendation.TotalUpdatesAvailable = packageUpdates.Count;

            _logger.LogInformation("Generated {UpdateCount} update recommendations", packageUpdates.Count);

            return recommendation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating update recommendations");
            return recommendation;
        }
    }

    private async Task<List<ProjectDependency>> ExtractProjectDependenciesAsync()
    {
        var dependencies = new List<ProjectDependency>();

        // プロジェクトファイルの解析（実際の実装ではcsprojファイルを解析）
        // ここでは簡易的な実装としてハードコードされた依存関係を使用

        var projectFiles = new[]
        {
            "src/Potion.Service/Potion.Service.csproj"
        };

        foreach (var projectFile in projectFiles)
        {
            try
            {
                // 実際の実装ではXMLパーサーを使用してcsprojファイルを解析
                // ここでは例として一般的な依存関係を追加
                dependencies.AddRange(new[]
                {
                    new ProjectDependency { PackageId = "Microsoft.AspNetCore.OpenApi", Version = "8.0.0" },
                    new ProjectDependency { PackageId = "Serilog.Extensions.Hosting", Version = "8.0.0" },
                    new ProjectDependency { PackageId = "System.Reactive", Version = "6.0.1" },
                    new ProjectDependency { PackageId = "Moq", Version = "4.20.69" },
                    new ProjectDependency { PackageId = "FluentAssertions", Version = "6.12.0" }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting dependencies from {ProjectFile}", projectFile);
            }
        }

        return dependencies;
    }

    private bool IsVersionAffected(NuGetVersion currentVersion, List<VersionRangeInfo> versionRanges)
    {
        return versionRanges.Any(range =>
            range.IsAffected &&
            NuGetVersionRange.Parse(range.VersionRange).Satisfies(currentVersion));
    }

    private string GetFixedInVersion(List<VersionRangeInfo> versionRanges)
    {
        var fixedVersion = versionRanges
            .Where(v => !v.IsAffected)
            .OrderByDescending(v => v.VersionRange)
            .FirstOrDefault();

        return fixedVersion?.VersionRange ?? string.Empty;
    }

    private VulnerabilitySeverity MapSeverity(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "critical" => VulnerabilitySeverity.Critical,
            "high" => VulnerabilitySeverity.High,
            "moderate" => VulnerabilitySeverity.Medium,
            "low" => VulnerabilitySeverity.Low,
            _ => VulnerabilitySeverity.Medium
        };
    }

    private async Task<string> GetLatestPackageVersionAsync(string packageId)
    {
        try
        {
            var url = $"{_nuGetApiBaseUrl}/registration-semver1/{packageId.ToLowerInvariant()}/index.json";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var jsonContent = await response.Content.ReadAsStringAsync();
            var registrationData = JsonSerializer.Deserialize<PackageRegistrationResponse>(jsonContent);

            return registrationData?.Items?
                .OrderByDescending(item => item.Upper)
                .FirstOrDefault()?
                .Items?
                .OrderByDescending(item => item.CatalogEntry.Version)
                .FirstOrDefault()?
                .CatalogEntry?
                .Version ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting latest version for package {PackageId}", packageId);
            return string.Empty;
        }
    }

    // API応答用のクラス
    private class VulnerabilityApiResponse
    {
        public List<VulnerabilityInfo> Vulnerabilities { get; set; } = new();
    }

    private class VulnerabilityInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string CveId { get; set; } = string.Empty;
        public DateTime PublishedDate { get; set; }
        public List<VersionRangeInfo> Versions { get; set; } = new();
    }

    private class VersionRangeInfo
    {
        public string VersionRange { get; set; } = string.Empty;
        public bool IsAffected { get; set; }
    }

    private class ProjectDependency
    {
        public string PackageId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }

    private class PackageRegistrationResponse
    {
        public List<RegistrationPage> Items { get; set; } = new();
    }

    private class RegistrationPage
    {
        public string Upper { get; set; } = string.Empty;
        public List<RegistrationLeaf> Items { get; set; } = new();
    }

    private class RegistrationLeaf
    {
        public PackageCatalogEntry CatalogEntry { get; set; } = new();
    }

    private class PackageCatalogEntry
    {
        public string Version { get; set; } = string.Empty;
    }
}
