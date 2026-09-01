using System.Threading;
using System.Globalization;
using Potion.Tray.Core;
using Potion.Tray.Core.Checks;
using Potion.Tray.Core.Repairs;
using Potion.Tray.Core.Resources;

namespace Potion.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        const string showHistoryName = @"Local\Potion.Tray.ShowHistory";
        using var showHistorySignal = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            showHistoryName,
            out _);
        using var mutex = new Mutex(true, @"Local\Potion.Tray.SingleInstance", out var created);
        if (!created)
        {
            if (EventWaitHandle.TryOpenExisting(showHistoryName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        var log = new FileTrayLog();
        Application.ThreadException += (_, args) => log.Error("An unhandled UI exception occurred.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error("An unhandled exception occurred.", args.ExceptionObject as Exception);

        var settingsStore = new JsonSettingsStore(log: log);
        var settings = settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(settings.UiCulture))
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(settings.UiCulture);
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
            }
            catch (CultureNotFoundException)
            {
            }
        }
        var localizer = new ResourceLocalizer();
        StartupRegistration.Apply(settings.RunAtWindowsStartup, log);
        var system = new WindowsSystemInfoProvider();
        var processRunner = new SystemProcessRunner();
        var history = new JsonlHistoryStore(
            maxEntries: settings.HistoryMaxEntries,
            retentionDays: settings.HistoryRetentionDays,
            log: log);
        var checkState = new JsonCheckStateStore(log: log);
        var notifier = new BalloonNotifier();
        var checks = new IHealthCheck[]
        {
            new DiskSpaceHealthCheck(system, localizer),
            new CriticalServiceHealthCheck(system, localizer),
            new ComponentStoreHealthCheck(processRunner, log, localizer),
            new MemoryPressureHealthCheck(system, localizer),
            new PendingRebootHealthCheck(system, localizer),
            new NetworkHealthCheck(system, localizer)
        };
        var repairs = new IRepairAction[]
        {
            new DiskSpaceRepair(new WindowsTempFileCleaner(system), processRunner, system, localizer),
            new ServiceRestartRepair(processRunner, system, localizer),
            new ComponentStoreRepair(processRunner, localizer),
            new DnsFlushRepair(processRunner, system, localizer)
        };
        var engine = new SelfHealingEngine(
            checks,
            repairs,
            history,
            settingsStore,
            notifier,
            system,
            new SystemTrayClock(),
            log,
            localizer,
            checkState);

        Application.Run(new TrayApplicationContext(
            engine,
            history,
            settingsStore,
            system,
            notifier,
            log,
            localizer,
            showHistorySignal));
    }
}
