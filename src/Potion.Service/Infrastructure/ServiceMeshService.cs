using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Potion.Service.Infrastructure;

/// <summary>
/// サービスメッシュとマイクロサービス通信パターン
/// Istio/Linkerdに着想を得たサービス間通信の最適化
/// </summary>
public interface IServiceMeshService
{
    Task<HttpResponseMessage> RouteRequestAsync(string serviceName, HttpRequestMessage request);
    Task<ServiceHealth> GetServiceHealthAsync(string serviceName);
    Task<IEnumerable<ServiceEndpoint>> GetServiceEndpointsAsync(string serviceName);
    Task<bool> RegisterServiceAsync(ServiceRegistration registration);
    Task UnregisterServiceAsync(string serviceName);
    Task<ServiceDiscoveryResult> DiscoverServicesAsync();
}

/// <summary>
/// サービス登録情報
/// </summary>
public record ServiceRegistration(
    string ServiceName,
    string Version,
    string BaseUrl,
    Dictionary<string, string> Metadata,
    HealthCheckEndpoint HealthCheck,
    ServiceCapabilities Capabilities);

/// <summary>
/// サービスヘルス情報
/// </summary>
public record ServiceHealth(
    string ServiceName,
    HealthStatus Status,
    DateTimeOffset LastChecked,
    TimeSpan ResponseTime,
    Dictionary<string, object> Metrics);

/// <summary>
/// サービスエンドポイント
/// </summary>
public record ServiceEndpoint(
    string ServiceName,
    string Url,
    bool IsHealthy,
    DateTimeOffset LastHealthCheck);

/// <summary>
/// ヘルスチェックエンドポイント
/// </summary>
public record HealthCheckEndpoint(
    string Path,
    HttpMethod Method,
    TimeSpan Timeout,
    int ExpectedStatusCode);

/// <summary>
/// サービス機能情報
/// </summary>
public record ServiceCapabilities(
    string[] SupportedProtocols,
    string[] AuthenticationMethods,
    RateLimitInfo RateLimits,
    SecurityInfo Security);

/// <summary>
/// レート制限情報
/// </summary>
public record RateLimitInfo(
    int RequestsPerSecond,
    int BurstLimit,
    TimeSpan WindowSize);

/// <summary>
/// セキュリティ情報
/// </summary>
public record SecurityInfo(
    string[] EncryptionAlgorithms,
    string[] HashAlgorithms,
    bool SupportsMutualTls);

/// <summary>
/// サービス発見結果
/// </summary>
public record ServiceDiscoveryResult(
    IEnumerable<ServiceRegistration> Services,
    DateTimeOffset DiscoveredAt,
    int TotalServices,
    Dictionary<string, ServiceHealth> ServiceHealthStatus);

/// <summary>
/// ヘルス状態
/// </summary>
public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy,
    Unknown
}

/// <summary>
/// サービスメッシュサービス実装
/// </summary>
public class ServiceMeshService : IServiceMeshService
{
    private readonly ILogger<ServiceMeshService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, ServiceRegistration> _serviceRegistry = new();
    private readonly ConcurrentDictionary<string, ServiceHealth> _serviceHealth = new();
    private readonly ConcurrentDictionary<string, CircuitBreaker> _circuitBreakers = new();
    private readonly Timer _healthCheckTimer;
    private readonly Timer _cleanupTimer;

