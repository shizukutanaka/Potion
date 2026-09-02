using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Potion.Tray.Core;
using System.ServiceProcess;
using System.Net.NetworkInformation;
using Potion.Tray.Core.Resources;

namespace Potion.Tray;

internal sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    public bool IsElevated =>
        OperatingSystem.IsWindows() &&
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public string SystemDriveRoot
    {
        get
        {
            var windowsRoot = DriveScope.Root(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            return windowsRoot.Length > 0
                ? windowsRoot
                : DriveScope.Root(Environment.SystemDirectory);
        }
    }

    public IReadOnlyList<DriveSnapshot> GetFixedDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveSnapshot(d.Name, d.TotalSize, d.AvailableFreeSpace))
            .ToList();
    }

    public MemorySnapshot GetMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? new MemorySnapshot((long)status.TotalPhys, (long)status.AvailPhys)
            : new MemorySnapshot(0, 0);
    }

    public IReadOnlyList<ProcessMemorySnapshot> GetTopMemoryProcesses(int count)
    {
        if (count <= 0)
        {
            return Array.Empty<ProcessMemorySnapshot>();
        }

        try
        {
            var snapshots = new List<ProcessMemorySnapshot>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        snapshots.Add(new ProcessMemorySnapshot(process.ProcessName, process.WorkingSet64));
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Win32Exception)
                    {
                    }
                }
            }

            return snapshots
                .OrderByDescending(process => process.WorkingSetBytes)
                .Take(count)
                .ToList();
        }
        catch
        {
            return Array.Empty<ProcessMemorySnapshot>();
        }
    }

    public IReadOnlyList<StorageConsumer> GetLargeStorageConsumers()
    {
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var roots = new List<(string NameKey, string Path)>();
            if (systemRoot is { Length: > 0 })
            {
                roots.Add(("Storage.RecycleBin", Path.Combine(systemRoot, "$Recycle.Bin")));
            }

            if (userProfile.Length > 0)
            {
                roots.Add(("Storage.Downloads", Path.Combine(userProfile, "Downloads")));
            }

            if (windows.Length > 0)
            {
                roots.Add(("Storage.WindowsUpdateCache", Path.Combine(windows, "SoftwareDistribution", "Download")));
            }

            roots.Add(("Storage.TempFiles", Path.GetTempPath()));
            var consumers = new List<StorageConsumer>();
            foreach (var (nameKey, path) in roots)
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    continue;
                }

                var result = SumFiles(path);
                if (result.Bytes >= 1L * 1024 * 1024 * 1024)
                {
                    consumers.Add(new StorageConsumer(nameKey, result.Bytes, result.Truncated));
                }
            }

            return consumers
                .OrderByDescending(consumer => consumer.Bytes)
                .Take(3)
                .ToList();
        }
        catch
        {
            return Array.Empty<StorageConsumer>();
        }
    }

    private static (long Bytes, bool Truncated) SumFiles(string root)
    {
        var total = 0L;
        var scanned = 0;
        var stopwatch = Stopwatch.StartNew();
        IEnumerator<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root)
                .EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                })
                .GetEnumerator();
        }
        catch (UnauthorizedAccessException)
        {
            return (0, false);
        }
        catch (DirectoryNotFoundException)
        {
            return (0, false);
        }
        catch (IOException)
        {
            return (0, false);
        }

        using (files)
        {
            while (scanned < 20_000)
            {
                try
                {
                    if (!files.MoveNext())
                    {
                        return (total, false);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return (total, false);
                }
                catch (DirectoryNotFoundException)
                {
                    return (total, false);
                }
                catch (IOException)
                {
                    return (total, false);
                }

                if (stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    return (total, true);
                }

                scanned++;
                try
                {
                    total += files.Current.Length;
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
                catch (IOException)
                {
                }
            }
        }

        return (total, true);
    }

    public IReadOnlyList<ServiceSnapshot> GetServices(IReadOnlyList<string> names)
    {
        return names.Select(name =>
        {
            try
            {
                using var service = new ServiceController(name);
                var status = service.Status;
                var startType = service.StartType switch
                {
                    ServiceStartMode.Boot => ServiceStartType.Boot,
                    ServiceStartMode.System => ServiceStartType.System,
                    ServiceStartMode.Automatic => ServiceStartType.Automatic,
                    ServiceStartMode.Manual => ServiceStartType.Manual,
                    ServiceStartMode.Disabled => ServiceStartType.Disabled,
                    _ => ServiceStartType.Unknown
                };
                return new ServiceSnapshot(
                    name,
                    true,
                    status == ServiceControllerStatus.Running,
                    startType,
                    status is ServiceControllerStatus.StartPending or
                        ServiceControllerStatus.ContinuePending);
            }
            catch (InvalidOperationException)
            {
                return new ServiceSnapshot(name, false, false);
            }
        }).ToList();
    }

    public bool IsNetworkAvailable => NetworkInterface.GetIsNetworkAvailable();

    public bool IsRebootPending()
    {
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing");
            using var cbsRebootPending = cbs?.OpenSubKey("RebootPending");
            using var cbsRebootInProgress = cbs?.OpenSubKey("RebootInProgress");
            using var cbsPackagesPending = cbs?.OpenSubKey("PackagesPending");
            using var update = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            using var activeComputerName = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName");
            using var computerName = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName");
            var pendingFileRenameOperations = session?.GetValue("PendingFileRenameOperations") switch
            {
                string[] operations => operations,
                string operation => new[] { operation },
                _ => null
            };
            return RebootPendingEvaluator.IsPending(new RebootPendingSignals(
                cbsRebootPending is not null,
                cbsRebootInProgress is not null,
                cbsPackagesPending is not null,
                update is not null,
                pendingFileRenameOperations,
                activeComputerName?.GetValue("ComputerName") as string,
                computerName?.GetValue("ComputerName") as string));
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CanResolveDnsAsync(string host, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await Dns.GetHostEntryAsync(host, timeoutCts.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

internal static class CultureConfigurator
{
    private static readonly CultureInfo OriginalUiCulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo OriginalCulture = CultureInfo.CurrentCulture;

    public static void Apply(string uiCulture)
    {
        try
        {
            var culture = string.IsNullOrWhiteSpace(uiCulture)
                ? OriginalCulture
                : CultureInfo.GetCultureInfo(uiCulture);
            var ui = string.IsNullOrWhiteSpace(uiCulture) ? OriginalUiCulture : culture;
            CultureInfo.DefaultThreadCurrentUICulture = ui;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = ui;
            Thread.CurrentThread.CurrentCulture = culture;
        }
        catch (CultureNotFoundException)
        {
        }
    }
}

internal sealed class WindowsTempFileCleaner : ITempFileCleaner
{
    private readonly ISystemInfoProvider system;
    private readonly TempDirectoryCleaner cleaner;

    public WindowsTempFileCleaner(ISystemInfoProvider system, ITrayClock clock)
    {
        this.system = system;
        cleaner = new TempDirectoryCleaner(clock);
    }

    public Task<TempCleanupResult> CleanAsync(
        TimeSpan minimumAge,
        IReadOnlyCollection<string>? driveRoots,
        CancellationToken ct)
    {
        var roots = new[]
        {
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } windows
                ? Path.Combine(windows, "Temp")
                : null
        }.Where(path => path is not null).Cast<string>();
        roots = roots.Where(path => DriveScope.Includes(driveRoots, path));
        if (!system.IsElevated)
        {
            roots = roots.Take(1);
        }

        if (!roots.Any())
        {
            return Task.FromResult(new TempCleanupResult(0, 0));
        }

        return Task.FromResult(cleaner.Clean(roots, minimumAge, ct));
    }
}

internal static class StartupRegistration
{
    public static bool Apply(bool enabled, ITrayLog log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key is null)
            {
                log.Warn("Unable to update Windows startup registration.");
                return false;
            }

            if (enabled)
            {
                key.SetValue("PotionTray", $"\"{Application.ExecutablePath}\"");
                if (StartupRegistrationPolicy.Matches(
                        key.GetValue("PotionTray") as string,
                        Application.ExecutablePath))
                {
                    return true;
                }
            }
            else
            {
                key.DeleteValue("PotionTray", false);
                if (key.GetValue("PotionTray") is null)
                {
                    return true;
                }
            }

            log.Warn("Unable to update Windows startup registration.");
            return false;
        }
        catch (Exception ex)
        {
            log.Warn("Unable to update Windows startup registration.", ex);
            return false;
        }
    }
}

internal static class AdministratorRestart
{
    public static void Restart(ITrayLog log, INotifier notifier)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            startInfo.ArgumentList.Add(Program.ElevatedHandoverArgument);
            Process.Start(startInfo);
            Application.Exit();
        }
        catch (Win32Exception)
        {
        }
        catch (Exception ex)
        {
            log.Warn("Unable to restart as administrator.", ex);
            var localizer = new ResourceLocalizer();
            notifier.Notify(new Notification(
                localizer.Get("Notify.ActionFailed.Title"),
                localizer.Get("Notify.ActionFailed.Message"),
                HealthStatus.Warning));
        }
    }
}
