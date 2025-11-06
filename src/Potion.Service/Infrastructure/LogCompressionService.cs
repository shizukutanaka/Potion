using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// ログ圧縮の進捗情報
/// </summary>
public sealed record CompressionProgress(
    string CurrentFile,
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    double ProgressPercent);

/// <summary>
/// ログファイルの自動圧縮と管理サービス（並行処理対応）
/// </summary>
public interface ILogCompressionService
{
    /// <summary>
    /// 古いログファイルを圧縮します
    /// </summary>
    Task CompressOldLogsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// ログディレクトリのサイズをチェックします
    /// </summary>
    Task<LogDirectoryInfo> GetLogDirectoryInfoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 圧縮処理の進捗を取得します
    /// </summary>
    IObservable<CompressionProgress> GetProgressObservable();
}

public sealed record LogDirectoryInfo(
    long TotalSizeBytes,
    int FileCount,
    int CompressedFileCount,
    DateTimeOffset OldestLogTime,
    DateTimeOffset NewestLogTime);

public sealed class LogCompressionService : BackgroundService, ILogCompressionService
{
    private readonly ILogger<LogCompressionService> _logger;
    private readonly IOptionsMonitor<LogCompressionOptions> _optionsMonitor;

    private readonly Subject<CompressionProgress> _progressSubject = new();
    private readonly SemaphoreSlim _compressionSemaphore = new(3); // 最大3つの並行圧縮

    public LogCompressionService(
        ILogger<LogCompressionService> logger,
        IOptionsMonitor<LogCompressionOptions> optionsMonitor)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
    }

    public IObservable<CompressionProgress> GetProgressObservable() => _progressSubject.AsObservable();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ログ圧縮サービスを開始します");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _optionsMonitor.CurrentValue;
                var interval = TimeSpan.FromHours(options.CompressionIntervalHours);

                await CompressOldLogsAsync(stoppingToken);

                await Task.Delay(interval, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "ログ圧縮処理でエラーが発生しました");
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
    }

    public async Task CompressOldLogsAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var logDirectory = ServicePaths.Logs;

        if (!Directory.Exists(logDirectory))
        {
            _logger.LogDebug("ログディレクトリが存在しません: {LogDirectory}", logDirectory);
            return;
        }

        var cutoffTime = DateTimeOffset.UtcNow.AddDays(-options.CompressionAgeDays);
        var logFiles = Directory.GetFiles(logDirectory, "otedama-*.log")
            .Where(file => File.GetLastWriteTimeUtc(file) < cutoffTime)
            .Where(file => !file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!logFiles.Any())
        {
            _logger.LogDebug("圧縮対象のログファイルが見つかりません");
            return;
        }

        _logger.LogInformation("圧縮対象のログファイルを処理します: {FileCount}個", logFiles.Length);

        var totalFiles = logFiles.Length;
        var processedFiles = 0;
        var totalBytes = logFiles.Sum(file => new FileInfo(file).Length);
        var processedBytes = 0L;

        // 並行処理でログファイルを圧縮
        var compressionTasks = logFiles.Select(async logFile =>
        {
            try
            {
                await _compressionSemaphore.WaitAsync(cancellationToken);

                var fileSize = new FileInfo(logFile).Length;
                var result = await CompressLogFileAsync(logFile, cancellationToken);

                Interlocked.Increment(ref processedFiles);
                Interlocked.Add(ref processedBytes, fileSize);

                // 進捗報告
                var progress = new CompressionProgress(
                    Path.GetFileName(logFile),
                    processedFiles,
                    totalFiles,
                    processedBytes,
                    totalBytes,
                    (double)processedFiles / totalFiles * 100);

                _progressSubject.OnNext(progress);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ログファイルの圧縮に失敗しました: {LogFile}", logFile);
                return false;
            }
            finally
            {
                _compressionSemaphore.Release();
            }
        });

        await Task.WhenAll(compressionTasks);

        _logger.LogInformation("ログ圧縮処理が完了しました: {ProcessedFiles}/{TotalFiles} ファイル処理済み",
            processedFiles, totalFiles);

        // 完了報告
        var finalProgress = new CompressionProgress(
            "",
            processedFiles,
            totalFiles,
            processedBytes,
            totalBytes,
            100.0);

        _progressSubject.OnNext(finalProgress);
    }

    public async Task<LogDirectoryInfo> GetLogDirectoryInfoAsync(CancellationToken cancellationToken)
    {
        var logDirectory = ServicePaths.Logs;

        if (!Directory.Exists(logDirectory))
        {
            return new LogDirectoryInfo(0, 0, 0, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
        }

        var logFiles = Directory.GetFiles(logDirectory, "otedama-*");

        if (!logFiles.Any())
        {
            return new LogDirectoryInfo(0, 0, 0, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
        }

        var totalSize = logFiles.Sum(file => new FileInfo(file).Length);
        var compressedCount = logFiles.Count(file => file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
        var fileCount = logFiles.Length;
        var lastWriteTimes = logFiles.Select(File.GetLastWriteTimeUtc).ToArray();

        return new LogDirectoryInfo(
            totalSize,
            fileCount,
            compressedCount,
            lastWriteTimes.Min(),
            lastWriteTimes.Max());
    }

    private async Task<bool> CompressLogFileAsync(string logFilePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(logFilePath);
        var compressedFilePath = logFilePath + ".gz";

        // 既に圧縮済みファイルが存在する場合は元のファイルを削除
        if (File.Exists(compressedFilePath))
        {
            _logger.LogDebug("圧縮済みファイルが既に存在します: {CompressedFile}", compressedFilePath);
            File.Delete(logFilePath);
            return true;
        }

        // ファイルサイズチェック
        var options = _optionsMonitor.CurrentValue;
        if (fileInfo.Length > options.MaxCompressionFileSizeBytes)
        {
            _logger.LogWarning("ファイルサイズが制限を超えています。圧縮をスキップします: {LogFile} ({FileSize} bytes)",
                logFilePath, fileInfo.Length);
            return false;
        }

        _logger.LogInformation("ログファイルを圧縮します: {LogFile} ({FileSize} bytes)",
            logFilePath, fileInfo.Length);

        const int bufferSize = 81920; // 80KBバッファでメモリ効率を最適化

        try
        {
            using var inputStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
            using var outputStream = new FileStream(compressedFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);

            // 圧縮レベルを動的に決定（ファイルサイズに応じて）
            var compressionLevel = fileInfo.Length > 10485760 ? CompressionLevel.Fastest : CompressionLevel.Optimal; // 10MB以上は高速圧縮

            using var gzipStream = new GZipStream(outputStream, compressionLevel);

            await inputStream.CopyToAsync(gzipStream, bufferSize, cancellationToken);

            // 明示的にストリームを閉じてから削除
            await gzipStream.DisposeAsync();
            await outputStream.DisposeAsync();

            // 元ファイルを削除（圧縮成功時のみ）
            File.Delete(logFilePath);

            var compressedInfo = new FileInfo(compressedFilePath);
            var compressionRatio = 1.0 - (double)compressedInfo.Length / fileInfo.Length;

            _logger.LogInformation("ログファイルを圧縮しました: {LogFile} -> {CompressedFile} (圧縮率: {CompressionRatio:P1})",
                logFilePath, compressedFilePath, compressionRatio);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 圧縮失敗時は一時ファイルを削除
            if (File.Exists(compressedFilePath))
            {
                try { File.Delete(compressedFilePath); } catch { /* 無視 */ }
            }

            _logger.LogError(ex, "ログファイルの圧縮に失敗しました: {LogFile}", logFilePath);
            return false;
        }
    }
}