    public ServiceMeshService(
        ILogger<ServiceMeshService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("ServiceMesh");

        // 30秒ごとにヘルスチェックを実行
        _healthCheckTimer = new Timer(PerformHealthChecks, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        // 5分ごとにクリーンアップを実行
        _cleanupTimer = new Timer(CleanupStaleServices, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public async Task<HttpResponseMessage> RouteRequestAsync(string serviceName, HttpRequestMessage request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(request);

        var circuitBreaker = _circuitBreakers.GetOrAdd(serviceName, _ => new CircuitBreaker());
        var endpoint = await GetHealthyEndpointAsync(serviceName);

        if (endpoint == null)
        {
            throw new ServiceUnavailableException($"No healthy endpoint found for service: {serviceName}");
        }

        try
        {
            return await circuitBreaker.ExecuteAsync(async () =>
            {
                var response = await _httpClient.SendAsync(request);
                await UpdateServiceHealthAsync(serviceName, HealthStatus.Healthy, response.StatusCode.ToString());
                return response;
            });
        }
        catch (Exception ex)
        {
            await UpdateServiceHealthAsync(serviceName, HealthStatus.Unhealthy, ex.Message);
            throw;
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (_serviceHealth.TryGetValue(serviceName, out var health))
        {
            return health;
        }

        return new ServiceHealth(serviceName, HealthStatus.Unknown, DateTimeOffset.UtcNow, TimeSpan.Zero, new());
    }

    public async Task<IEnumerable<ServiceEndpoint>> GetServiceEndpointsAsync(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var registration = _serviceRegistry.GetOrAdd(serviceName, _ =>
        {
            _logger.LogWarning("Service {ServiceName} not found in registry", serviceName);
            return null;
        });

        if (registration == null)
        {
            return Enumerable.Empty<ServiceEndpoint>();
        }

        return new[]
        {
            new ServiceEndpoint(serviceName, registration.BaseUrl, true, DateTimeOffset.UtcNow)
        };
    }

    public async Task<bool> RegisterServiceAsync(ServiceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.ServiceName);

        _serviceRegistry[registration.ServiceName] = registration;

        // 初期ヘルスチェックを実行
        await PerformHealthCheckAsync(registration.ServiceName);

        _logger.LogInformation("Registered service: {ServiceName} v{Version} at {BaseUrl}",
            registration.ServiceName, registration.Version, registration.BaseUrl);

        return true;
    }

    public async Task UnregisterServiceAsync(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        _serviceRegistry.TryRemove(serviceName, out _);
        _serviceHealth.TryRemove(serviceName, out _);
        _circuitBreakers.TryRemove(serviceName, out _);

        _logger.LogInformation("Unregistered service: {ServiceName}", serviceName);
    }

    public async Task<ServiceDiscoveryResult> DiscoverServicesAsync()
    {
        var services = _serviceRegistry.Values.ToList();
        var healthStatus = new Dictionary<string, ServiceHealth>();

        foreach (var service in services)
        {
            var health = await GetServiceHealthAsync(service.ServiceName);
            healthStatus[service.ServiceName] = health;
        }

        return new ServiceDiscoveryResult(
            services,
            DateTimeOffset.UtcNow,
            services.Count,
            healthStatus
        );
    }

    private async Task<ServiceEndpoint?> GetHealthyEndpointAsync(string serviceName)
    {
        if (!_serviceRegistry.TryGetValue(serviceName, out var registration))
        {
            return null;
        }

        var health = await GetServiceHealthAsync(serviceName);
        if (health.Status != HealthStatus.Healthy)
        {
            return null;
        }

        return new ServiceEndpoint(serviceName, registration.BaseUrl, true, health.LastChecked);
    }

    private async Task UpdateServiceHealthAsync(string serviceName, HealthStatus status, string details)
    {
        var health = new ServiceHealth(
            serviceName,
            status,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(100), // 仮の応答時間
            new Dictionary<string, object>
            {
                ["Details"] = details,
                ["LastStatusChange"] = DateTimeOffset.UtcNow
            }
        );

        _serviceHealth[serviceName] = health;
    }

    private async void PerformHealthChecks(object state)
    {
        try
        {
            var tasks = _serviceRegistry.Keys.Select(serviceName => PerformHealthCheckAsync(serviceName));
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during service health checks");
        }
    }

    private async Task PerformHealthCheckAsync(string serviceName)
    {
        if (!_serviceRegistry.TryGetValue(serviceName, out var registration))
        {
            return;
        }

        try
        {
            var healthCheckUrl = $"{registration.BaseUrl.TrimEnd('/')}{registration.HealthCheck.Path}";
            var request = new HttpRequestMessage(registration.HealthCheck.Method, healthCheckUrl);

            using var cts = new CancellationTokenSource(registration.HealthCheck.Timeout);
            var response = await _httpClient.SendAsync(request, cts.Token);

            var isHealthy = response.StatusCode == System.Net.HttpStatusCode.OK;
            var status = isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy;

            await UpdateServiceHealthAsync(serviceName, status, response.StatusCode.ToString());
        }
        catch (Exception ex)
        {
            await UpdateServiceHealthAsync(serviceName, HealthStatus.Unhealthy, ex.Message);
        }
    }

    private async void CleanupStaleServices(object state)
    {
        try
        {
            var staleServices = _serviceHealth
                .Where(h => h.Value.LastChecked < DateTimeOffset.UtcNow.AddMinutes(-10))
                .Select(h => h.Key)
                .ToList();

            foreach (var serviceName in staleServices)
            {
                await UpdateServiceHealthAsync(serviceName, HealthStatus.Unknown, "Health check timeout");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during service cleanup");
        }
    }
}

/// <summary>
/// サービスメッシュのサーキットブレーカー
/// </summary>
public class CircuitBreaker
{
    private readonly int _failureThreshold = 5;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private CircuitState _state = CircuitState.Closed;
    private int _failureCount = 0;
    private DateTime _lastFailureTime = DateTime.MinValue;

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        if (_state == CircuitState.Open)
        {
            if (DateTime.UtcNow - _lastFailureTime > _timeout)
            {
                _state = CircuitState.HalfOpen;
            }
            else
            {
                throw new ServiceUnavailableException("Circuit breaker is Open");
            }
        }

        try
        {
            var result = await operation();
            Reset();
            return result;
        }
        catch (Exception ex)
        {
            RecordFailure();
            throw;
        }
    }

    private void RecordFailure()
    {
        _failureCount++;
        _lastFailureTime = DateTime.UtcNow;

        if (_failureCount >= _failureThreshold)
        {
            _state = CircuitState.Open;
        }
    }

    private void Reset()
    {
        _state = CircuitState.Closed;
        _failureCount = 0;
        _lastFailureTime = DateTime.MinValue;
    }

    public CircuitState GetState() => _state;
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

public class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message) : base(message) { }
}
