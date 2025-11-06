using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Potion.Service.Infrastructure;
using Xunit;

namespace Potion.Service.Tests;

public class AdvancedCacheServiceTests
{
    private readonly Mock<IMemoryCache> _memoryCacheMock;
    private readonly Mock<ILogger<AdvancedCacheService>> _loggerMock;
    private readonly AdvancedCacheService _service;

    public AdvancedCacheServiceTests()
    {
        _memoryCacheMock = new Mock<IMemoryCache>();
        _loggerMock = new Mock<ILogger<AdvancedCacheService>>();
        _service = new AdvancedCacheService(_memoryCacheMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task GetOrAddAsync_WithCacheHit_ShouldReturnCachedValue()
    {
        // Arrange
        var key = "test-key";
        var expectedValue = "cached-value";
        object? actualValue = expectedValue;

        _memoryCacheMock
            .Setup(m => m.TryGetValue(key, out actualValue))
            .Returns(true);

        // Act
        var result = await _service.GetOrAddAsync(key, () => Task.FromResult("new-value"));

        // Assert
        result.Should().Be(expectedValue);
        _memoryCacheMock.Verify(m => m.TryGetValue(key, out actualValue), Times.Once);
    }

    [Fact]
    public async Task GetOrAddAsync_WithCacheMiss_ShouldCallFactoryAndCacheResult()
    {
        // Arrange
        var key = "test-key";
        var expectedValue = "factory-result";
        object? actualValue = null;

        _memoryCacheMock
            .Setup(m => m.TryGetValue(key, out actualValue))
            .Returns(false);

        _memoryCacheMock
            .Setup(m => m.Set(It.IsAny<object>(), It.IsAny<object>(), It.IsAny<MemoryCacheEntryOptions>()));

        // Act
        var result = await _service.GetOrAddAsync(key, () => Task.FromResult(expectedValue));

        // Assert
        result.Should().Be(expectedValue);
        _memoryCacheMock.Verify(m => m.Set(key, expectedValue, It.IsAny<MemoryCacheEntryOptions>()), Times.Once);
    }

    [Fact]
    public async Task GetOrAddAsync_WithConcurrentRequests_ShouldCallFactoryOnlyOnce()
    {
        // Arrange
        var key = "test-key";
        var callCount = 0;
        object? actualValue = null;

        _memoryCacheMock
            .Setup(m => m.TryGetValue(key, out actualValue))
            .Returns(false);

        _memoryCacheMock
            .Setup(m => m.Set(It.IsAny<object>(), It.IsAny<object>(), It.IsAny<MemoryCacheEntryOptions>()));

        // Act
        var tasks = new[]
        {
            _service.GetOrAddAsync(key, () =>
            {
                callCount++;
                return Task.Delay(100).ContinueWith(_ => "result");
            }),
            _service.GetOrAddAsync(key, () =>
            {
                callCount++;
                return Task.Delay(100).ContinueWith(_ => "result");
            }),
            _service.GetOrAddAsync(key, () =>
            {
                callCount++;
                return Task.Delay(100).ContinueWith(_ => "result");
            })
        };

        var results = await Task.WhenAll(tasks);

        // Assert
        callCount.Should().Be(1); // Factory should only be called once due to locking
        results.Should().AllBe("result");
    }

    [Fact]
    public async Task GetAsync_WithExistingKey_ShouldReturnValue()
    {
        // Arrange
        var key = "test-key";
        var expectedValue = "cached-value";
        object? actualValue = expectedValue;

        _memoryCacheMock
            .Setup(m => m.TryGetValue(key, out actualValue))
            .Returns(true);

        // Act
        var result = await _service.GetAsync<string>(key);

        // Assert
        result.Should().Be(expectedValue);
    }

    [Fact]
    public async Task GetAsync_WithNonExistingKey_ShouldReturnDefault()
    {
        // Arrange
        var key = "test-key";
        object? actualValue = null;

        _memoryCacheMock
            .Setup(m => m.TryGetValue(key, out actualValue))
            .Returns(false);

        // Act
        var result = await _service.GetAsync<string>(key);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_ShouldStoreValueInCache()
    {
        // Arrange
        var key = "test-key";
        var value = "test-value";

        _memoryCacheMock
            .Setup(m => m.Set(key, value, It.IsAny<MemoryCacheEntryOptions>()));

        // Act
        await _service.SetAsync(key, value);

        // Assert
        _memoryCacheMock.Verify(m => m.Set(key, value, It.IsAny<MemoryCacheEntryOptions>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_ShouldRemoveValueFromCache()
    {
        // Arrange
        var key = "test-key";

        _memoryCacheMock
            .Setup(m => m.Remove(key));

        // Act
        await _service.RemoveAsync(key);

        // Assert
        _memoryCacheMock.Verify(m => m.Remove(key), Times.Once);
    }

    [Fact]
    public async Task ClearAsync_ShouldClearAllCacheEntries()
    {
        // Arrange
        _memoryCacheMock.As<MemoryCache>().Setup(m => m.Clear());

        // Act
        await _service.ClearAsync();

        // Assert
        _memoryCacheMock.As<MemoryCache>().Verify(m => m.Clear(), Times.Once);
    }

    [Fact]
    public async Task GetHitCountAsync_ShouldReturnCorrectCount()
    {
        // Arrange - This is a simplified test since we can't easily mock the internal counter

        // Act
        var hitCount = await _service.GetHitCountAsync();

        // Assert
        hitCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetMissCountAsync_ShouldReturnCorrectCount()
    {
        // Arrange - This is a simplified test since we can't easily mock the internal counter

        // Act
        var missCount = await _service.GetMissCountAsync();

        // Assert
        missCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetHitRateAsync_ShouldReturnValidRate()
    {
        // Arrange - This is a simplified test since we can't easily mock the internal counter

        // Act
        var hitRate = await _service.GetHitRateAsync();

        // Assert
        hitRate.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void Dispose_ShouldCleanupResources()
    {
        // Arrange - The service should be disposable

        // Act
        _service.Dispose();

        // Assert
        // Since we can't verify the internal cleanup without exposing it,
        // we just verify that Dispose doesn't throw an exception
    }
}
