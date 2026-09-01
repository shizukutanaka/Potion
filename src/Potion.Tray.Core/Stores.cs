using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Potion.Tray.Core;

public sealed class FileTrayLog : ITrayLog
{
    private readonly string directory;
    private readonly ITrayClock clock;
    private readonly int retentionDays;
    private readonly object gate = new();
    private string? lastCleanupDay;

    public FileTrayLog(string? directory = null, ITrayClock? clock = null, int retentionDays = 30)
    {
        this.directory = directory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Potion", "logs");
        this.clock = clock ?? new SystemTrayClock();
        this.retentionDays = Math.Clamp(retentionDays, 1, 3650);
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var now = clock.UtcNow;
            var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, $"tray-{day}.log");
            var line = $"{now:O} [{level}] {message}";
            if (exception is not null)
            {
                line += $" {exception}";
            }

            lock (gate)
            {
                Directory.CreateDirectory(directory);
                if (!string.Equals(lastCleanupDay, day, StringComparison.Ordinal))
                {
                    Cleanup(now.Date);
                    lastCleanupDay = day;
                }

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private void Cleanup(DateTime currentDate)
    {
        var cutoff = currentDate.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(directory, "tray-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length != "tray-yyyyMMdd".Length ||
                !name.StartsWith("tray-", StringComparison.Ordinal) ||
                !DateTime.TryParseExact(
                    name["tray-".Length..],
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var fileDate) ||
                fileDate >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }
    }
}

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string path;
    private readonly ITrayLog log;
    private readonly ITrayClock clock;
    private readonly JsonSerializerOptions options = CreateOptions();

    public JsonSettingsStore(string? path = null, ITrayLog? log = null, ITrayClock? clock = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Potion",
            "settings.json");
        this.log = log ?? new FileTrayLog();
        this.clock = clock ?? new SystemTrayClock();
    }

    public TraySettings Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return NewDefaults();
            }

            var settings = JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path), options) ?? NewDefaults();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            if (File.Exists(path))
            {
                var directory = Path.GetDirectoryName(path) ?? string.Empty;
                var name = $"settings.invalid-{clock.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
                var invalidPath = Path.Combine(directory, $"{name}.json");
                var suffix = 1;
                while (File.Exists(invalidPath))
                {
                    invalidPath = Path.Combine(directory, $"{name}-{suffix++}.json");
                }
                try
                {
                    File.Move(path, invalidPath);
                    log.Error($"Invalid settings file moved to '{invalidPath}'.", ex);
                }
                catch (Exception moveException)
                {
                    log.Error($"Unable to move invalid settings file to '{invalidPath}'.", moveException);
                }
            }
            else
            {
                log.Warn("Unable to load settings; using defaults.", ex);
            }

            return NewDefaults();
        }
    }

    public void Save(TraySettings settings)
    {
        try
        {
            settings.Normalize();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, options), Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Warn("Unable to save settings.", ex);
        }
    }

    private static TraySettings NewDefaults()
    {
        var settings = new TraySettings();
        settings.Normalize();
        return settings;
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class JsonCheckStateStore : ICheckStateStore
{
    private readonly string path;
    private readonly ITrayLog log;
    private readonly JsonSerializerOptions options = new();

    public JsonCheckStateStore(string? path = null, ITrayLog? log = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Potion",
            "state.json");
        this.log = log ?? new FileTrayLog();
    }

    public IReadOnlyDictionary<string, DateTimeOffset> Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            }

            return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(
                       File.ReadAllText(path),
                       options)
                   ?? new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            log.Warn("Unable to load check state; using an empty state.", ex);
            return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(IReadOnlyDictionary<string, DateTimeOffset> lastInspections)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(lastInspections, options), Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Warn("Unable to save check state.", ex);
        }
    }
}

public sealed class JsonlHistoryStore : IHistoryStore, IDisposable
{
    private readonly string path;
    private readonly ITrayLog log;
    private readonly int maxEntries;
    private readonly int retentionDays;
    private readonly double compactionThreshold;
    private readonly ITrayClock clock;
    private readonly int pruneInterval;
    private int appendsSincePrune;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions options = CreateOptions();

    public JsonlHistoryStore(
        string? path = null,
        ITrayLog? log = null,
        int maxEntries = 1000,
        int retentionDays = 90,
        double compactionThreshold = 1.2,
        ITrayClock? clock = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Potion",
            "history.jsonl");
        this.log = log ?? new FileTrayLog();
        this.maxEntries = Math.Max(1, maxEntries);
        this.retentionDays = Math.Max(1, retentionDays);
        this.compactionThreshold = Math.Max(1.0, compactionThreshold);
        this.clock = clock ?? new SystemTrayClock();
        pruneInterval = Math.Max(20, this.maxEntries / 10);
    }

    public bool LastAppendFailed { get; private set; }

    public async Task AppendAsync(HistoryEntry entry, CancellationToken ct)
    {
        LastAppendFailed = false;
        await gate.WaitAsync(ct);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(
                path,
                JsonSerializer.Serialize(entry, options) + Environment.NewLine,
                Encoding.UTF8,
                ct);

            appendsSincePrune++;
            if (appendsSincePrune == 1 || appendsSincePrune >= pruneInterval)
            {
                var lines = await ReadEntriesAsync(ct);
                if (lines.Count > maxEntries * compactionThreshold ||
                    lines.Any(e => e.TimestampUtc < clock.UtcNow.AddDays(-retentionDays)))
                {
                    await RewriteAsync(Prune(lines), ct);
                }

                appendsSincePrune = 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastAppendFailed = true;
            log.Error("Unable to save history.", ex);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<HistoryEntry>> ReadRecentAsync(int max, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return (await ReadEntriesAsync(ct))
                .OrderByDescending(e => e.TimestampUtc)
                .Take(Math.Max(0, max))
                .ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> CountRepairAttemptsSinceAsync(string checkId, DateTimeOffset sinceUtc, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return (await ReadEntriesAsync(ct)).Count(e =>
                e.CheckId.Equals(checkId, StringComparison.OrdinalIgnoreCase) &&
                e.TimestampUtc >= sinceUtc &&
                e.Outcome is HistoryOutcome.Repaired or HistoryOutcome.RepairFailed);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HistoryEntry?> FindLastAsync(string checkId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return (await ReadEntriesAsync(ct))
                .Select((entry, index) => (entry, index))
                .Where(item => item.entry.CheckId.Equals(checkId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.entry.TimestampUtc)
                .ThenByDescending(item => item.index)
                .Select(item => item.entry)
                .FirstOrDefault();
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private async Task<List<HistoryEntry>> ReadEntriesAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new List<HistoryEntry>();
        }

        var entries = new List<HistoryEntry>();
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line, options);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
            }
        }

        return entries;
    }

    private List<HistoryEntry> Prune(IEnumerable<HistoryEntry> entries)
    {
        var cutoff = clock.UtcNow.AddDays(-retentionDays);
        return entries
            .Where(e => e.TimestampUtc >= cutoff)
            .OrderByDescending(e => e.TimestampUtc)
            .Take(maxEntries)
            .OrderBy(e => e.TimestampUtc)
            .ToList();
    }

    private async Task RewriteAsync(IEnumerable<HistoryEntry> entries, CancellationToken ct)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllLinesAsync(
            tempPath,
            entries.Select(e => JsonSerializer.Serialize(e, options)),
            Encoding.UTF8,
            ct);
        File.Move(tempPath, path, overwrite: true);
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
