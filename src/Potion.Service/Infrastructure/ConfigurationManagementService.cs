using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

/// <summary>
/// 設定ホットリロードサービス
/// 設定変更のリアルタイム反映とフィーチャーフラグ管理
/// </summary>
public interface IConfigurationHotReloadService
{
    Task<T> GetFeatureFlagAsync<T>(string key, T defaultValue = default!);
    Task SetFeatureFlagAsync<T>(string key, T value);
    Task<bool> IsFeatureEnabledAsync(string featureName);
    Task<IEnumerable<string>> GetAllFeatureFlagsAsync();
    Task<ConfigurationSnapshot> GetConfigurationSnapshotAsync();
    event Action<ConfigurationChangeEventArgs>? OnConfigurationChanged;
}

/// <summary>
/// 設定変更イベント引数
/// </summary>
public class ConfigurationChangeEventArgs : EventArgs
{
    public string Key { get; }
    public object? OldValue { get; }
    public object? NewValue { get; }
    public DateTimeOffset ChangedAt { get; }

    public ConfigurationChangeEventArgs(string key, object? oldValue, object? newValue)
    {
        Key = key;
        OldValue = oldValue;
        NewValue = newValue;
        ChangedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// 設定スナップショット
/// </summary>
public class ConfigurationSnapshot
{
    public DateTimeOffset Timestamp { get; set; }
    public Dictionary<string, object?> Settings { get; set; } = new();
    public Dictionary<string, object?> FeatureFlags { get; set; } = new();
    public int TotalSettings { get; set; }
    public int EnabledFeatures { get; set; }
}

/// <summary>
/// 設定ホットリロードサービス実装
/// </summary>
public class ConfigurationHotReloadService : IConfigurationHotReloadService
{
    private readonly ILogger<ConfigurationHotReloadService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, object> _featureFlags = new();
    private readonly ConcurrentDictionary<string, IChangeToken> _changeTokens = new();
    private readonly Timer _monitoringTimer;

    public event Action<ConfigurationChangeEventArgs>? OnConfigurationChanged;

    public ConfigurationHotReloadService(
        IConfiguration configuration,
        ILogger<ConfigurationHotReloadService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _monitoringTimer = new Timer(MonitorConfigurationChanges, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        LoadInitialFeatureFlags();
        SetupChangeMonitoring();
    }

    public async Task<T> GetFeatureFlagAsync<T>(string key, T defaultValue = default!)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_featureFlags.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }

        // 設定から取得を試行
        var configValue = _configuration.GetValue<T>(key);
        if (configValue != null)
        {
            _featureFlags[key] = configValue;
            return configValue;
        }

        // デフォルト値を設定
        _featureFlags[key] = defaultValue;
        return defaultValue;
    }

    public async Task SetFeatureFlagAsync<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var oldValue = _featureFlags.GetOrAdd(key, value);
        _featureFlags[key] = value;

        OnConfigurationChanged?.Invoke(new ConfigurationChangeEventArgs(key, oldValue, value));

        _logger.LogInformation("Updated feature flag: {Key} = {Value}", key, value);
    }

    public async Task<bool> IsFeatureEnabledAsync(string featureName)
    {
        var value = await GetFeatureFlagAsync(featureName, false);
        return value is bool boolValue ? boolValue : false;
    }

    public async Task<IEnumerable<string>> GetAllFeatureFlagsAsync()
    {
        return _featureFlags.Keys.ToList();
    }

    public async Task<ConfigurationSnapshot> GetConfigurationSnapshotAsync()
    {
        var snapshot = new ConfigurationSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            TotalSettings = _configuration.AsEnumerable().Count(),
            EnabledFeatures = await GetEnabledFeaturesCountAsync()
        };

        // 設定を収集
        foreach (var (key, value) in _configuration.AsEnumerable())
        {
            if (!string.IsNullOrEmpty(key))
            {
                snapshot.Settings[key] = value;
            }
        }

        // フィーチャーフラグを収集
        foreach (var (key, value) in _featureFlags)
        {
            snapshot.FeatureFlags[key] = value;
        }

