using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

/// <summary>
/// イミュータブル監査トレイルとブロックチェーン着想のログ
/// 改ざん耐性のある監査ログと暗号学的検証
/// </summary>
public interface IAuditTrailService
{
    Task<AuditEntry> LogEventAsync(AuditEvent auditEvent);
    Task<AuditEntry> LogSecurityEventAsync(SecurityAuditEvent securityEvent);
    Task<AuditEntry> LogPerformanceEventAsync(PerformanceAuditEvent performanceEvent);
    Task<AuditEntry> LogConfigurationEventAsync(ConfigurationAuditEvent configEvent);
    Task<IEnumerable<AuditEntry>> GetAuditTrailAsync(DateTimeOffset from, DateTimeOffset to);
    Task<AuditEntry> GetAuditEntryAsync(string entryId);
    Task<VerificationResult> VerifyAuditTrailAsync();
    Task<ImmutableLog> ExportAuditLogAsync();
    Task<bool> ValidateEntryAsync(string entryId, string expectedHash);
}

/// <summary>
/// 監査イベント
/// </summary>
public record AuditEvent(
    string EventType,
    string Component,
    string Action,
    string UserId,
    Dictionary<string, object> Details,
    DateTimeOffset Timestamp,
    string IpAddress,
    string UserAgent);

/// <summary>
/// セキュリティ監査イベント
/// </summary>
public record SecurityAuditEvent(
    string ThreatLevel,
    string AttackType,
    string SourceIp,
    string TargetComponent,
    string Description,
    Dictionary<string, object> SecurityDetails);

/// <summary>
/// パフォーマンス監査イベント
/// </summary>
public record PerformanceAuditEvent(
    string MetricName,
    double Value,
    double Threshold,
    string Component,
    PerformanceImpact Impact);

/// <summary>
/// 設定監査イベント
/// </summary>
public record ConfigurationAuditEvent(
    string ConfigSection,
    string ChangeType,
    string OldValue,
    string NewValue,
    string ChangedBy);

/// <summary>
/// 監査エントリ
/// </summary>
public record AuditEntry(
    string Id,
    string EventType,
    string Component,
    string Action,
    string Hash,
    string PreviousHash,
    DateTimeOffset Timestamp,
    string Data,
    string Signature,
    string UserId,
    Dictionary<string, object> Metadata);

/// <summary>
/// 検証結果
/// </summary>
public record VerificationResult(
    bool IsValid,
    int TotalEntries,
    int ValidEntries,
    int InvalidEntries,
    List<string> InvalidEntryIds,
    DateTimeOffset VerifiedAt);

/// <summary>
/// イミュータブルログ
/// </summary>
public record ImmutableLog(
    string Version,
    List<AuditEntry> Entries,
    string RootHash,
    DateTimeOffset CreatedAt,
    int TotalEntries);

/// <summary>
/// パフォーマンス影響度
/// </summary>
public enum PerformanceImpact
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// 監査トレイルサービス実装
/// </summary>
public class AuditTrailService : IAuditTrailService
{
    private readonly ILogger<AuditTrailService> _logger;
    private readonly List<AuditEntry> _auditEntries = new();
    private readonly object _entriesLock = new();
    private readonly SHA256 _sha256 = SHA256.Create();
    private readonly RSA _rsa = RSA.Create();
    private string _lastHash = string.Empty;
    private readonly Timer _verificationTimer;
    private readonly Timer _cleanupTimer;

