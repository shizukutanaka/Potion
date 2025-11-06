using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// ログセキュリティの強化サービス
/// ログデータの暗号化とアクセス制御の強化を実装
/// </summary>
public interface ISecureLogService
{
    Task WriteEncryptedLogAsync(string message, LogLevel level, string category = "General");
    Task<string> ReadDecryptedLogAsync(string logFilePath);
    Task<IEnumerable<LogEntry>> SearchLogsAsync(string searchTerm, DateTime? startDate = null, DateTime? endDate = null);
    Task<bool> ArchiveOldLogsAsync(int retentionDays);
    Task<LogSecurityReport> GetSecurityReportAsync();
    void SetAccessControl(string logDirectory);
}

/// <summary>
/// ログレベル
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>
/// ログエントリ
/// </summary>
public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public LogLevel Level { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MachineName { get; set; } = Environment.MachineName;
    public string UserName { get; set; } = Environment.UserName;
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// ログセキュリティレポート
/// </summary>
public class LogSecurityReport
{
    public bool IsSecure { get; set; }
    public int TotalLogFiles { get; set; }
    public int EncryptedFiles { get; set; }
    public int UnencryptedFiles { get; set; }
    public long TotalSize { get; set; }
    public DateTime LastSecurityCheck { get; set; } = DateTime.UtcNow;
    public List<string> SecurityIssues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

/// <summary>
/// セキュアログサービス実装
/// </summary>
public class SecureLogService : ISecureLogService
{
    private readonly ILogger<SecureLogService> _logger;
    private readonly string _logDirectory;
    private readonly string _encryptionKey;
    private readonly byte[] _keyBytes;
    private readonly byte[] _ivBytes;

    public SecureLogService(ILogger<SecureLogService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _logDirectory = ServicePaths.Logs;
        Directory.CreateDirectory(_logDirectory);

        // 暗号化キーの初期化
        _encryptionKey = Environment.GetEnvironmentVariable("LOG_ENCRYPTION_KEY") ?? GenerateEncryptionKey();
        _keyBytes = Encoding.UTF8.GetBytes(_encryptionKey.Substring(0, 32)); // 256ビット
        _ivBytes = Encoding.UTF8.GetBytes(_encryptionKey.Substring(0, 16)); // 128ビット

        // ログディレクトリのアクセス制御を設定
        SetAccessControl(_logDirectory);

        _logger.LogInformation("Secure log service initialized with encryption");
    }

    public async Task WriteEncryptedLogAsync(string message, LogLevel level, string category = "General")
    {
        try
        {
            var logEntry = new LogEntry
            {
                Level = level,
                Category = category,
                Message = message,
                Properties = new Dictionary<string, object>
                {
                    ["ProcessId"] = Environment.ProcessId,
                    ["ThreadId"] = Environment.CurrentManagedThreadId,
                    ["AssemblyVersion"] = GetType().Assembly.GetName().Version?.ToString() ?? "Unknown"
                }
            };

            var jsonContent = System.Text.Json.JsonSerializer.Serialize(logEntry,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });

            // ログを暗号化してファイルに書き込み
            var encryptedContent = EncryptString(jsonContent);

            var logFileName = $"secure_{DateTime.UtcNow:yyyyMMdd}.log.enc";
            var logFilePath = Path.Combine(_logDirectory, logFileName);

            await File.AppendAllTextAsync(logFilePath, encryptedContent + Environment.NewLine);

            _logger.LogDebug("Encrypted log written to {LogFile}", logFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing encrypted log");
            // 暗号化ログの書き込みに失敗した場合、プレーンテキストでログを記録（ただし警告付き）
            await WritePlainTextLogAsync($"SECURITY WARNING: Failed to encrypt log entry: {ex.Message}", LogLevel.Warning, "Security");
        }
    }

    public async Task<string> ReadDecryptedLogAsync(string logFilePath)
    {
        try
        {
            if (!File.Exists(logFilePath))
            {
                throw new FileNotFoundException("Log file not found", logFilePath);
            }

            // ファイルのアクセス権を確認
            if (!HasReadAccess(logFilePath))
            {
                throw new UnauthorizedAccessException("No read access to log file");
            }

            var encryptedContent = await File.ReadAllTextAsync(logFilePath);
            return DecryptString(encryptedContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading decrypted log from {LogFile}", logFilePath);
            throw new InvalidOperationException("Failed to read log file", ex);
        }
    }

    public async Task<IEnumerable<LogEntry>> SearchLogsAsync(string searchTerm, DateTime? startDate = null, DateTime? endDate = null)
    {
        var logEntries = new List<LogEntry>();

        try
        {
            var logFiles = Directory.GetFiles(_logDirectory, "secure_*.log.enc");

            foreach (var logFile in logFiles)
            {
                try
                {
                    var decryptedContent = await ReadDecryptedLogAsync(logFile);
                    var lines = decryptedContent.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        try
                        {
                            var logEntry = System.Text.Json.JsonSerializer.Deserialize<LogEntry>(line);
                            if (logEntry != null)
                            {
                                // 日付フィルター
                                if (startDate.HasValue && logEntry.Timestamp < startDate.Value)
                                    continue;

                                if (endDate.HasValue && logEntry.Timestamp > endDate.Value)
                                    continue;

                                // 検索語句フィルター
                                if (!string.IsNullOrEmpty(searchTerm))
                                {
                                    if (!logEntry.Message.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) &&
                                        !logEntry.Category.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }
                                }

                                logEntries.Add(logEntry);
                            }
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            // 無効なJSON行はスキップ
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing log file {LogFile}", logFile);
                    continue;
                }
            }

            return logEntries.OrderByDescending(e => e.Timestamp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching logs");
            return Enumerable.Empty<LogEntry>();
        }
    }

    public async Task<bool> ArchiveOldLogsAsync(int retentionDays)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
            var logFiles = Directory.GetFiles(_logDirectory, "secure_*.log.enc");

            var archivedCount = 0;

            foreach (var logFile in logFiles)
            {
                var fileInfo = new FileInfo(logFile);
                if (fileInfo.CreationTime < cutoffDate)
                {
                    // 古いログファイルをアーカイブディレクトリに移動
                    var archiveDir = Path.Combine(_logDirectory, "archive");
                    Directory.CreateDirectory(archiveDir);

                    var archivePath = Path.Combine(archiveDir, Path.GetFileName(logFile));
                    File.Move(logFile, archivePath);

                    archivedCount++;

                    _logger.LogInformation("Archived old log file: {LogFile}", logFile);
                }
            }

            _logger.LogInformation("Archived {ArchivedCount} old log files", archivedCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error archiving old logs");
            return false;
        }
    }

    public async Task<LogSecurityReport> GetSecurityReportAsync()
    {
        var report = new LogSecurityReport();

        try
        {
            var logFiles = Directory.GetFiles(_logDirectory, "*.enc");
            report.TotalLogFiles = logFiles.Length;

            var totalSize = 0L;
            var encryptedFiles = 0;

            foreach (var logFile in logFiles)
            {
                var fileInfo = new FileInfo(logFile);
                totalSize += fileInfo.Length;

                // ファイルが暗号化されているかチェック（簡易チェック）
                if (logFile.EndsWith(".enc"))
                {
                    encryptedFiles++;
                }
                else
                {
                    report.SecurityIssues.Add($"Unencrypted log file found: {Path.GetFileName(logFile)}");
                }

                // アクセス権のチェック
                if (!HasSecureAccess(logFile))
                {
                    report.SecurityIssues.Add($"Insecure access permissions on: {Path.GetFileName(logFile)}");
                }
            }

            report.TotalSize = totalSize;
            report.EncryptedFiles = encryptedFiles;
            report.UnencryptedFiles = report.TotalLogFiles - encryptedFiles;
            report.IsSecure = report.SecurityIssues.Count == 0 && report.UnencryptedFiles == 0;

            if (!report.IsSecure)
            {
                report.Recommendations.Add("Ensure all log files are encrypted");
                report.Recommendations.Add("Review and fix access permissions on log files");
                report.Recommendations.Add("Implement log rotation and archiving policies");
            }

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating log security report");
            report.IsSecure = false;
            report.SecurityIssues.Add($"Error generating security report: {ex.Message}");
            return report;
        }
    }

    public void SetAccessControl(string logDirectory)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(logDirectory);

            // 現在のアクセス制御を取得
            var directorySecurity = directoryInfo.GetAccessControl();

            // 管理者とシステムのみにフルアクセスを許可
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            // 既存のアクセスルールをクリア
            directorySecurity.SetAccessRuleProtection(true, false);

            // 管理者とシステムにフルアクセスを許可
            var adminRule = new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            var systemRule = new FileSystemAccessRule(
                system,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            directorySecurity.AddAccessRule(adminRule);
            directorySecurity.AddAccessRule(systemRule);

            // ログサービスアカウントにもアクセスを許可（必要に応じて）
            var serviceAccount = Environment.GetEnvironmentVariable("LOG_SERVICE_ACCOUNT");
            if (!string.IsNullOrEmpty(serviceAccount))
            {
                try
                {
                    var account = new NTAccount(serviceAccount);
                    var serviceRule = new FileSystemAccessRule(
                        account,
                        FileSystemRights.ReadAndExecute | FileSystemRights.Write,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow);

                    directorySecurity.AddAccessRule(serviceRule);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not add service account access rule");
                }
            }

            directoryInfo.SetAccessControl(directorySecurity);

            _logger.LogInformation("Set secure access control on log directory: {LogDirectory}", logDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting access control on log directory: {LogDirectory}", logDirectory);
        }
    }

    private string EncryptString(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = _keyBytes;
        aes.IV = _ivBytes;

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);
        using var sw = new StreamWriter(cs);

        sw.Write(plainText);
        sw.Close();

        return Convert.ToBase64String(ms.ToArray());
    }

    private string DecryptString(string encryptedText)
    {
        var cipherText = Convert.FromBase64String(encryptedText);

        using var aes = Aes.Create();
        aes.Key = _keyBytes;
        aes.IV = _ivBytes;

        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream(cipherText);
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr = new StreamReader(cs);

        return sr.ReadToEnd();
    }

    private async Task WritePlainTextLogAsync(string message, LogLevel level, string category)
    {
        try
        {
            var logEntry = new LogEntry
            {
                Level = level,
                Category = category,
                Message = $"PLAIN TEXT LOG (ENCRYPTION FAILED): {message}"
            };

            var jsonContent = System.Text.Json.JsonSerializer.Serialize(logEntry,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });

            var logFileName = $"plaintext_{DateTime.UtcNow:yyyyMMdd}.log";
            var logFilePath = Path.Combine(_logDirectory, logFileName);

            await File.AppendAllTextAsync(logFilePath, jsonContent + Environment.NewLine);

            _logger.LogWarning("Wrote plain text log due to encryption failure: {LogFile}", logFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write even plain text log");
        }
    }

    private bool HasReadAccess(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var currentUser = WindowsIdentity.GetCurrent();

            var fileSecurity = fileInfo.GetAccessControl();
            var accessRules = fileSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in accessRules)
            {
                if (currentUser.User?.Equals(rule.IdentityReference) == true)
                {
                    if ((rule.FileSystemRights & FileSystemRights.Read) == FileSystemRights.Read)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool HasSecureAccess(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSecurity = fileInfo.GetAccessControl();

            // 適切なアクセス権があるかチェック
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            var hasAdminAccess = false;
            var hasSystemAccess = false;

            foreach (FileSystemAccessRule rule in fileSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference.Equals(administrators) && rule.AccessControlType == AccessControlType.Allow)
                {
                    hasAdminAccess = true;
                }

                if (rule.IdentityReference.Equals(system) && rule.AccessControlType == AccessControlType.Allow)
                {
                    hasSystemAccess = true;
                }

                // 他のユーザー/グループにはフルアクセスを許可していないかチェック
                if (!rule.IdentityReference.Equals(administrators) &&
                    !rule.IdentityReference.Equals(system) &&
                    rule.AccessControlType == AccessControlType.Allow)
                {
                    if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                    {
                        return false; // 不適切なアクセス権が見つかった
                    }
                }
            }

            return hasAdminAccess && hasSystemAccess;
        }
        catch
        {
            return false;
        }
    }

    private string GenerateEncryptionKey()
    {
        var keyBytes = new byte[48]; // 256ビットキー + 128ビットIV
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(keyBytes);
        }

        return Convert.ToBase64String(keyBytes);
    }

    /// <summary>
/// ログアクセス制御ヘルパー
/// </summary>
    public static class LogAccessControl
    {
        public static void GrantLogAccess(string directoryPath, string accountName)
        {
            try
            {
                var directoryInfo = new DirectoryInfo(directoryPath);
                var directorySecurity = directoryInfo.GetAccessControl();

                var account = new NTAccount(accountName);
                var rule = new FileSystemAccessRule(
                    account,
                    FileSystemRights.ReadAndExecute | FileSystemRights.Write,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow);

                directorySecurity.AddAccessRule(rule);
                directoryInfo.SetAccessControl(directorySecurity);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to grant log access to {accountName}", ex);
            }
        }

        public static void RevokeLogAccess(string directoryPath, string accountName)
        {
            try
            {
                var directoryInfo = new DirectoryInfo(directoryPath);
                var directorySecurity = directoryInfo.GetAccessControl();

                var account = new NTAccount(accountName);
                var rule = new FileSystemAccessRule(
                    account,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Deny);

                directorySecurity.AddAccessRule(rule);
                directoryInfo.SetAccessControl(directorySecurity);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to revoke log access from {accountName}", ex);
            }
        }

        public static bool IsLogAccessGranted(string directoryPath, string accountName)
        {
            try
            {
                var directoryInfo = new DirectoryInfo(directoryPath);
                var directorySecurity = directoryInfo.GetAccessControl();

                var account = new NTAccount(accountName);
                var accessRules = directorySecurity.GetAccessRules(true, true, typeof(NTAccount));

                foreach (FileSystemAccessRule rule in accessRules)
                {
                    if (rule.IdentityReference.Equals(account) &&
                        rule.AccessControlType == AccessControlType.Allow &&
                        (rule.FileSystemRights & FileSystemRights.Read) == FileSystemRights.Read)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
