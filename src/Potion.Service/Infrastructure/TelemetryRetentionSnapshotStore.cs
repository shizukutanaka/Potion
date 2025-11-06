using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface ITelemetryRetentionSnapshotStore
{
    Task WriteAsync(TelemetryRetentionMetricsSnapshot snapshot, CancellationToken cancellationToken);

    Task<TelemetryRetentionMetricsSnapshot?> LoadAsync(CancellationToken cancellationToken);

    Task CleanupQuarantinedSnapshotsAsync(CancellationToken cancellationToken);
}

public sealed class TelemetryRetentionSnapshotStore : ITelemetryRetentionSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false, // パフォーマンス向上のためインデントを無効化
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // JavaScriptとの互換性向上
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, // null値を無視
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } // enumを文字列としてシリアライズ
    };

    private readonly ILogger<TelemetryRetentionSnapshotStore> _logger;

    public TelemetryRetentionSnapshotStore(ILogger<TelemetryRetentionSnapshotStore> logger)
    {
        _logger = logger;
    }

    public Task CleanupQuarantinedSnapshotsAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ServicePaths.GetTelemetryRetentionSnapshotPath())!;
        if (!Directory.Exists(directory))
        {
            return Task.CompletedTask;
        }

        var files = Directory.EnumerateFiles(directory, "telemetry-retention.corrupt-*.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directory, "telemetry-retention.corrupt-*.sha256", SearchOption.TopDirectoryOnly))
            .ToArray();

        if (files.Length == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(file);
                _logger.LogInformation("Removed quarantined telemetry snapshot artifact {File}", file);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to remove quarantined telemetry snapshot artifact {File}", file);
            }
        }

        return Task.CompletedTask;
    }

    public async Task WriteAsync(TelemetryRetentionMetricsSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = ServicePaths.GetTelemetryRetentionSnapshotPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Join(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // メモリ効率を向上させるため、FileStreamを直接使用
            await using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
#if NET8_0_OR_GREATER
            await stream.FlushAsync(flushToDisk: true, cancellationToken);
#else
            stream.Flush(true);
#endif
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist telemetry retention snapshot to {Path}", path);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    public async Task<TelemetryRetentionMetricsSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        var path = ServicePaths.GetTelemetryRetentionSnapshotPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<TelemetryRetentionMetricsSnapshot>(stream, SerializerOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read telemetry retention snapshot from {Path}", path);
            QuarantineCorruptSnapshot(path);
            return null;
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private void QuarantineCorruptSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path)!;
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var quarantineName = $"{fileNameWithoutExtension}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}{extension}";
            var quarantinePath = Path.Join(directory, quarantineName);

            File.Move(path, quarantinePath, overwrite: true);
            _logger.LogWarning("Quarantined corrupt telemetry retention snapshot to {QuarantinePath}", quarantinePath);
        }
        catch (Exception quarantineEx)
        {
            _logger.LogDebug(quarantineEx, "Failed to quarantine telemetry retention snapshot {Path}", path);
        }
    }
}
