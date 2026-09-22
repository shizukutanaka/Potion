using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

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
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Potion",
            "appsettings.json");
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
        Console.WriteLine("  --config, -c <path>    - Path to appsettings.json (default: %ProgramData%\\Potion\\appsettings.json)");
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

            var config = new ConfigurationBuilder()
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
            if (policy.MaxConcurrency < 1)
            {
                failures.Add("RemediationPolicy:MaxConcurrency must be >= 1");
            }
            if (policy.CommandAllowlist.Count == 0)
            {
                failures.Add("RemediationPolicy:CommandAllowlist must not be empty");
            }
            var taskNames = policy.Tasks.Select(t => t.Name).ToList();
            if (taskNames.Count != taskNames.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                failures.Add("RemediationPolicy:Tasks contains duplicate task names");
            }
            foreach (var task in policy.Tasks)
            {
                if (!policy.CommandAllowlist.Contains(task.Command, StringComparer.OrdinalIgnoreCase))
                {
                    failures.Add($"Task '{task.Name}' command '{task.Command}' is not in CommandAllowlist");
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

    static int GenerateDefaultConfig(string configPath)
    {
        Console.WriteLine("Generating default configuration...");

        var defaultConfig = new
        {
            RemediationPolicy = new
            {
                MaxConcurrency = 2,
                SchedulerIntervalSeconds = 300,
                ScheduleJitterSeconds = 60,
                CommandAllowlist = new[]
                {
                    "sfc.exe", "dism.exe", "cleanmgr.exe", "chkdsk.exe", "wevtutil.exe",
                    "powercfg.exe", "net.exe", "netsh.exe", "ipconfig.exe", "systeminfo.exe", "ngen.exe"
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
                        TimeoutSeconds = 7200,
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
                        Name = "dism_health_restore",
                        DisplayName = "DISM Health Restore",
                        Command = "dism.exe",
                        Arguments = "/Online /Cleanup-Image /RestoreHealth",
                        RunEveryMinutes = 10080,
                        TimeoutSeconds = 10800,
                        RequiresElevation = true,
                        Enabled = true,
                        MaxRetries = 1,
                        RetryBackoffSeconds = 3600,
                        StopOnFailure = false,
                        MaintenanceWindowTag = "overnight",
                        AllowedExitCodes = new[] { 0 }
                    },
                    new
                    {
                        Name = "disk_cleanup",
                        DisplayName = "Disk Cleanup",
                        Command = "cleanmgr.exe",
                        Arguments = "/sagerun:1",
                        RunEveryMinutes = 1440,
                        TimeoutSeconds = 3600,
                        RequiresElevation = true,
                        Enabled = true,
                        MaxRetries = 2,
                        RetryBackoffSeconds = 900,
                        StopOnFailure = true,
                        MaintenanceWindowTag = "business_hours",
                        AllowedExitCodes = new[] { 0 }
                    }
                }
            }
        };

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
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"Potion_config_backup_{timestamp}.json");

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

            var destinationDir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
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
