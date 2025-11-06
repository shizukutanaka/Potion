using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class RateLimitingOptions
{
    public bool Enabled { get; set; } = true;
    public int RequestsPerMinute { get; set; } = 60;
    public int RequestsPerHour { get; set; } = 1000;
    public int BurstLimit { get; set; } = 10;
    public TimeSpan WindowDuration { get; set; } = TimeSpan.FromMinutes(1);
}

public class RateLimitingService
{
    private readonly ILogger<RateLimitingService> _logger;
    private readonly RateLimitingOptions _options;
    private readonly ConcurrentDictionary<string, ClientRateLimit> _clientLimits = new();

    public RateLimitingService(
        ILogger<RateLimitingService> logger,
        IOptions<RateLimitingOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public bool IsAllowed(string clientId, string endpoint)
    {
        if (!_options.Enabled)
            return true;

        var key = $"{clientId}:{endpoint}";
        var now = DateTimeOffset.UtcNow;

        var limit = _clientLimits.GetOrAdd(key, _ => new ClientRateLimit
        {
            ClientId = clientId,
            Endpoint = endpoint,
            WindowStart = now,
            RequestCount = 0
        });

        // Reset window if expired
        if (now - limit.WindowStart > _options.WindowDuration)
        {
            limit.WindowStart = now;
            limit.RequestCount = 0;
        }

        // Check burst limit
        if (limit.RequestCount >= _options.BurstLimit)
        {
            _logger.LogWarning("Rate limit exceeded for client {ClientId} on endpoint {Endpoint}", clientId, endpoint);
            return false;
        }

        limit.RequestCount++;
        limit.LastRequest = now;

        return true;
    }

    public RateLimitStatus GetStatus(string clientId, string endpoint)
    {
        var key = $"{clientId}:{endpoint}";
        var limit = _clientLimits.GetValueOrDefault(key);

        if (limit == null)
        {
            return new RateLimitStatus
            {
                RequestsRemaining = _options.BurstLimit,
                ResetTime = DateTimeOffset.UtcNow + _options.WindowDuration
            };
        }

        var now = DateTimeOffset.UtcNow;
        var timeToReset = _options.WindowDuration - (now - limit.WindowStart);
        var remaining = Math.Max(0, _options.BurstLimit - limit.RequestCount);

        return new RateLimitStatus
        {
            RequestsRemaining = remaining,
            ResetTime = now + timeToReset
        };
    }

    public void CleanupExpiredLimits()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _clientLimits
            .Where(kvp => now - kvp.Value.WindowStart > _options.WindowDuration * 2)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _clientLimits.TryRemove(key, out _);
        }

        if (expiredKeys.Any())
        {
            _logger.LogInformation("Cleaned up {Count} expired rate limits", expiredKeys.Count);
        }
    }
}

public class ClientRateLimit
{
    public string ClientId { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public DateTimeOffset WindowStart { get; set; }
    public int RequestCount { get; set; }
    public DateTimeOffset LastRequest { get; set; }
}

public class RateLimitStatus
{
    public int RequestsRemaining { get; set; }
    public DateTimeOffset ResetTime { get; set; }
}

// Middleware for ASP.NET Core
public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimitingService _rateLimitingService;
    private readonly ILogger<RateLimitingMiddleware> _logger;

    public RateLimitingMiddleware(
        RequestDelegate next,
        RateLimitingService rateLimitingService,
        ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _rateLimitingService = rateLimitingService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var clientId = GetClientId(context);
        var endpoint = context.Request.Path.Value ?? "/";

        if (!_rateLimitingService.IsAllowed(clientId, endpoint))
        {
            var status = _rateLimitingService.GetStatus(clientId, endpoint);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["X-RateLimit-Remaining"] = status.RequestsRemaining.ToString();
            context.Response.Headers["X-RateLimit-Reset"] = status.ResetTime.ToString("O");
            context.Response.Headers["Retry-After"] = Math.Ceiling((status.ResetTime - DateTimeOffset.UtcNow).TotalSeconds).ToString();

            await context.Response.WriteAsync("Rate limit exceeded. Try again later.");
            return;
        }

        await _next(context);
    }

    private string GetClientId(HttpContext context)
    {
        // Try to get from API key header
        if (context.Request.Headers.TryGetValue("X-API-Key", out var apiKey))
        {
            return apiKey.ToString();
        }

        // Fallback to IP address
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

// Extension method for easy registration
public static class RateLimitingExtensions
{
    public static IServiceCollection AddRateLimiting(this IServiceCollection services, Action<RateLimitingOptions> configureOptions)
    {
        services.Configure(configureOptions);
        services.AddSingleton<RateLimitingService>();
        return services;
    }

    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app)
    {
        app.UseMiddleware<RateLimitingMiddleware>();
        return app;
    }
}
