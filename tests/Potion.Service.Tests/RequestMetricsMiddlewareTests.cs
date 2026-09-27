using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

/// <summary>
/// The pipeline has no UseExceptionHandler: an unhandled downstream exception
/// unwinds while Response.StatusCode is still 200. The middleware must count
/// such requests as server errors or the reported error rate silently drops
/// to zero. Client aborts are not server errors.
/// </summary>
public sealed class RequestMetricsMiddlewareTests
{
    private static DefaultHttpContext NewContext(string path = "/api/health")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }

    [Fact]
    public async Task InvokeAsync_DownstreamThrows_CountsAsServerError()
    {
        var tracker = new RequestMetricsTracker();
        var middleware = new RequestMetricsMiddleware(
            _ => Task.FromException(new InvalidOperationException("boom")), tracker);

        var context = NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        var (_, _, errorRate) = tracker.Snapshot();
        Assert.Equal(1.0, errorRate);
    }

    [Fact]
    public async Task InvokeAsync_ClientAbort_IsNotCountedAsError()
    {
        var tracker = new RequestMetricsTracker();
        var middleware = new RequestMetricsMiddleware(
            _ => Task.FromException(new OperationCanceledException()), tracker);

        var context = NewContext();
        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        var (_, _, errorRate) = tracker.Snapshot();
        Assert.Equal(0.0, errorRate);
    }

    [Fact]
    public async Task InvokeAsync_SuccessfulRequest_RecordsLatencyWithoutError()
    {
        var tracker = new RequestMetricsTracker();
        var middleware = new RequestMetricsMiddleware(_ => Task.CompletedTask, tracker);

        await middleware.InvokeAsync(NewContext());

        var (rps, _, errorRate) = tracker.Snapshot();
        Assert.True(rps > 0);
        Assert.Equal(0.0, errorRate);
    }

    [Fact]
    public async Task InvokeAsync_Explicit500Status_CountsAsError()
    {
        var tracker = new RequestMetricsTracker();
        var middleware = new RequestMetricsMiddleware(
            ctx => { ctx.Response.StatusCode = 500; return Task.CompletedTask; }, tracker);

        await middleware.InvokeAsync(NewContext());

        var (_, _, errorRate) = tracker.Snapshot();
        Assert.Equal(1.0, errorRate);
    }

    [Fact]
    public async Task InvokeAsync_ExcludedPath_IsNotRecorded()
    {
        var tracker = new RequestMetricsTracker();
        var middleware = new RequestMetricsMiddleware(_ => Task.CompletedTask, tracker);

        await middleware.InvokeAsync(NewContext("/metrics"));

        var (rps, _, _) = tracker.Snapshot();
        Assert.Equal(0.0, rps);
    }
}
