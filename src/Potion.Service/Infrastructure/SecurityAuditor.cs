using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

public interface ISecurityAuditor
{
    Task<SecurityAuditResult> PerformSecurityAuditAsync(CancellationToken cancellationToken);
    event EventHandler<SecurityAlert>? SecurityAlert;
}

public sealed record SecurityAuditResult(
    DateTimeOffset Timestamp,
    bool IsSecure,
    IReadOnlyList<SecurityIssue> Issues,
    IReadOnlyList<SecurityAlert> Alerts,
    SecurityAuditScore Score,
    IReadOnlyList<SecurityEvaluation> Evaluations);

public sealed record SecurityIssue(
    string IssueId,
    SecurityIssueSeverity Severity,
    string Category,
    string Title,
    string Description,
    string Recommendation,
    DateTimeOffset Timestamp);

public sealed record SecurityAlert(
    string AlertId,
    SecurityAlertSeverity Severity,
    string Category,
    string Message,
    DateTimeOffset Timestamp,
    Dictionary<string, object> Details);

public enum SecurityIssueSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum SecurityAlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public sealed class SecurityAuditor : BackgroundService, ISecurityAuditor
{
    private readonly ILogger<SecurityAuditor> _logger;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _remediationOptions;
    private readonly IOptionsMonitor<SecurityAuditOptions> _securityOptions;
    private readonly ICommandGuard _commandGuard;
    private readonly ITelemetryIntegrityService _telemetryIntegrityService;
    private readonly TimeSpan _defaultAuditInterval = TimeSpan.FromHours(24);
    private TimeSpan _auditInterval;
    private readonly IDisposable? _securityOptionsChangeRegistration;

    public event EventHandler<SecurityAlert>? SecurityAlert;

    public SecurityAuditor(
        ILogger<SecurityAuditor> logger,
        IOptionsMonitor<RemediationPolicyOptions> remediationOptions,
        IOptionsMonitor<SecurityAuditOptions> securityOptions,
        ICommandGuard commandGuard,
        ITelemetryIntegrityService telemetryIntegrityService)
    {
        _logger = logger;
        _remediationOptions = remediationOptions;
        _securityOptions = securityOptions;
        _commandGuard = commandGuard;
        _telemetryIntegrityService = telemetryIntegrityService;
        _auditInterval = ResolveAuditInterval(_securityOptions.CurrentValue);
        _securityOptionsChangeRegistration = _securityOptions.OnChange(OnSecurityOptionsChanged);
    }

    private void OnSecurityOptionsChanged(SecurityAuditOptions updated, string _)
    {
        _auditInterval = ResolveAuditInterval(updated);
        _logger.LogInformation("Security audit interval updated to {Interval} hours", _auditInterval.TotalHours);
    }

    private TimeSpan ResolveAuditInterval(SecurityAuditOptions options)
    {
        if (options.Enabled)
        {
            var hours = options.AuditIntervalHours;
            if (hours >= 1 && hours <= 168)
            {
                return TimeSpan.FromHours(hours);
            }

            _logger.LogWarning("Invalid security audit interval specified. Falling back to default {DefaultHours} hours.", _defaultAuditInterval.TotalHours);
        }

        return _defaultAuditInterval;
    }

