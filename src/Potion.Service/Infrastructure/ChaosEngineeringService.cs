using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

/// <summary>
/// カオスエンジニアリングサービス
/// 制御された障害注入によるレジリエンステスト
/// </summary>
public interface IChaosEngineeringService
{
    Task<ChaosExperiment> StartExperimentAsync(ChaosExperimentDefinition definition);
    Task StopExperimentAsync(string experimentId);
    Task<IEnumerable<ChaosExperiment>> GetActiveExperimentsAsync();
    Task<ChaosExperimentResult> GetExperimentResultAsync(string experimentId);
    Task<IEnumerable<ChaosExperimentResult>> GetAllExperimentResultsAsync();
    Task<bool> IsExperimentRunningAsync(string experimentId);
}

/// <summary>
/// カオス実験定義
/// </summary>
public record ChaosExperimentDefinition(
    string Name,
    string Description,
    ChaosFaultType FaultType,
    TimeSpan Duration,
    double Intensity,
    Dictionary<string, object>? Parameters = null);

/// <summary>
/// カオス障害タイプ
/// </summary>
public enum ChaosFaultType
{
    NetworkLatency,
    NetworkPartition,
    CpuStress,
    MemoryStress,
    DiskStress,
    ServiceKill,
    ConfigurationChange,
    DependencyFailure
}

/// <summary>
/// カオス実験状態
/// </summary>
public enum ChaosExperimentState
{
    Pending,
    Running,
    Completed,
    Failed,
    Stopped
}

/// <summary>
/// カオス実験
/// </summary>
public class ChaosExperiment
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ChaosFaultType FaultType { get; set; }
    public ChaosExperimentState State { get; set; } = ChaosExperimentState.Pending;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public double Intensity { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
    public List<ChaosEvent> Events { get; set; } = new();
}

/// <summary>
/// カオスイベント
/// </summary>
public record ChaosEvent(
    DateTimeOffset Timestamp,
    string EventType,
    string Message,
    Dictionary<string, object>? Data = null);

/// <summary>
/// カオス実験結果
/// </summary>
public class ChaosExperimentResult
{
    public string ExperimentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ChaosExperimentState FinalState { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan ActualDuration { get; set; }
    public int EventCount { get; set; }
    public bool SystemRecovered { get; set; }
    public TimeSpan RecoveryTime { get; set; }
    public List<ChaosEvent> Events { get; set; } = new();
    public Dictionary<string, object> Metrics { get; set; } = new();
}

/// <summary>
/// カオスエンジニアリングサービス実装
/// </summary>
public class ChaosEngineeringService : IChaosEngineeringService
{
    private readonly ILogger<ChaosEngineeringService> _logger;
    private readonly ConcurrentDictionary<string, ChaosExperiment> _activeExperiments = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _experimentTokens = new();
    private readonly List<ChaosExperimentResult> _experimentResults = new();
    private readonly object _resultsLock = new();

    public ChaosEngineeringService(ILogger<ChaosEngineeringService> logger)
    {
        _logger = logger;
    }

    public async Task<ChaosExperiment> StartExperimentAsync(ChaosExperimentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var experiment = new ChaosExperiment
        {
            Name = definition.Name,
            Description = definition.Description,
            FaultType = definition.FaultType,
            Duration = definition.Duration,
            Intensity = definition.Intensity,
            Parameters = definition.Parameters ?? new Dictionary<string, object>(),
            StartedAt = DateTimeOffset.UtcNow,
            State = ChaosExperimentState.Running
        };

        _activeExperiments[experiment.Id] = experiment;

        var cancellationTokenSource = new CancellationTokenSource();
        _experimentTokens[experiment.Id] = cancellationTokenSource;

        // 実験を非同期で実行
        _ = ExecuteExperimentAsync(experiment, cancellationTokenSource.Token);

        _logger.LogInformation("Started chaos experiment: {ExperimentName} ({ExperimentId})", definition.Name, experiment.Id);

        return experiment;
    }

    public async Task StopExperimentAsync(string experimentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);

        if (_experimentTokens.TryGetValue(experimentId, out var tokenSource))
        {
            tokenSource.Cancel();

            if (_activeExperiments.TryGetValue(experimentId, out var experiment))
            {
                experiment.State = ChaosExperimentState.Stopped;
                experiment.CompletedAt = DateTimeOffset.UtcNow;

                await CompleteExperimentAsync(experiment);
            }
        }
    }

    public async Task<IEnumerable<ChaosExperiment>> GetActiveExperimentsAsync()
    {
        return _activeExperiments.Values
            .Where(e => e.State == ChaosExperimentState.Running)
            .ToList();
    }

    public async Task<ChaosExperimentResult> GetExperimentResultAsync(string experimentId)
    {
        lock (_resultsLock)
        {
            return _experimentResults.FirstOrDefault(r => r.ExperimentId == experimentId)
                ?? throw new KeyNotFoundException($"Experiment result not found for ID: {experimentId}");
        }
    }

