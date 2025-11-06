using System.Text.Json.Serialization;

namespace Potion.Service.Infrastructure;

public enum TaskExecutionStatus
{
    NeverRun = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    TimedOut = 4,
    Cancelled = 5
}

public sealed class TaskState
{
    public string TaskName { get; set; } = string.Empty;

    public DateTimeOffset? LastRunUtc { get; set; }

    public DateTimeOffset? LastSuccessUtc { get; set; }

    public TaskExecutionStatus LastStatus { get; set; } = TaskExecutionStatus.NeverRun;

    public int? LastExitCode { get; set; }

    public double? LastDurationSeconds { get; set; }

    public string? LastTelemetryPath { get; set; }

    public string? LastError { get; set; }

    public int RetryAttempts { get; set; }

    public DateTimeOffset? NextEligibleRunUtc { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtensionData { get; set; }
}
