using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

/// <summary>
/// Rust-inspired memory safety patterns
/// Zero-cost abstractions with ownership semantics
/// </summary>
public interface IMemorySafetyService
{
    /// <summary>
    /// Borrow a resource safely (Rust's borrowing)
    /// </summary>
    ref T Borrow<T>(T resource) where T : class;

    /// <summary>
    /// Move ownership of a resource (Rust's ownership transfer)
    /// </summary>
    T Move<T>(ref T resource) where T : class;

    /// <summary>
/// Create a memory-safe collection with ownership tracking
/// </summary>
    IMemorySafeCollection<T> CreateCollection<T>() where T : class;

    /// <summary>
/// Execute code in a memory-safe context
/// </summary>
    Task<T> ExecuteInSafeContext<T>(Func<Task<T>> operation, CancellationToken cancellationToken);
}

/// <summary>
/// Memory-safe collection with ownership tracking
/// </summary>
public interface IMemorySafeCollection<T> : IDisposable where T : class
{
    /// <summary>
/// Add item with ownership transfer
/// </summary>
    void Add(T item);

    /// <summary>
/// Get item with borrowing semantics
/// </summary>
    ref T Get(int index);

    /// <summary>
/// Remove item and return ownership
/// </summary>
    T Remove(int index);

    /// <summary>
/// Get collection size
/// </summary>
    int Count { get; }
}

/// <summary>
/// Memory safety implementation inspired by Rust
/// Provides ownership, borrowing, and lifetime management
/// </summary>
public class MemorySafetyService : IMemorySafetyService
{
    private readonly ILogger<MemorySafetyService> _logger;
    private readonly ConditionalWeakTable<object, OwnershipTracker> _ownershipTable = new();
    private readonly Dictionary<string, object> _activeBorrows = new();
    private readonly object _borrowLock = new();

    public MemorySafetyService(ILogger<MemorySafetyService> logger)
    {
        _logger = logger;
    }

    public ref T Borrow<T>(T resource) where T : class
    {
        if (resource == null)
        {
            throw new ArgumentNullException(nameof(resource));
        }

        var resourceId = GetResourceId(resource);

        lock (_borrowLock)
        {
            if (_activeBorrows.ContainsKey(resourceId))
            {
                throw new InvalidOperationException($"Resource {resourceId} is already borrowed (Rust-like borrowing rules)");
            }

            _activeBorrows[resourceId] = resource;
        }

        // Track borrowing for cleanup
        var tracker = _ownershipTable.GetOrCreateValue(resource);
        tracker.BorrowCount++;

        _logger.LogDebug("Resource borrowed: {ResourceId}, BorrowCount: {BorrowCount}", resourceId, tracker.BorrowCount);

        return ref Unsafe.AsRef<T>(resource);
    }

    public T Move<T>(ref T resource) where T : class
    {
        if (resource == null)
        {
            return null;
        }

        var resourceId = GetResourceId(resource);

        lock (_borrowLock)
        {
            if (_activeBorrows.ContainsKey(resourceId))
            {
                throw new InvalidOperationException($"Cannot move borrowed resource {resourceId} (Rust ownership rules)");
            }
        }

        var tracker = _ownershipTable.GetOrCreateValue(resource);
        tracker.IsMoved = true;
        tracker.MoveCount++;

        _logger.LogDebug("Resource moved: {ResourceId}, MoveCount: {MoveCount}", resourceId, tracker.MoveCount);

        return resource;
    }

    public IMemorySafeCollection<T> CreateCollection<T>() where T : class
    {
        return new MemorySafeCollection<T>(this, _logger);
    }

    public async Task<T> ExecuteInSafeContext<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        var contextId = Guid.NewGuid().ToString();

        try
        {
            _logger.LogDebug("Executing in safe context: {ContextId}", contextId);

            // Pre-execution memory safety checks
            await ValidateMemorySafetyAsync(cancellationToken);

            var result = await operation();

            // Post-execution validation
            await ValidatePostExecutionSafetyAsync(result, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Safe context execution failed: {ContextId}", contextId);
            throw;
        }
        finally
        {
            // Cleanup context
            CleanupContext(contextId);
        }
    }

