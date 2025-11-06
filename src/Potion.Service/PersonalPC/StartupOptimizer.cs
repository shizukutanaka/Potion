using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Potion.Service.PersonalPC;

/// <summary>
/// Windows 起動時のアプリケーション最適化マネージャー
/// </summary>
public interface IStartupOptimizer
{
    Task<StartupAnalysisReport> AnalyzeStartupItemsAsync(CancellationToken cancellationToken = default);
    Task<bool> DisableStartupItemAsync(string itemName, CancellationToken cancellationToken = default);
    Task<bool> EnableStartupItemAsync(string itemName, CancellationToken cancellationToken = default);
    Task<OptimizationResult> OptimizeStartupAsync(CancellationToken cancellationToken = default);
}

public sealed record StartupItem(
    string Name,
    string Location,
    string Command,
    bool IsEnabled,
    StartupItemImpact EstimatedImpact,
    string Source,
    bool IsMicrosoft);

public enum StartupItemImpact
{
    Low,      // < 100ms
    Medium,   // 100-500ms
    High,     // 500ms-2s
    VeryHigh  // > 2s
}

public sealed record StartupAnalysisReport(
    StartupItem[] AllItems,
    StartupItem[] EnabledItems,
    StartupItem[] HighImpactItems,
    int TotalItemCount,
    int EnabledItemCount,
    TimeSpan EstimatedBootDelay,
    string[] Recommendations);

public sealed record OptimizationResult(
    bool Success,
    int ItemsDisabled,
    TimeSpan EstimatedTimeSaved,
    string[] DisabledItems);

public sealed class StartupOptimizer : IStartupOptimizer
{
    private readonly ILogger<StartupOptimizer> _logger;

    private static readonly string[] SafeToDisable = new[]
    {
        "Adobe", "iTunes", "Spotify", "Discord", "Slack", "OneDrive",
        "Dropbox", "Google", "Steam", "Epic", "Teams"
    };

    private static readonly string[] NeverDisable = new[]
    {
        "Windows Defender", "Microsoft", "ctfmon", "explorer",
        "SecurityHealth", "WindowsDefender", "WinLogon"
    };

    public StartupOptimizer(ILogger<StartupOptimizer> logger)
    {
        _logger = logger;
    }

    public async Task<StartupAnalysisReport> AnalyzeStartupItemsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var items = new List<StartupItem>();

            // レジストリからスタートアップアイテムを取得
            items.AddRange(await GetRegistryStartupItemsAsync(cancellationToken));

            // スタートアップフォルダからアイテムを取得
            items.AddRange(await GetStartupFolderItemsAsync(cancellationToken));

            // タスクスケジューラからスタートアップタスクを取得
            items.AddRange(await GetScheduledStartupTasksAsync(cancellationToken));

            var enabledItems = items.Where(i => i.IsEnabled).ToArray();
            var highImpactItems = items.Where(i => i.EstimatedImpact >= StartupItemImpact.High).ToArray();

            var estimatedDelay = TimeSpan.FromMilliseconds(
                enabledItems.Sum(i => EstimateImpactMilliseconds(i.EstimatedImpact)));

            var recommendations = GenerateRecommendations(items);

