using Microsoft.Extensions.Logging;
using System.Net.NetworkInformation;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 5G network optimization service for enhanced telemetry and remote management.
/// Monitors network performance and optimizes for high-speed connectivity.
/// </summary>
public class NetworkOptimizationService : BackgroundService
{
    private readonly ILogger<NetworkOptimizationService> _logger;
    private readonly ITelemetryRetentionService _telemetryService;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastMeasurement;

    public NetworkOptimizationService(
        ILogger<NetworkOptimizationService> logger,
        ITelemetryRetentionService telemetryService)
    {
        _logger = logger;
        _telemetryService = telemetryService;
        _lastMeasurement = DateTime.UtcNow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Network Optimization Service");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Monitor every 30 seconds

                await MonitorAndOptimizeNetwork();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in network optimization cycle");
            }
        }
    }

    private async Task MonitorAndOptimizeNetwork()
    {
        var networkMetrics = GetNetworkMetrics();

        // Calculate bandwidth utilization
        var currentTime = DateTime.UtcNow;
        var timeDiff = currentTime - _lastMeasurement;

        if (_lastMeasurement != DateTime.MinValue && timeDiff.TotalSeconds > 0)
        {
            var bytesReceivedDiff = networkMetrics.BytesReceived - _lastBytesReceived;
            var bytesSentDiff = networkMetrics.BytesSent - _lastBytesSent;

            var receiveRateMbps = (bytesReceivedDiff * 8) / (timeDiff.TotalSeconds * 1000000);
            var sendRateMbps = (bytesSentDiff * 8) / (timeDiff.TotalSeconds * 1000000);

            _logger.LogDebug("Network rates - Receive: {ReceiveMbps:F2} Mbps, Send: {SendMbps:F2} Mbps",
                receiveRateMbps, sendRateMbps);

            // Check if we need to optimize for high-speed performance
            if (receiveRateMbps > 50 || sendRateMbps > 50) // Threshold for optimization
            {
                await OptimizeForHighSpeed();
            }
        }

        _lastBytesReceived = networkMetrics.BytesReceived;
        _lastBytesSent = networkMetrics.BytesSent;
        _lastMeasurement = currentTime;
    }

    private async Task OptimizeForHighSpeed()
    {
        // Optimize telemetry transmission for high-speed networks
        await _telemetryService.OptimizeForHighSpeedAsync();

        _logger.LogInformation("Optimized telemetry for high-speed network");
    }

    private NetworkMetrics GetNetworkMetrics()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .ToArray();

        long totalReceived = 0;
        long totalSent = 0;

        foreach (var ni in interfaces)
        {
            var stats = ni.GetIPv4Statistics();
            totalReceived += stats.BytesReceived;
            totalSent += stats.BytesSent;
        }

        return new NetworkMetrics(totalReceived, totalSent);
    }

    private record NetworkMetrics(long BytesReceived, long BytesSent);
}