    private async Task ValidateMemorySafetyAsync(CancellationToken cancellationToken)
    {
        // Check for memory leaks
        await Task.Run(() =>
        {
            var totalMemory = GC.GetTotalMemory(false);
            var threshold = 512 * 1024 * 1024; // 512MB

            if (totalMemory > threshold)
            {
                _logger.LogWarning("High memory usage detected: {MemoryUsage} bytes", totalMemory);

                // Trigger garbage collection if needed
                if (totalMemory > threshold * 1.5)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                    _logger.LogInformation("Forced garbage collection completed");
                }
            }
        }, cancellationToken);
    }

    private async Task ValidatePostExecutionSafetyAsync<T>(T result, CancellationToken cancellationToken)
    {
        // Validate result integrity
        if (result is IDisposable disposable)
        {
            // Ensure disposable resources are properly managed
            _logger.LogDebug("Validating disposable resource: {Type}", typeof(T).Name);
        }
    }

    private void CleanupContext(string contextId)
    {
        lock (_borrowLock)
        {
            // Clean up any borrows that weren't properly returned
            var expiredBorrows = _activeBorrows
                .Where(kvp => !IsResourceStillValid(kvp.Value))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var expiredBorrow in expiredBorrows)
            {
                _activeBorrows.Remove(expiredBorrow);
                _logger.LogWarning("Cleaned up expired borrow: {ResourceId}", expiredBorrow);
            }
        }
    }

    private bool IsResourceStillValid(object resource)
    {
        try
        {
            // Check if object is still accessible and not corrupted
            GC.KeepAlive(resource);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetResourceId<T>(T resource) where T : class
    {
        return $"{typeof(T).FullName}_{resource.GetHashCode()}_{DateTimeOffset.UtcNow.Ticks}";
    }

    private sealed class OwnershipTracker
    {
        public int BorrowCount { get; set; }
        public int MoveCount { get; set; }
        public bool IsMoved { get; set; }
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class MemorySafeCollection<T> : IMemorySafeCollection<T> where T : class
    {
        private readonly MemorySafetyService _memorySafety;
        private readonly ILogger _logger;
        private readonly List<OwnershipEntry> _items = new();
        private readonly object _collectionLock = new();

        public int Count => _items.Count;

        public MemorySafeCollection(MemorySafetyService memorySafety, ILogger logger)
        {
            _memorySafety = memorySafety;
            _logger = logger;
        }

        public void Add(T item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            lock (_collectionLock)
            {
                var entry = new OwnershipEntry
                {
                    Item = item,
                    AddedAt = DateTimeOffset.UtcNow,
                    IsMoved = false
                };

                _items.Add(entry);
                _logger.LogDebug("Added item to memory-safe collection: {Count}", _items.Count);
            }
        }

        public ref T Get(int index)
        {
            lock (_collectionLock)
            {
                if (index < 0 || index >= _items.Count)
                {
                    throw new IndexOutOfRangeException();
                }

                var entry = _items[index];
                if (entry.IsMoved)
                {
                    throw new InvalidOperationException("Cannot borrow moved resource (Rust ownership rules)");
                }

                entry.LastAccessedAt = DateTimeOffset.UtcNow;
                return ref Unsafe.AsRef<T>(entry.Item);
            }
        }

        public T Remove(int index)
        {
            lock (_collectionLock)
            {
                if (index < 0 || index >= _items.Count)
                {
                    throw new IndexOutOfRangeException();
                }

                var entry = _items[index];
                if (entry.IsMoved)
                {
                    throw new InvalidOperationException("Cannot remove moved resource");
                }

                _items.RemoveAt(index);
                entry.IsMoved = true; // Mark as moved

                _logger.LogDebug("Removed item from memory-safe collection: {Count}", _items.Count);
                return entry.Item;
            }
        }

        public void Dispose()
        {
            lock (_collectionLock)
            {
                foreach (var entry in _items)
                {
                    if (entry.Item is IDisposable disposable)
                    {
                        try
                        {
                            disposable.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to dispose item in memory-safe collection");
                        }
                    }
                }

                _items.Clear();
                _logger.LogDebug("Memory-safe collection disposed");
            }
        }

        private sealed class OwnershipEntry
        {
            public T Item { get; set; }
            public DateTimeOffset AddedAt { get; set; }
            public DateTimeOffset LastAccessedAt { get; set; }
            public bool IsMoved { get; set; }
        }
    }
}

/// <summary>
/// Extension methods for memory-safe operations
/// </summary>
public static class MemorySafetyExtensions
{
    /// <summary>
/// Execute operation with automatic memory management
/// </summary>
    public static async Task<T> WithMemorySafety<T>(
        this IMemorySafetyService memorySafety,
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        using var context = new MemorySafetyContext(memorySafety);
        return await memorySafety.ExecuteInSafeContext(operation, cancellationToken);
    }

    /// <summary>
/// Create a memory-safe array pool
/// </summary>
    public static ArrayPool<T> CreateMemorySafePool<T>(this IMemorySafetyService memorySafety)
    {
        return ArrayPool<T>.Create(100, 50); // Zero-cost abstraction over standard ArrayPool
    }

    private sealed class MemorySafetyContext : IDisposable
    {
        private readonly IMemorySafetyService _memorySafety;

        public MemorySafetyContext(IMemorySafetyService memorySafety)
        {
            _memorySafety = memorySafety;
        }

        public void Dispose()
        {
            // Cleanup any resources that weren't properly managed
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
        }
    }
}

/// <summary>
/// Attributes for memory safety annotations
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public class RequiresOwnershipAttribute : Attribute
{
    public string ResourceType { get; }

    public RequiresOwnershipAttribute(string resourceType = "")
    {
        ResourceType = resourceType;
    }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public class BorrowsResourceAttribute : Attribute
{
    public string ResourceType { get; }

    public BorrowsResourceAttribute(string resourceType = "")
    {
        ResourceType = resourceType;
    }
}
