using System.Drawing;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;
using Potion.Tray.Core;
using Potion.Tray.Core.Resources;

namespace Potion.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SelfHealingEngine engine;
    private readonly IHistoryStore history;
    private readonly ISettingsStore settingsStore;
    private readonly ISystemInfoProvider system;
    private readonly INotifier notifier;
    private readonly ITrayLog log;
    private readonly ILocalizer localizer;
    private readonly NotifyIcon notifyIcon;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem lastScanItem;
    private readonly ToolStripMenuItem autoRepairItem;
    private readonly ToolStripMenuItem notificationAllItem;
    private readonly ToolStripMenuItem notificationRepairsItem;
    private readonly ToolStripMenuItem notificationFailuresItem;
    private readonly ToolStripMenuItem notificationNoneItem;
    private readonly ToolStripMenuItem scanItem;
    private readonly ToolStripMenuItem historyItem;
    private readonly ToolStripMenuItem settingsItem;
    private readonly ToolStripMenuItem notificationMenu;
    private readonly ToolStripMenuItem? adminItem;
    private readonly ToolStripMenuItem exitItem;
    private readonly System.Threading.Timer scanTimer;
    private readonly SynchronizationContext uiContext;
    private readonly CancellationTokenSource shutdown = new();
    private readonly RegisteredWaitHandle? showHistoryWait;
    private bool historyOpen;
    private DateTimeOffset lastKickUtc;

    public TrayApplicationContext(
        SelfHealingEngine engine,
        IHistoryStore history,
        ISettingsStore settingsStore,
        ISystemInfoProvider system,
        INotifier notifier,
        ITrayLog log,
        ILocalizer localizer,
        EventWaitHandle? showHistorySignal = null)
    {
        this.engine = engine;
        this.history = history;
        this.settingsStore = settingsStore;
        this.system = system;
        this.notifier = notifier;
        this.log = log;
        this.localizer = localizer;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var settings = settingsStore.Load();
        statusItem = new ToolStripMenuItem(engine.StatusText) { Enabled = false };
        lastScanItem = new ToolStripMenuItem(engine.LastScanText) { Enabled = false };
        autoRepairItem = new ToolStripMenuItem(localizer.Get("Ui.Menu.AutoRepair"))
        {
            CheckOnClick = true,
            Checked = settings.AutoRepairEnabled
        };
        autoRepairItem.Click += (_, _) =>
        {
            var current = settingsStore.Load();
            current.AutoRepairEnabled = autoRepairItem.Checked;
            settingsStore.Save(current);
        };

        notificationAllItem = NotificationItem("Ui.Menu.AllNotifications", NotificationMode.All);
        notificationRepairsItem = NotificationItem("Ui.Menu.RepairsOnly", NotificationMode.RepairsOnly);
        notificationFailuresItem = NotificationItem("Ui.Menu.FailuresOnly", NotificationMode.FailuresOnly);
        notificationNoneItem = NotificationItem("Ui.Menu.NoNotifications", NotificationMode.None);
        ApplyNotificationChecks(settings.Notifications);

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(lastScanItem);
        menu.Items.Add(new ToolStripSeparator());
        scanItem = new ToolStripMenuItem(localizer.Get("Ui.Menu.ScanNow"));
        menu.Items.Add(scanItem);
        scanItem.Click += async (_, _) => await RunScanAsync();
        historyItem = new ToolStripMenuItem(localizer.Get("Ui.Menu.History"));
        menu.Items.Add(historyItem);
        historyItem.Click += (_, _) => ShowHistory();
        settingsItem = new ToolStripMenuItem(localizer.Get("Ui.Menu.Settings"));
        menu.Items.Add(settingsItem);
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoRepairItem);
        notificationMenu = new ToolStripMenuItem(localizer.Get("Ui.Menu.Notifications"));
        notificationMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            notificationAllItem, notificationRepairsItem, notificationFailuresItem, notificationNoneItem
        });
        menu.Items.Add(notificationMenu);
        menu.Items.Add(new ToolStripSeparator());
        if (!system.IsElevated)
        {
            adminItem = new ToolStripMenuItem(localizer.Get("Ui.Menu.RestartAsAdmin"));
            menu.Items.Add(adminItem);
            adminItem!.Click += (_, _) => AdministratorRestart.Restart(log);
        }

        exitItem = new ToolStripMenuItem(localizer.Get("Ui.Menu.Exit"));
        menu.Items.Add(exitItem);
        exitItem.Click += (_, _) => ExitThread();
        notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = Shorten($"{engine.StatusText} / {engine.LastScanText}"),
            Icon = IconFactory.Create(EngineState.Idle),
            ContextMenuStrip = menu
        };
        if (notifier is BalloonNotifier balloonNotifier)
        {
            balloonNotifier.Attach(notifyIcon);
        }
        ShowStartupNotices(settings);
        notifyIcon.DoubleClick += (_, _) => ShowHistory();

        engine.StateChanged += EngineOnStateChanged;
        engine.CycleCompleted += EngineOnStateChanged;
        SystemEvents.PowerModeChanged += SystemEventsOnPowerModeChanged;
        SystemEvents.SessionSwitch += SystemEventsOnSessionSwitch;
        NetworkChange.NetworkAvailabilityChanged += NetworkChangeOnNetworkAvailabilityChanged;
        showHistoryWait = null;
        if (showHistorySignal is not null)
        {
            showHistoryWait = ThreadPool.RegisterWaitForSingleObject(
                showHistorySignal,
                (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        uiContext.Post(_ => ShowHistory(), null);
                    }
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        scanTimer = new System.Threading.Timer(
            async _ =>
            {
                try
                {
                    await RunScanAsync();
                }
                catch (Exception ex)
                {
                    log.Error("Scan callback failed.", ex);
                }
            },
            null,
            TimeSpan.FromSeconds(30),
            Timeout.InfiniteTimeSpan);
    }

    protected override void ExitThreadCore()
    {
        shutdown.Cancel();
        engine.StateChanged -= EngineOnStateChanged;
        engine.CycleCompleted -= EngineOnStateChanged;
        SystemEvents.PowerModeChanged -= SystemEventsOnPowerModeChanged;
        SystemEvents.SessionSwitch -= SystemEventsOnSessionSwitch;
        NetworkChange.NetworkAvailabilityChanged -= NetworkChangeOnNetworkAvailabilityChanged;
        scanTimer.Dispose();
        showHistoryWait?.Unregister(null);
        notifyIcon.Visible = false;
        var icon = notifyIcon.Icon;
        notifyIcon.Icon = null;
        notifyIcon.Dispose();
        icon?.Dispose();
        shutdown.Dispose();
        if (history is IDisposable disposable)
        {
            disposable.Dispose();
        }
        base.ExitThreadCore();
    }

    private ToolStripMenuItem NotificationItem(string key, NotificationMode mode)
    {
        var item = new ToolStripMenuItem(localizer.Get(key))
        {
            CheckOnClick = true,
            Tag = mode
        };
        item.Click += (_, _) =>
        {
            var settings = settingsStore.Load();
            settings.Notifications = mode;
            settingsStore.Save(settings);
            ApplyNotificationChecks(mode);
        };
        return item;
    }

    private void ShowStartupNotices(TraySettings settings)
    {
        if (settings.Notifications == NotificationMode.None)
        {
            return;
        }

        var current = settingsStore.Load();
        var changed = false;
        if (!current.HasSeenWelcome)
        {
            notifier.Notify(new Notification(
                localizer.Get("Notify.Welcome.Title"),
                localizer.Get("Notify.Welcome.Message"),
                HealthStatus.Healthy));
            current.HasSeenWelcome = true;
            changed = true;
        }
        else if (!system.IsElevated && !current.HasSeenAdminNotice)
        {
            notifier.Notify(new Notification(
                localizer.Get("Notify.AdminRequired.Title"),
                localizer.Get("Notify.AdminRequired.Message"),
                HealthStatus.Warning));
            current.HasSeenAdminNotice = true;
            changed = true;
        }

        if (changed)
        {
            settingsStore.Save(current);
        }
    }

    private void ApplyNotificationChecks(NotificationMode mode)
    {
        notificationAllItem.Checked = mode == NotificationMode.All;
        notificationRepairsItem.Checked = mode == NotificationMode.RepairsOnly;
        notificationFailuresItem.Checked = mode == NotificationMode.FailuresOnly;
        notificationNoneItem.Checked = mode == NotificationMode.None;
    }

    private async Task RunScanAsync()
    {
        if (shutdown.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await engine.RunCycleAsync(shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.Error("Periodic scan failed with an unhandled exception.", ex);
        }
        finally
        {
            if (!shutdown.IsCancellationRequested)
            {
                try
                {
                    var settings = settingsStore.Load();
                    try
                    {
                        scanTimer.Change(TimeSpan.FromMinutes(settings.ScanIntervalMinutes), Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
                catch (Exception ex)
                {
                    log.Error("Unable to schedule the next scan.", ex);
                }
            }
        }
    }

    private void EngineOnStateChanged(object? sender, EventArgs e)
    {
        var state = engine.State;
        uiContext.Post(_ =>
        {
            statusItem.Text = engine.StatusText;
            lastScanItem.Text = engine.LastScanText;
            notifyIcon.Text = Shorten($"{engine.StatusText} / {engine.LastScanText}");
            var oldIcon = notifyIcon.Icon;
            notifyIcon.Icon = IconFactory.Create(state);
            oldIcon?.Dispose();
        }, null);
    }

    private void ShowHistory()
    {
        if (historyOpen)
        {
            return;
        }

        historyOpen = true;
        try
        {
            using var form = new HistoryForm(history, settingsStore.Load(), localizer, log);
            form.ShowDialog();
        }
        finally
        {
            historyOpen = false;
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(settingsStore, log, localizer, system);
        if (form.ShowDialog() == DialogResult.OK)
        {
            var settings = settingsStore.Load();
            ApplyCulture(settings);
            autoRepairItem.Checked = settings.AutoRepairEnabled;
            ApplyNotificationChecks(settings.Notifications);
            try
            {
                scanTimer.Change(TimeSpan.FromMinutes(settings.ScanIntervalMinutes), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void ApplyCulture(TraySettings settings)
    {
        CultureConfigurator.Apply(settings.UiCulture);
        statusItem.Text = engine.StatusText;
        lastScanItem.Text = engine.LastScanText;
        scanItem.Text = localizer.Get("Ui.Menu.ScanNow");
        historyItem.Text = localizer.Get("Ui.Menu.History");
        settingsItem.Text = localizer.Get("Ui.Menu.Settings");
        autoRepairItem.Text = localizer.Get("Ui.Menu.AutoRepair");
        notificationMenu.Text = localizer.Get("Ui.Menu.Notifications");
        notificationAllItem.Text = localizer.Get("Ui.Menu.AllNotifications");
        notificationRepairsItem.Text = localizer.Get("Ui.Menu.RepairsOnly");
        notificationFailuresItem.Text = localizer.Get("Ui.Menu.FailuresOnly");
        notificationNoneItem.Text = localizer.Get("Ui.Menu.NoNotifications");
        if (adminItem is not null)
        {
            adminItem.Text = localizer.Get("Ui.Menu.RestartAsAdmin");
        }
        exitItem.Text = localizer.Get("Ui.Menu.Exit");
        notifyIcon.Text = Shorten($"{engine.StatusText} / {engine.LastScanText}");
    }

    private void SystemEventsOnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            KickScan("power resume");
        }
    }

    private void SystemEventsOnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
        {
            KickScan("session resume");
        }
    }

    private void NetworkChangeOnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            KickScan("network availability");
        }
    }

    private void KickScan(string reason)
    {
        if (shutdown.IsCancellationRequested)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (lastKickUtc != default && now - lastKickUtc < TimeSpan.FromMinutes(2))
        {
            return;
        }

        lastKickUtc = now;
        log.Info($"Triggering a scan after {reason}.");
        _ = RunScanAsync();
    }

    private static string Shorten(string text) => text.Length <= 63 ? text : text[..60] + "...";
}

internal static class IconFactory
{
    public static Icon Create(EngineState state)
    {
        var color = state switch
        {
            EngineState.Warning => Color.Orange,
            EngineState.Critical => Color.Red,
            EngineState.Scanning or EngineState.Repairing => Color.DodgerBlue,
            _ => Color.ForestGreen
        };
        var iconSize = Math.Clamp(Math.Max(SystemInformation.SmallIconSize.Width, SystemInformation.SmallIconSize.Height), 16, 64);
        var margin = Math.Max(1, (int)Math.Round(iconSize / 16d));
        var diameter = iconSize - margin * 2;
        using var bitmap = new Bitmap(iconSize, iconSize);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, margin, margin, diameter, diameter);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal sealed class BalloonNotifier : INotifier
{
    private NotifyIcon? notifyIcon;
    private SynchronizationContext? context;

    public void Attach(NotifyIcon icon)
    {
        notifyIcon = icon;
        context ??= SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    }

    public void Notify(Notification notification)
    {
        (context ??= SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext()).Post(_ =>
        {
            if (notifyIcon is null)
            {
                return;
            }

            var icon = notification.Severity switch
            {
                HealthStatus.Critical => ToolTipIcon.Error,
                HealthStatus.Warning => ToolTipIcon.Warning,
                _ => ToolTipIcon.Info
            };
            notifyIcon.ShowBalloonTip(10000, notification.Title, notification.Message, icon);
        }, null);
    }
}