    public async Task<IEnumerable<ChaosExperimentResult>> GetAllExperimentResultsAsync()
    {
        lock (_resultsLock)
        {
            return _experimentResults.OrderByDescending(r => r.StartedAt).ToList();
        }
    }

    public async Task<bool> IsExperimentRunningAsync(string experimentId)
    {
        return _activeExperiments.TryGetValue(experimentId, out var experiment)
            && experiment.State == ChaosExperimentState.Running;
    }

    private async Task ExecuteExperimentAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        try
        {
            await AddEventAsync(experiment, "ExperimentStarted", "Chaos experiment started");

            var faultInjector = CreateFaultInjector(experiment.FaultType);
            await faultInjector.InjectFaultAsync(experiment, cancellationToken);

            experiment.State = ChaosExperimentState.Completed;
            experiment.CompletedAt = DateTimeOffset.UtcNow;

            await AddEventAsync(experiment, "ExperimentCompleted", "Chaos experiment completed successfully");

            await CompleteExperimentAsync(experiment);
        }
        catch (OperationCanceledException)
        {
            experiment.State = ChaosExperimentState.Stopped;
            experiment.CompletedAt = DateTimeOffset.UtcNow;
            await AddEventAsync(experiment, "ExperimentStopped", "Chaos experiment was stopped");
            await CompleteExperimentAsync(experiment);
        }
        catch (Exception ex)
        {
            experiment.State = ChaosExperimentState.Failed;
            experiment.CompletedAt = DateTimeOffset.UtcNow;
            await AddEventAsync(experiment, "ExperimentFailed", $"Chaos experiment failed: {ex.Message}");
            await CompleteExperimentAsync(experiment);
        }
    }

    private async Task CompleteExperimentAsync(ChaosExperiment experiment)
    {
        _activeExperiments.TryRemove(experiment.Id, out _);
        _experimentTokens.TryRemove(experiment.Id, out _);

        var result = new ChaosExperimentResult
        {
            ExperimentId = experiment.Id,
            Name = experiment.Name,
            FinalState = experiment.State,
            StartedAt = experiment.StartedAt,
            CompletedAt = experiment.CompletedAt,
            ActualDuration = experiment.CompletedAt - experiment.StartedAt ?? TimeSpan.Zero,
            EventCount = experiment.Events.Count,
            Events = experiment.Events.ToList()
        };

        lock (_resultsLock)
        {
            _experimentResults.Add(result);
        }

        _logger.LogInformation("Completed chaos experiment: {ExperimentName} ({ExperimentId}) - State: {State}",
            experiment.Name, experiment.Id, experiment.State);
    }

    private async Task AddEventAsync(ChaosExperiment experiment, string eventType, string message, Dictionary<string, object>? data = null)
    {
        var chaosEvent = new ChaosEvent(DateTimeOffset.UtcNow, eventType, message, data);
        experiment.Events.Add(chaosEvent);

        _logger.LogDebug("Chaos experiment {ExperimentId}: {EventType} - {Message}",
            experiment.Id, eventType, message);
    }

    private IFaultInjector CreateFaultInjector(ChaosFaultType faultType)
    {
        return faultType switch
        {
            ChaosFaultType.NetworkLatency => new NetworkLatencyFaultInjector(_logger),
            ChaosFaultType.CpuStress => new CpuStressFaultInjector(_logger),
            ChaosFaultType.MemoryStress => new MemoryStressFaultInjector(_logger),
            ChaosFaultType.DiskStress => new DiskStressFaultInjector(_logger),
            ChaosFaultType.ServiceKill => new ServiceKillFaultInjector(_logger),
            ChaosFaultType.ConfigurationChange => new ConfigurationChangeFaultInjector(_logger),
            _ => new NoOpFaultInjector(_logger)
        };
    }
}

/// <summary>
/// 障害注入インターフェース
/// </summary>
public interface IFaultInjector
{
    Task InjectFaultAsync(ChaosExperiment experiment, CancellationToken cancellationToken);
}

/// <summary>
/// ネットワーク遅延障害注入
/// </summary>
public class NetworkLatencyFaultInjector : IFaultInjector
{
    private readonly ILogger _logger;

    public NetworkLatencyFaultInjector(ILogger logger)
    {
        _logger = logger;
    }

    public async Task InjectFaultAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        var intensity = experiment.Intensity;
        var duration = experiment.Duration;

        _logger.LogWarning("Injecting network latency fault with intensity {Intensity} for duration {Duration}", intensity, duration);

        // シミュレートされたネットワーク遅延注入
        await Task.Delay(duration, cancellationToken);

        _logger.LogInformation("Network latency fault injection completed");
    }
}

/// <summary>
/// CPUストレス障害注入
/// </summary>
public class CpuStressFaultInjector : IFaultInjector
{
    private readonly ILogger _logger;

    public CpuStressFaultInjector(ILogger logger)
    {
        _logger = logger;
    }

