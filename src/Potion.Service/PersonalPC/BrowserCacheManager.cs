using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Potion.Service.PersonalPC;

/// <summary>
/// ブラウザキャッシュの管理とクリーンアップを行うマネージャー
/// </summary>
public interface IBrowserCacheManager
{
    Task<BrowserCacheReport> AnalyzeCacheAsync(CancellationToken cancellationToken = default);
    Task<CleanupResult> CleanupAllBrowsersAsync(CancellationToken cancellationToken = default);
    Task<CleanupResult> CleanupBrowserAsync(BrowserType browser, CancellationToken cancellationToken = default);
}

public enum BrowserType
{
    Chrome,
    Edge,
    Firefox,
    Opera,
    Brave
}

public sealed record BrowserCacheInfo(
    BrowserType Browser,
    bool IsInstalled,
    long CacheSizeBytes,
    int FileCount,
    string[] CachePaths);

public sealed record BrowserCacheReport(
    BrowserCacheInfo[] Browsers,
    long TotalCacheSizeBytes,
    int TotalFileCount,
    DateTimeOffset AnalyzedAt);

public sealed record CleanupResult(
    BrowserType Browser,
    bool Success,
    long BytesFreed,
    int FilesDeleted,
    string? ErrorMessage);

public sealed class BrowserCacheManager : IBrowserCacheManager
{
    private readonly ILogger<BrowserCacheManager> _logger;
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public BrowserCacheManager(ILogger<BrowserCacheManager> logger)
    {
        _logger = logger;
    }

    public async Task<BrowserCacheReport> AnalyzeCacheAsync(CancellationToken cancellationToken = default)
    {
        var browsers = new List<BrowserCacheInfo>();

        foreach (BrowserType browser in Enum.GetValues<BrowserType>())
        {
            var info = await AnalyzeBrowserCacheAsync(browser, cancellationToken);
            browsers.Add(info);
        }

        var totalSize = browsers.Sum(b => b.CacheSizeBytes);
        var totalFiles = browsers.Sum(b => b.FileCount);

        return new BrowserCacheReport(
            browsers.ToArray(),
            totalSize,
            totalFiles,
            DateTimeOffset.UtcNow);
    }

    public async Task<CleanupResult> CleanupAllBrowsersAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<CleanupResult>();

        foreach (BrowserType browser in Enum.GetValues<BrowserType>())
        {
            var result = await CleanupBrowserAsync(browser, cancellationToken);
            results.Add(result);
        }

        var totalBytesFreed = results.Sum(r => r.BytesFreed);
        var totalFilesDeleted = results.Sum(r => r.FilesDeleted);
        var allSuccess = results.All(r => r.Success);

