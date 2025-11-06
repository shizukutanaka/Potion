using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Potion.Service.Infrastructure;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    double PeakMemoryMb,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated);

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner, IDisposable
{
    private const int MaxCapturedCharacters = 128_000; // Increased for better diagnostics
    private const long DefaultMaxMemoryUsageBytes = 768 * 1024 * 1024; // 768MB - Optimized for modern systems
    private static readonly int MaxConcurrentProcesses = Math.Max(1, Environment.ProcessorCount / 2); // Dynamic based on CPU cores
    private readonly SemaphoreSlim _processSemaphore = new(MaxConcurrentProcesses, MaxConcurrentProcesses);
    private bool _disposed;
    private readonly long _maxMemoryUsageBytes;
    private readonly double _sustainedPressureThreshold;
    private long _consecutivePressureHits;

    public ProcessRunner(ILogger<ProcessRunner> logger) : this(logger, GetConfiguredMemoryLimit())
    {
    }

    internal ProcessRunner(ILogger<ProcessRunner> logger, long maxMemoryUsageBytes)
    {
        this.logger = logger;
        _maxMemoryUsageBytes = Math.Clamp(maxMemoryUsageBytes, 128 * 1024 * 1024, 4L * 1024 * 1024 * 1024);
        _sustainedPressureThreshold = _maxMemoryUsageBytes * 0.8;
    }

    public async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(startInfo);

        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            throw new ArgumentException("Process file name cannot be null or empty", nameof(startInfo));
        }

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite");
        }

        if (timeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout cannot exceed 24 hours for safety");
        }

        await _processSemaphore.WaitAsync(cancellationToken);

        try
        {
            return await ExecuteProcessAsync(startInfo, timeout, cancellationToken);
        }
        finally
        {
            _processSemaphore.Release();
        }
    }


    private async Task<ProcessResult> ExecuteProcessAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WorkingDirectory = Path.GetDirectoryName(startInfo.FileName) ?? Environment.CurrentDirectory;

        var outputBuffer = new StringBuilder(capacity: Math.Min(MaxCapturedCharacters, 4_096));
        var errorBuffer = new StringBuilder(capacity: Math.Min(MaxCapturedCharacters, 4_096));
        var outputTruncated = false;
        var errorTruncated = false;
        var outputQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errorQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var outputLock = new object();
        var errorLock = new object();
        var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // バッファ処理をバックグラウンドで継続的に実行
        var processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outputProcessingTask = Task.Run(() => ProcessQueueContinuously(outputQueue, outputBuffer, ref outputTruncated, outputLock, processingCts.Token));
        var errorProcessingTask = Task.Run(() => ProcessQueueContinuously(errorQueue, errorBuffer, ref errorTruncated, errorLock, processingCts.Token));

        void AppendWithLimit(System.Collections.Concurrent.ConcurrentQueue<string> queue, string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            queue.Enqueue(data);
        }

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                outputTcs.TrySetResult(true);
                return;
            }

            AppendWithLimit(outputQueue, args.Data);
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                errorTcs.TrySetResult(true);
                return;
            }

            AppendWithLimit(errorQueue, args.Data);
        };

        var stopwatch = Stopwatch.StartNew();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            linkedCts.CancelAfter(timeout);
        }

        SafeFileHandle? jobHandle = null;
        if (OperatingSystem.IsWindows())
        {
            jobHandle = CreateAndConfigureJobObject();
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Process {startInfo.FileName} did not start");
            }

            if (jobHandle is not null && !jobHandle.IsInvalid)
            {
                if (!AssignProcessToJobObject(jobHandle, process.SafeHandle))
                {
                    var error = Marshal.GetLastWin32Error();
                    logger.LogWarning("Failed to assign process {FileName} to job object. Win32Error={Error}", startInfo.FileName, error);
                }
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var waitForExitTask = process.WaitForExitAsync(linkedCts.Token);
            try
            {
                await Task.WhenAll(
                    waitForExitTask,
                    outputTcs.Task.WaitAsync(linkedCts.Token),
                    errorTcs.Task.WaitAsync(linkedCts.Token));
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                throw new TimeoutException($"Process {startInfo.FileName} timed out after {timeout}");
            }
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
        catch (Exception ex)
        {
            TryTerminate(process);
            logger.LogError(ex, "Failed to execute process {FileName}", startInfo.FileName);
            throw;
        }
        finally
        {
            outputTcs.TrySetResult(true);
            errorTcs.TrySetResult(true);
            stopwatch.Stop();

            // バックグラウンド処理をキャンセル
            processingCts.Cancel();

            // バックグラウンド処理が完了するのを待つ
            await Task.WhenAll(outputProcessingTask, errorProcessingTask);

            // キューに残っているデータを確実に処理
            FlushRemainingData(outputQueue, outputLock, outputBuffer, ref outputTruncated);
            FlushRemainingData(errorQueue, errorLock, errorBuffer, ref errorTruncated);

            if (!process.HasExited)
            {
                TryTerminate(process);
            }

            jobHandle?.Dispose();
            processingCts.Dispose();
        }

        var peakMemory = process.PeakWorkingSet64;
        var peakMemoryMb = peakMemory / (1024.0 * 1024.0);
        var standardOutput = outputBuffer.ToString();
        var standardError = errorBuffer.ToString();

        // メモリ使用量のチェック
        if (peakMemory > _maxMemoryUsageBytes)
        {
            logger.LogWarning("Process {FileName} exceeded memory limit {LimitMB:F1} MB with peak {PeakMemoryMB:F1} MB", startInfo.FileName, _maxMemoryUsageBytes / (1024.0 * 1024.0), peakMemoryMb);
            Interlocked.Increment(ref _consecutivePressureHits);
        }
        else
        {
            Interlocked.Exchange(ref _consecutivePressureHits, 0);
        }

        if (peakMemory > _sustainedPressureThreshold)
        {
            logger.LogDebug("Process {FileName} peak memory {PeakMemoryMB:F1} MB is near threshold {ThresholdMB:F1} MB", startInfo.FileName, peakMemoryMb, _sustainedPressureThreshold / (1024.0 * 1024.0));
        }

        if (Volatile.Read(ref _consecutivePressureHits) >= 3)
        {
            logger.LogError("ProcessRunner observed sustained memory pressure. Consider reducing concurrency or tightening allow list.");
        }

        return new ProcessResult(
            process.ExitCode,
            standardOutput,
            standardError,
            stopwatch.Elapsed,
            peakMemoryMb,
            outputTruncated,
            errorTruncated);
    }

    private static void ProcessQueueContinuously(System.Collections.Concurrent.ConcurrentQueue<string> queue, StringBuilder buffer, ref bool truncated, object sync, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batchSize = 0;
                var items = new List<string>();

                // バッチでアイテムを取得（ロック時間を最小化）
                while (batchSize < 100 && queue.TryDequeue(out var item))
                {
                    items.Add(item);
                    batchSize++;
                }

                if (batchSize == 0)
                {
                    // キューが空になったら少し待機
                    Thread.Sleep(1);
                    continue;
                }

                // バッチ処理
                lock (sync)
                {
                    foreach (var item in items)
                    {
                        if (Volatile.Read(ref truncated) || cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        var remaining = MaxCapturedCharacters - buffer.Length;
                        if (remaining <= 0)
                        {
                            Volatile.Write(ref truncated, true);
                            return;
                        }

                        var toAppend = item.Length > remaining ? item[..remaining] : item;
                        buffer.AppendLine(toAppend);

                        if (toAppend.Length < item.Length)
                        {
                            Volatile.Write(ref truncated, true);
                            return;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常終了
        }
    }

    private static void FlushRemainingData(System.Collections.Concurrent.ConcurrentQueue<string> queue, object sync, StringBuilder buffer, ref bool truncated)
    {
        lock (sync)
        {
            while (queue.TryDequeue(out var item))
            {
                if (Volatile.Read(ref truncated))
                {
                    return;
                }

                var remaining = MaxCapturedCharacters - buffer.Length;
                if (remaining <= 0)
                {
                    Volatile.Write(ref truncated, true);
                    return;
                }

                var toAppend = item.Length > remaining ? item[..remaining] : item;
                buffer.AppendLine(toAppend);

                if (toAppend.Length < item.Length)
                {
                    Volatile.Write(ref truncated, true);
                    return;
                }
            }
        }
    }

    private static void TryTerminate(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied or process not found
        }
    }

    private static long GetConfiguredMemoryLimit()
    {
        return EnvironmentVariableHelper.GetLongFromEnvironment("POTION_PROCESS_MAX_MEMORY_MB", DefaultMaxMemoryUsageBytes / (1024 * 1024)) * 1024 * 1024;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _processSemaphore.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ProcessRunner));
        }
    }

    private SafeFileHandle? CreateAndConfigureJobObject()
    {
        try
        {
            var jobHandle = CreateJobObjectW(IntPtr.Zero, null);
            if (jobHandle is null || jobHandle.IsInvalid)
            {
                return null;
            }

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitFlags.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JobObjectLimitFlags.JOB_OBJECT_LIMIT_JOB_MEMORY
                },
                JobMemoryLimit = (UIntPtr)_maxMemoryUsageBytes
            };

            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            if (!SetInformationJobObject(jobHandle, JobObjectInfoClass.ExtendedLimitInformation, ref limits, size))
            {
                var error = Marshal.GetLastWin32Error();
                logger.LogWarning("Failed to apply job object limits. Win32Error={Error}", error);
                jobHandle.Dispose();
                return null;
            }

            return jobHandle;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create job object containment");
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle hJob, JobObjectInfoClass infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, SafeProcessHandle hProcess);

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [Flags]
    private enum JobObjectLimitFlags : uint
    {
        JOB_OBJECT_LIMIT_WORKINGSET = 0x00000001,
        JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200,
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobObjectLimitFlags LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
