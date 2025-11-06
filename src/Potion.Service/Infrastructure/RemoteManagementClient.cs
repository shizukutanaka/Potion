using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

public interface IRemoteManagementClient
{
    Task<RemoteManagementResult> SendHeartbeatAsync(CancellationToken cancellationToken);
    Task<RemoteManagementResult> RequestPolicyUpdateAsync(CancellationToken cancellationToken);
    Task<RemoteManagementResult> SendLogsAsync(IEnumerable<string> logFiles, CancellationToken cancellationToken);
    Task<RemoteManagementResult> RequestCommandExecutionAsync(string command, string arguments, CancellationToken cancellationToken);
}

public sealed record RemoteManagementResult(
    bool Success,
    string? ErrorMessage,
    int? HttpStatusCode,
    Dictionary<string, object>? Metadata);

public sealed record RemoteManagementConfig
{
    public string ServerEndpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan LogSyncInterval { get; set; } = TimeSpan.FromHours(1);
}

public sealed class RemoteManagementClient : IRemoteManagementClient
{
    private readonly ILogger<RemoteManagementClient> _logger;
    private readonly ISecureCommunicator _secureCommunicator;
    private readonly IConfigurationManager _configurationManager;
    private readonly ISystemHealthMonitor _healthMonitor;
    private RemoteManagementConfig _config;
    private const int MaxOfflineQueueLength = 100;
    private const int ApiKeyMinimumLength = 32;
    private const int MaxLogFileBytes = 2 * 1024 * 1024; // 2MB safety cap per payload item
    private const int MaxRetryCount = 3;
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient;
    private readonly IDisposable? _configChangeRegistration;
    private readonly string _machineId;
    private readonly Queue<QueuedRemoteAction> _offlineQueue = new();
    private readonly SemaphoreSlim _queueSemaphore = new(1, 1);
    private int _isProcessingOfflineQueue;

    public RemoteManagementClient(
        ILogger<RemoteManagementClient> logger,
        ISecureCommunicator secureCommunicator,
        IConfigurationManager configurationManager,
        ISystemHealthMonitor healthMonitor,
        IOptions<RemoteManagementConfig> config,
        HttpClient httpClient)
    {
        _logger = logger;
        _secureCommunicator = secureCommunicator;
        _configurationManager = configurationManager;
        _healthMonitor = healthMonitor;
        _config = config.Value;

        _httpClient = httpClient;
        ConfigureHttpClient(_config);

        _configChangeRegistration = config.OnChange(updated =>
        {
            _config = updated;
            try
            {
                ValidateConfiguration(updated);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote management configuration invalid");
            }

            ConfigureHttpClient(updated);
        });

        _machineId = string.IsNullOrWhiteSpace(_config.MachineId)
            ? GenerateMachineId()
            : _config.MachineId;
        ValidateConfiguration(_config);
    }

