using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// システム状態レポート生成サービス
/// </summary>
public interface ISystemReportService
{
    /// <summary>
    /// システム状態レポートを生成します
    /// </summary>
    Task<SystemReport> GenerateReportAsync(CancellationToken cancellationToken);

    /// <summary>
    /// レポートをファイルに保存します
    /// </summary>
    Task<string> SaveReportAsync(SystemReport report, CancellationToken cancellationToken);

    /// <summary>
    /// 古いレポートファイルをクリーンアップします
    /// </summary>
    Task<int> CleanupOldReportsAsync(CancellationToken cancellationToken);
}

public sealed record SystemReport(
    string ReportId,
    DateTimeOffset GeneratedAt,
    SystemHealthSnapshot HealthSnapshot,
    LogErrorStatistics ErrorStatistics,
    LogPerformanceStatistics PerformanceStatistics,
    SecurityAuditResult SecurityAudit,
    IReadOnlyList<BackupFileInfo> RecentBackups,
    ReportMetadata Metadata);

public sealed record ReportMetadata(
    string Version,
    string MachineName,
    TimeSpan GenerationDuration,
    int TotalIssuesFound);

public sealed class SystemReportService : ISystemReportService
{
    private readonly ILogger<SystemReportService> _logger;
    private readonly ISystemHealthMonitor _healthMonitor;
    private readonly ILogAnalysisService _logAnalysisService;
    private readonly ISecurityAuditor _securityAuditor;
    private readonly IBackupService _backupService;
    private readonly IOptionsMonitor<ReportOptions> _optionsMonitor;

    public SystemReportService(
        ILogger<SystemReportService> logger,
        ISystemHealthMonitor healthMonitor,
        ILogAnalysisService logAnalysisService,
        ISecurityAuditor securityAuditor,
        IBackupService backupService,
        IOptionsMonitor<ReportOptions> optionsMonitor)
    {
        _logger = logger;
        _healthMonitor = healthMonitor;
        _logAnalysisService = logAnalysisService;
        _securityAuditor = securityAuditor;
        _backupService = backupService;
        _optionsMonitor = optionsMonitor;
    }

    public async Task<SystemReport> GenerateReportAsync(CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var reportId = Guid.NewGuid().ToString("N");

        _logger.LogInformation("システムレポートを生成します: {ReportId}", reportId);

        try
        {
            // 並行して各種データを収集
            var healthTask = _healthMonitor.GetCurrentHealthAsync(cancellationToken);
            var errorStatsTask = _logAnalysisService.AnalyzeErrorStatisticsAsync(cancellationToken);
            var perfStatsTask = _logAnalysisService.AnalyzePerformanceStatisticsAsync(cancellationToken);
            var securityTask = _securityAuditor.PerformSecurityAuditAsync(cancellationToken);
            var backupsTask = _backupService.GetBackupFilesAsync(cancellationToken);

            await Task.WhenAll(healthTask, errorStatsTask, perfStatsTask, securityTask, backupsTask);

            var healthSnapshot = await healthTask;
            var errorStatistics = await errorStatsTask;
            var performanceStatistics = await perfStatsTask;
            var securityAudit = await securityTask;
            var recentBackups = await backupsTask;

            var endTime = DateTimeOffset.UtcNow;
            var duration = endTime - startTime;

            var totalIssues = healthSnapshot.Alerts.Count +
                             (securityAudit.IsSecure ? 0 : securityAudit.Issues.Count) +
                             errorStatistics.CriticalErrorCount;

            var metadata = new ReportMetadata(
                "1.0",
                Environment.MachineName,
                duration,
                totalIssues);

            var report = new SystemReport(
                reportId,
                endTime,
                healthSnapshot,
                errorStatistics,
                performanceStatistics,
                securityAudit,
                recentBackups.Take(10).ToList(),
                metadata);

            _logger.LogInformation("システムレポートの生成が完了しました: {ReportId}, 期間: {Duration}", reportId, duration);

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "システムレポートの生成に失敗しました: {ReportId}", reportId);

            // エラー時は基本的なレポートを返す
            var fallbackReport = new SystemReport(
                reportId,
                DateTimeOffset.UtcNow,
                new SystemHealthSnapshot(new SystemMetrics(
                    new CpuMetrics(0, 0, 0, 0, 0),
                    new MemoryMetrics(0, 0, 0, 0, 0, 0),
                    new DiskMetrics(0, 0, 0, 0, 0, 0),
                    new NetworkMetrics(0, 0, 0),
                    new WindowsEventMetrics(0, 0, 0, 0, DateTimeOffset.UtcNow),
                    new ServiceMetrics(0, 0, 0, 0, Array.Empty<string>()),
                    new SecurityMetrics(false, false, 0, false, DateTimeOffset.UtcNow),
                    new SystemIntegrityMetrics(false, 0, 0, false, DateTimeOffset.UtcNow),
                    new InventoryMetrics(Environment.MachineName, "", "", "", ""),
                    new SecurityContextMetrics("", false, false, false),
                    new PerformanceMetrics(0, 0, 0, 0, 0),
                    new ResourceMonitoringMetrics(0, 0, 0),
                    new ResourcePressureMetrics(PressureLevel.None, PressureLevel.None, PressureLevel.None, PressureLevel.None),
                    new EventCorrelationMetrics(0, 0),
                    new CompatibilityMetrics("", false)),
                    Array.Empty<SystemHealthAlert>()),
                new LogErrorStatistics(0, 0, 0, Array.Empty<string>(), startTime, DateTimeOffset.UtcNow),
                new LogPerformanceStatistics(0, 0, 0, 0, Array.Empty<PerformanceMetric>()),
                new SecurityAuditResult(false, Array.Empty<SecurityIssue>(), Array.Empty<SecurityAlert>()),
                Array.Empty<BackupFileInfo>(),
                new ReportMetadata("1.0", Environment.MachineName, TimeSpan.Zero, 1));

            return fallbackReport;
        }
    }

    public async Task<string> SaveReportAsync(SystemReport report, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var reportDir = Path.Combine(ServicePaths.Reports);
        Directory.CreateDirectory(reportDir);

        var fileName = $"system_report_{report.ReportId}_{report.GeneratedAt:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(reportDir, fileName);

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(report, jsonOptions);

        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        _logger.LogInformation("システムレポートを保存しました: {FilePath}", filePath);

        return filePath;
    }

    public async Task<int> CleanupOldReportsAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var reportDir = Path.Combine(ServicePaths.Reports);
        var cutoffTime = DateTimeOffset.UtcNow.AddDays(-options.RetentionDays);

        if (!Directory.Exists(reportDir))
            return 0;

        var reportFiles = Directory.GetFiles(reportDir, "system_report_*.json")
            .Select(file => new FileInfo(file))
            .Where(file => file.LastWriteTimeUtc < cutoffTime)
            .ToArray();

        var deletedCount = 0;
        foreach (var file in reportFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                file.Delete();
                deletedCount++;
                _logger.LogDebug("古いレポートファイルを削除しました: {FileName}", file.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "レポートファイルの削除に失敗しました: {FileName}", file.Name);
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation("レポートファイルのクリーンアップが完了しました: {DeletedCount}個削除", deletedCount);
        }

        return deletedCount;
    }
}
