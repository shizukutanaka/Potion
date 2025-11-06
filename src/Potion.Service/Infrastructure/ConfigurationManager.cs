using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

public interface IConfigurationManager
{
    Task<ConfigurationUpdateResult> UpdateConfigurationAsync(string configJson, CancellationToken cancellationToken);
    Task<ConfigurationBackup> CreateBackupAsync();
    Task<ConfigurationRestoreResult> RestoreFromBackupAsync(ConfigurationBackup backup, CancellationToken cancellationToken);
    event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;
}

public sealed record ConfigurationUpdateResult(
    bool Success,
    string? ErrorMessage,
    ConfigurationBackup? Backup);

public sealed record ConfigurationBackup(
    string BackupId,
    DateTimeOffset Timestamp,
    string ConfigurationJson,
    string EnvironmentName);

public sealed record ConfigurationRestoreResult(
    bool Success,
    string? ErrorMessage);

public sealed class ConfigurationChangedEventArgs : EventArgs
{
    public string OldConfigurationHash { get; }
    public string NewConfigurationHash { get; }
    public DateTimeOffset ChangedAt { get; }

    public ConfigurationChangedEventArgs(string oldHash, string newHash, DateTimeOffset changedAt)
    {
        OldConfigurationHash = oldHash;
        NewConfigurationHash = newHash;
        ChangedAt = changedAt;
    }
}

public sealed class ConfigurationManager : IConfigurationManager
{
    private readonly ILogger<ConfigurationManager> _logger;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _remediationOptions;
    private readonly IOptionsMonitor<TelemetryRetentionOptions> _telemetryOptions;
    private readonly string _configPath;
    private readonly string _backupDirectory;

    public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    private string _currentConfigHash;

