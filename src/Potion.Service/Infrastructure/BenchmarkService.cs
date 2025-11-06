using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class BenchmarkOptions
{
    public bool Enabled { get; set; } = false;
    public int BenchmarkIntervalHours { get; set; } = 168; // Weekly
    public string BenchmarkDirectory { get; set; } = "benchmarks";
    public List<string> BenchmarkTypes { get; set; } = new() { "CPU", "Memory", "Disk", "Network" };
}

public class BenchmarkService : IHostedService, IDisposable
{
    private readonly ILogger<BenchmarkService> _logger;
    private readonly BenchmarkOptions _options;
    private readonly ISystemHealthMonitor _healthMonitor;
    private Timer? _benchmarkTimer;

    public BenchmarkService(
        ILogger<BenchmarkService> logger,
        IOptions<BenchmarkOptions> options,
        ISystemHealthMonitor healthMonitor)
    {
        _logger = logger;
        _options = options.Value;
        _healthMonitor = healthMonitor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Benchmark service is disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting benchmark service");

        _benchmarkTimer = new Timer(RunBenchmarks, null, TimeSpan.FromHours(1), // Start after 1 hour
            TimeSpan.FromHours(_options.BenchmarkIntervalHours));

        return Task.CompletedTask;
    }

    private async void RunBenchmarks(object? state)
    {
        try
        {
            var results = new List<BenchmarkResult>();

            foreach (var benchmarkType in _options.BenchmarkTypes)
            {
                var result = await RunBenchmarkAsync(benchmarkType);
                results.Add(result);
            }

            var report = new BenchmarkReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Results = results,
                SystemInfo = await GetSystemInfoAsync()
            };

            await SaveBenchmarkReportAsync(report);
            _logger.LogInformation("Benchmark completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run benchmarks");
        }
    }

    private async Task<BenchmarkResult> RunBenchmarkAsync(string benchmarkType)
    {
        var result = new BenchmarkResult
        {
            Type = benchmarkType,
            StartTime = DateTimeOffset.UtcNow,
            Metrics = new Dictionary<string, double>()
        };

        try
        {
            switch (benchmarkType.ToUpper())
            {
                case "CPU":
                    result = await RunCpuBenchmarkAsync(result);
                    break;
                case "MEMORY":
                    result = await RunMemoryBenchmarkAsync(result);
                    break;
                case "DISK":
                    result = await RunDiskBenchmarkAsync(result);
                    break;
                case "NETWORK":
                    result = await RunNetworkBenchmarkAsync(result);
                    break;
                default:
                    _logger.LogWarning("Unknown benchmark type: {Type}", benchmarkType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run {Type} benchmark", benchmarkType);
            result.Error = ex.Message;
        }

        result.EndTime = DateTimeOffset.UtcNow;
        result.Duration = result.EndTime - result.StartTime;

        return result;
    }

    private async Task<BenchmarkResult> RunCpuBenchmarkAsync(BenchmarkResult result)
    {
        // CPU benchmark using matrix multiplication
        const int size = 500;
        var matrixA = GenerateMatrix(size);
        var matrixB = GenerateMatrix(size);

        var stopwatch = Stopwatch.StartNew();
        var resultMatrix = MultiplyMatrices(matrixA, matrixB);
        stopwatch.Stop();

        result.Metrics["operations_per_second"] = (size * size * size) / stopwatch.Elapsed.TotalSeconds;
        result.Metrics["elapsed_ms"] = stopwatch.Elapsed.TotalMilliseconds;
        result.Score = (long)result.Metrics["operations_per_second"];

        return result;
    }

    private async Task<BenchmarkResult> RunMemoryBenchmarkAsync(BenchmarkResult result)
    {
        // Memory benchmark using allocation and access patterns
        const int arraySize = 1000000;
        const int iterations = 100;

        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            var array = new long[arraySize];
            for (int j = 0; j < arraySize; j++)
            {
                array[j] = j;
            }
            // Access pattern
            long sum = 0;
            for (int j = 0; j < arraySize; j += 100) // Strided access
            {
                sum += array[j];
            }
        }

        stopwatch.Stop();

        result.Metrics["allocations_per_second"] = iterations / stopwatch.Elapsed.TotalSeconds;
        result.Metrics["elapsed_ms"] = stopwatch.Elapsed.TotalMilliseconds;
        result.Score = (long)(result.Metrics["allocations_per_second"] * 1000);

        return result;
    }

    private async Task<BenchmarkResult> RunDiskBenchmarkAsync(BenchmarkResult result)
    {
        // Disk I/O benchmark
        var tempFile = Path.GetTempFileName();
        const int bufferSize = 1024 * 1024; // 1MB
        var buffer = new byte[bufferSize];
        var random = new Random();

        // Write benchmark
        var stopwatch = Stopwatch.StartNew();
        using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
        {
            for (int i = 0; i < 100; i++)
            {
                random.NextBytes(buffer);
                await fileStream.WriteAsync(buffer);
            }
        }
        stopwatch.Stop();

        var writeTime = stopwatch.Elapsed.TotalSeconds;

        // Read benchmark
        stopwatch.Restart();
        using (var fileStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize))
        {
            for (int i = 0; i < 100; i++)
            {
                await fileStream.ReadAsync(buffer);
            }
        }
        stopwatch.Stop();

        var readTime = stopwatch.Elapsed.TotalSeconds;

        File.Delete(tempFile);

        result.Metrics["write_mb_per_second"] = (100.0 * bufferSize / (1024 * 1024)) / writeTime;
        result.Metrics["read_mb_per_second"] = (100.0 * bufferSize / (1024 * 1024)) / readTime;
        result.Metrics["total_io_time_seconds"] = writeTime + readTime;
        result.Score = (long)((result.Metrics["write_mb_per_second"] + result.Metrics["read_mb_per_second"]) * 100);

        return result;
    }

    private async Task<BenchmarkResult> RunNetworkBenchmarkAsync(BenchmarkResult result)
    {
        // Network benchmark using local loopback
        const int packetSize = 64 * 1024; // 64KB
        const int iterations = 100;
        var buffer = new byte[packetSize];

        var stopwatch = Stopwatch.StartNew();

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", 80); // Try to connect to localhost HTTP

        using var stream = client.GetStream();
        for (int i = 0; i < iterations; i++)
        {
            await stream.WriteAsync(buffer);
            var readBuffer = new byte[packetSize];
            var bytesRead = await stream.ReadAsync(readBuffer);
        }

        stopwatch.Stop();

        result.Metrics["packets_per_second"] = iterations / stopwatch.Elapsed.TotalSeconds;
        result.Metrics["throughput_mbps"] = (iterations * packetSize * 8) / (stopwatch.Elapsed.TotalSeconds * 1024 * 1024);
        result.Metrics["elapsed_ms"] = stopwatch.Elapsed.TotalMilliseconds;
        result.Score = (long)(result.Metrics["throughput_mbps"] * 100);

        return result;
    }

    private double[][] GenerateMatrix(int size)
    {
        var matrix = new double[size][];
        var random = new Random();
        for (int i = 0; i < size; i++)
        {
            matrix[i] = new double[size];
            for (int j = 0; j < size; j++)
            {
                matrix[i][j] = random.NextDouble();
            }
        }
        return matrix;
    }

    private double[][] MultiplyMatrices(double[][] a, double[][] b)
    {
        int size = a.Length;
        var result = new double[size][];

        for (int i = 0; i < size; i++)
        {
            result[i] = new double[size];
            for (int j = 0; j < size; j++)
            {
                for (int k = 0; k < size; k++)
                {
                    result[i][j] += a[i][k] * b[k][j];
                }
            }
        }

        return result;
    }

    private async Task<SystemInfo> GetSystemInfoAsync()
    {
        var healthSnapshot = await _healthMonitor.GetCurrentHealthAsync(CancellationToken.None);

        return new SystemInfo
        {
            OsVersion = Environment.OSVersion.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            TotalMemoryGB = healthSnapshot.Metrics.Memory.TotalBytes / (1024.0 * 1024 * 1024),
            Hostname = Environment.MachineName
        };
    }

    private async Task SaveBenchmarkReportAsync(BenchmarkReport report)
    {
        var reportDir = Path.Combine(ServicePaths.Base, _options.BenchmarkDirectory);
        Directory.CreateDirectory(reportDir);

        var fileName = $"benchmark_report_{report.GeneratedAt:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(reportDir, fileName);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);
        _logger.LogInformation("Benchmark report saved to {Path}", filePath);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _benchmarkTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _benchmarkTimer?.Dispose();
    }
}

public class BenchmarkReport
{
    public DateTimeOffset GeneratedAt { get; set; }
    public SystemInfo SystemInfo { get; set; } = new();
    public List<BenchmarkResult> Results { get; set; } = new();
}

public class BenchmarkResult
{
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new();
    public long Score { get; set; }
    public string? Error { get; set; }
}

public class SystemInfo
{
    public string OsVersion { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public double TotalMemoryGB { get; set; }
    public string Hostname { get; set; } = string.Empty;
}
