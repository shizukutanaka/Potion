using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Manages automatic rollback of failed operations with snapshot-based recovery
/// スナップショットベースのリカバリによる失敗操作の自動ロールバック管理
/// </summary>
public sealed class AutomaticRollbackManager : IDisposable
{
    private readonly ILogger<AutomaticRollbackManager> _logger;
    private readonly ConcurrentDictionary<string, OperationSnapshot> _snapshots = new();
    private readonly string _snapshotDirectory;
    private const int MaxSnapshotsPerOperation = 10;
    private const int SnapshotRetentionDays = 7;

    public AutomaticRollbackManager(ILogger<AutomaticRollbackManager> logger)
    {
        _logger = logger;
        _snapshotDirectory = Path.Combine(ServicePaths.State, "snapshots");
        Directory.CreateDirectory(_snapshotDirectory);

        // Load existing snapshots
        LoadSnapshotsAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<string> CreateSnapshotAsync(
        string operationId,
        string operationType,
        Dictionary<string, object> state,
        CancellationToken cancellationToken)
    {
        var snapshotId = Guid.NewGuid().ToString("N");
        var snapshot = new OperationSnapshot(
            snapshotId,
            operationId,
            operationType,
            DateTimeOffset.UtcNow,
            state);

        _snapshots[snapshotId] = snapshot;

        await PersistSnapshotAsync(snapshot, cancellationToken);

        _logger.LogInformation(
            "Created snapshot {SnapshotId} for operation {OperationId} ({OperationType})",
            snapshotId, operationId, operationType);

        await CleanupOldSnapshotsAsync(operationId, cancellationToken);

        return snapshotId;
    }

    public async Task<bool> RollbackAsync(
        string snapshotId,
        Func<Dictionary<string, object>, Task> rollbackAction,
        CancellationToken cancellationToken)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
        {
            _logger.LogWarning("Snapshot {SnapshotId} not found for rollback", snapshotId);
            return false;
        }

        try
        {
            _logger.LogInformation(
                "Starting rollback for snapshot {SnapshotId} (Operation: {OperationId}, Type: {OperationType})",
                snapshotId, snapshot.OperationId, snapshot.OperationType);

            await rollbackAction(snapshot.State);

            _logger.LogInformation(
                "Successfully rolled back snapshot {SnapshotId}",
                snapshotId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to rollback snapshot {SnapshotId} for operation {OperationId}",
                snapshotId, snapshot.OperationId);

            return false;
        }
    }

    public async Task<bool> RollbackLatestAsync(
        string operationId,
        Func<Dictionary<string, object>, Task> rollbackAction,
        CancellationToken cancellationToken)
    {
        var latestSnapshot = _snapshots.Values
            .Where(s => s.OperationId == operationId)
            .OrderByDescending(s => s.Timestamp)
            .FirstOrDefault();

        if (latestSnapshot == null)
        {
            _logger.LogWarning("No snapshots found for operation {OperationId}", operationId);
            return false;
        }

        return await RollbackAsync(latestSnapshot.SnapshotId, rollbackAction, cancellationToken);
    }

    public IReadOnlyList<OperationSnapshot> GetSnapshots(string operationId)
    {
        return _snapshots.Values
            .Where(s => s.OperationId == operationId)
            .OrderByDescending(s => s.Timestamp)
            .ToList();
    }

    public bool DeleteSnapshot(string snapshotId)
    {
        if (!_snapshots.TryRemove(snapshotId, out var snapshot))
        {
            return false;
        }

        try
        {
            var snapshotFile = GetSnapshotFilePath(snapshotId);
            if (File.Exists(snapshotFile))
            {
                File.Delete(snapshotFile);
            }

            _logger.LogInformation("Deleted snapshot {SnapshotId}", snapshotId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete snapshot file for {SnapshotId}", snapshotId);
            return false;
        }
    }

    private async Task PersistSnapshotAsync(OperationSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            var snapshotFile = GetSnapshotFilePath(snapshot.SnapshotId);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(snapshotFile, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist snapshot {SnapshotId}", snapshot.SnapshotId);
        }
    }

    private async Task LoadSnapshotsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshotFiles = Directory.GetFiles(_snapshotDirectory, "snapshot_*.json");

            foreach (var file in snapshotFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken);
                    var snapshot = JsonSerializer.Deserialize<OperationSnapshot>(json);

                    if (snapshot != null)
                    {
                        // Skip snapshots older than retention period
                        if (DateTimeOffset.UtcNow - snapshot.Timestamp > TimeSpan.FromDays(SnapshotRetentionDays))
                        {
                            File.Delete(file);
                            continue;
                        }

                        _snapshots[snapshot.SnapshotId] = snapshot;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load snapshot from {File}", file);
                }
            }

            _logger.LogInformation("Loaded {Count} existing snapshots", _snapshots.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load snapshots from disk");
        }
    }

    private async Task CleanupOldSnapshotsAsync(string operationId, CancellationToken cancellationToken)
    {
        var operationSnapshots = _snapshots.Values
            .Where(s => s.OperationId == operationId)
            .OrderByDescending(s => s.Timestamp)
            .ToList();

        // Keep only the latest N snapshots per operation
        var toRemove = operationSnapshots.Skip(MaxSnapshotsPerOperation).ToList();

        foreach (var snapshot in toRemove)
        {
            DeleteSnapshot(snapshot.SnapshotId);
        }

        // Remove snapshots older than retention period
        var cutoff = DateTimeOffset.UtcNow.AddDays(-SnapshotRetentionDays);
        var expiredSnapshots = _snapshots.Values
            .Where(s => s.Timestamp < cutoff)
            .ToList();

        foreach (var snapshot in expiredSnapshots)
        {
            DeleteSnapshot(snapshot.SnapshotId);
        }

        if (toRemove.Count > 0 || expiredSnapshots.Count > 0)
        {
            _logger.LogInformation(
                "Cleaned up {RemovedCount} old snapshots, {ExpiredCount} expired snapshots",
                toRemove.Count, expiredSnapshots.Count);
        }
    }

    private string GetSnapshotFilePath(string snapshotId)
    {
        return Path.Combine(_snapshotDirectory, $"snapshot_{snapshotId}.json");
    }

    public void Dispose()
    {
        // Snapshots are already persisted, nothing to dispose
    }
}

public sealed record OperationSnapshot(
    string SnapshotId,
    string OperationId,
    string OperationType,
    DateTimeOffset Timestamp,
    Dictionary<string, object> State);
