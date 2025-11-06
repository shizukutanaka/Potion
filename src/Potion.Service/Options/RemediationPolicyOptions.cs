using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

public sealed class RemediationPolicyOptions
{
    private const int MaxSchedulerIntervalSeconds = 86400; // 24 hours

    [Range(1, 8)]
    public int MaxConcurrency { get; set; } = 1;

    [Range(15, MaxSchedulerIntervalSeconds)]
    public int SchedulerIntervalSeconds { get; set; } = 300;

    [Range(0, 900)]
    public int ScheduleJitterSeconds { get; set; } = 60;

    [Required]
    public List<string> CommandAllowlist { get; set; } = new();

    public bool SkipSignatureValidation { get; set; } = false;

    public List<MaintenanceWindowOption> MaintenanceWindows { get; set; } = new();

    [Required]
    public List<RemediationTaskOption> Tasks { get; set; } = new();
}

public sealed class RemediationTaskOption
{
    [Required]
    [RegularExpression(@"^[a-zA-Z0-9_\-\.]{1,64}$", ErrorMessage = "Task name must be alphanumeric and shorter than 64 characters.")]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(128)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    [StringLength(260)]
    public string Command { get; set; } = string.Empty;

    [StringLength(512)]
    public string Arguments { get; set; } = string.Empty;

    [Range(5, 10080)]
    public int RunEveryMinutes { get; set; } = 60;

    [Range(60, 14400)]
    public int TimeoutSeconds { get; set; } = 3600;

    public bool RequiresElevation { get; set; } = true;

    public bool Enabled { get; set; } = true;

    [Range(0, 10)]
    public int MaxRetries { get; set; } = 0;

    [Range(1, 3600)]
    public int RetryBackoffSeconds { get; set; } = 300;

    public bool StopOnFailure { get; set; } = false;

    public string? MaintenanceWindowTag { get; set; }

    public List<int> AllowedExitCodes { get; set; } = new();
}

public sealed class MaintenanceWindowOption
{
    [Required]
    [StringLength(64)]
    public string Tag { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^([01]?\d|2[0-3]):[0-5]\d$", ErrorMessage = "StartTime must be in HH:mm format.")]
    public string StartTime { get; set; } = "00:00";

    [Required]
    [RegularExpression(@"^([01]?\d|2[0-3]):[0-5]\d$", ErrorMessage = "EndTime must be in HH:mm format.")]
    public string EndTime { get; set; } = "23:59";

    [MinLength(1)]
    public List<DayOfWeek> DaysOfWeek { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
}

public static class RemediationPolicyOptionsValidators
{
    public static bool HasUniqueTaskNames(RemediationPolicyOptions options)
    {
        if (options.Tasks.Count == 0)
        {
            return false;
        }

        var taskNames = options.Tasks.Select(task => task.Name).ToList();
        var duplicates = taskNames
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Any())
        {
            throw new ValidationException($"Duplicate task names found: {string.Join(", ", duplicates)}");
        }

        return taskNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == taskNames.Count;
    }

    public static bool CommandsAreAllowlisted(RemediationPolicyOptions options)
    {
        if (options.CommandAllowlist.Count == 0)
        {
            return false;
        }

        var allowlist = new HashSet<string>(options.CommandAllowlist, StringComparer.OrdinalIgnoreCase);
        var missingCommands = new List<string>();

        foreach (var task in options.Tasks)
        {
            if (!task.Enabled)
            {
                continue;
            }

            var command = task.Command;
            if (allowlist.Contains(command))
            {
                continue;
            }

            var commandFileName = Path.GetFileName(command);
            if (!string.IsNullOrEmpty(commandFileName) && allowlist.Contains(commandFileName))
            {
                continue;
            }

            missingCommands.Add($"{task.Name}: {command}");
        }

        if (missingCommands.Any())
        {
            throw new ValidationException(
                $"The following tasks reference commands not in the allowlist: {string.Join(", ", missingCommands)}");
        }

        return true;
    }

    public static bool MaintenanceWindowsAreValid(RemediationPolicyOptions options)
    {
        if (options.MaintenanceWindows.Count == 0)
        {
            return true; // No windows is valid - tasks can run anytime
        }

        var tags = options.MaintenanceWindows.Select(w => w.Tag).ToList();
        var duplicateTags = tags
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateTags.Any())
        {
            throw new ValidationException($"Duplicate maintenance window tags found: {string.Join(", ", duplicateTags)}");
        }

        foreach (var window in options.MaintenanceWindows)
        {
            if (!TimeSpan.TryParse(window.StartTime, out var startTime) ||
                !TimeSpan.TryParse(window.EndTime, out var endTime))
            {
                throw new ValidationException($"Invalid time format in maintenance window '{window.Tag}'");
            }

            if (window.DaysOfWeek.Count == 0)
            {
                throw new ValidationException($"Maintenance window '{window.Tag}' must have at least one day of week");
            }
        }

        return true;
    }
}
