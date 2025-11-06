using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class LogAnalysisOptions
{
    public bool Enabled { get; set; } = false;
    public int AnalysisIntervalMinutes { get; set; } = 5;
    public string LogDirectory { get; set; } = "logs";
    public List<string> LogPatterns { get; set; } = new();
    public int MaxLogEntries { get; set; } = 10000;
    public double AnomalyThreshold { get; set; } = 2.0; // Standard deviations
}

public class LogAnalysisCorrelationService : IHostedService, IDisposable
{
    private readonly ILogger<LogAnalysisCorrelationService> _logger;
    private readonly LogAnalysisOptions _options;
    private readonly EventCorrelationService _eventCorrelationService;
    private readonly ConcurrentDictionary<string, LogPattern> _patterns = new();
    private Timer? _analysisTimer;

    public LogAnalysisCorrelationService(
        ILogger<LogAnalysisCorrelationService> logger,
        IOptions<LogAnalysisOptions> options,
        EventCorrelationService eventCorrelationService)
    {
        _logger = logger;
        _options = options.Value;
        _eventCorrelationService = eventCorrelationService;
        InitializePatterns();
    }

    private void InitializePatterns()
    {
        // Common log patterns to analyze
        _patterns["error"] = new LogPattern
        {
            Name = "Error Pattern",
            Regex = new Regex(@"ERROR|Exception|Failed", RegexOptions.IgnoreCase),
            Severity = "Error",
            Category = "ApplicationError"
        };

        _patterns["warning"] = new LogPattern
        {
            Name = "Warning Pattern",
            Regex = new Regex(@"WARN|Warning", RegexOptions.IgnoreCase),
            Severity = "Warning",
            Category = "ApplicationWarning"
        };

        _patterns["timeout"] = new LogPattern
        {
            Name = "Timeout Pattern",
            Regex = new Regex(@"timeout|timed out", RegexOptions.IgnoreCase),
            Severity = "Error",
            Category = "Timeout"
        };

        _patterns["security"] = new LogPattern
        {
            Name = "Security Pattern",
            Regex = new Regex(@"unauthorized|forbidden|security|breach", RegexOptions.IgnoreCase),
            Severity = "Critical",
            Category = "Security"
        };

        _patterns["performance"] = new LogPattern
        {
            Name = "Performance Pattern",
            Regex = new Regex(@"slow|performance|latency", RegexOptions.IgnoreCase),
            Severity = "Warning",
            Category = "Performance"
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Log analysis and correlation is disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting log analysis and correlation service");

        _analysisTimer = new Timer(AnalyzeLogs, null, TimeSpan.Zero,
            TimeSpan.FromMinutes(_options.AnalysisIntervalMinutes));

        return Task.CompletedTask;
    }

    private async void AnalyzeLogs(object? state)
    {
        try
        {
            var logEntries = await CollectLogEntriesAsync();
            var analyzedEntries = AnalyzeLogEntries(logEntries);
            var correlations = CorrelateLogEntries(analyzedEntries);

            await ProcessCorrelationsAsync(correlations);
            await GenerateAnalysisReportAsync(analyzedEntries);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze logs");
        }
    }

    private async Task<List<LogEntry>> CollectLogEntriesAsync()
    {
        var logEntries = new List<LogEntry>();
        var logDir = Path.Combine(ServicePaths.Base, _options.LogDirectory);

        if (!Directory.Exists(logDir))
            return logEntries;

        var logFiles = Directory.GetFiles(logDir, "*.log", SearchOption.AllDirectories)
                               .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                               .Take(10); // Analyze last 10 log files

        foreach (var logFile in logFiles)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(logFile);
                var recentLines = lines.Reverse().Take(1000).Reverse(); // Last 1000 lines

                foreach (var line in recentLines)
                {
                    var entry = ParseLogEntry(line, Path.GetFileName(logFile));
                    if (entry != null)
                    {
                        logEntries.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read log file: {File}", logFile);
            }
        }

        // Keep only recent entries (last 24 hours)
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        logEntries = logEntries.Where(e => e.Timestamp >= cutoff).ToList();

        // Limit total entries
        if (logEntries.Count > _options.MaxLogEntries)
        {
            logEntries = logEntries.OrderByDescending(e => e.Timestamp)
                                  .Take(_options.MaxLogEntries)
                                  .ToList();
        }

        return logEntries;
    }

    private LogEntry? ParseLogEntry(string line, string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Simple log parsing - could be enhanced for specific log formats
        var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return null;

        DateTimeOffset timestamp;
        if (!DateTimeOffset.TryParse(parts[0] + " " + parts[1], out timestamp))
        {
            timestamp = DateTimeOffset.UtcNow; // Fallback
        }

        var level = parts.Length > 2 ? parts[2] : "INFO";
        var message = parts.Length > 3 ? parts[3] : line;

        return new LogEntry
        {
            Timestamp = timestamp,
            Level = level,
            Message = message,
            Source = sourceFile,
            RawLine = line
        };
    }

    private List<AnalyzedLogEntry> AnalyzeLogEntries(List<LogEntry> entries)
    {
        var analyzedEntries = new List<AnalyzedLogEntry>();

        foreach (var entry in entries)
        {
            var analyzed = new AnalyzedLogEntry
            {
                Entry = entry,
                MatchedPatterns = new List<string>()
            };

            // Match against patterns
            foreach (var pattern in _patterns)
            {
                if (pattern.Value.Regex.IsMatch(entry.Message))
                {
                    analyzed.MatchedPatterns.Add(pattern.Key);
                    analyzed.PrimaryCategory = pattern.Value.Category;
                    analyzed.Severity = pattern.Value.Severity;
                }
            }

            analyzedEntries.Add(analyzed);
        }

        return analyzedEntries;
    }

    private List<LogCorrelation> CorrelateLogEntries(List<AnalyzedLogEntry> entries)
    {
        var correlations = new List<LogCorrelation>();

        // Group by time windows (5-minute windows)
        var timeGroups = entries
            .Where(e => e.MatchedPatterns.Any())
            .GroupBy(e => e.Entry.Timestamp.Ticks / TimeSpan.FromMinutes(5).Ticks)
            .Where(g => g.Count() >= 3) // At least 3 related entries
            .Select(g => new
            {
                TimeWindow = g.Key,
                Entries = g.ToList(),
                PatternCounts = g.SelectMany(e => e.MatchedPatterns)
                                .GroupBy(p => p)
                                .ToDictionary(g => g.Key, g => g.Count())
            });

        foreach (var group in timeGroups)
        {
            var dominantPattern = group.PatternCounts
                                      .OrderByDescending(p => p.Value)
                                      .FirstOrDefault();

            if (dominantPattern.Value >= 3) // At least 3 occurrences of same pattern
            {
                correlations.Add(new LogCorrelation
                {
                    Pattern = dominantPattern.Key,
                    Count = dominantPattern.Value,
                    TimeWindow = DateTimeOffset.FromUnixTimeSeconds(group.TimeWindow * 300), // 5 min windows
                    Entries = group.Entries.Where(e => e.MatchedPatterns.Contains(dominantPattern.Key)).ToList(),
                    Severity = group.Entries.First().Severity
                });
            }
        }

        return correlations;
    }

    private async Task ProcessCorrelationsAsync(List<LogCorrelation> correlations)
    {
        foreach (var correlation in correlations)
        {
            // Record correlation event
            _eventCorrelationService.RecordEvent(
                "log_correlation",
                new
                {
                    Pattern = correlation.Pattern,
                    Count = correlation.Count,
                    Severity = correlation.Severity
                },
                correlation.TimeWindow);

            _logger.LogWarning("Log correlation detected: {Pattern} ({Count} occurrences) in {Severity} severity",
                correlation.Pattern, correlation.Count, correlation.Severity);
        }
    }

    private async Task GenerateAnalysisReportAsync(List<AnalyzedLogEntry> entries)
    {
        var report = new LogAnalysisReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalEntries = entries.Count,
            PatternStatistics = entries
                .SelectMany(e => e.MatchedPatterns)
                .GroupBy(p => p)
                .ToDictionary(g => g.Key, g => g.Count()),
            SeverityBreakdown = entries
                .Where(e => !string.IsNullOrEmpty(e.Severity))
                .GroupBy(e => e.Severity)
                .ToDictionary(g => g.Key, g => g.Count()),
            TopErrorMessages = entries
                .Where(e => e.Severity == "Error" || e.Severity == "Critical")
                .GroupBy(e => e.Entry.Message)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToDictionary(g => g.Key, g => g.Count())
        };

        // Save report
        var reportDir = Path.Combine(ServicePaths.Base, "reports/log-analysis");
        Directory.CreateDirectory(reportDir);

        var fileName = $"log_analysis_{report.GeneratedAt:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(reportDir, fileName);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);
        _logger.LogInformation("Log analysis report saved to {Path}", filePath);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _analysisTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _analysisTimer?.Dispose();
    }
}

public class LogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string RawLine { get; set; } = string.Empty;
}

public class AnalyzedLogEntry
{
    public LogEntry Entry { get; set; } = new();
    public List<string> MatchedPatterns { get; set; } = new();
    public string? PrimaryCategory { get; set; }
    public string? Severity { get; set; }
}

public class LogCorrelation
{
    public string Pattern { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTimeOffset TimeWindow { get; set; }
    public List<AnalyzedLogEntry> Entries { get; set; } = new();
    public string Severity { get; set; } = string.Empty;
}

public class LogAnalysisReport
{
    public DateTimeOffset GeneratedAt { get; set; }
    public int TotalEntries { get; set; }
    public Dictionary<string, int> PatternStatistics { get; set; } = new();
    public Dictionary<string, int> SeverityBreakdown { get; set; } = new();
    public Dictionary<string, int> TopErrorMessages { get; set; } = new();
}

public class LogPattern
{
    public string Name { get; set; } = string.Empty;
    public Regex Regex { get; set; } = new Regex("");
    public string Severity { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}
