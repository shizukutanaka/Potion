using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Potion.Service.Infrastructure;

/// <summary>
/// リソースレベルでのアクセス制御サービス
/// ファイル、ディレクトリ、レジストリなどのリソースに対するアクセス制御を実装
/// </summary>
public sealed class ResourceAccessControlService : IResourceAccessControlService, IDisposable
{
    private readonly ILogger<ResourceAccessControlService> _logger;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _optionsMonitor;
    private readonly ConcurrentDictionary<string, ResourceAccessRule> _accessRules = new();
    private readonly object _lock = new();

    public ResourceAccessControlService(
        ILogger<ResourceAccessControlService> logger,
        IOptionsMonitor<RemediationPolicyOptions> optionsMonitor)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        InitializeDefaultAccessRules();
    }

    /// <summary>
    /// リソースへのアクセスをチェック
    /// </summary>
    public bool CheckAccess(string resourcePath, ResourceOperation operation, string userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath, nameof(resourcePath));

        try
        {
            var normalizedPath = Path.GetFullPath(resourcePath);
            var applicableRules = GetApplicableRules(normalizedPath);

            foreach (var rule in applicableRules)
            {
                if (rule.Operation == operation || rule.Operation == ResourceOperation.All)
                {
                    // ユーザーIDチェック
                    if (!string.IsNullOrEmpty(rule.UserId) && rule.UserId != userId)
                    {
                        continue;
                    }

                    // パスチェック
                    if (IsPathMatched(normalizedPath, rule.ResourcePath, rule.PathPattern))
                    {
                        var allowed = rule.Allow;

                        _logger.LogDebug("リソースアクセスチェック: {Path} - {Operation} - {Allowed}",
                            normalizedPath, operation, allowed);

                        return allowed;
                    }
                }
            }

            // デフォルトは拒否
            _logger.LogWarning("リソースアクセス拒否: {Path} - {Operation} - 該当するルールなし", normalizedPath, operation);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "リソースアクセスチェック中にエラーが発生しました: {Path}", resourcePath);
            return false;
        }
    }

    /// <summary>
    /// リソースのアクセス権限を検証
    /// </summary>
    public ResourceAccessResult ValidateAccess(string resourcePath, ResourceOperation operation, string userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath, nameof(resourcePath));

        var result = new ResourceAccessResult
        {
            ResourcePath = resourcePath,
            Operation = operation,
            UserId = userId,
            Allowed = false,
            Reason = "Unknown"
        };

        try
        {
            var normalizedPath = Path.GetFullPath(resourcePath);

            // 基本的なパスの検証
            if (!IsPathValid(normalizedPath))
            {
                result.Allowed = false;
                result.Reason = "Invalid path";
                return result;
            }

            // システムディレクトリのチェック
            if (IsSystemPath(normalizedPath))
            {
                result.Allowed = true;
                result.Reason = "System path access allowed";
                return result;
            }

            // カスタムルールチェック
            result.Allowed = CheckAccess(normalizedPath, operation, userId);
            result.Reason = result.Allowed ? "Access allowed by rule" : "Access denied by rule";

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "アクセス検証中にエラーが発生しました: {Path}", resourcePath);
            result.Allowed = false;
            result.Reason = $"Error: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// アクセスルールを追加
    /// </summary>
    public void AddAccessRule(string name, ResourceAccessRule rule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ArgumentNullException.ThrowIfNull(rule, nameof(rule));

        lock (_lock)
        {
            _accessRules[name] = rule;
            _logger.LogInformation("アクセスルールを追加しました: {Name}", name);
        }
    }

    /// <summary>
    /// アクセスルールを削除
    /// </summary>
    public bool RemoveAccessRule(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));

        lock (_lock)
        {
            var removed = _accessRules.TryRemove(name, out _);
            if (removed)
            {
                _logger.LogInformation("アクセスルールを削除しました: {Name}", name);
            }
            return removed;
        }
    }

    /// <summary>
    /// アクセスルールの一覧を取得
    /// </summary>
    public IReadOnlyDictionary<string, ResourceAccessRule> GetAccessRules()
    {
        lock (_lock)
        {
            return _accessRules.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }

    private List<ResourceAccessRule> GetApplicableRules(string path)
    {
        lock (_lock)
        {
            return _accessRules.Values
                .Where(rule => IsPathMatched(path, rule.ResourcePath, rule.PathPattern))
                .OrderByDescending(rule => rule.Priority)
                .ToList();
        }
    }

    private bool IsPathMatched(string targetPath, string rulePath, string pattern)
    {
        if (!string.IsNullOrEmpty(rulePath))
        {
            var normalizedRulePath = Path.GetFullPath(rulePath);
            if (targetPath.StartsWith(normalizedRulePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrEmpty(pattern))
        {
            // ワイルドカードパターンによるマッチング
            var regexPattern = pattern.Replace("*", ".*").Replace("?", ".");
            return System.Text.RegularExpressions.Regex.IsMatch(targetPath, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return false;
    }

    private bool IsPathValid(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.Length > 0 && fullPath.Length < 260; // MAX_PATH制限
        }
        catch
        {
            return false;
        }
    }

    private bool IsSystemPath(string path)
    {
        var systemRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        return systemRoots.Any(root =>
            !string.IsNullOrEmpty(root) &&
            path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private void InitializeDefaultAccessRules()
    {
        // システムディレクトリへの読み取りアクセスを許可
        AddAccessRule("system-read", new ResourceAccessRule
        {
            ResourcePath = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            PathPattern = $"{Environment.GetFolderPath(Environment.SpecialFolder.Windows)}*",
            Operation = ResourceOperation.Read,
            Allow = true,
            Priority = 100,
            Description = "System directory read access"
        });

        // Program Filesへの読み取りアクセスを許可
        AddAccessRule("program-files-read", new ResourceAccessRule
        {
            ResourcePath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            PathPattern = $"{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)}*",
            Operation = ResourceOperation.Read,
            Allow = true,
            Priority = 90,
            Description = "Program Files read access"
        });

        // Potionログディレクトリへの書き込みアクセスを許可
        AddAccessRule("potion-logs-write", new ResourceAccessRule
        {
            ResourcePath = ServicePaths.Logs,
            PathPattern = $"{ServicePaths.Logs}*",
            Operation = ResourceOperation.Write,
            Allow = true,
            Priority = 80,
            Description = "Potion logs write access"
        });

        // 一時ディレクトリへのアクセスを制限
        AddAccessRule("temp-restrict", new ResourceAccessRule
        {
            ResourcePath = Path.GetTempPath(),
            PathPattern = $"{Path.GetTempPath()}*",
            Operation = ResourceOperation.All,
            Allow = false,
            Priority = 50,
            Description = "Temporary directory access restricted"
        });
    }

    public void Dispose()
    {
        // クリーンアップ処理
        _accessRules.Clear();
    }
}

/// <summary>
/// リソースアクセス制御サービスインターフェース
/// </summary>
public interface IResourceAccessControlService
{
    bool CheckAccess(string resourcePath, ResourceOperation operation, string userId = null);
    ResourceAccessResult ValidateAccess(string resourcePath, ResourceOperation operation, string userId = null);
    void AddAccessRule(string name, ResourceAccessRule rule);
    bool RemoveAccessRule(string name);
    IReadOnlyDictionary<string, ResourceAccessRule> GetAccessRules();
}

/// <summary>
/// リソースアクセス結果
/// </summary>
public sealed class ResourceAccessResult
{
    public string ResourcePath { get; set; } = string.Empty;
    public ResourceOperation Operation { get; set; }
    public string? UserId { get; set; }
    public bool Allowed { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// リソースアクセスルール
/// </summary>
public sealed class ResourceAccessRule
{
    public string? ResourcePath { get; set; }
    public string? PathPattern { get; set; }
    public ResourceOperation Operation { get; set; }
    public bool Allow { get; set; }
    public int Priority { get; set; } = 0;
    public string? UserId { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// リソース操作の種類
/// </summary>
public enum ResourceOperation
{
    Read,
    Write,
    Execute,
    Delete,
    All
}