    public AuditTrailService(ILogger<AuditTrailService> logger)
    {
        _logger = logger;

        // 1時間ごとに監査ログを検証
        _verificationTimer = new Timer(VerifyAuditTrailIntegrity, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // 24時間ごとに古いエントリをクリーンアップ
        _cleanupTimer = new Timer(CleanupOldEntries, null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
    }

    public async Task<AuditEntry> LogEventAsync(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        return await LogAuditEventAsync("General", auditEvent.Component, auditEvent.Action,
            auditEvent.UserId, auditEvent.Details, auditEvent.Timestamp, auditEvent.IpAddress, auditEvent.UserAgent);
    }

    public async Task<AuditEntry> LogSecurityEventAsync(SecurityAuditEvent securityEvent)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);

        var details = new Dictionary<string, object>
        {
            ["ThreatLevel"] = securityEvent.ThreatLevel,
            ["AttackType"] = securityEvent.AttackType,
            ["SourceIp"] = securityEvent.SourceIp,
            ["TargetComponent"] = securityEvent.TargetComponent,
            ["Description"] = securityEvent.Description
        };
        details.AddRange(securityEvent.SecurityDetails);

        return await LogAuditEventAsync("Security", securityEvent.TargetComponent, securityEvent.AttackType,
            "system", details, DateTimeOffset.UtcNow, securityEvent.SourceIp, "SecurityAudit");
    }

    public async Task<AuditEntry> LogPerformanceEventAsync(PerformanceAuditEvent performanceEvent)
    {
        ArgumentNullException.ThrowIfNull(performanceEvent);

        var details = new Dictionary<string, object>
        {
            ["MetricName"] = performanceEvent.MetricName,
            ["Value"] = performanceEvent.Value,
            ["Threshold"] = performanceEvent.Threshold,
            ["Impact"] = performanceEvent.Impact.ToString()
        };

        return await LogAuditEventAsync("Performance", performanceEvent.Component, "MetricThresholdExceeded",
            "system", details, DateTimeOffset.UtcNow, "internal", "PerformanceMonitor");
    }

    public async Task<AuditEntry> LogConfigurationEventAsync(ConfigurationAuditEvent configEvent)
    {
        ArgumentNullException.ThrowIfNull(configEvent);

        var details = new Dictionary<string, object>
        {
            ["ConfigSection"] = configEvent.ConfigSection,
            ["ChangeType"] = configEvent.ChangeType,
            ["OldValue"] = configEvent.OldValue,
            ["NewValue"] = configEvent.NewValue,
            ["ChangedBy"] = configEvent.ChangedBy
        };

        return await LogAuditEventAsync("Configuration", configEvent.ConfigSection, configEvent.ChangeType,
            configEvent.ChangedBy, details, DateTimeOffset.UtcNow, "internal", "ConfigurationManager");
    }