    private static readonly SecurityIdentifier[] PrivilegedSecurityIdentifiers =
    {
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
        new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null)
    };

    public ConfigurationManager(
        ILogger<ConfigurationManager> logger,
        IOptionsMonitor<RemediationPolicyOptions> remediationOptions,
        IOptionsMonitor<TelemetryRetentionOptions> telemetryOptions)
    {
        _logger = logger;
        _remediationOptions = remediationOptions;
        _telemetryOptions = telemetryOptions;

        _configPath = ServicePaths.ConfigurationFile;
        _backupDirectory = ServicePaths.ConfigBackups;

        Directory.CreateDirectory(_backupDirectory);
        _currentConfigHash = CalculateConfigHash();

        HardenConfigStorage();
        _ = CleanupOldBackupsAsync();
    }

    public async Task<ConfigurationUpdateResult> UpdateConfigurationAsync(string configJson, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Attempting to update configuration");

            // 現在の設定をバックアップ
            var backup = await CreateBackupAsync();

            // 新しい設定を検証
            var validationResult = await ValidateConfigurationAsync(configJson, cancellationToken);
            if (!validationResult.IsValid)
            {
                _logger.LogError("Configuration validation failed: {Errors}", string.Join(", ", validationResult.Errors));
                return new ConfigurationUpdateResult(false, $"Validation failed: {string.Join(", ", validationResult.Errors)}", backup);
            }

            // 新しい設定をファイルに書き込み
            var tempConfigPath = _configPath + ".tmp";
            await File.WriteAllTextAsync(tempConfigPath, configJson, cancellationToken);

            // 設定ファイルの権限を確認
            if (!ValidateConfigFilePermissions(tempConfigPath))
            {
                TryDeleteFile(tempConfigPath);
                return new ConfigurationUpdateResult(false, "Invalid file permissions for configuration file", backup);
            }

            // 既存の設定ファイルを置き換え
            var oldConfigPath = _configPath + ".old";
            if (File.Exists(_configPath))
            {
                File.Replace(tempConfigPath, _configPath, oldConfigPath);
                TryDeleteFile(oldConfigPath);
            }
            else
            {
                File.Move(tempConfigPath, _configPath);
            }

            EnsureSecureFile(_configPath);

            // 設定変更を通知
            var newConfigHash = CalculateConfigHash();
            var eventArgs = new ConfigurationChangedEventArgs(_currentConfigHash, newConfigHash, DateTimeOffset.UtcNow);
            ConfigurationChanged?.Invoke(this, eventArgs);

            _currentConfigHash = newConfigHash;

            _logger.LogInformation("Configuration updated successfully");
            return new ConfigurationUpdateResult(true, null, backup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update configuration");
            return new ConfigurationUpdateResult(false, ex.Message, null);
        }
        finally
        {
            var tempConfigPath = _configPath + ".tmp";
            TryDeleteFile(tempConfigPath);
        }
    }

    public async Task<ConfigurationBackup> CreateBackupAsync()
    {
        try
        {
            var backupId = Guid.NewGuid().ToString();
            var timestamp = DateTimeOffset.UtcNow;
            var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

            string configJson;
            if (File.Exists(_configPath))
            {
                configJson = await File.ReadAllTextAsync(_configPath);
            }
            else
            {
                configJson = JsonSerializer.Serialize(new { }, new JsonSerializerOptions { WriteIndented = true });
            }

            var backup = new ConfigurationBackup(backupId, timestamp, configJson, environmentName);

            EnsureSecureDirectory(_backupDirectory);
            var backupPath = Path.Combine(_backupDirectory, $"{backupId}.json");
            await File.WriteAllTextAsync(backupPath, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
            EnsureSecureFile(backupPath);

            _logger.LogInformation("Configuration backup created: {BackupId}", backupId);
            return backup;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create configuration backup");
            throw;
        }
    }

    public async Task<ConfigurationRestoreResult> RestoreFromBackupAsync(ConfigurationBackup backup, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Restoring configuration from backup: {BackupId}", backup.BackupId);

            // 現在の設定をバックアップ
            await CreateBackupAsync();

            // バックアップから設定を復元
            await File.WriteAllTextAsync(_configPath, backup.ConfigurationJson, cancellationToken);
            EnsureSecureFile(_configPath);

            // 設定変更を通知
            var newConfigHash = CalculateConfigHash();
            var eventArgs = new ConfigurationChangedEventArgs(_currentConfigHash, newConfigHash, DateTimeOffset.UtcNow);
            ConfigurationChanged?.Invoke(this, eventArgs);

            _currentConfigHash = newConfigHash;

            _logger.LogInformation("Configuration restored successfully from backup: {BackupId}", backup.BackupId);
            return new ConfigurationRestoreResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore configuration from backup: {BackupId}", backup.BackupId);
            return new ConfigurationRestoreResult(false, ex.Message);
        }
    }

    private async Task<(bool IsValid, List<string> Errors)> ValidateConfigurationAsync(string configJson, CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        try
        {
            // JSON構文チェック
            JsonDocument.Parse(configJson);
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON syntax: {ex.Message}");
            return (false, errors);
        }

        try
        {
            // 設定オブジェクトをデシリアライズして検証
            using var jsonStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(configJson));
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(jsonStream)
                .Build();

            var services = new ServiceCollection();
            services.Configure<RemediationPolicyOptions>(configuration.GetSection("RemediationPolicy"));
            services.Configure<TelemetryRetentionOptions>(configuration.GetSection("TelemetryRetention"));

            using var serviceProvider = services.BuildServiceProvider();

            // バリデーション実行
            try
            {
                var remediationOptions = serviceProvider.GetRequiredService<IOptions<RemediationPolicyOptions>>();
                var remediationPolicy = remediationOptions.Value; // バリデーションをトリガー

                if (!RemediationPolicyOptionsValidators.HasUniqueTaskNames(remediationPolicy))
                {
                    errors.Add("Remediation tasks must have unique names.");
                }

                if (!RemediationPolicyOptionsValidators.CommandsAreAllowlisted(remediationPolicy))
                {
                    errors.Add("Enabled remediation task commands must be listed in CommandAllowlist.");
                }
            }
            catch (OptionsValidationException ex)
            {
                errors.AddRange(ex.Failures);
            }

            try
            {
                var telemetryOptions = serviceProvider.GetRequiredService<IOptions<TelemetryRetentionOptions>>();
                _ = telemetryOptions.Value; // バリデーションをトリガー
            }
            catch (OptionsValidationException ex)
            {
                errors.AddRange(ex.Failures);
            }

            foreach (var urlError in ValidateConfigurationUrls(configuration))
            {
                errors.Add(urlError);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Configuration validation error: {ex.Message}");
        }

        return (errors.Count == 0, errors);
    }

    private bool ValidateConfigFilePermissions(string configPath)
    {
        try
        {
            var fileInfo = new FileInfo(configPath);
            var accessControl = fileInfo.GetAccessControl();
            var rules = accessControl.GetAccessRules(true, true, typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & FileSystemRights.WriteData) != 0)
                {
                    var sid = TryGetSecurityIdentifier(rule.IdentityReference);
                    if (sid is null || !IsPrivilegedSid(sid))
                    {
                        _logger.LogWarning("Configuration file has insecure permissions");
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate configuration file permissions");
            return false;
        }
    }

    private static bool IsPrivilegedSid(System.Security.Principal.SecurityIdentifier sid)
    {
        return sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) ||
               sid.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) ||
               sid.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalServiceSid) ||
               sid.IsWellKnown(System.Security.Principal.WellKnownSidType.NetworkServiceSid);
    }

    private void HardenConfigStorage()
    {
        try
        {
            var configDirectory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
                EnsureSecureDirectory(configDirectory);
            }

            Directory.CreateDirectory(_backupDirectory);
            EnsureSecureDirectory(_backupDirectory);

            if (File.Exists(_configPath))
            {
                EnsureSecureFile(_configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to harden configuration storage");
        }
    }

    private void EnsureSecureDirectory(string directoryPath)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            if (!directoryInfo.Exists)
            {
                directoryInfo.Create();
            }

            var security = directoryInfo.GetAccessControl();
            HardenAccessControl(security, isDirectory: true);
            directoryInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to harden directory permissions for {Directory}", directoryPath);
        }
    }

    private void EnsureSecureFile(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                return;
            }

            var security = fileInfo.GetAccessControl();
            HardenAccessControl(security, isDirectory: false);
            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to harden configuration file permissions for {File}", filePath);
        }
    }

    private void HardenAccessControl(FileSystemSecurity security, bool isDirectory)
    {
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        foreach (var rule in rules)
        {
            var sid = TryGetSecurityIdentifier(rule.IdentityReference);
            if (sid is null || !IsPrivilegedSid(sid))
            {
                security.RemoveAccessRule(rule);
            }
        }

        foreach (var sid in PrivilegedSecurityIdentifiers)
        {
            FileSystemAccessRule rule = isDirectory
                ? new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)
                : new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow);

            security.SetAccessRule(rule);
        }
    }

    private static SecurityIdentifier? TryGetSecurityIdentifier(IdentityReference identity)
    {
        if (identity is SecurityIdentifier sid)
        {
            return sid;
        }

        try
        {
            return (SecurityIdentifier)identity.Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete temporary file {Path}", path);
        }
    }

    private string CalculateConfigHash()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return string.Empty;
            }

            var configContent = File.ReadAllText(_configPath);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(configContent));
            return Convert.ToHexString(hashBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate configuration hash");
            return string.Empty;
        }
    }

    // 古いバックアップファイルのクリーンアップ
    public async Task CleanupOldBackupsAsync(int retentionDays = 30)
    {
        try
        {
            var cutoffDate = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            var backupFiles = Directory.GetFiles(_backupDirectory, "*.json");

            foreach (var backupFile in backupFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(backupFile);
                    if (fileInfo.CreationTimeUtc < cutoffDate.UtcDateTime)
                    {
                        File.Delete(backupFile);
                        _logger.LogDebug("Cleaned up old backup: {BackupFile}", backupFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup backup file: {BackupFile}", backupFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old backups");
        }
    }

    private IEnumerable<string> ValidateConfigurationUrls(IConfiguration configuration)
    {
        foreach (var entry in configuration.AsEnumerable())
        {
            if (entry.Value is null)
            {
                continue;
            }

            var value = entry.Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (!value.Contains("://", StringComparison.Ordinal))
            {
                continue;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
            {
                yield return $"Configuration key '{entry.Key}' contains an invalid URL value.";
                continue;
            }

            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                yield return $"Configuration key '{entry.Key}' must use HTTPS endpoints.";
                continue;
            }

            if (!NetworkSecurityGuard.TryNormalizeHost(uri.Host, out var normalizedHost, out var isDnsName))
            {
                yield return $"Configuration key '{entry.Key}' contains an invalid host.";
                continue;
            }

            if (isDnsName && !NetworkSecurityGuard.HasValidDomainStructure(normalizedHost))
            {
                yield return $"Configuration key '{entry.Key}' contains a malformed domain.";
                continue;
            }

            if (NetworkSecurityGuard.IsHostRestricted(normalizedHost, isDnsName))
            {
                yield return $"Configuration key '{entry.Key}' uses a restricted host.";
                continue;
            }

            if (!uri.IsDefaultPort)
            {
                var isDangerousPort = false;
                if (!NetworkSecurityGuard.IsPortNumberAllowed(uri.Port, out isDangerousPort) || isDangerousPort)
                {
                    yield return $"Configuration key '{entry.Key}' specifies an unapproved port.";
                    continue;
                }
            }

            string decodedPathAndQuery;
            try
            {
                decodedPathAndQuery = Uri.UnescapeDataString(uri.PathAndQuery);
            }
            catch (Exception)
            {
                yield return $"Configuration key '{entry.Key}' contains an invalid URL path.";
                continue;
            }

            if (decodedPathAndQuery.Length > 2048)
            {
                yield return $"Configuration key '{entry.Key}' has a URL path exceeding allowed length.";
                continue;
            }

            if (decodedPathAndQuery.Any(ch => char.IsControl(ch) && ch != '\t'))
            {
                yield return $"Configuration key '{entry.Key}' contains control characters in the URL path.";
                continue;
            }

            if (NetworkSecurityGuard.ContainsCrossSiteScriptingPattern(decodedPathAndQuery) ||
                NetworkSecurityGuard.ContainsCrossSiteScriptingPattern(uri.Fragment))
            {
                yield return $"Configuration key '{entry.Key}' contains disallowed script patterns in the URL.";
            }
        }
    }
}
