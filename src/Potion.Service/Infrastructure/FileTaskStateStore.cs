using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Potion.Service.Infrastructure;

public interface ITaskStateStore
{
    Task<TaskState?> LoadAsync(string taskName, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, TaskState>> LoadAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(TaskState state, CancellationToken cancellationToken);
}

public sealed class FileTaskStateStore(ILogger<FileTaskStateStore> logger) : ITaskStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<TaskState?> LoadAsync(string taskName, CancellationToken cancellationToken)
    {
        var path = ServicePaths.GetTaskStatePath(taskName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<TaskState>(stream, SerializerOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load task state from {Path}", path);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, TaskState>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, TaskState>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(ServicePaths.State, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                var state = await JsonSerializer.DeserializeAsync<TaskState>(stream, SerializerOptions, cancellationToken);
                if (state is { TaskName.Length: > 0 })
                {
                    states[state.TaskName] = state;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load task state from {File}", file);
            }
        }

        return states;
    }

    public async Task SaveAsync(TaskState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.TaskName))
        {
            throw new ArgumentException("TaskName must be provided", nameof(state));
        }

        var path = ServicePaths.GetTaskStatePath(state.TaskName);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempFileName = $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp";
        var tempPath = Path.Join(directory, tempFileName);

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
#if NET8_0_OR_GREATER
                await stream.FlushAsync(flushToDisk: true, cancellationToken);
#else
                stream.Flush(true);
#endif
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save task state to {Path}", path);
            throw;
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