    public async Task<IEnumerable<AuditEntry>> GetAuditTrailAsync(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_entriesLock)
        {
            return _auditEntries
                .Where(e => e.Timestamp >= from && e.Timestamp <= to)
                .OrderBy(e => e.Timestamp)
                .ToList();
        }
    }

    public async Task<AuditEntry> GetAuditEntryAsync(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);

        lock (_entriesLock)
        {
            return _auditEntries.FirstOrDefault(e => e.Id == entryId)
                ?? throw new KeyNotFoundException($"Audit entry not found: {entryId}");
        }
    }

    public async Task<VerificationResult> VerifyAuditTrailAsync()
    {
        lock (_entriesLock)
        {
            var invalidEntries = new List<string>();
            var currentHash = string.Empty;

            for (int i = 0; i < _auditEntries.Count; i++)
            {
                var entry = _auditEntries[i];
                var computedHash = ComputeEntryHash(entry, i == 0 ? string.Empty : _auditEntries[i - 1].Hash);

                if (computedHash != entry.Hash)
                {
                    invalidEntries.Add(entry.Id);
                }

                currentHash = computedHash;
            }

            return new VerificationResult(
                _auditEntries.Count > 0 && invalidEntries.Count == 0,
                _auditEntries.Count,
                _auditEntries.Count - invalidEntries.Count,
                invalidEntries.Count,
                invalidEntries,
                DateTimeOffset.UtcNow
            );
        }
    }

    public async Task<ImmutableLog> ExportAuditLogAsync()
    {
        lock (_entriesLock)
        {
            var entries = _auditEntries.OrderBy(e => e.Timestamp).ToList();
            var rootHash = entries.Count > 0 ? entries.Last().Hash : string.Empty;

            return new ImmutableLog(
                "1.0",
                entries,
                rootHash,
                DateTimeOffset.UtcNow,
                entries.Count
            );
        }
    }

    public async Task<bool> ValidateEntryAsync(string entryId, string expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);

        try
        {
            var entry = await GetAuditEntryAsync(entryId);
            return entry.Hash == expectedHash;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private async Task<AuditEntry> LogAuditEventAsync(
        string eventType,
        string component,
        string action,
        string userId,
        Dictionary<string, object> details,
        DateTimeOffset timestamp,
        string ipAddress,
        string userAgent)
    {
        var entryId = Guid.NewGuid().ToString();
        var data = JsonSerializer.Serialize(details);
        var entryHash = ComputeEntryHash(data, timestamp, _lastHash);
        var signature = SignData(entryHash);

        var entry = new AuditEntry(
            entryId,
            eventType,
            component,
            action,
            entryHash,
            _lastHash,
            timestamp,
            data,
            signature,
            userId,
            new Dictionary<string, object>
            {
                ["IpAddress"] = ipAddress,
                ["UserAgent"] = userAgent,
                ["EntryId"] = entryId
            }
        );

        lock (_entriesLock)
        {
            _auditEntries.Add(entry);
            _lastHash = entryHash;
        }

        _logger.LogInformation("Audit entry logged: {EventType} - {Component} - {Action} by {UserId}",
            eventType, component, action, userId);

        return entry;
    }

    private string ComputeEntryHash(AuditEntry entry, string previousHash)
    {
        return ComputeEntryHash(entry.Data, entry.Timestamp, previousHash);
    }

    private string ComputeEntryHash(string data, DateTimeOffset timestamp, string previousHash)
    {
        var content = $"{previousHash}:{timestamp:O}:{data}";
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = _sha256.ComputeHash(contentBytes);
        return Convert.ToHexString(hashBytes);
    }

    private string SignData(string hash)
    {
        try
        {
            var hashBytes = Convert.FromHexString(hash);
            var signatureBytes = _rsa.SignHash(hashBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(signatureBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error signing audit data");
            return string.Empty;
        }
    }

    private async void VerifyAuditTrailIntegrity(object state)
    {
        try
        {
            var result = await VerifyAuditTrailAsync();

            if (!result.IsValid)
            {
                _logger.LogError("Audit trail integrity verification failed! {InvalidEntries} invalid entries found",
                    result.InvalidEntries);

                // 改ざん検知イベントをログ
                await LogEventAsync(new AuditEvent(
                    "Security",
                    "AuditTrail",
                    "IntegrityViolation",
                    "system",
                    new Dictionary<string, object>
                    {
                        ["InvalidEntries"] = result.InvalidEntries,
                        ["VerificationResult"] = result
                    },
                    DateTimeOffset.UtcNow,
                    "internal",
                    "IntegrityCheck"
                ));
            }
            else
            {
                _logger.LogDebug("Audit trail integrity verification passed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during audit trail verification");
        }
    }

    private async void CleanupOldEntries(object state)
    {
        try
        {
            var cutoffTime = DateTimeOffset.UtcNow.AddDays(-30); // 30日保持

            lock (_entriesLock)
            {
                var initialCount = _auditEntries.Count;
                _auditEntries.RemoveAll(e => e.Timestamp < cutoffTime);

                if (_auditEntries.Count < initialCount)
                {
                    _logger.LogInformation("Cleaned up {Count} old audit entries", initialCount - _auditEntries.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during audit entries cleanup");
        }
    }
}

/// <summary>
/// ブロックチェーン着想のログチェーン
/// </summary>
public interface IBlockchainAuditService
{
    Task<Block> CreateBlockAsync(IEnumerable<AuditEntry> entries);
    Task<bool> ValidateChainAsync();
    Task<Block> GetBlockAsync(string blockId);
    Task<string> GetMerkleRootAsync(IEnumerable<AuditEntry> entries);
}

/// <summary>
/// ブロック
/// </summary>
public class Block
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int Index { get; set; }
    public string PreviousHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string MerkleRoot { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public List<AuditEntry> Transactions { get; set; } = new();
    public int Nonce { get; set; }
    public string Signature { get; set; } = string.Empty;
}

/// <summary>
/// ブロックチェーン監査サービス実装
/// </summary>
public class BlockchainAuditService : IBlockchainAuditService
{
    private readonly ILogger<BlockchainAuditService> _logger;
    private readonly List<Block> _blocks = new();
    private readonly SHA256 _sha256 = SHA256.Create();
    private readonly object _blocksLock = new();

    public BlockchainAuditService(ILogger<BlockchainAuditService> logger)
    {
        _logger = logger;
    }

    public async Task<Block> CreateBlockAsync(IEnumerable<AuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var entriesList = entries.ToList();

        lock (_blocksLock)
        {
            var previousBlock = _blocks.LastOrDefault() ?? CreateGenesisBlock();
            var previousHash = previousBlock.Hash;

            var block = new Block
            {
                Index = previousBlock.Index + 1,
                PreviousHash = previousHash,
                Timestamp = DateTimeOffset.UtcNow,
                Transactions = entriesList
            };

            block.MerkleRoot = GetMerkleRootAsync(entriesList).GetAwaiter().GetResult();
            block.Hash = ComputeBlockHash(block);
            block.Signature = SignBlock(block);

            _blocks.Add(block);

            _logger.LogInformation("Created blockchain block {BlockIndex} with {TransactionCount} transactions",
                block.Index, entriesList.Count);

            return block;
        }
    }

    public async Task<bool> ValidateChainAsync()
    {
        lock (_blocksLock)
        {
            for (int i = 1; i < _blocks.Count; i++)
            {
                var currentBlock = _blocks[i];
                var previousBlock = _blocks[i - 1];

                if (currentBlock.PreviousHash != previousBlock.Hash)
                {
                    _logger.LogError("Chain validation failed: Block {BlockIndex} has invalid previous hash", i);
                    return false;
                }

                if (currentBlock.Hash != ComputeBlockHash(currentBlock))
                {
                    _logger.LogError("Chain validation failed: Block {BlockIndex} has invalid hash", i);
                    return false;
                }
            }

            return true;
        }
    }

    public async Task<Block> GetBlockAsync(string blockId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockId);

        lock (_blocksLock)
        {
            return _blocks.FirstOrDefault(b => b.Id == blockId)
                ?? throw new KeyNotFoundException($"Block not found: {blockId}");
        }
    }

    public async Task<string> GetMerkleRootAsync(IEnumerable<AuditEntry> entries)
    {
        var hashes = entries.Select(e => e.Hash).ToList();

        while (hashes.Count > 1)
        {
            var newHashes = new List<string>();

            for (int i = 0; i < hashes.Count; i += 2)
            {
                var left = hashes[i];
                var right = i + 1 < hashes.Count ? hashes[i + 1] : left;
                var combined = left + right;
                var combinedBytes = Encoding.UTF8.GetBytes(combined);
                var hashBytes = _sha256.ComputeHash(combinedBytes);
                newHashes.Add(Convert.ToHexString(hashBytes));
            }

            hashes = newHashes;
        }

        return hashes.FirstOrDefault() ?? string.Empty;
    }

    private Block CreateGenesisBlock()
    {
        var genesisBlock = new Block
        {
            Index = 0,
            PreviousHash = "0000000000000000000000000000000000000000000000000000000000000000",
            Timestamp = DateTimeOffset.UtcNow,
            Hash = "0000000000000000000000000000000000000000000000000000000000000000",
            MerkleRoot = string.Empty,
            Signature = string.Empty
        };

        return genesisBlock;
    }

    private string ComputeBlockHash(Block block)
    {
        var content = $"{block.Index}:{block.PreviousHash}:{block.MerkleRoot}:{block.Timestamp:O}:{block.Nonce}";
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = _sha256.ComputeHash(contentBytes);
        return Convert.ToHexString(hashBytes);
    }

    private string SignBlock(Block block)
    {
        // 簡易的な署名（実際には適切な秘密鍵を使用）
        var content = $"{block.Index}:{block.Hash}:{block.Timestamp:O}";
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = _sha256.ComputeHash(contentBytes);
        return Convert.ToBase64String(hashBytes);
    }
}
