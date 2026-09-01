using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Potion.Tray.Core;
using System.ServiceProcess;

namespace Potion.Tray;

internal sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    public bool IsElevated =>
        OperatingSystem.IsWindows() &&
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

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

    public IReadOnlyList<ServiceSnapshot> GetServices(IReadOnlyList<string> names)
    {
        return names.Select(name =>
        {
            try
            {
                using var service = new ServiceController(name);
                var status = service.Status;
                return new ServiceSnapshot(name, true, status == ServiceControllerStatus.Running);
            }
            catch (InvalidOperationException)
            {
                return new ServiceSnapshot(name, false, false);
            }
        }).ToList();
    }

    public bool IsRebootPending()
    {
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var update = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return cbs is not null ||
                   update is not null ||
                   session?.GetValue("PendingFileRenameOperations") is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CanResolveDnsAsync(string host, CancellationToken ct)
    {
        try
        {
            await Dns.GetHostEntryAsync(host, ct);
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

internal sealed class WindowsTempFileCleaner : ITempFileCleaner
{
    public Task<TempCleanupResult> CleanAsync(CancellationToken ct)
    {
        var filesDeleted = 0;
        long bytesFreed = 0;
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var roots = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        var size = info.Length;
                        info.Delete();
                        filesDeleted++;
                        bytesFreed += size;
                    }
                }
                catch
                {
                }
            }
        }

        return Task.FromResult(new TempCleanupResult(filesDeleted, bytesFreed));
    }
}

internal static class StartupRegistration
{
    public static void Apply(bool enabled) => Apply(enabled, new FileTrayLog());

    public static void Apply(bool enabled, ITrayLog log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled)
            {
                key?.SetValue("PotionTray", Application.ExecutablePath);
            }
            else
            {
                key?.DeleteValue("PotionTray", false);
            }
        }
        catch (Exception ex)
        {
            log.Warn("Unable to update Windows startup registration.", ex);
        }
    }
}

internal static class AdministratorRestart
{
    public static void Restart(ITrayLog log)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Exit();
        }
        catch (Win32Exception)
        {
        }
        catch (Exception ex)
        {
            log.Warn("Unable to restart as administrator.", ex);
        }
    }
}