    public async Task<RemoteManagementResult> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_config.Enabled)
            {
                return new RemoteManagementResult(false, "Remote management is disabled", null, null);
            }

            var healthSnapshot = await _healthMonitor.GetCurrentHealthAsync(cancellationToken);

            var heartbeatData = new
            {
                MachineId = _machineId,
                Timestamp = DateTimeOffset.UtcNow,
                Version = GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0",
                Status = "Online",
                Metrics = new
                {
                    CpuUsage = healthSnapshot.Metrics.Cpu.UsagePercent,
                    MemoryUsage = healthSnapshot.Metrics.Memory.UsedPercent,
                    DiskUsage = healthSnapshot.Metrics.Disk.UsedPercent,
                    ActiveAlerts = healthSnapshot.Alerts.Count
                },
                LastBootTime = GetLastBootTime(),
                OsVersion = Environment.OSVersion.ToString(),
                Domain = "REDACTED", // セキュリティのためドメイン名を送信しない
                Username = "REDACTED" // セキュリティのためユーザー名を送信しない
            };

            var endpoint = $"{_config.ServerEndpoint.TrimEnd('/')}/api/machines/{_machineId}/heartbeat";

            var result = await TryExecuteRemoteCallAsync(
                endpoint,
                async ct =>
                {
                    var secureResult = await _secureCommunicator.SendTelemetryAsync(endpoint, heartbeatData, ct);
                    return ToRemoteManagementResult(endpoint, secureResult, new Dictionary<string, object>
                    {
                        ["HeartbeatSent"] = true,
                        ["Timestamp"] = DateTimeOffset.UtcNow
                    });
                },
                cancellationToken);

            if (!result.Success && !IsOfflineReplayInProgress)
            {
                await EnqueueOfflineAsync(ct => SendHeartbeatAsync(ct), cancellationToken);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send heartbeat");
            await EnqueueOfflineAsync(ct => SendHeartbeatAsync(ct), cancellationToken);
            return new RemoteManagementResult(false, ex.Message, null, null);
        }
    }

    public async Task<RemoteManagementResult> RequestPolicyUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_config.Enabled)
            {
                return new RemoteManagementResult(false, "Remote management is disabled", null, null);
            }

            var endpoint = $"{_config.ServerEndpoint.TrimEnd('/')}/api/machines/{_machineId}/policy";

            var result = await TryExecuteRemoteCallAsync(
                endpoint,
                async ct =>
                {
                    using var response = await _httpClient.GetAsync(endpoint, ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        return new RemoteManagementResult(
                            false,
                            $"HTTP {(int)response.StatusCode}: {body}",
                            (int)response.StatusCode,
                            null);
                    }

                    var policyJson = await response.Content.ReadAsStringAsync(ct);
                    var configPath = Path.Combine(ServicePaths.Base, "appsettings.json");
                    var currentConfig = File.Exists(configPath)
                        ? await File.ReadAllTextAsync(configPath, ct)
                        : "{}";

                    var updatedConfigJson = MergePolicyIntoConfig(currentConfig, policyJson);
                    var updateResult = await _configurationManager.UpdateConfigurationAsync(updatedConfigJson, ct);

                    if (!updateResult.Success)
                    {
                        return new RemoteManagementResult(false, updateResult.ErrorMessage, (int)response.StatusCode, null);
                    }

                    _logger.LogInformation("Policy updated successfully from remote server");
                    return new RemoteManagementResult(true, null, (int)response.StatusCode, new Dictionary<string, object>
                    {
                        ["UpdatedAt"] = DateTimeOffset.UtcNow,
                        ["BackupId"] = updateResult.Backup?.BackupId ?? string.Empty
                    });
                },
                cancellationToken);

            if (!result.Success && !IsOfflineReplayInProgress)
            {
                await EnqueueOfflineAsync(ct => RequestPolicyUpdateAsync(ct), cancellationToken);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to request policy update");
            await EnqueueOfflineAsync(ct => RequestPolicyUpdateAsync(ct), cancellationToken);
            return new RemoteManagementResult(false, ex.Message, null, null);
        }
    }

    public async Task<RemoteManagementResult> SendLogsAsync(IEnumerable<string> logFiles, CancellationToken cancellationToken)
    {
        try
        {
            if (!_config.Enabled)
            {
                return new RemoteManagementResult(false, "Remote management is disabled", null, null);
            }

            var logFileArray = logFiles?.ToArray() ?? Array.Empty<string>();

            var materializedLogs = await PrepareLogsPayloadAsync(logFileArray, cancellationToken);
            if (materializedLogs.LogCount == 0)
            {
                return new RemoteManagementResult(true, null, 200, new Dictionary<string, object>
                {
                    ["Files"] = 0,
                    ["Skipped"] = true
                });
            }

            var endpoint = $"{_config.ServerEndpoint.TrimEnd('/')}/api/machines/{_machineId}/logs";
            var payload = new
            {
                MachineId = _machineId,
                Timestamp = DateTimeOffset.UtcNow,
                Logs = materializedLogs.Entries,
                TotalBytes = materializedLogs.TotalBytes,
                CompressedBytes = materializedLogs.CompressedBytes
            };

            var result = await TryExecuteRemoteCallAsync(
                endpoint,
                async ct =>
                {
                    var secureResult = await _secureCommunicator.SendTelemetryAsync(endpoint, payload, ct);
                    return ToRemoteManagementResult(endpoint, secureResult, new Dictionary<string, object>
                    {
                        ["Files"] = materializedLogs.LogCount,
                        ["TotalBytes"] = materializedLogs.TotalBytes,
                        ["TransferredBytes"] = materializedLogs.TransferredBytes
                    });
                },
                cancellationToken);

            if (!result.Success && !IsOfflineReplayInProgress)
            {
                await EnqueueOfflineAsync(ct => SendLogsAsync(logFileArray, ct), cancellationToken);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send logs");
            var logFileArray = logFiles?.ToArray() ?? Array.Empty<string>();
            await EnqueueOfflineAsync(ct => SendLogsAsync(logFileArray, ct), cancellationToken);
            return new RemoteManagementResult(false, ex.Message, null, null);
        }
    }

    public async Task<RemoteManagementResult> RequestCommandExecutionAsync(string command, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            if (!_config.Enabled)
            {
                return new RemoteManagementResult(false, "Remote management is disabled", null, null);
            }

            var commandRequest = new
            {
                MachineId = _machineId,
                Command = command,
                Arguments = arguments,
                RequestedAt = DateTimeOffset.UtcNow,
                RequestedBy = "SYSTEM" // セキュリティのためユーザー名を送信しない
            };

            var endpoint = $"{_config.ServerEndpoint.TrimEnd('/')}/api/machines/{_machineId}/execute";
            var result = await TryExecuteRemoteCallAsync(
                endpoint,
                async ct =>
                {
                    var secureResult = await _secureCommunicator.SendTelemetryAsync(endpoint, commandRequest, ct);
                    if (secureResult.Success)
                    {
                        _logger.LogInformation("Remote command execution requested: {Command}", command);
                    }

                    return ToRemoteManagementResult(endpoint, secureResult, new Dictionary<string, object>
                    {
                        ["Command"] = command,
                        ["ArgumentsLength"] = arguments?.Length ?? 0
                    });
                },
                cancellationToken);

            if (!result.Success && !IsOfflineReplayInProgress)
            {
                await EnqueueOfflineAsync(ct => RequestCommandExecutionAsync(command, arguments, ct), cancellationToken);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to request command execution");
            return new RemoteManagementResult(false, ex.Message, null, null);
        }
    }

    private void ValidateConfiguration(RemoteManagementConfig config)
    {
        if (!config.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ServerEndpoint))
        {
            throw new InvalidOperationException("Remote management requires ServerEndpoint to be configured.");
        }

        if (!Uri.TryCreate(config.ServerEndpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Remote management requires a valid HTTPS endpoint.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Remote management endpoints must not embed credentials.");
        }

        if (!NetworkSecurityGuard.TryNormalizeHost(uri.Host, out var normalizedHost, out var isDnsName))
        {
            throw new InvalidOperationException("Remote management endpoint host is invalid.");
        }

        if (normalizedHost.Length == 0 || normalizedHost.Length > 253)
        {
            throw new InvalidOperationException("Remote management endpoint host length is invalid.");
        }

        if (isDnsName && !NetworkSecurityGuard.HasValidDomainStructure(normalizedHost))
        {
            throw new InvalidOperationException("Remote management endpoint domain is malformed.");
        }

        if (NetworkSecurityGuard.IsHostRestricted(normalizedHost, isDnsName))
        {
            throw new InvalidOperationException("Remote management endpoint host is not permitted.");
        }

        if (!uri.IsDefaultPort)
        {
            var isDangerousPort = false;
            if (!NetworkSecurityGuard.IsPortNumberAllowed(uri.Port, out isDangerousPort) || isDangerousPort)
            {
                throw new InvalidOperationException("Remote management endpoint port is not permitted.");
            }
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey) || config.ApiKey.Length < ApiKeyMinimumLength)
        {
            throw new InvalidOperationException($"API key must be at least {ApiKeyMinimumLength} characters.");
        }
    }

    private async Task<RemoteManagementResult> TryExecuteRemoteCallAsync(
        string endpoint,
        Func<CancellationToken, Task<RemoteManagementResult>> operation,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration(_config);

        for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            try
            {
                var result = await operation(cancellationToken);
                if (result.Success)
                {
                    await ProcessOfflineQueueAsync(cancellationToken);
                }
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Remote call to {Endpoint} timed out on attempt {Attempt}", endpoint, attempt);
            }
            catch (Exception ex) when (attempt < MaxRetryCount)
            {
                _logger.LogWarning(ex, "Remote call to {Endpoint} failed on attempt {Attempt}. Retrying.", endpoint, attempt);
            }

            if (attempt < MaxRetryCount)
            {
                var delay = TimeSpan.FromMilliseconds(RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogError("Remote call to {Endpoint} exhausted {RetryCount} attempts", endpoint, MaxRetryCount);
        return new RemoteManagementResult(false, $"Failed to invoke {endpoint} after {MaxRetryCount} attempts", null, null);
    }

    private async Task EnqueueOfflineAsync(Func<CancellationToken, Task<RemoteManagementResult>> action, CancellationToken cancellationToken)
    {
        await _queueSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_offlineQueue.Count >= MaxOfflineQueueLength)
            {
                var dropped = _offlineQueue.Dequeue();
                _logger.LogWarning("Offline queue capacity reached. Dropping oldest action {Action}", dropped.Name);
            }

            _offlineQueue.Enqueue(new QueuedRemoteAction(
                Name: action.Method.Name ?? "RemoteAction",
                Action: action,
                EnqueuedAt: DateTimeOffset.UtcNow));

            _logger.LogInformation("Queued remote management action for retry. Queue length={Length}", _offlineQueue.Count);
        }
        finally
        {
            _queueSemaphore.Release();
        }
    }

    private async Task ProcessOfflineQueueAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isProcessingOfflineQueue, 1, 0) != 0)
        {
            return;
        }
        try
        {
            await _queueSemaphore.WaitAsync(cancellationToken);
            try
            {
                while (_offlineQueue.Count > 0)
                {
                    var queued = _offlineQueue.Peek();
                    _logger.LogInformation("Replaying queued action {Action} (queued at {EnqueuedAt})", queued.Name, queued.EnqueuedAt);
                    var result = await queued.Action(cancellationToken);
                    if (!result.Success)
                    {
                        _logger.LogWarning("Queued action {Action} failed again. Stopping replay.", queued.Name);
                        break;
                    }

                    _offlineQueue.Dequeue();
                }
            }
            finally
            {
                _queueSemaphore.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessingOfflineQueue, 0);
        }
    }

    private async Task<PreparedLogs> PrepareLogsPayloadAsync(IEnumerable<string> logFiles, CancellationToken cancellationToken)
    {
        var entries = new List<object>();
        long totalBytes = 0;
        long transferredBytes = 0;

        foreach (var file in logFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file))
            {
                _logger.LogWarning("Log file {File} does not exist", file);
                continue;
            }

            var info = new FileInfo(file);
            totalBytes += info.Length;

            if (info.Length > MaxLogFileBytes)
            {
                _logger.LogWarning("Log file {File} exceeds maximum allowed size {Limit} bytes. Truncating.", file, MaxLogFileBytes);
            }

            await using var stream = File.OpenRead(file);
            using var limitedStream = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long remaining = MaxLogFileBytes;
                int read;
                while (remaining > 0 && (read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), cancellationToken)) > 0)
                {
                    await limitedStream.WriteAsync(buffer, 0, read, cancellationToken);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            byte[] payload;
            string encoding;

            if (limitedStream.Length > 128 * 1024)
            {
                payload = CompressToGzip(limitedStream.ToArray());
                transferredBytes += payload.Length;
                encoding = "gzip";
            }
            else
            {
                payload = limitedStream.ToArray();
                transferredBytes += payload.Length;
                encoding = "plain";
            }

            entries.Add(new
            {
                FileName = info.Name,
                SizeBytes = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc,
                PayloadEncoding = encoding,
                Content = Convert.ToBase64String(payload)
            });
        }

        return new PreparedLogs(entries, entries.Count, totalBytes, transferredBytes);
    }

    private static byte[] CompressToGzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static RemoteManagementResult ToRemoteManagementResult(string endpoint, SecureCommunicationResult secureResult, Dictionary<string, object> metadata)
    {
        return new RemoteManagementResult(
            secureResult.Success,
            secureResult.ErrorMessage,
            secureResult.HttpStatusCode,
            metadata);
    }

    private bool IsOfflineReplayInProgress => Volatile.Read(ref _isProcessingOfflineQueue) == 1;

    private sealed record QueuedRemoteAction(string Name, Func<CancellationToken, Task<RemoteManagementResult>> Action, DateTimeOffset EnqueuedAt);

    private sealed record PreparedLogs(IReadOnlyList<object> Entries, int LogCount, long TotalBytes, long TransferredBytes);

    private string MergePolicyIntoConfig(string currentConfig, string policyJson)
    {
        try
        {
            using var currentDoc = JsonDocument.Parse(currentConfig);
            using var policyDoc = JsonDocument.Parse(policyJson);

            // 現在の設定を基盤として、ポリシーをマージ
            var merged = JsonMergePatch.Merge(currentDoc.RootElement, policyDoc.RootElement);

            return JsonSerializer.Serialize(merged, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to merge policy into configuration");
            return currentConfig; // エラー時は現在の設定を維持
        }
    }

    private static string GenerateMachineId()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[16];
        rng.GetBytes(bytes);
        return new Guid(bytes).ToString();
    }

    private static DateTimeOffset GetLastBootTime()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var bootTime = System.Management.ManagementDateTimeConverter.ToDateTime(obj["LastBootUpTime"].ToString());
                return new DateTimeOffset(bootTime);
            }
        }
        catch (Exception ex)
        {
            // フォールバック：現在のプロセス開始時間を使用
        }

        return DateTimeOffset.UtcNow.AddHours(-1); // デフォルト値
    }

    private void ConfigureHttpClient(RemoteManagementConfig config)
    {
        _httpClient.DefaultRequestHeaders.Clear();

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Otedama-Remote-Client");
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }
}