            return new StartupAnalysisReport(
                items.ToArray(),
                enabledItems,
                highImpactItems,
                items.Count,
                enabledItems.Length,
                estimatedDelay,
                recommendations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze startup items");
            return new StartupAnalysisReport(
                Array.Empty<StartupItem>(),
                Array.Empty<StartupItem>(),
                Array.Empty<StartupItem>(),
                0, 0,
                TimeSpan.Zero,
                new[] { "分析中にエラーが発生しました: " + ex.Message });
        }
    }

    public async Task<bool> DisableStartupItemAsync(string itemName, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Disabling startup item: {ItemName}", itemName);

            // レジストリから無効化を試みる
            if (await DisableFromRegistryAsync(itemName, cancellationToken))
            {
                return true;
            }

            // タスクスケジューラから無効化を試みる
            if (await DisableScheduledTaskAsync(itemName, cancellationToken))
            {
                return true;
            }

            _logger.LogWarning("Startup item not found: {ItemName}", itemName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable startup item: {ItemName}", itemName);
            return false;
        }
    }

    public async Task<bool> EnableStartupItemAsync(string itemName, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Enabling startup item: {ItemName}", itemName);

            // レジストリから有効化を試みる
            if (await EnableFromRegistryAsync(itemName, cancellationToken))
            {
                return true;
            }

            // タスクスケジューラから有効化を試みる
            if (await EnableScheduledTaskAsync(itemName, cancellationToken))
            {
                return true;
            }

            _logger.LogWarning("Startup item not found: {ItemName}", itemName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable startup item: {ItemName}", itemName);
            return false;
        }
    }

    public async Task<OptimizationResult> OptimizeStartupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Optimizing startup items");

            var analysis = await AnalyzeStartupItemsAsync(cancellationToken);
            var itemsToDisable = analysis.EnabledItems
                .Where(item => IsSafeToDisable(item) && item.EstimatedImpact >= StartupItemImpact.Medium)
                .ToArray();

            int disabledCount = 0;
            var disabledNames = new List<string>();
            TimeSpan timeSaved = TimeSpan.Zero;

            foreach (var item in itemsToDisable)
            {
                if (await DisableStartupItemAsync(item.Name, cancellationToken))
                {
                    disabledCount++;
                    disabledNames.Add(item.Name);
                    timeSaved += TimeSpan.FromMilliseconds(EstimateImpactMilliseconds(item.EstimatedImpact));
                }
            }

            _logger.LogInformation("Startup optimization completed: {Count} items disabled, estimated {TimeSaved}ms saved",
                disabledCount, timeSaved.TotalMilliseconds);

            return new OptimizationResult(
                true,
                disabledCount,
                timeSaved,
                disabledNames.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to optimize startup");
            return new OptimizationResult(false, 0, TimeSpan.Zero, Array.Empty<string>());
        }
    }

    private async Task<List<StartupItem>> GetRegistryStartupItemsAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var items = new List<StartupItem>();

            var registryPaths = new[]
            {
                (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", RegistryHive.CurrentUser, "HKCU"),
                (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", RegistryHive.LocalMachine, "HKLM"),
            };

            foreach (var (path, hive, source) in registryPaths)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(path);

                    if (key == null) continue;

                    foreach (var valueName in key.GetValueNames())
                    {
                        var command = key.GetValue(valueName)?.ToString() ?? "";
                        var isMicrosoft = IsMicrosoftApplication(command);
                        var impact = EstimateStartupImpact(valueName, command);

                        items.Add(new StartupItem(
                            valueName,
                            $@"{source}\{path}",
                            command,
                            true,
                            impact,
                            "Registry",
                            isMicrosoft));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read registry path: {Path}", path);
                }
            }

            return items;
        }, cancellationToken);
    }

    private async Task<List<StartupItem>> GetStartupFolderItemsAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var items = new List<StartupItem>();

            var startupFolders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup"
            };

            foreach (var folder in startupFolders)
            {
                try
                {
                    if (!System.IO.Directory.Exists(folder)) continue;

                    var files = System.IO.Directory.GetFiles(folder, "*.*");
                    foreach (var file in files)
                    {
                        var name = System.IO.Path.GetFileNameWithoutExtension(file);
                        var isMicrosoft = IsMicrosoftApplication(file);
                        var impact = EstimateStartupImpact(name, file);

                        items.Add(new StartupItem(
                            name,
                            folder,
                            file,
                            true,
                            impact,
                            "StartupFolder",
                            isMicrosoft));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read startup folder: {Folder}", folder);
                }
            }

            return items;
        }, cancellationToken);
    }

    private async Task<List<StartupItem>> GetScheduledStartupTasksAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var items = new List<StartupItem>();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\TaskScheduler",
                    "SELECT * FROM MSFT_ScheduledTask WHERE State = 3"); // State 3 = Ready

                foreach (ManagementObject task in searcher.Get())
                {
                    var taskName = task["TaskName"]?.ToString() ?? "";
                    var taskPath = task["TaskPath"]?.ToString() ?? "";

                    // ログオン時に実行されるタスクのみ
                    var triggers = task["Triggers"] as ManagementBaseObject[];
                    if (triggers == null || !triggers.Any()) continue;

                    var isMicrosoft = taskPath.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
                    var impact = EstimateStartupImpact(taskName, taskPath);

                    items.Add(new StartupItem(
                        taskName,
                        taskPath,
                        "",
                        true,
                        impact,
                        "TaskScheduler",
                        isMicrosoft));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read scheduled tasks");
            }

            return items;
        }, cancellationToken);
    }

    private static bool IsMicrosoftApplication(string path)
    {
        return NeverDisable.Any(keyword => path.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeToDisable(StartupItem item)
    {
        if (item.IsMicrosoft || NeverDisable.Any(keyword => item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return SafeToDisable.Any(keyword => item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static StartupItemImpact EstimateStartupImpact(string name, string command)
    {
        // 簡易的な影響度推定
        var lowImpactKeywords = new[] { "tray", "helper", "notify", "update" };
        var highImpactKeywords = new[] { "sync", "cloud", "backup", "antivirus" };

        var lowerName = name.ToLowerInvariant();
        var lowerCommand = command.ToLowerInvariant();

        if (highImpactKeywords.Any(k => lowerName.Contains(k) || lowerCommand.Contains(k)))
        {
            return StartupItemImpact.High;
        }

        if (lowImpactKeywords.Any(k => lowerName.Contains(k) || lowerCommand.Contains(k)))
        {
            return StartupItemImpact.Low;
        }

        return StartupItemImpact.Medium;
    }

    private static int EstimateImpactMilliseconds(StartupItemImpact impact)
    {
        return impact switch
        {
            StartupItemImpact.Low => 50,
            StartupItemImpact.Medium => 300,
            StartupItemImpact.High => 1000,
            StartupItemImpact.VeryHigh => 3000,
            _ => 100
        };
    }

    private static string[] GenerateRecommendations(List<StartupItem> items)
    {
        var recommendations = new List<string>();

        var enabledCount = items.Count(i => i.IsEnabled);
        if (enabledCount > 15)
        {
            recommendations.Add($"{enabledCount}個のスタートアップアイテムが有効です。10個以下に削減することを推奨します。");
        }

        var highImpactEnabled = items.Count(i => i.IsEnabled && i.EstimatedImpact >= StartupItemImpact.High);
        if (highImpactEnabled > 0)
        {
            recommendations.Add($"{highImpactEnabled}個の高負荷スタートアップアイテムがあります。無効化を検討してください。");
        }

        var safeToDisableCount = items.Count(i => i.IsEnabled && IsSafeToDisable(i));
        if (safeToDisableCount > 0)
        {
            recommendations.Add($"{safeToDisableCount}個のアイテムは安全に無効化できます。");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("スタートアップは最適化されています。");
        }

        return recommendations.ToArray();
    }

    private async Task<bool> DisableFromRegistryAsync(string itemName, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                var registryPaths = new[]
                {
                    (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", RegistryHive.CurrentUser),
                    (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", RegistryHive.LocalMachine),
                };

                foreach (var (path, hive) in registryPaths)
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(path, writable: true);

                    if (key == null) continue;

                    if (key.GetValueNames().Contains(itemName))
                    {
                        key.DeleteValue(itemName);
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }, cancellationToken);
    }

    private async Task<bool> EnableFromRegistryAsync(string itemName, CancellationToken cancellationToken)
    {
        // レジストリから削除されたアイテムは元に戻せないため、false を返す
        return await Task.FromResult(false);
    }

    private async Task<bool> DisableScheduledTaskAsync(string taskName, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Change /TN \"{taskName}\" /DISABLE",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }, cancellationToken);
    }

    private async Task<bool> EnableScheduledTaskAsync(string taskName, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Change /TN \"{taskName}\" /ENABLE",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }, cancellationToken);
    }
}
