using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// バックアップ処理の進捗情報
/// </summary>
public sealed record BackupProgress(
    string CurrentFile,
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    double ProgressPercent);

/// <summary>
/// 設定ファイルとシステム状態の自動バックアップサービス（並行処理対応）
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// フルバックアップを実行します
    /// </summary>
    Task<BackupResult> CreateFullBackupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 設定ファイルのみのバックアップを実行します
    /// </summary>
    Task<BackupResult> CreateConfigBackupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 古いバックアップファイルをクリーンアップします
    /// </summary>
    Task<int> CleanupOldBackupsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// バックアップファイルの一覧を取得します
    /// </summary>
    Task<IReadOnlyList<BackupFileInfo>> GetBackupFilesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// バックアップ処理の進捗を取得します
    /// </summary>
    IObservable<BackupProgress> GetProgressObservable();
}

public sealed class BackupService : BackgroundService, IBackupService
{
    private readonly ILogger<BackupService> _logger;
    private readonly IOptionsMonitor<BackupOptions> _optionsMonitor;

    private readonly Subject<BackupProgress> _progressSubject = new();
    private readonly SemaphoreSlim _backupSemaphore = new(2); // 最大2つの並行バックアップ

    public BackupService(
        ILogger<BackupService> logger,
        IOptionsMonitor<BackupOptions> optionsMonitor)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
    }

    public IObservable<BackupProgress> GetProgressObservable() => _progressSubject.AsObservable();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("バックアップサービスを開始します");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _optionsMonitor.CurrentValue;
                var interval = TimeSpan.FromHours(options.BackupIntervalHours);

                await CreateConfigBackupAsync(stoppingToken);
                await CleanupOldBackupsAsync(stoppingToken);

                await Task.Delay(interval, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "バックアップ処理でエラーが発生しました");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    public async Task<BackupResult> CreateFullBackupAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var backupRoot = ServicePaths.Backups;
        Directory.CreateDirectory(backupRoot);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupName = $"full_backup_{timestamp}.zip";
        var backupPath = Path.Combine(backupRoot, backupName);

        _logger.LogInformation("フルバックアップを作成します: {BackupPath}", backupPath);

        var filesToBackup = GetFilesForFullBackup();
        var createdAt = DateTimeOffset.UtcNow;

        try
        {
            using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);

            var totalFiles = 0;
            foreach (var filePath in filesToBackup)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    if (File.Exists(filePath))
                    {
                        archive.CreateEntryFromFile(filePath, Path.GetFileName(filePath));
                        totalFiles++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "バックアップ対象ファイルの追加に失敗しました: {FilePath}", filePath);
                }
            }

            var fileInfo = new FileInfo(backupPath);
            var result = new BackupResult(true, backupPath, fileInfo.Length, totalFiles, createdAt);

            _logger.LogInformation("フルバックアップが完了しました: {FileCount}個のファイル, {SizeBytes}バイト",
                totalFiles, fileInfo.Length);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "フルバックアップの作成に失敗しました");
            return new BackupResult(false, backupPath, 0, 0, createdAt);
        }
    }

    public async Task<BackupResult> CreateConfigBackupAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var backupRoot = ServicePaths.Backups;
        Directory.CreateDirectory(backupRoot);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupName = $"config_backup_{timestamp}.zip";
        var backupPath = Path.Combine(backupRoot, backupName);

        _logger.LogInformation("設定バックアップを作成します: {BackupPath}", backupPath);

        var configFiles = GetConfigFiles();
        var createdAt = DateTimeOffset.UtcNow;

        try
        {
            using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);

            var totalFiles = configFiles.Count;
            var processedFiles = 0;
            var totalBytes = configFiles.Sum(file => File.Exists(file) ? new FileInfo(file).Length : 0);
            var processedBytes = 0L;

            // 並行処理でファイルを追加（最大4つの並行処理）
            var addFileTasks = configFiles.Select(async configFile =>
            {
                try
                {
                    if (File.Exists(configFile))
                    {
                        var fileSize = new FileInfo(configFile).Length;
                        var relativePath = Path.GetRelativePath(ServicePaths.Base, configFile);

                        await Task.Run(() =>
                        {
                            archive.CreateEntryFromFile(configFile, relativePath);
                        });

                        Interlocked.Increment(ref processedFiles);
                        Interlocked.Add(ref processedBytes, fileSize);

                        // 進捗報告
                        var progress = new BackupProgress(
                            Path.GetFileName(configFile),
                            processedFiles,
                            totalFiles,
                            processedBytes,
                            totalBytes,
                            (double)processedFiles / totalFiles * 100);

                        _progressSubject.OnNext(progress);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "設定ファイルのバックアップに失敗しました: {FilePath}", configFile);
                }
            });

            await Task.WhenAll(addFileTasks);

            var fileInfo = new FileInfo(backupPath);
            var result = new BackupResult(true, backupPath, fileInfo.Length, processedFiles, createdAt);

            _logger.LogInformation("設定バックアップが完了しました: {FileCount}個のファイル, {SizeBytes}バイト",
                processedFiles, fileInfo.Length);

            // 完了報告
            var finalProgress = new BackupProgress(
                "",
                processedFiles,
                totalFiles,
                fileInfo.Length,
                totalBytes,
                100.0);

            _progressSubject.OnNext(finalProgress);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定バックアップの作成に失敗しました");
            return new BackupResult(false, backupPath, 0, 0, createdAt);
        }
    }

    public async Task<int> CleanupOldBackupsAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var backupRoot = ServicePaths.Backups;
        var cutoffTime = DateTimeOffset.UtcNow.AddDays(-options.RetentionDays);

        if (!Directory.Exists(backupRoot))
            return 0;

        var backupFiles = Directory.GetFiles(backupRoot, "*.zip")
            .Select(file => new FileInfo(file))
            .Where(file => file.LastWriteTimeUtc < cutoffTime)
            .ToArray();

        if (!backupFiles.Any())
        {
            _logger.LogDebug("削除対象のバックアップファイルが見つかりません");
            return 0;
        }

        _logger.LogInformation("古いバックアップファイルを削除します: {FileCount}個", backupFiles.Length);

        var deletedCount = 0;
        var totalFiles = backupFiles.Length;

        // 並行処理でファイルを削除（最大8つの並行処理）
        var deleteTasks = backupFiles.Select(async (file, index) =>
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            try
            {
                await Task.Run(() => file.Delete());

                var currentDeleted = Interlocked.Increment(ref deletedCount);

                // 進捗報告
                var progress = new BackupProgress(
                    file.Name,
                    currentDeleted,
                    totalFiles,
                    0, // 削除処理ではバイト数は計算しない
                    0,
                    (double)currentDeleted / totalFiles * 100);

                _progressSubject.OnNext(progress);

                _logger.LogInformation("古いバックアップファイルを削除しました: {FileName}", file.Name);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "バックアップファイルの削除に失敗しました: {FileName}", file.Name);
                return false;
            }
        });

        await Task.WhenAll(deleteTasks);

        _logger.LogInformation("バックアップファイルのクリーンアップが完了しました: {DeletedCount}個削除", deletedCount);

        // 完了報告
        var finalProgress = new BackupProgress(
            "",
            deletedCount,
            totalFiles,
            0,
            0,
            100.0);

        _progressSubject.OnNext(finalProgress);

        return deletedCount;
    }

    public async Task<IReadOnlyList<BackupFileInfo>> GetBackupFilesAsync(CancellationToken cancellationToken)
    {
        var backupRoot = ServicePaths.Backups;

        if (!Directory.Exists(backupRoot))
            return Array.Empty<BackupFileInfo>();

        return Directory.GetFiles(backupRoot, "*.zip")
            .Select(file =>
            {
                var fileInfo = new FileInfo(file);
                var type = DetermineBackupType(fileInfo.Name);
                return new BackupFileInfo(
                    fileInfo.Name,
                    fileInfo.FullName,
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc,
                    type);
            })
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    private static IReadOnlyList<string> GetFilesForFullBackup()
    {
        var files = new List<string>();

        // 設定ファイル
        files.AddRange(GetConfigFiles());

        // ログファイル（最新のもののみ）
        var logDir = ServicePaths.Logs;
        if (Directory.Exists(logDir))
        {
            files.AddRange(Directory.GetFiles(logDir, "otedama-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(5)); // 最新5個のみ
        }

        // 状態ファイル
        var stateDir = Path.Combine(ServicePaths.State, "task_states.json");
        if (File.Exists(stateDir))
        {
            files.Add(stateDir);
        }

        var securityStateDir = Path.Combine(ServicePaths.State, "security");
        if (Directory.Exists(securityStateDir))
        {
            files.AddRange(Directory.GetFiles(securityStateDir, "*.json"));
        }

        return files;
    }

    private static IReadOnlyList<string> GetConfigFiles()
    {
        var configDir = ServicePaths.Base;
        return new[]
        {
            Path.Combine(configDir, "appsettings.json"),
            Path.Combine(configDir, "appsettings.Production.json"),
            Path.Combine(configDir, "appsettings.Personal.json")
        };
    }

    private static BackupType DetermineBackupType(string fileName)
    {
        if (fileName.Contains("full_backup", StringComparison.OrdinalIgnoreCase))
            return BackupType.Full;
        if (fileName.Contains("config_backup", StringComparison.OrdinalIgnoreCase))
            return BackupType.Configuration;
        return BackupType.SystemState;
    }
}
