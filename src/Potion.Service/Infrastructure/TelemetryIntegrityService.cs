using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface ITelemetryIntegrityService
{
    Task WriteDigestAsync(string telemetryPath, CancellationToken cancellationToken);
}

public sealed class TelemetryIntegrityService(ILogger<TelemetryIntegrityService> logger) : ITelemetryIntegrityService
{
    public async Task WriteDigestAsync(string telemetryPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(telemetryPath))
        {
            throw new ArgumentException("Telemetry path must be provided", nameof(telemetryPath));
        }

        if (!File.Exists(telemetryPath))
        {
            throw new FileNotFoundException("Telemetry file not found", telemetryPath);
        }

        var digestPath = ServicePaths.GetTelemetryDigestPath(telemetryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(digestPath)!);

        await using var stream = File.OpenRead(telemetryPath);
        var hash = await ComputeSha256Async(stream, cancellationToken);
        var payload = $"SHA256:{hash}";

        await File.WriteAllTextAsync(digestPath, payload, Encoding.UTF8, cancellationToken);
        logger.LogDebug("Wrote telemetry digest for {TelemetryPath}", telemetryPath);
    }

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