// JSON Merge Patch ユーティリティ
public static class JsonMergePatch
{
    public static JsonElement Merge(JsonElement target, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            return patch; // パッチがオブジェクトでない場合はそのまま返す
        }

        var result = new Dictionary<string, JsonElement>();

        // ターゲットのプロパティをコピー
        foreach (var property in target.EnumerateObject())
        {
            if (patch.TryGetProperty(property.Name, out var patchValue))
            {
                if (patchValue.ValueKind == JsonValueKind.Null)
                {
                    // null値はプロパティを削除
                    continue;
                }
                else
                {
                    result[property.Name] = Merge(property.Value, patchValue);
                }
            }
            else
            {
                result[property.Name] = property.Value;
            }
        }

        // パッチにのみ存在するプロパティを追加
        foreach (var property in patch.EnumerateObject())
        {
            if (!target.TryGetProperty(property.Name, out _))
            {
                result[property.Name] = property.Value;
            }
        }

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        foreach (var (key, value) in result)
        {
            writer.WritePropertyName(key);
            WriteJsonElement(writer, value);
        }
        writer.WriteEndObject();

        writer.Flush();
        stream.Position = 0;

        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static void WriteJsonElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteJsonElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteJsonElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteNumberValue(element.GetInt64());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
        }
    }
}