    public override void Dispose()
    {
        _securityOptionsChangeRegistration?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Security auditing started");

        // 初回監査を即座に実行
        await PerformInitialAuditAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_auditInterval, stoppingToken);
                await PerformPeriodicAuditAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during security audit cycle");
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); // エラー時は30分待機
            }
        }

        _logger.LogInformation("Security auditing stopped");
    }

    private async Task PerformInitialAuditAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Performing initial security audit");
        var result = await PerformSecurityAuditAsync(cancellationToken);
        await ProcessAuditResultAsync(result);
    }

    private async Task PerformPeriodicAuditAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Performing periodic security audit");
        var result = await PerformSecurityAuditAsync(cancellationToken);
        await ProcessAuditResultAsync(result);
    }

    public async Task<SecurityAuditResult> PerformSecurityAuditAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = DateTimeOffset.UtcNow;
        var issues = new List<SecurityIssue>();
        var alerts = new List<SecurityAlert>();

        try
        {
            // 設定セキュリティチェック
            var evaluations = new List<SecurityEvaluation>();
            await AuditConfigurationSecurityAsync(issues, alerts, evaluations, cancellationToken);

            // ファイルシステムセキュリティチェック
            await AuditFileSystemSecurityAsync(issues, alerts, evaluations, cancellationToken);

            // コマンドセキュリティチェック
            await AuditCommandSecurityAsync(issues, alerts, evaluations, cancellationToken);

            // アクセス権限チェック
            AuditAccessPermissions(issues, alerts, evaluations);

            // ログセキュリティチェック
            AuditLogSecurity(issues, alerts, evaluations);

            var score = SecurityAuditScore.Calculate(issues, evaluations);
            var isSecure = score.Grade is SecurityAuditGrade.A or SecurityAuditGrade.B;

            _logger.LogInformation("Security audit score: {Grade} (Issues={IssueCount}, Alerts={AlertCount}, Risk={RiskScore:F2})",
                score.Grade,
                issues.Count,
                alerts.Count,
                score.RiskScore);

            if (score.CategoryBreakdown.Count > 0)
            {
                foreach (var category in score.CategoryBreakdown)
                {
                    _logger.LogInformation("Category {Category} score: {Grade} (Risk={Risk:F2}, Issues={IssueCount})",
                        category.Category,
                        category.Grade,
                        category.RiskScore,
                        category.IssueCount);
                }
            }

            return new SecurityAuditResult(timestamp, isSecure, issues, alerts, score, evaluations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Security audit failed");
            alerts.Add(new SecurityAlert(
                Guid.NewGuid().ToString(),
                SecurityAlertSeverity.Error,
                "Audit",
                "Security audit execution failed",
                timestamp,
                new Dictionary<string, object>
                {
                    ["Error"] = ex.Message,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                }));

            var fallbackScore = new SecurityAuditScore(SecurityAuditGrade.F, 100, Array.Empty<SecurityCategoryScore>());
            return new SecurityAuditResult(timestamp, false, issues, alerts, fallbackScore, Array.Empty<SecurityEvaluation>());
        }
    }

    private async Task AuditConfigurationSecurityAsync(List<SecurityIssue> issues, List<SecurityAlert> alerts, List<SecurityEvaluation> evaluations, CancellationToken cancellationToken)
    {
        var options = _remediationOptions.CurrentValue;

        // コマンド許可リストの検証
        cancellationToken.ThrowIfCancellationRequested();
        if (options.CommandAllowlist.Count == 0)
        {
            issues.Add(new SecurityIssue(
                Guid.NewGuid().ToString(),
                SecurityIssueSeverity.Critical,
                "Configuration",
                "Empty Command Allowlist",
                "No commands are allowed to execute, which will prevent all remediation tasks from running.",
                "Add required commands to the CommandAllowlist in appsettings.json",
                DateTimeOffset.UtcNow));
        }

        evaluations.Add(new SecurityEvaluation("Configuration", "CommandAllowlist", options.CommandAllowlist.Count > 0 ? SecurityAuditGrade.C : SecurityAuditGrade.F, options.CommandAllowlist.Count > 0 ? "Allowlist configured" : "Allowlist is empty"));

        // 危険なコマンドのチェック
        var dangerousCommands = new[] { "cmd.exe", "regedit.exe", "net.exe", "taskkill.exe" };
        foreach (var command in options.CommandAllowlist)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dangerousCommands.Contains(command.ToLower()))
            {
                issues.Add(new SecurityIssue(
                    Guid.NewGuid().ToString(),
                    SecurityIssueSeverity.High,
                    "Configuration",
                    "Potentially Dangerous Command Allowed",
                    $"Command '{command}' is in the allowlist but may pose security risks.",
                    "Review and remove dangerous commands from the allowlist if not absolutely necessary",
                    DateTimeOffset.UtcNow));
            }
        }

        // タスク設定の検証
        foreach (var task in options.Tasks.Where(t => t.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(task.Command))
            {
                issues.Add(new SecurityIssue(
                    Guid.NewGuid().ToString(),
                    SecurityIssueSeverity.High,
                    "Configuration",
                    "Empty Command in Task",
                    $"Task '{task.Name}' has an empty command.",
                    "Specify a valid command for the task or disable it",
                    DateTimeOffset.UtcNow));
            }

            if (task.TimeoutSeconds > 7200) // 2時間
            {
                issues.Add(new SecurityIssue(
                    Guid.NewGuid().ToString(),
                    SecurityIssueSeverity.Medium,
                    "Configuration",
                    "Long Task Timeout",
                    $"Task '{task.Name}' has a very long timeout ({task.TimeoutSeconds}s).",
                    "Consider reducing the timeout to prevent resource exhaustion",
                    DateTimeOffset.UtcNow));
            }
        }

        evaluations.Add(new SecurityEvaluation("Configuration", "TaskValidation", issues.Any(i => i.Category == "Configuration" && i.Severity >= SecurityIssueSeverity.High) ? SecurityAuditGrade.D : SecurityAuditGrade.B, "Validated remediation task definitions"));
    }

    private async Task AuditFileSystemSecurityAsync(List<SecurityIssue> issues, List<SecurityAlert> alerts, List<SecurityEvaluation> evaluations, CancellationToken cancellationToken)
    {
        var servicePaths = new[]
        {
            ServicePaths.Base,
            ServicePaths.Logs,
            ServicePaths.State,
            ServicePaths.Telemetry
        };

        foreach (var path in servicePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var directory = new DirectoryInfo(path);
                if (!directory.Exists)
                {
                    issues.Add(new SecurityIssue(
                        Guid.NewGuid().ToString(),
                        SecurityIssueSeverity.Medium,
                        "FileSystem",
                        "Missing Service Directory",
                        $"Service directory '{path}' does not exist.",
                        "Ensure the directory exists and is writable by the service account",
                        DateTimeOffset.UtcNow));
                    continue;
                }

                // ディレクトリアクセス権限チェック
                await AuditDirectoryPermissionsAsync(path, issues, cancellationToken);
            }
            catch (Exception ex)
            {
                issues.Add(new SecurityIssue(
                    Guid.NewGuid().ToString(),
                    SecurityIssueSeverity.High,
                    "FileSystem",
                    "Directory Access Error",
                    $"Cannot access service directory '{path}': {ex.Message}",
                    "Check directory permissions and ensure the service account has access",
                    DateTimeOffset.UtcNow));
            }
        }

        evaluations.Add(new SecurityEvaluation("FileSystem", "DirectoryPermissions", issues.Any(i => i.Category == "FileSystem" && i.Severity >= SecurityIssueSeverity.High) ? SecurityAuditGrade.D : SecurityAuditGrade.B, "Validated service directories"));
    }

    private async Task AuditCommandSecurityAsync(List<SecurityIssue> issues, List<SecurityAlert> alerts, List<SecurityEvaluation> evaluations, CancellationToken cancellationToken)
    {
        var options = _remediationOptions.CurrentValue;

        foreach (var task in options.Tasks.Where(t => t.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _commandGuard.EnsureCommandIsAllowed(task.Command);
            }
            catch (Exception ex)
            {
                issues.Add(new SecurityIssue(
                    Guid.NewGuid().ToString(),
                    SecurityIssueSeverity.Critical,
                    "Command",
                    "Unauthorized Command",
                    $"Task '{task.Name}' references unauthorized command '{task.Command}': {ex.Message}",
                    "Remove the task or add the command to the allowlist",
                    DateTimeOffset.UtcNow));
            }
        }

        evaluations.Add(new SecurityEvaluation("Command", "SignatureValidation", issues.Any(i => i.Category == "Command" && i.Severity >= SecurityIssueSeverity.High) ? SecurityAuditGrade.D : SecurityAuditGrade.B, "Validated command authenticity"));
    }

    private void AuditAccessPermissions(List<SecurityIssue> issues, List<SecurityAlert> alerts, List<SecurityEvaluation> evaluations)
    {
        try
        {
            // 現在のプロセスが管理者権限で実行されているかチェック
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);

            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                issues.Add(new SecurityIssue(
                    Guid.NewGuid().ToString(),
                    SecurityIssueSeverity.Critical,
                    "Permissions",
                    "Insufficient Privileges",
                    "The service is not running with administrator privileges.",
                    "Ensure the service runs as administrator or SYSTEM account",
                    DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to audit access permissions");
        }

        var privilegeScore = issues.Any(i => i.Category == "Permissions" && i.Severity >= SecurityIssueSeverity.High) ? SecurityAuditGrade.F : SecurityAuditGrade.A;
        evaluations.Add(new SecurityEvaluation("Permissions", "PrivilegeLevel", privilegeScore, $"Validated service privilege: {privilegeScore}"));

        var logSecurityScore = issues.Any(i => i.Category == "Logging" && i.Severity >= SecurityIssueSeverity.High) ? SecurityAuditGrade.F : SecurityAuditGrade.A;
        evaluations.Add(new SecurityEvaluation("Logging", "LogSecurity", logSecurityScore, $"Validated log security: {logSecurityScore}"));
    }
    private void AuditLogSecurity(List<SecurityIssue> issues, List<SecurityAlert> alerts, List<SecurityEvaluation> evaluations)
    {
        try
        {
            var logPath = Path.Combine(ServicePaths.Logs, "potion-security.log");
            if (File.Exists(logPath))
            {
                var fileInfo = new FileInfo(logPath);

                // ログファイルが大きすぎる場合
                if (fileInfo.Length > 100 * 1024 * 1024) // 100MB
                {
                    issues.Add(new SecurityIssue(
                        Guid.NewGuid().ToString(),
                        SecurityIssueSeverity.Medium,
                        "Logging",
                        "Large Log File",
                        $"Log file is very large ({fileInfo.Length / (1024.0 * 1024):F1} MB).",
                        "Consider log rotation or archival strategy",
                        DateTimeOffset.UtcNow));
                }

                // ログファイルのアクセス権限チェック
                var accessControl = fileInfo.GetAccessControl();
                var rules = accessControl.GetAccessRules(true, true, typeof(SecurityIdentifier));

                var hasInsecurePermissions = false;
                foreach (FileSystemAccessRule rule in rules)
                {
                    if (rule.AccessControlType == AccessControlType.Allow &&
                        (rule.FileSystemRights & FileSystemRights.WriteData) != 0)
                    {
                        if (rule.IdentityReference is SecurityIdentifier sid &&
                            !IsPrivilegedSid(sid))
                        {
                            hasInsecurePermissions = true;
                            break;
                        }
                    }
                }

                if (hasInsecurePermissions)
                {
                    issues.Add(new SecurityIssue(
                        Guid.NewGuid().ToString(),
                        SecurityIssueSeverity.High,
                        "Logging",
                        "Insecure Log Permissions",
                        "Log files have insecure permissions that may allow unauthorized access.",
                        "Restrict log file permissions to service account and administrators only",
                        DateTimeOffset.UtcNow));
                }
            }

            var securityOptions = _securityOptions.CurrentValue;
            if (securityOptions.Enabled && securityOptions.MaxLogRetentionDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-securityOptions.MaxLogRetentionDays);
                try
                {
                    var logFiles = Directory.GetFiles(ServicePaths.Logs, "potion-*.log");
                    var expiredLogs = logFiles
                        .Select(path => new FileInfo(path))
                        .Where(info => info.LastWriteTimeUtc < cutoff)
                        .ToList();

                    if (expiredLogs.Count > 0)
                    {
                        issues.Add(new SecurityIssue(
                            Guid.NewGuid().ToString(),
                            SecurityIssueSeverity.Medium,
                            "Logging",
                            "Expired Log Files Retained",
                            $"Found {expiredLogs.Count} log files older than configured retention ({securityOptions.MaxLogRetentionDays} days).",
                            "Remove outdated log files or adjust MaxLogRetentionDays to match storage policies.",
                            DateTimeOffset.UtcNow));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to evaluate log retention policy");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to audit log security");
        }

        evaluations.Add(new SecurityEvaluation("Logging", "LogRetention", issues.Any(i => i.Category == "Logging" && i.Severity >= SecurityIssueSeverity.High) ? SecurityAuditGrade.D : SecurityAuditGrade.B, "Validated log storage"));
    }

    private async Task AuditDirectoryPermissionsAsync(string directoryPath, List<SecurityIssue> issues, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var directory = new DirectoryInfo(directoryPath);
            var accessControl = directory.GetAccessControl();
            var rules = accessControl.GetAccessRules(true, true, typeof(SecurityIdentifier));

            var hasInsecurePermissions = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.CreateFiles)) != 0)
                {
                    if (rule.IdentityReference is SecurityIdentifier sid &&
                        !IsPrivilegedSid(sid))
                    {
                        hasInsecurePermissions = true;
                        break;
                    }
                }
            }

            if (hasInsecurePermissions)
            {
                issues.Add(new SecurityIssue(
                    Guid.NewGuid().ToString(),
                    SecurityIssueSeverity.High,
                    "FileSystem",
                    "Insecure Directory Permissions",
                    $"Directory '{directoryPath}' has insecure permissions.",
                    "Restrict directory permissions to service account and administrators only",
                    DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to audit directory permissions for {DirectoryPath}", directoryPath);
        }
    }

    private static bool IsPrivilegedSid(SecurityIdentifier sid)
    {
        return sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
               sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
               sid.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
               sid.IsWellKnown(WellKnownSidType.NetworkServiceSid);
    }

    private async Task ProcessAuditResultAsync(SecurityAuditResult result)
    {
        if (!result.IsSecure)
        {
            _logger.LogWarning("Security audit found {IssueCount} issues", result.Issues.Count);
        }

        foreach (var issue in result.Issues)
        {
            var logLevel = issue.Severity switch
            {
                SecurityIssueSeverity.Low => LogLevel.Information,
                SecurityIssueSeverity.Medium => LogLevel.Warning,
                SecurityIssueSeverity.High => LogLevel.Error,
                SecurityIssueSeverity.Critical => LogLevel.Critical,
                _ => LogLevel.Warning
            };

            _logger.Log(logLevel, "Security issue [{Category}]: {Title} - {Description}", issue.Category, issue.Title, issue.Description);
        }

        foreach (var alert in result.Alerts)
        {
            var logLevel = alert.Severity switch
            {
                SecurityAlertSeverity.Info => LogLevel.Information,
                SecurityAlertSeverity.Warning => LogLevel.Warning,
                SecurityAlertSeverity.Error => LogLevel.Error,
                SecurityAlertSeverity.Critical => LogLevel.Critical,
                _ => LogLevel.Warning
            };

            _logger.Log(logLevel, "Security alert [{Category}]: {Message}", alert.Category, alert.Message);

            // イベントを発行
            SecurityAlert?.Invoke(this, alert);
        }

        await PersistAuditReportAsync(result);
    }

    private static readonly JsonSerializerOptions EvaluationSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private async Task PersistAuditReportAsync(SecurityAuditResult result)
    {
        try
        {
            var latestReportPath = ServicePaths.GetSecurityAuditReportPath();
            Directory.CreateDirectory(Path.GetDirectoryName(latestReportPath)!);

            var timestampedName = $"audit_{result.Timestamp:yyyyMMddTHHmmssZ}.json";
            var sanitizedName = string.Concat(timestampedName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                sanitizedName = $"audit_{Guid.NewGuid():N}.json";
            }

            var historicalReportPath = Path.Combine(ServicePaths.Security, sanitizedName);

            await using (var historicalStream = File.Create(historicalReportPath))
            {
                await JsonSerializer.SerializeAsync(historicalStream, result, JsonSerializerOptions, CancellationToken.None);
                await historicalStream.FlushAsync(CancellationToken.None);
            }

            await using (var latestStream = File.Create(latestReportPath))
            {
                await JsonSerializer.SerializeAsync(latestStream, result, JsonSerializerOptions, CancellationToken.None);
                await latestStream.FlushAsync(CancellationToken.None);
            }

            await _telemetryIntegrityService.WriteDigestAsync(historicalReportPath, CancellationToken.None);
            await _telemetryIntegrityService.WriteDigestAsync(latestReportPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist security audit report");
        }
    }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public sealed record SecurityAuditScore(SecurityAuditGrade Grade, double RiskScore, IReadOnlyList<SecurityCategoryScore> CategoryBreakdown)
    {
        public static SecurityAuditScore Calculate(IReadOnlyList<SecurityIssue> issues, IReadOnlyList<SecurityEvaluation> evaluations)
        {
            if (issues.Count == 0)
            {
                var baseline = new SecurityCategoryScore("Baseline", SecurityAuditGrade.A, 0, 0);
                return new SecurityAuditScore(SecurityAuditGrade.A, 0, new[] { baseline });
            }

            var scoreByCategory = issues
                .GroupBy(i => i.Category)
                .Select(group => SecurityCategoryScore.FromIssues(group.Key, group.ToList()))
                .ToList();

            foreach (var evaluation in evaluations)
            {
                var existing = scoreByCategory.FirstOrDefault(s => s.Category.Equals(evaluation.Category, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    scoreByCategory.Add(SecurityCategoryScore.FromEvaluation(evaluation));
                }
            }

            var overallRisk = scoreByCategory.Sum(s => s.RiskScore);
            var grade = overallRisk switch
            {
                <= 10 => SecurityAuditGrade.A,
                <= 25 => SecurityAuditGrade.B,
                <= 50 => SecurityAuditGrade.C,
                <= 75 => SecurityAuditGrade.D,
                _ => SecurityAuditGrade.F
            };

            return new SecurityAuditScore(grade, overallRisk, scoreByCategory);
        }
    }

    public sealed record SecurityCategoryScore(string Category, SecurityAuditGrade Grade, double RiskScore, int IssueCount)
    {
        public static SecurityCategoryScore FromIssues(string category, IReadOnlyList<SecurityIssue> issues)
        {
            var risk = issues.Sum(i => i.Severity switch
            {
                SecurityIssueSeverity.Low => 1,
                SecurityIssueSeverity.Medium => 5,
                SecurityIssueSeverity.High => 15,
                SecurityIssueSeverity.Critical => 30,
                _ => 0
            });

            var grade = risk switch
            {
                <= 5 => SecurityAuditGrade.A,
                <= 15 => SecurityAuditGrade.B,
                <= 25 => SecurityAuditGrade.C,
                <= 40 => SecurityAuditGrade.D,
                _ => SecurityAuditGrade.F
            };

            return new SecurityCategoryScore(category, grade, risk, issues.Count);
        }

        public static SecurityCategoryScore FromEvaluation(SecurityEvaluation evaluation)
        {
            var risk = evaluation.Grade switch
            {
                SecurityAuditGrade.A => 0,
                SecurityAuditGrade.B => 5,
                SecurityAuditGrade.C => 10,
                SecurityAuditGrade.D => 20,
                SecurityAuditGrade.F => 30,
                _ => 0
            };

            return new SecurityCategoryScore(evaluation.Category, evaluation.Grade, risk, 0);
        }
    }

    public sealed record SecurityEvaluation(string Category, string ControlId, SecurityAuditGrade Grade, string Notes);

    public enum SecurityAuditGrade
    {
        A,
        B,
        C,
        D,
        F
    }
}
