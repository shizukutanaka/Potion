using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Potion.Service.Options;

namespace Potion.ConfigTool
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Potion Configuration Tool");
            Console.WriteLine("========================");

            if (args.Length == 0)
            {
                ShowHelp();
                return;
            }

            var command = args[0].ToLower();

            switch (command)
            {
                case "validate":
                    await ValidateConfigurationAsync();
                    break;
                case "generate":
                    await GenerateDefaultConfigAsync();
                    break;
                case "show":
                    await ShowCurrentConfigAsync();
                    break;
                case "backup":
                    await BackupConfigurationAsync();
                    break;
                case "restore":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Backup file path required");
                        return;
                    }
                    await RestoreConfigurationAsync(args[1]);
                    break;
                case "help":
                case "--help":
                case "-h":
                default:
                    ShowHelp();
                    break;
            }
        }

        static void ShowHelp()
        {
            Console.WriteLine("Usage: Potion.ConfigTool <command>");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  validate    - Validate current configuration");
            Console.WriteLine("  generate    - Generate default configuration");
            Console.WriteLine("  show        - Show current configuration");
            Console.WriteLine("  backup      - Backup current configuration");
            Console.WriteLine("  restore     - Restore configuration from backup");
            Console.WriteLine("  help        - Show this help message");
            Console.WriteLine();
        }

        static async Task ValidateConfigurationAsync()
        {
            Console.WriteLine("Validating configuration...");

            try
            {
                var config = BuildConfiguration();
                var services = new ServiceCollection();
                services.Configure<RemediationPolicyOptions>(config.GetSection("RemediationPolicy"));
                services.Configure<TelemetryRetentionOptions>(config.GetSection("TelemetryRetention"));

                var serviceProvider = services.BuildServiceProvider();

                var remediationOptions = serviceProvider.GetRequiredService<IOptions<RemediationPolicyOptions>>();
                var telemetryOptions = serviceProvider.GetRequiredService<IOptions<TelemetryRetentionOptions>>();

                // バリデーション実行
                var remediationResults = new List<string>();
                var telemetryResults = new List<string>();

                try
                {
                    var remediationValidatedOptions = remediationOptions.Value;
                    Console.WriteLine("✓ Remediation policy validation passed");
                }
                catch (OptionsValidationException ex)
                {
                    remediationResults.AddRange(ex.Failures);
                    Console.WriteLine("✗ Remediation policy validation failed:");
                    foreach (var failure in ex.Failures)
                    {
                        Console.WriteLine($"  - {failure}");
                    }
                }

                try
                {
                    var telemetryValidatedOptions = telemetryOptions.Value;
                    Console.WriteLine("✓ Telemetry retention validation passed");
                }
                catch (OptionsValidationException ex)
                {
                    telemetryResults.AddRange(ex.Failures);
                    Console.WriteLine("✗ Telemetry retention validation failed:");
                    foreach (var failure in ex.Failures)
                    {
                        Console.WriteLine($"  - {failure}");
                    }
                }

                if (remediationResults.Count == 0 && telemetryResults.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("✓ All configuration validations passed!");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("✗ Configuration validation failed. Please fix the issues above.");
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Configuration validation error: {ex.Message}");
                Environment.Exit(1);
            }
        }

        static async Task GenerateDefaultConfigAsync()
        {
            Console.WriteLine("Generating default configuration...");

            var defaultConfig = new
            {
                Serilog = new
                {
                    MinimumLevel = new
                    {
                        Default = "Debug",
                        Override = new
                        {
                            Microsoft = "Warning",
                            System = "Warning"
                        }
                    }
                },
                RemediationPolicy = new
                {
                    MaxConcurrency = 2,
                    SchedulerIntervalSeconds = 300,
                    ScheduleJitterSeconds = 60,
                    CommandAllowlist = new[] { "sfc.exe", "dism.exe", "cleanmgr.exe", "powershell.exe" },
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
                },
                TelemetryRetention = new
                {
                    Enabled = true,
                    RetentionDays = 30,
                    CleanupIntervalHours = 12
                }
            };

            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Potion",
                "appsettings.json");

            var directory = Path.GetDirectoryName(configPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(configPath, json);

            Console.WriteLine($"✓ Default configuration generated at: {configPath}");
        }

        static async Task ShowCurrentConfigAsync()
        {
            Console.WriteLine("Current configuration:");

            try
            {
                var configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Potion",
                    "appsettings.json");

                if (!File.Exists(configPath))
                {
                    Console.WriteLine("✗ Configuration file not found. Run 'generate' command first.");
                    return;
                }

                var json = await File.ReadAllTextAsync(configPath);
                var config = JsonDocument.Parse(json);

                Console.WriteLine(JsonSerializer.Serialize(config.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Error reading configuration: {ex.Message}");
            }
        }

        static async Task BackupConfigurationAsync()
        {
            Console.WriteLine("Backing up configuration...");

            try
            {
                var sourcePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Potion",
                    "appsettings.json");

                if (!File.Exists(sourcePath))
                {
                    Console.WriteLine("✗ Configuration file not found");
                    return;
                }

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    $"Potion_config_backup_{timestamp}.json");

                File.Copy(sourcePath, backupPath);

                Console.WriteLine($"✓ Configuration backed up to: {backupPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Backup failed: {ex.Message}");
            }
        }

        static async Task RestoreConfigurationAsync(string backupPath)
        {
            Console.WriteLine($"Restoring configuration from: {backupPath}");

            try
            {
                if (!File.Exists(backupPath))
                {
                    Console.WriteLine("✗ Backup file not found");
                    return;
                }

                var destinationPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Potion",
                    "appsettings.json");

                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                File.Copy(backupPath, destinationPath, true);

                Console.WriteLine($"✓ Configuration restored to: {destinationPath}");

                // サービス再起動を促す
                Console.WriteLine("Note: Please restart the Potion service to apply the restored configuration:");
                Console.WriteLine("  net stop \"Potion Self-Healing Service\"");
                Console.WriteLine("  net start \"Potion Self-Healing Service\"");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Restore failed: {ex.Message}");
            }
        }

        static IConfiguration BuildConfiguration()
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Potion",
                "appsettings.json");

            return new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(configPath, optional: true)
                .Build();
        }
    }
}
