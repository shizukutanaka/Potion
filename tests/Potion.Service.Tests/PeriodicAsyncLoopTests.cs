using System;
using System.Threading;
using System.Threading.Tasks;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

public sealed class PeriodicAsyncLoopTests
{
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task RunsImmediately_ThenRepeatsOnPeriod()
    {
        var count = 0;
        await using var loop = new PeriodicAsyncLoop(
            TimeSpan.Zero, TimeSpan.FromMilliseconds(50),
            _ => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            _ => { });

        await WaitForAsync(() => Volatile.Read(ref count) >= 2);

        Assert.True(Volatile.Read(ref count) >= 2);
    }

    [Fact]
    public async Task IterationError_IsReported_AndLoopContinues()
    {
        var calls = 0;
        var errors = 0;
        await using var loop = new PeriodicAsyncLoop(
            TimeSpan.Zero, TimeSpan.FromMilliseconds(50),
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("boom");
                }
                return Task.CompletedTask;
            },
            _ => Interlocked.Increment(ref errors));

        await WaitForAsync(() => Volatile.Read(ref calls) >= 2);

        Assert.Equal(1, Volatile.Read(ref errors));
        Assert.True(Volatile.Read(ref calls) >= 2);
    }

    [Fact]
    public async Task DisposeAsync_StopsFurtherIterations()
    {
        var count = 0;
        var loop = new PeriodicAsyncLoop(
            TimeSpan.Zero, TimeSpan.FromMilliseconds(50),
            _ => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            _ => { });

        await WaitForAsync(() => Volatile.Read(ref count) >= 1);
        await loop.DisposeAsync();
        var stable = Volatile.Read(ref count);
        await Task.Delay(200);

        Assert.Equal(stable, Volatile.Read(ref count));
    }

    [Fact]
    public async Task DisposeAsync_AwaitsInFlightIteration()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = new PeriodicAsyncLoop(
            TimeSpan.Zero, TimeSpan.FromMinutes(1),
            async _ => { started.TrySetResult(); await release.Task; },
            _ => { });

        await started.Task;
        var disposeTask = loop.DisposeAsync().AsTask();
        Assert.False(disposeTask.IsCompleted);

        release.SetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelNow_IsSafe_BeforeAndAfterDispose()
    {
        var loop = new PeriodicAsyncLoop(
            TimeSpan.Zero, TimeSpan.FromMinutes(1),
            _ => Task.CompletedTask,
            _ => { });

        loop.CancelNow();
        await loop.DisposeAsync();
        loop.CancelNow(); // must not throw on an already-disposed source
    }

    [Fact]
    public async Task HonorsInitialDelay_BeforeFirstRun()
    {
        var count = 0;
        await using var loop = new PeriodicAsyncLoop(
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(50),
            _ => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            _ => { });

        await Task.Delay(75);
        Assert.Equal(0, Volatile.Read(ref count));

        await WaitForAsync(() => Volatile.Read(ref count) >= 1);
        Assert.True(Volatile.Read(ref count) >= 1);
    }
}