        return snapshot;
    }

    private void LoadInitialFeatureFlags()
    {
        var featureFlagSection = _configuration.GetSection("FeatureFlags");
        if (featureFlagSection.Exists())
        {
            foreach (var (key, value) in featureFlagSection.AsEnumerable())
            {
                if (!string.IsNullOrEmpty(key) && value != null)
                {
                    _featureFlags[key] = value;
                }
            }
        }

        _logger.LogInformation("Loaded {Count} feature flags", _featureFlags.Count);
    }

    private void SetupChangeMonitoring()
    {
        var changeToken = _configuration.GetReloadToken();
        changeToken.RegisterChangeCallback(ConfigurationChanged, changeToken);
    }

    private void ConfigurationChanged(object state)
    {
        try
        {
            var token = (IChangeToken)state;
            _logger.LogInformation("Configuration changed, reloading...");

            // フィーチャーフラグをリロード
            LoadInitialFeatureFlags();

            // 変更通知を発行
            OnConfigurationChanged?.Invoke(new ConfigurationChangeEventArgs("Configuration", null, "Reloaded"));

            // 新しいトークンで監視を再設定
            SetupChangeMonitoring();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling configuration change");
        }
    }

    private void MonitorConfigurationChanges(object state)
    {
        try
        {
            // 定期的に設定の有効性をチェック
            var snapshot = GetConfigurationSnapshotAsync().GetAwaiter().GetResult();

            _logger.LogDebug("Configuration monitoring: {TotalSettings} settings, {EnabledFeatures} enabled features",
                snapshot.TotalSettings, snapshot.EnabledFeatures);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in configuration monitoring");
        }
    }

    private async Task<int> GetEnabledFeaturesCountAsync()
    {
        var enabledCount = 0;

        foreach (var (key, value) in _featureFlags)
        {
            if (value is bool boolValue && boolValue)
            {
                enabledCount++;
            }
        }

        return enabledCount;
    }

    public void Dispose()
    {
        _monitoringTimer?.Dispose();
    }
}

/// <summary>
/// フィーチャーフラグ管理サービス
/// </summary>
public interface IFeatureFlagService
{
    Task<bool> IsFeatureEnabledAsync(string featureName);
    Task<T> GetFeatureValueAsync<T>(string featureName, T defaultValue = default!);
    Task EnableFeatureAsync(string featureName);
    Task DisableFeatureAsync(string featureName);
    Task<FeatureFlagInfo> GetFeatureInfoAsync(string featureName);
    Task<IEnumerable<FeatureFlagInfo>> GetAllFeaturesAsync();
}

/// <summary>
/// フィーチャーフラグ情報
/// </summary>
public record FeatureFlagInfo(
    string Name,
    bool IsEnabled,
    object? Value,
    string Description,
    DateTimeOffset LastModified);

/// <summary>
/// フィーチャーフラグサービス実装
/// </summary>
public class FeatureFlagService : IFeatureFlagService
{
    private readonly IConfigurationHotReloadService _hotReloadService;
    private readonly ILogger<FeatureFlagService> _logger;
    private readonly ConcurrentDictionary<string, FeatureFlagInfo> _featureCache = new();

    public FeatureFlagService(
        IConfigurationHotReloadService hotReloadService,
        ILogger<FeatureFlagService> logger)
    {
        _hotReloadService = hotReloadService;
        _logger = logger;
    }

    public async Task<bool> IsFeatureEnabledAsync(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);

        var value = await _hotReloadService.GetFeatureFlagAsync(featureName, false);
        return value is bool boolValue ? boolValue : false;
    }

    public async Task<T> GetFeatureValueAsync<T>(string featureName, T defaultValue = default!)
    {
        return await _hotReloadService.GetFeatureFlagAsync(featureName, defaultValue);
    }

    public async Task EnableFeatureAsync(string featureName)
    {
        await _hotReloadService.SetFeatureFlagAsync(featureName, true);
        _logger.LogInformation("Enabled feature: {FeatureName}", featureName);
    }

    public async Task DisableFeatureAsync(string featureName)
    {
        await _hotReloadService.SetFeatureFlagAsync(featureName, false);
        _logger.LogInformation("Disabled feature: {FeatureName}", featureName);
    }

    public async Task<FeatureFlagInfo> GetFeatureInfoAsync(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);

        if (_featureCache.TryGetValue(featureName, out var cachedInfo))
        {
            return cachedInfo;
        }

        var value = await _hotReloadService.GetFeatureFlagAsync(featureName, false);
        var isEnabled = value is bool boolValue ? boolValue : false;

        var info = new FeatureFlagInfo(
            Name: featureName,
            IsEnabled: isEnabled,
            Value: value,
            Description: $"Feature flag for {featureName}",
            LastModified: DateTimeOffset.UtcNow
        );

        _featureCache[featureName] = info;
        return info;
    }

    public async Task<IEnumerable<FeatureFlagInfo>> GetAllFeaturesAsync()
    {
        var featureNames = await _hotReloadService.GetAllFeatureFlagsAsync();
        var features = new List<FeatureFlagInfo>();

        foreach (var name in featureNames)
        {
            var info = await GetFeatureInfoAsync(name);
            features.Add(info);
        }

        return features.OrderBy(f => f.Name);
    }
}
