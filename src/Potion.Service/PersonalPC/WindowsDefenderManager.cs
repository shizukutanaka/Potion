using System;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.PersonalPC;

/// <summary>
/// Windows Defender の管理と最適化を行うマネージャー
/// </summary>
public interface IWindowsDefenderManager
{
    Task<DefenderStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<bool> UpdateDefinitionsAsync(CancellationToken cancellationToken = default);
    Task<bool> RunQuickScanAsync(CancellationToken cancellationToken = default);
    Task<bool> RunFullScanAsync(CancellationToken cancellationToken = default);
    Task<DefenderHealthReport> GenerateHealthReportAsync(CancellationToken cancellationToken = default);
}

public sealed record DefenderStatus(
    bool IsEnabled,
    bool RealTimeProtectionEnabled,
    bool BehaviorMonitorEnabled,
    bool CloudDeliveryEnabled,
    DateTimeOffset? LastQuickScan,
    DateTimeOffset? LastFullScan,
    DateTimeOffset? DefinitionUpdated,
    string DefinitionVersion,
    string EngineVersion,
    int ThreatDetectionCount);

public sealed record DefenderHealthReport(
    DefenderStatus Status,
    string OverallHealth,
    string[] Recommendations,
    string[] Warnings);

public sealed class WindowsDefenderManager : IWindowsDefenderManager
{
    private readonly ILogger<WindowsDefenderManager> _logger;
    private const string DefenderNamespace = @"root\Microsoft\Windows\Defender";

    public WindowsDefenderManager(ILogger<WindowsDefenderManager> logger)
    {
        _logger = logger;
    }

    public async Task<DefenderStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(DefenderNamespace, "SELECT * FROM MSFT_MpComputerStatus");
                using var results = searcher.Get();

                foreach (ManagementObject obj in results)
                {
                    var isEnabled = GetBoolValue(obj, "AntivirusEnabled");
                    var rtpEnabled = GetBoolValue(obj, "RealTimeProtectionEnabled");
                    var behaviorEnabled = GetBoolValue(obj, "BehaviorMonitorEnabled");
                    var cloudEnabled = GetBoolValue(obj, "IoavProtectionEnabled");

                    var lastQuickScan = GetDateTimeValue(obj, "QuickScanEndTime");
                    var lastFullScan = GetDateTimeValue(obj, "FullScanEndTime");
                    var definitionUpdated = GetDateTimeValue(obj, "AntivirusSignatureLastUpdated");

                    var definitionVersion = GetStringValue(obj, "AntivirusSignatureVersion");
                    var engineVersion = GetStringValue(obj, "AMEngineVersion");

                    // 脅威検出数を取得
                    var threatCount = GetThreatCount();

                    return new DefenderStatus(
                        isEnabled,
                        rtpEnabled,
                        behaviorEnabled,
                        cloudEnabled,
                        lastQuickScan,
                        lastFullScan,
                        definitionUpdated,
                        definitionVersion,
                        engineVersion,
                        threatCount);
                }

                return new DefenderStatus(false, false, false, false, null, null, null, "Unknown", "Unknown", 0);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Windows Defender status");
            return new DefenderStatus(false, false, false, false, null, null, null, "Error", "Error", 0);
        }
    }

    public async Task<bool> UpdateDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Updating Windows Defender definitions");

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Update-MpSignature\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start definition update process");
                return false;
            }

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Windows Defender definitions updated successfully");
                return true;
            }

            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            _logger.LogWarning("Definition update completed with warnings: {Error}", error);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Windows Defender definitions");
            return false;
        }
    }

    public async Task<bool> RunQuickScanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting Windows Defender quick scan");

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Start-MpScan -ScanType QuickScan\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start quick scan process");
                return false;
            }

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Windows Defender quick scan completed successfully");
                return true;
            }

            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            _logger.LogWarning("Quick scan completed with warnings: {Error}", error);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Windows Defender quick scan");
            return false;
        }
    }

    public async Task<bool> RunFullScanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting Windows Defender full scan");

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Start-MpScan -ScanType FullScan\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start full scan process");
                return false;
            }

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Windows Defender full scan completed successfully");
                return true;
            }

            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            _logger.LogWarning("Full scan completed with warnings: {Error}", error);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Windows Defender full scan");
            return false;
        }
    }

    public async Task<DefenderHealthReport> GenerateHealthReportAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        var recommendations = new List<string>();
        var warnings = new List<string>();
        var overallHealth = "Healthy";

        // リアルタイム保護チェック
        if (!status.RealTimeProtectionEnabled)
        {
            warnings.Add("リアルタイム保護が無効になっています");
            overallHealth = "Warning";
        }

        // クラウド配信保護チェック
        if (!status.CloudDeliveryEnabled)
        {
            recommendations.Add("クラウド配信保護を有効にすることを推奨します");
        }

        // 定義更新チェック
        if (status.DefinitionUpdated.HasValue)
        {
            var daysSinceUpdate = (DateTimeOffset.UtcNow - status.DefinitionUpdated.Value).TotalDays;
            if (daysSinceUpdate > 7)
            {
                warnings.Add($"定義ファイルが{daysSinceUpdate:F0}日間更新されていません");
                overallHealth = "Critical";
            }
            else if (daysSinceUpdate > 3)
            {
                recommendations.Add("定義ファイルの更新を推奨します");
            }
        }

        // スキャンチェック
        if (status.LastQuickScan.HasValue)
        {
            var daysSinceScan = (DateTimeOffset.UtcNow - status.LastQuickScan.Value).TotalDays;
            if (daysSinceScan > 7)
            {
                recommendations.Add("クイックスキャンの実行を推奨します");
            }
        }
        else
        {
            recommendations.Add("クイックスキャンを実行してください");
        }

        if (status.LastFullScan.HasValue)
        {
            var daysSinceScan = (DateTimeOffset.UtcNow - status.LastFullScan.Value).TotalDays;
            if (daysSinceScan > 30)
            {
                recommendations.Add("フルスキャンの実行を推奨します（最終実行から30日以上経過）");
            }
        }
        else
        {
            recommendations.Add("定期的なフルスキャンの設定を推奨します");
        }

        // 脅威検出チェック
        if (status.ThreatDetectionCount > 0)
        {
            warnings.Add($"{status.ThreatDetectionCount}件の脅威が検出されています");
            overallHealth = "Critical";
        }

        return new DefenderHealthReport(
            status,
            overallHealth,
            recommendations.ToArray(),
            warnings.ToArray());
    }

    private static bool GetBoolValue(ManagementObject obj, string propertyName)
    {
        try
        {
            var value = obj[propertyName];
            return value != null && Convert.ToBoolean(value);
        }
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset? GetDateTimeValue(ManagementObject obj, string propertyName)
    {
        try
        {
            var value = obj[propertyName];
            if (value == null) return null;

            if (value is DateTime dt)
            {
                return new DateTimeOffset(dt);
            }

            if (DateTime.TryParse(value.ToString(), out var parsed))
            {
                return new DateTimeOffset(parsed);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetStringValue(ManagementObject obj, string propertyName)
    {
        try
        {
            var value = obj[propertyName];
            return value?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private int GetThreatCount()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(DefenderNamespace, "SELECT * FROM MSFT_MpThreat");
            using var results = searcher.Get();
            return results.Count;
        }
        catch
        {
            return 0;
        }
    }
}
