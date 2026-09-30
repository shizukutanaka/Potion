using System.Threading;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Runs an async work delegate on a fixed period via <see cref="PeriodicTimer"/>.
/// Unlike <c>new Timer(_ => _ = work(), ...)</c>, executions are serialized (no
/// overlap while a run is in flight), per-iteration failures are logged and the
/// loop continues, and cancellation stops the loop deterministically.
/// </summary>
internal sealed class PeriodicAsyncLoop : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private int _disposed;

    public PeriodicAsyncLoop(
        TimeSpan initialDelay,
        TimeSpan period,
        Func<CancellationToken, Task> work,
        Action<Exception> onIterationError)
    {
        _task = RunAsync(initialDelay, period, work, onIterationError, _cts.Token);
    }

    private static async Task RunAsync(
        TimeSpan initialDelay,
        TimeSpan period,
        Func<CancellationToken, Task> work,
        Action<Exception> onIterationError,
        CancellationToken cancellationToken)
    {
        try
        {
            if (initialDelay > TimeSpan.Zero)
            {
                await Task.Delay(initialDelay, cancellationToken).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(period);
            do
            {
                try
                {
                    await work(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    onIterationError(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Non-blocking cancel used by sync Dispose(); DisposeAsync owns the
    // CancellationTokenSource, so an already-disposed source is tolerated.
    public void CancelNow()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _task.ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
