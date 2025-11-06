using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class EventCorrelationOptions
{
    public bool Enabled { get; set; } = false;
    public int CorrelationWindowMinutes { get; set; } = 5;
    public int MaxEventsToCorrelate { get; set; } = 1000;
    public List<string> CorrelationRules { get; set; } = new();
}

public class EventCorrelationService : IHostedService, IDisposable
{
    private readonly ILogger<EventCorrelationService> _logger;
    private readonly EventCorrelationOptions _options;
    private readonly ConcurrentQueue<SystemEvent> _eventBuffer = new();
    private readonly List<CorrelationRule> _rules = new();
    private Timer? _correlationTimer;

    public EventCorrelationService(
        ILogger<EventCorrelationService> logger,
        IOptions<EventCorrelationOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        InitializeRules();
    }

    private void InitializeRules()
    {
        // Default correlation rules
        _rules.Add(new CorrelationRule
        {
            Name = "High CPU + Memory Pressure",
            Conditions = new List<EventCondition>
            {
                new EventCondition { EventType = "cpu_usage", Operator = ">", Threshold = 90.0 },
                new EventCondition { EventType = "memory_usage", Operator = ">", Threshold = 85.0 }
            },
            Severity = "Critical",
            Description = "System under extreme resource pressure"
        });

        _rules.Add(new CorrelationRule
        {
            Name = "Network + Disk I/O Storm",
            Conditions = new List<EventCondition>
            {
                new EventCondition { EventType = "network_bytes_per_sec", Operator = ">", Threshold = 100000000 }, // 100MB/s
                new EventCondition { EventType = "disk_write_bytes_per_sec", Operator = ">", Threshold = 50000000 } // 50MB/s
            },
            Severity = "High",
            Description = "High I/O activity detected"
        });

        _rules.Add(new CorrelationRule
        {
            Name = "Service Failures Cascade",
            Conditions = new List<EventCondition>
            {
                new EventCondition { EventType = "service_failed", Operator = "count", Threshold = 3 },
                new EventCondition { EventType = "error_logged", Operator = "count", Threshold = 10 }
            },
            Severity = "High",
            Description = "Multiple service failures detected"
        });

        // Add custom rules from configuration
        foreach (var ruleConfig in _options.CorrelationRules)
        {
            // Parse and add custom rules if needed
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Event correlation is disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting event correlation service");

        _correlationTimer = new Timer(ProcessEventCorrelations, null, TimeSpan.Zero,
            TimeSpan.FromMinutes(_options.CorrelationWindowMinutes));

        return Task.CompletedTask;
    }

    public void RecordEvent(string eventType, object data, DateTimeOffset timestamp, string source = "system")
    {
        var systemEvent = new SystemEvent
        {
            Id = Guid.NewGuid().ToString(),
            Type = eventType,
            Data = data,
            Timestamp = timestamp,
            Source = source
        };

        _eventBuffer.Enqueue(systemEvent);

        // Keep buffer size manageable
        while (_eventBuffer.Count > _options.MaxEventsToCorrelate)
        {
            _eventBuffer.TryDequeue(out _);
        }
    }

    private void ProcessEventCorrelations(object? state)
    {
        try
        {
            var events = _eventBuffer.ToArray();
            var windowStart = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(_options.CorrelationWindowMinutes);

            // Get events within correlation window
            var recentEvents = events
                .Where(e => e.Timestamp >= windowStart)
                .OrderBy(e => e.Timestamp)
                .ToList();

            if (!recentEvents.Any())
                return;

            var correlations = FindCorrelations(recentEvents);

            foreach (var correlation in correlations)
            {
                _logger.LogWarning("Event correlation detected: {Name} - {Description}",
                    correlation.Rule.Name, correlation.Rule.Description);

                // Trigger alerts or remediation
                HandleCorrelation(correlation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process event correlations");
        }
    }

    private List<EventCorrelation> FindCorrelations(List<SystemEvent> events)
    {
        var correlations = new List<EventCorrelation>();

        foreach (var rule in _rules)
        {
            if (EvaluateRule(rule, events))
            {
                var matchedEvents = events.Where(e => MatchesCondition(e, rule.Conditions)).ToList();
                correlations.Add(new EventCorrelation
                {
                    Rule = rule,
                    MatchedEvents = matchedEvents,
                    DetectedAt = DateTimeOffset.UtcNow
                });
            }
        }

        return correlations;
    }

    private bool EvaluateRule(CorrelationRule rule, List<SystemEvent> events)
    {
        foreach (var condition in rule.Conditions)
        {
            if (!EvaluateCondition(condition, events))
                return false;
        }
        return true;
    }

    private bool EvaluateCondition(EventCondition condition, List<SystemEvent> events)
    {
        var matchingEvents = events.Where(e => e.Type == condition.EventType).ToList();

        switch (condition.Operator)
        {
            case ">":
                return matchingEvents.Any(e => GetEventValue(e) > condition.Threshold);
            case "<":
                return matchingEvents.Any(e => GetEventValue(e) < condition.Threshold);
            case ">=":
                return matchingEvents.Any(e => GetEventValue(e) >= condition.Threshold);
            case "<=":
                return matchingEvents.Any(e => GetEventValue(e) <= condition.Threshold);
            case "count":
                return matchingEvents.Count >= condition.Threshold;
            default:
                return false;
        }
    }

    private bool MatchesCondition(SystemEvent systemEvent, List<EventCondition> conditions)
    {
        return conditions.Any(c => c.EventType == systemEvent.Type);
    }

    private double GetEventValue(SystemEvent systemEvent)
    {
        // Extract numeric value from event data
        if (systemEvent.Data is double d)
            return d;
        if (systemEvent.Data is int i)
            return i;
        if (systemEvent.Data is long l)
            return l;
        if (systemEvent.Data is float f)
            return f;

        // Try to parse from string
        if (systemEvent.Data is string s && double.TryParse(s, out var value))
            return value;

        return 0;
    }

    private void HandleCorrelation(EventCorrelation correlation)
    {
        // Log detailed correlation information
        _logger.LogWarning(
            "Correlation Details: Rule={RuleName}, Severity={Severity}, Events={EventCount}, TimeWindow={Minutes}min",
            correlation.Rule.Name,
            correlation.Rule.Severity,
            correlation.MatchedEvents.Count,
            _options.CorrelationWindowMinutes);

        // Here you could trigger:
        // - Alert notifications
        // - Automated remediation
        // - Incident creation
        // - Escalation procedures

        // For now, just log the correlation
        var correlationJson = JsonSerializer.Serialize(correlation, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        _logger.LogInformation("Correlation data: {Data}", correlationJson);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _correlationTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _correlationTimer?.Dispose();
    }
}

public class SystemEvent
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public object Data { get; set; } = new();
    public DateTimeOffset Timestamp { get; set; }
    public string Source { get; set; } = string.Empty;
}

public class CorrelationRule
{
    public string Name { get; set; } = string.Empty;
    public List<EventCondition> Conditions { get; set; } = new();
    public string Severity { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class EventCondition
{
    public string EventType { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public double Threshold { get; set; }
}

public class EventCorrelation
{
    public CorrelationRule Rule { get; set; } = new();
    public List<SystemEvent> MatchedEvents { get; set; } = new();
    public DateTimeOffset DetectedAt { get; set; }
}
