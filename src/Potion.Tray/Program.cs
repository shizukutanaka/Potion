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
        using var mutex = new Mutex(true, @"Global\Potion.Tray.SingleInstance", out var created);
        if (!created)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        var log = new FileTrayLog();
        Application.ThreadException += (_, args) => log.Error("UI で未処理例外が発生しました。", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error("未処理例外が発生しました。", args.ExceptionObject as Exception);

        var settingsStore = new JsonSettingsStore(log: log);
        var settings = settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(settings.UiCulture))
        {
            var culture = CultureInfo.GetCultureInfo(settings.UiCulture);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
        }
        var localizer = new ResourceLocalizer();
        var system = new WindowsSystemInfoProvider();
        var processRunner = new SystemProcessRunner();
        var history = new JsonlHistoryStore(
            maxEntries: settings.HistoryMaxEntries,
            retentionDays: settings.HistoryRetentionDays,
            log: log);
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
            new DiskSpaceRepair(new WindowsTempFileCleaner(), processRunner, system, localizer),
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
            localizer);

        Application.Run(new TrayApplicationContext(engine, history, settingsStore, system, notifier, log, localizer));
    }
}
