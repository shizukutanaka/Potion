using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.Service.Scheduling;

public interface IMaintenanceWindowEvaluator
{
    bool IsInMaintenanceWindow(RemediationTaskOption task, DateTimeOffset timestampUtc);
}

public sealed class MaintenanceWindowEvaluator : IMaintenanceWindowEvaluator, IDisposable
{
    private readonly ILogger<MaintenanceWindowEvaluator> _logger;
    private readonly IOptionsMonitor<RemediationPolicyOptions> _optionsMonitor;
    private readonly IDisposable _subscription;
    private IReadOnlyDictionary<string, MaintenanceWindowSnapshot> _windows;

    private sealed record MaintenanceWindowSnapshot(string Tag, TimeSpan Start, TimeSpan End, IReadOnlySet<DayOfWeek> Days);

    public MaintenanceWindowEvaluator(ILogger<MaintenanceWindowEvaluator> logger, IOptionsMonitor<RemediationPolicyOptions> optionsMonitor)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
        _windows = BuildLookup(optionsMonitor.CurrentValue);
        _subscription = _optionsMonitor.OnChange(options =>
        {
            _logger.LogInformation("Maintenance window configuration updated");
            _windows = BuildLookup(options);
        });
    }

    public bool IsInMaintenanceWindow(RemediationTaskOption task, DateTimeOffset timestampUtc)
    {
        if (task.MaintenanceWindowTag is null)
        {
            return true;
        }

        if (!_windows.TryGetValue(task.MaintenanceWindowTag, out var window))
        {
            _logger.LogWarning("Task {TaskName} references unknown maintenance window tag {Tag}", task.Name, task.MaintenanceWindowTag);
            return false;
        }

        var localTimestamp = timestampUtc.ToLocalTime();
        if (!window.Days.Contains(localTimestamp.DayOfWeek))
        {
            return false;
        }

        var timeOfDay = localTimestamp.TimeOfDay;

        if (window.Start <= window.End)
        {
            return timeOfDay >= window.Start && timeOfDay <= window.End;
        }

        return timeOfDay >= window.Start || timeOfDay <= window.End;
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }

    private IReadOnlyDictionary<string, MaintenanceWindowSnapshot> BuildLookup(RemediationPolicyOptions options)
    {
        var dictionary = new Dictionary<string, MaintenanceWindowSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in options.MaintenanceWindows)
        {
            if (!TimeSpan.TryParse(window.StartTime, out var start))
            {
                _logger.LogWarning("Invalid maintenance window start time {Start} for tag {Tag}", window.StartTime, window.Tag);
                continue;
            }

            if (!TimeSpan.TryParse(window.EndTime, out var end))
            {
                _logger.LogWarning("Invalid maintenance window end time {End} for tag {Tag}", window.EndTime, window.Tag);
                continue;
            }

            var days = window.DaysOfWeek.Any()
                ? window.DaysOfWeek.ToHashSet()
                : Enum.GetValues<DayOfWeek>().ToHashSet();

            dictionary[window.Tag] = new MaintenanceWindowSnapshot(window.Tag, start, end, days);
        }

        return dictionary;
    }
}