        return new CleanupResult(
            BrowserType.Chrome, // 代表値
            allSuccess,
            totalBytesFreed,
            totalFilesDeleted,
            allSuccess ? null : "一部のブラウザでクリーンアップに失敗しました");
    }

    public async Task<CleanupResult> CleanupBrowserAsync(BrowserType browser, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Cleaning up {Browser} cache", browser);

            var cachePaths = GetBrowserCachePaths(browser);
            long bytesFreed = 0;
            int filesDeleted = 0;

            foreach (var cachePath in cachePaths)
            {
                if (!Directory.Exists(cachePath))
                {
                    continue;
                }

                try
                {
                    var (freed, deleted) = await CleanupDirectoryAsync(cachePath, cancellationToken);
                    bytesFreed += freed;
                    filesDeleted += deleted;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup {Path}", cachePath);
                }
            }

            _logger.LogInformation("Cleaned up {Browser}: {BytesFreed:N0} bytes, {FilesDeleted} files",
                browser, bytesFreed, filesDeleted);

            return new CleanupResult(browser, true, bytesFreed, filesDeleted, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup {Browser} cache", browser);
            return new CleanupResult(browser, false, 0, 0, ex.Message);
        }
    }

    private async Task<BrowserCacheInfo> AnalyzeBrowserCacheAsync(BrowserType browser, CancellationToken cancellationToken)
    {
        try
        {
            var cachePaths = GetBrowserCachePaths(browser);
            var isInstalled = cachePaths.Any(p => Directory.Exists(Path.GetDirectoryName(p)));

            if (!isInstalled)
            {
                return new BrowserCacheInfo(browser, false, 0, 0, Array.Empty<string>());
            }

            long totalSize = 0;
            int totalFiles = 0;

            foreach (var cachePath in cachePaths)
            {
                if (!Directory.Exists(cachePath))
                {
                    continue;
                }

                var (size, count) = await CalculateDirectorySizeAsync(cachePath, cancellationToken);
                totalSize += size;
                totalFiles += count;
            }

            return new BrowserCacheInfo(browser, true, totalSize, totalFiles, cachePaths);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze {Browser} cache", browser);
            return new BrowserCacheInfo(browser, false, 0, 0, Array.Empty<string>());
        }
    }

    private static string[] GetBrowserCachePaths(BrowserType browser)
    {
        return browser switch
        {
            BrowserType.Chrome => new[]
            {
                Path.Combine(LocalAppData, @"Google\Chrome\User Data\Default\Cache"),
                Path.Combine(LocalAppData, @"Google\Chrome\User Data\Default\Code Cache"),
                Path.Combine(LocalAppData, @"Google\Chrome\User Data\Default\GPUCache"),
                Path.Combine(LocalAppData, @"Google\Chrome\User Data\Default\Storage\ext"),
            },
            BrowserType.Edge => new[]
            {
                Path.Combine(LocalAppData, @"Microsoft\Edge\User Data\Default\Cache"),
                Path.Combine(LocalAppData, @"Microsoft\Edge\User Data\Default\Code Cache"),
                Path.Combine(LocalAppData, @"Microsoft\Edge\User Data\Default\GPUCache"),
            },
            BrowserType.Firefox => new[]
            {
                Path.Combine(LocalAppData, @"Mozilla\Firefox\Profiles"),
            },
            BrowserType.Opera => new[]
            {
                Path.Combine(LocalAppData, @"Opera Software\Opera Stable\Cache"),
                Path.Combine(LocalAppData, @"Opera Software\Opera Stable\GPUCache"),
            },
            BrowserType.Brave => new[]
            {
                Path.Combine(LocalAppData, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"),
                Path.Combine(LocalAppData, @"BraveSoftware\Brave-Browser\User Data\Default\Code Cache"),
                Path.Combine(LocalAppData, @"BraveSoftware\Brave-Browser\User Data\Default\GPUCache"),
            },
            _ => Array.Empty<string>()
        };
    }

    private async Task<(long size, int count)> CalculateDirectorySizeAsync(string directoryPath, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                var directory = new DirectoryInfo(directoryPath);
                if (!directory.Exists)
                {
                    return (0, 0);
                }

                long totalSize = 0;
                int fileCount = 0;

                var files = directory.GetFiles("*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        totalSize += file.Length;
                        fileCount++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // スキップ
                    }
                }

                return (totalSize, fileCount);
            }
            catch
            {
                return (0, 0);
            }
        }, cancellationToken);
    }

    private async Task<(long bytesFreed, int filesDeleted)> CleanupDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                var directory = new DirectoryInfo(directoryPath);
                if (!directory.Exists)
                {
                    return (0, 0);
                }

                long bytesFreed = 0;
                int filesDeleted = 0;

                // ファイルの削除
                var files = directory.GetFiles("*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        bytesFreed += size;
                        filesDeleted++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // スキップ
                    }
                    catch (IOException)
                    {
                        // ファイルが使用中の場合はスキップ
                    }
                }

                // 空のディレクトリの削除
                var directories = directory.GetDirectories("*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.FullName.Length);

                foreach (var dir in directories)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        if (!dir.EnumerateFileSystemInfos().Any())
                        {
                            dir.Delete(false);
                        }
                    }
                    catch
                    {
                        // スキップ
                    }
                }

                return (bytesFreed, filesDeleted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup directory: {Path}", directoryPath);
                return (0, 0);
            }
        }, cancellationToken);
    }
}