    public async Task InjectFaultAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        var intensity = experiment.Intensity;
        var duration = experiment.Duration;

        _logger.LogWarning("Injecting CPU stress fault with intensity {Intensity} for duration {Duration}", intensity, duration);

        var endTime = DateTimeOffset.UtcNow + duration;
        var tasks = new List<Task>();

        // CPU負荷をかけるタスクを作成
        for (int i = 0; i < Environment.ProcessorCount * intensity; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (DateTimeOffset.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
                {
                    // CPU負荷をかける計算
                    var result = 0.0;
                    for (int j = 0; j < 1000; j++)
                    {
                        result += Math.Sqrt(j);
                    }
                    await Task.Delay(1, cancellationToken);
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        _logger.LogInformation("CPU stress fault injection completed");
    }
}

/// <summary>
/// メモリストレス障害注入
/// </summary>
public class MemoryStressFaultInjector : IFaultInjector
{
    private readonly ILogger _logger;

    public MemoryStressFaultInjector(ILogger logger)
    {
        _logger = logger;
    }

    public async Task InjectFaultAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        var intensity = experiment.Intensity;
        var duration = experiment.Duration;

        _logger.LogWarning("Injecting memory stress fault with intensity {Intensity} for duration {Duration}", intensity, duration);

        var memoryBlocks = new List<byte[]>();
        var blockSize = 1024 * 1024; // 1MB
        var maxBlocks = (int)(intensity * 100);

        try
        {
            for (int i = 0; i < maxBlocks; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var block = new byte[blockSize];
                // メモリを埋める
                for (int j = 0; j < blockSize; j++)
                {
                    block[j] = (byte)(j % 256);
                }
                memoryBlocks.Add(block);

                await Task.Delay(100, cancellationToken);
            }

            await Task.Delay(duration - TimeSpan.FromMilliseconds(maxBlocks * 100), cancellationToken);
        }
        finally
        {
            // メモリを解放
            memoryBlocks.Clear();
            GC.Collect();
        }

        _logger.LogInformation("Memory stress fault injection completed");
    }
}

/// <summary>
/// ディスクストレス障害注入
/// </summary>
public class DiskStressFaultInjector : IFaultInjector
{
    private readonly ILogger _logger;

    public DiskStressFaultInjector(ILogger logger)
    {
        _logger = logger;
    }

    public async Task InjectFaultAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        var intensity = experiment.Intensity;
        var duration = experiment.Duration;

        _logger.LogWarning("Injecting disk stress fault with intensity {Intensity} for duration {Duration}", intensity, duration);

        var tempDir = Path.Combine(Path.GetTempPath(), "chaos-disk-test");
        Directory.CreateDirectory(tempDir);

        try
        {
            var endTime = DateTimeOffset.UtcNow + duration;
            var fileIndex = 0;

            while (DateTimeOffset.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
            {
                var fileName = Path.Combine(tempDir, $"chaos-test-{fileIndex}.tmp");
                var data = new byte[1024 * intensity]; // サイズはintensityに基づく

                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)(i % 256);
                }

                await File.WriteAllBytesAsync(fileName, data, cancellationToken);
                fileIndex++;

                await Task.Delay(10, cancellationToken);
            }
        }
        finally
        {
            // クリーンアップ
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }

        _logger.LogInformation("Disk stress fault injection completed");
    }
}

/// <summary>
/// サービスキル障害注入
/// </summary>
public class ServiceKillFaultInjector : IFaultInjector
{
    private readonly ILogger _logger;

    public ServiceKillFaultInjector(ILogger logger)
    {
        _logger = logger;
    }

    public async Task InjectFaultAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        var duration = experiment.Duration;

        _logger.LogWarning("Injecting service kill fault for duration {Duration}", duration);

        // 実際のサービスキルは危険なので、シミュレートのみ
        await Task.Delay(duration, cancellationToken);

        _logger.LogInformation("Service kill fault injection completed (simulated)");
    }
}

/// <summary>
/// 設定変更障害注入
/// </summary>
public class ConfigurationChangeFaultInjector : IFaultInjector
{
    private readonly ILogger _logger;

    public ConfigurationChangeFaultInjector(ILogger logger)
    {
        _logger = logger;
    }

    public async Task InjectFaultAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        var duration = experiment.Duration;

        _logger.LogWarning("Injecting configuration change fault for duration {Duration}", duration);

        // 設定変更をシミュレート
        await Task.Delay(duration, cancellationToken);

        _logger.LogInformation("Configuration change fault injection completed");
    }
}

/// <summary>
/// 何もしない障害注入（デフォルト）
/// </summary>
public class NoOpFaultInjector : IFaultInjector
{
    private readonly ILogger _logger;

    public NoOpFaultInjector(ILogger logger)
    {
        _logger = logger;
    }

    public async Task InjectFaultAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        await Task.Delay(experiment.Duration, cancellationToken);
        _logger.LogInformation("No-op fault injection completed");
    }
}
