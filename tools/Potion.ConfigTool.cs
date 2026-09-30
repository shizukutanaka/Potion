using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Potion.Service.Hubs;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using DataAnnotations = System.ComponentModel.DataAnnotations;

namespace Potion.ConfigTool;

class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("Potion Configuration Tool");
        Console.WriteLine("========================");

        var parsed = ParseArgs(args);
        if (parsed.Command is null)
        {
            ShowHelp();
            return args.Length == 0 ? 0 : 1;
        }

        switch (parsed.Command)
        {
            case "validate":
                return ValidateConfiguration(parsed.ConfigPath);
            case "generate":
                return GenerateDefaultConfig(parsed.ConfigPath);
            case "show":
                return ShowCurrentConfig(parsed.ConfigPath);
            case "backup":
                return BackupConfiguration(parsed.ConfigPath);
            case "restore":
                if (parsed.Positional is null)
                {
                    Console.WriteLine("Error: Backup file path required");
                    return 1;
                }
                return RestoreConfiguration(parsed.Positional, parsed.ConfigPath);
            default:
                ShowHelp();
                return parsed.Command is "help" or "--help" or "-h" ? 0 : 1;
        }
    }

    record ParsedArgs(string? Command, string? Positional, string ConfigPath);

    static ParsedArgs ParseArgs(string[] args)
    {
        // Same file the service loads as its external override layer
        // (Program.cs AddJsonFile): {Base}/config/appsettings.json.
        var configPath = ServicePaths.ConfigurationFile;
        string? positional = null;

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--config" or "-c" && i + 1 < args.Length)
            {
                configPath = args[++i];
            }
            else if (!args[i].StartsWith('-') && positional is null)
            {
                positional = args[i];
            }
        }

        return new ParsedArgs(args.Length > 0 ? args[0].ToLowerInvariant() : null, positional, configPath);
    }

    static void ShowHelp()
    {
        Console.WriteLine("Usage: Potion.ConfigTool <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  validate               - Validate current configuration");
        Console.WriteLine("  generate               - Generate default configuration");
        Console.WriteLine("  show                   - Show current configuration");
        Console.WriteLine("  backup                 - Backup current configuration");
        Console.WriteLine("  restore <backup-file>  - Restore configuration from backup");
        Console.WriteLine("  help                   - Show this help message");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine($"  --config, -c <path>    - Path to appsettings.json (default: {ServicePaths.ConfigurationFile})");
        Console.WriteLine();
    }

    static int ValidateConfiguration(string configPath)
    {
        Console.WriteLine($"Validating configuration: {configPath}");

        try
        {
            if (!File.Exists(configPath))
            {
                Console.WriteLine("✗ Configuration file not found");
                return 1;
            }

            // Overlay semantics: the external file layers on top of the bundled
            // defaults, so validate the merged view — a file that omits
            // RemediationPolicy inherits the bundled section and is valid.
            var defaultsJson = JsonSerializer.Serialize(BuildDefaultConfig());
            using var defaultsStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(defaultsJson));
            var config = new ConfigurationBuilder()
                .AddJsonStream(defaultsStream)
                .AddJsonFile(configPath, optional: false)
                .Build();

            var services = new ServiceCollection();
            services.AddOptions<RemediationPolicyOptions>()
                .Bind(config.GetSection("RemediationPolicy"));
            services.AddOptions<MemoryMonitorOptions>()
                .Bind(config.GetSection(MemoryMonitorOptions.SectionName));
            services.AddOptions<PerformanceOptimizerOptions>()
                .Bind(config.GetSection(PerformanceOptimizerOptions.SectionName));

            var serviceProvider = services.BuildServiceProvider();
            var failures = new List<string>();

            var policy = serviceProvider.GetRequiredService<IOptions<RemediationPolicyOptions>>().Value;

            // Mirror the service's startup options validation (Startup.cs:
            // ValidateDataAnnotations + the five policy validators with
            // ValidateOnStart) so a file the tool approves is one the service
            // can actually boot with — the ad-hoc checks that used to live here
            // let startup-breaking configs through (e.g. task arguments outside
            // CommandArgumentAllowlist).
            var annotationResults = new List<DataAnnotations.ValidationResult>();
            if (!DataAnnotations.Validator.TryValidateObject(
                    policy, new DataAnnotations.ValidationContext(policy), annotationResults,
                    validateAllProperties: true))
            {
                failures.AddRange(annotationResults
                    .Select(r => r.ErrorMessage ?? "RemediationPolicy has an invalid value"));
            }

            var startupChecks = new (Func<RemediationPolicyOptions, bool> Check, string FailureMessage)[]
            {
                (RemediationPolicyOptionsValidators.HasUniqueTaskNames, "Remediation policy contains duplicate task names."),
                (RemediationPolicyOptionsValidators.CommandsAreAllowlisted, "Remediation policy references commands outside the allowlist."),
                (RemediationPolicyOptionsValidators.ArgumentsAreSafe, "Remediation policy contains unsafe task arguments."),
                (RemediationPolicyOptionsValidators.ArgumentsAreAllowlisted, "Remediation policy uses arguments outside the command argument allowlist."),
                (RemediationPolicyOptionsValidators.MaintenanceWindowsAreValid, "Remediation policy contains invalid maintenance windows.")
            };
            foreach (var (check, failureMessage) in startupChecks)
            {
                try
                {
                    if (!check(policy))
                    {
                        failures.Add(failureMessage);
                    }
                }
                catch (DataAnnotations.ValidationException ex)
                {
                    failures.Add(ex.Message);
                }
            }

            if (failures.Count == 0)
            {
                Console.WriteLine("✓ All configuration validations passed!");
                return 0;
            }

            Console.WriteLine("✗ Configuration validation failed:");
            foreach (var failure in failures)
            {
                Console.WriteLine($"  - {failure}");
            }
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Configuration validation error: {ex.Message}");
            return 1;
        }
    }

    static object BuildDefaultConfig()
    {
        return new
        {
            RemediationPolicy = new
            {
                MaxConcurrency = 4,
                SchedulerIntervalSeconds = 300,
                ScheduleJitterSeconds = 60,
                // Keep this list in sync with the shipped appsettings.json —
                // argument-abusable binaries (net/sc/reg/wmic/wevtutil/…)
                // were removed from the defaults on purpose.
                CommandAllowlist = new[]
                {
                    "sfc.exe", "dism.exe", "cleanmgr.exe", "chkdsk.exe", "ngen.exe",
                    "powercfg.exe", "netsh.exe"
                },
                // Per-command argument allowlist — the service rejects an
                // enabled task whose arguments aren't listed for its command
                // (unlisted commands stay unrestricted).
                CommandArgumentAllowlist = new Dictionary<string, string[]>
                {
                    ["sfc.exe"] = new[] { "/scannow" },
                    ["dism.exe"] = new[] { "/Online /Cleanup-Image /RestoreHealth" },
                    ["cleanmgr.exe"] = new[] { "/verylowdisk" },
                    ["ngen.exe"] = new[] { "update /force" }
                },
                MaintenanceWindows = new[]
                {
                    new
                    {
                        Tag = "overnight",
                        StartTime = "22:00",
                        EndTime = "06:00",
                        DaysOfWeek = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday" }
                    },
                    new
                    {
                        Tag = "business_hours",
                        StartTime = "08:00",
                        EndTime = "18:00",
                        DaysOfWeek = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" }
                    }
                },
                Tasks = new[]
                {
                    new
                    {
                        Name = "sfc_integrity_scan",
                        DisplayName = "System File Checker Integrity Scan",
                        Command = "sfc.exe",
                        Arguments = "/scannow",
                        RunEveryMinutes = 10080,
                        TimeoutSeconds = 1800,
                        RequiresElevation = true,
                        Enabled = true,
                        MaxRetries = 1,
                        RetryBackoffSeconds = 900,
                        StopOnFailure = false,
                        MaintenanceWindowTag = "overnight",
                        AllowedExitCodes = new[] { 0 }
                    },
                    new
                    {
                        Name = "dism_health_restore",
                        DisplayName = "DISM Health Restore",
                        Command = "dism.exe",
                        Arguments = "/Online /Cleanup-Image /RestoreHealth",
                        RunEveryMinutes = 10080,
                        TimeoutSeconds = 3600,
                        RequiresElevation = true,
                        Enabled = true,
                        MaxRetries = 1,
                        RetryBackoffSeconds = 1800,
                        StopOnFailure = false,
                        MaintenanceWindowTag = "overnight",
                        AllowedExitCodes = new[] { 0 }
                    },
                    new
                    {
                        Name = "disk_cleanup",
                        DisplayName = "Disk Cleanup",
                        Command = "cleanmgr.exe",
                        Arguments = "/verylowdisk",
                        RunEveryMinutes = 1440,
                        TimeoutSeconds = 3600,
                        RequiresElevation = true,
                        Enabled = true,
                        MaxRetries = 1,
                        RetryBackoffSeconds = 900,
                        StopOnFailure = false,
                        MaintenanceWindowTag = "business_hours",
                        AllowedExitCodes = new[] { 0 }
                    },
                    new
                    {
                        Name = "dotnet_optimization",
                        DisplayName = ".NET Runtime Optimization",
                        Command = "ngen.exe",
                        Arguments = "update /force",
                        RunEveryMinutes = 10080,
                        TimeoutSeconds = 3600,
                        RequiresElevation = true,
                        Enabled = true,
                        MaxRetries = 1,
                        RetryBackoffSeconds = 1800,
                        StopOnFailure = false,
                        MaintenanceWindowTag = "overnight",
                        AllowedExitCodes = new[] { 0 }
                    }
                }
            },
            // Emit every bound section with its code defaults so operators can
            // see every knob; anything omitted still inherits the bundled
            // appsettings.json (this file is an override layer).
            Collaboration = new CollaborationOptions(),
            Compliance = new ComplianceOptions(),
            EventCorrelation = new EventCorrelationOptions(),
            MemoryMonitor = new MemoryMonitorOptions(),
            PerformanceOptimizer = new PerformanceOptimizerOptions(),
            FeatureFlags = new { RepairExecutionEnabled = false },
            Observability = new { OtlpEndpoint = "http://localhost:4317" },
            Kestrel = new
            {
                Limits = new
                {
                    MaxConcurrentConnections = 100,
                    MaxConcurrentUpgradedConnections = 10,
                    MaxRequestBodySize = 1048576,
                    MinRequestBodyDataRate = new { BytesPerSecond = 100, GracePeriod = "00:00:10" },
                    MinResponseDataRate = new { BytesPerSecond = 100, GracePeriod = "00:00:10" }
                }
            }
        };
    }

    static int GenerateDefaultConfig(string configPath)
    {
        Console.WriteLine("Generating default configuration...");

        var defaultConfig = BuildDefaultConfig();

        try
        {
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(configPath, json);
            Console.WriteLine($"✓ Default configuration generated at: {configPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Generation failed: {ex.Message}");
            return 1;
        }
    }

    static int ShowCurrentConfig(string configPath)
    {
        Console.WriteLine($"Current configuration: {configPath}");

        try
        {
            if (!File.Exists(configPath))
            {
                Console.WriteLine("✗ Configuration file not found. Run 'generate' command first.");
                return 1;
            }

            var json = File.ReadAllText(configPath);
            var config = JsonDocument.Parse(json);

            Console.WriteLine(JsonSerializer.Serialize(config.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error reading configuration: {ex.Message}");
            return 1;
        }
    }

    static int BackupConfiguration(string configPath)
    {
        Console.WriteLine("Backing up configuration...");

        try
        {
            if (!File.Exists(configPath))
            {
                Console.WriteLine("✗ Configuration file not found");
                return 1;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(
                ServicePaths.ConfigBackups,
                $"appsettings_{timestamp}.json");

            File.Copy(configPath, backupPath);

            Console.WriteLine($"✓ Configuration backed up to: {backupPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Backup failed: {ex.Message}");
            return 1;
        }
    }

    static int RestoreConfiguration(string backupPath, string configPath)
    {
        Console.WriteLine($"Restoring configuration from: {backupPath}");

        try
        {
            if (!File.Exists(backupPath))
            {
                Console.WriteLine("✗ Backup file not found");
                return 1;
            }

            // Refuse to overwrite live config with a backup that fails the same
            // checks `validate` applies — a syntactically valid file can still
            // break startup (empty allowlist, duplicate task names, etc.).
            if (ValidateConfiguration(backupPath) != 0)
            {
                Console.WriteLine("✗ Backup failed validation; live configuration left untouched");
                return 1;
            }

            var destinationDir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // Preserve the current config before the destructive overwrite.
            if (File.Exists(configPath))
            {
                var preRestore = $"{configPath}.prerestore-{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                File.Copy(configPath, preRestore);
                Console.WriteLine($"  Current config preserved to: {preRestore}");
            }

            File.Copy(backupPath, configPath, true);

            Console.WriteLine($"✓ Configuration restored to: {configPath}");
            Console.WriteLine("Note: Please restart the Potion service to apply the restored configuration:");
            Console.WriteLine("  net stop \"Potion Self-Healing Service\"");
            Console.WriteLine("  net start \"Potion Self-Healing Service\"");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Restore failed: {ex.Message}");
            return 1;
        }
    }
}
