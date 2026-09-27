using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Rolling-window HTTP request counters shared between the metrics middleware and the
/// health monitor. Window is one minute; long-lived or scrape endpoints are excluded.
/// </summary>
public sealed class RequestMetricsTracker
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly Queue<(DateTimeOffset At, double LatencyMs, bool Error)> _window = new();

    public static bool ShouldTrack(PathString path)
    {
        return !path.StartsWithSegments("/collaboration", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);
    }

    public void Record(double latencyMs, int statusCode)
    {
        lock (_gate)
        {
            _window.Enqueue((DateTimeOffset.UtcNow, latencyMs, statusCode >= 500));
            Trim();
        }
    }

    /// <summary>RPS over the window, mean latency in ms, error fraction 0..1 (5xx/total).</summary>
    public (double Rps, double AverageLatencyMs, double ErrorRate) Snapshot()
    {
        lock (_gate)
        {
            Trim();
            var count = _window.Count;
            if (count == 0)
            {
                return (0.0, 0.0, 0.0);
            }

            var avg = _window.Average(x => x.LatencyMs);
            var errors = _window.Count(x => x.Error);
            return (count / WindowLength.TotalSeconds, avg, (double)errors / count);
        }
    }

    private void Trim()
    {
        var cutoff = DateTimeOffset.UtcNow - WindowLength;
        while (_window.Count > 0 && _window.Peek().At < cutoff)
        {
            _window.Dequeue();
        }
    }
}

/// <summary>
/// Measures request latency and status for the shared <see cref="RequestMetricsTracker"/>.
/// Registered after static files so only API/SignalR/health traffic is measured.
/// </summary>
public sealed class RequestMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestMetricsTracker _tracker;

    public RequestMetricsMiddleware(RequestDelegate next, RequestMetricsTracker tracker)
    {
        _next = next;
        _tracker = tracker;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            if (RequestMetricsTracker.ShouldTrack(context.Request.Path))
            {
                // There is no UseExceptionHandler in this pipeline: an unhandled
                // exception unwinds while StatusCode is still its default, so
                // crashed requests must be counted as 500 here or the error rate
                // silently drops to zero. Client aborts are not server errors.
                var statusCode = context.Response.StatusCode;
                if (failure != null && statusCode < 500 && failure is not OperationCanceledException)
                {
                    statusCode = 500;
                }
                _tracker.Record(stopwatch.Elapsed.TotalMilliseconds, statusCode);
            }
        }
    }
}
