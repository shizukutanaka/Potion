using System.Globalization;
using System.Text;
using Potion.Tray.Core;
using Potion.Tray.Core.Resources;

namespace Potion.Tray;

internal sealed class HistoryForm : Form
{
    private readonly IHistoryStore history;
    private readonly ITrayLog log;
    private readonly TraySettings settings;
    private readonly ILocalizer localizer;
    private readonly ListView list = new();
    private readonly TextBox details = new();
    private readonly TextBox search = new() { Width = 220 };
    private readonly ComboBox filter = new();
    private readonly Button refresh = new();
    private IReadOnlyList<HistoryEntry> entries = Array.Empty<HistoryEntry>();

    public HistoryForm(IHistoryStore history, TraySettings settings, ILocalizer localizer, ITrayLog log)
    {
        this.history = history;
        this.log = log;
        this.settings = settings;
        this.localizer = localizer;
        Text = localizer.Get("Ui.History.Title");
        Width = 980;
        Height = 620;
        StartPosition = FormStartPosition.CenterScreen;

        filter.DropDownStyle = ComboBoxStyle.DropDownList;
        filter.Items.AddRange(new object[]
        {
            localizer.Get("Ui.History.All"),
            localizer.Get("Ui.History.Repaired"),
            localizer.Get("Ui.History.Failed"),
            localizer.Get("Ui.History.Skipped"),
            localizer.Get("Ui.History.Manual")
        });
        filter.SelectedIndex = 0;
        filter.SelectedIndexChanged += (_, _) => RefreshList();
        search.PlaceholderText = localizer.Get("Ui.History.SearchHint");
        search.TextChanged += (_, _) => RefreshList();
        refresh.Text = localizer.Get("Ui.History.Refresh");
        refresh.AutoSize = true;
        refresh.Click += async (_, _) => await LoadHistoryAsync();
        var export = new Button { Text = localizer.Get("Ui.History.Export"), AutoSize = true };
        export.Click += (_, _) => ExportCsv();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8) };
        top.Controls.Add(new Label { Text = localizer.Get("Ui.History.Filter"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(filter);
        top.Controls.Add(search);
        top.Controls.Add(refresh);
        top.Controls.Add(export);

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.Columns.Add(localizer.Get("Ui.History.Column.Timestamp"), 170);
        list.Columns.Add(localizer.Get("Ui.History.Column.Target"), 150);
        list.Columns.Add(localizer.Get("Ui.History.Column.Severity"), 90);
        list.Columns.Add(localizer.Get("Ui.History.Column.Result"), 120);
        list.Columns.Add(localizer.Get("Ui.History.Column.Summary"), 380);
        list.SelectedIndexChanged += (_, _) => ShowSelectedDetails();

        details.Dock = DockStyle.Bottom;
        details.Multiline = true;
        details.ReadOnly = true;
        details.ScrollBars = ScrollBars.Vertical;
        details.Height = 170;

        Controls.Add(list);
        Controls.Add(details);
        Controls.Add(top);
        Shown += async (_, _) => await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        refresh.Enabled = false;
        try
        {
            entries = await history.ReadRecentAsync(settings.HistoryMaxEntries, CancellationToken.None);
            if (history.LastReadFailed)
            {
                ShowHistoryUnavailable();
                return;
            }

            RefreshList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("Unable to load history.", ex);
            ShowHistoryUnavailable();
        }
        finally
        {
            refresh.Enabled = true;
        }
    }

    private void ShowHistoryUnavailable()
    {
        MessageBox.Show(
            this,
            localizer.Get("Notify.HistoryUnavailable.Message"),
            localizer.Get("Notify.HistoryUnavailable.Title"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void RefreshList()
    {
        list.Items.Clear();
        var filtered = entries.Where(MatchesEntry).ToList();
        foreach (var entry in filtered)
        {
            var item = new ListViewItem(entry.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            item.SubItems.Add(entry.Title);
            item.SubItems.Add(StatusText(entry.Status));
            item.SubItems.Add(OutcomeText(entry.Outcome));
            item.SubItems.Add(HistoryText.SingleLine(entry.Detail));
            item.Tag = entry;
            list.Items.Add(item);
        }

        if (filtered.Count == 0)
        {
            details.Text = localizer.Get("Ui.History.Empty");
        }
    }

    private bool MatchesFilter(HistoryEntry entry) =>
        filter.SelectedIndex switch
        {
            1 => entry.Outcome == HistoryOutcome.Repaired,
            2 => entry.Outcome == HistoryOutcome.RepairFailed,
            3 => entry.Outcome == HistoryOutcome.Skipped,
            4 => entry.Outcome == HistoryOutcome.ManualActionRequired,
            _ => true
        };

    private bool MatchesEntry(HistoryEntry entry) =>
        MatchesFilter(entry) && HistorySearch.Matches(entry, search.Text);

    private void ShowSelectedDetails()
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not HistoryEntry entry)
        {
            details.Text = list.Items.Count == 0 ? localizer.Get("Ui.History.Empty") : string.Empty;
            return;
        }

        var builder = new StringBuilder()
            .AppendLine(entry.Detail)
            .AppendLine(localizer.Format("Ui.History.Detail.Result", OutcomeText(entry.Outcome)))
            .AppendLine(localizer.Format("Ui.History.Detail.Duration", DurationFormatter.Format(entry.Duration, localizer)))
            .AppendLine(localizer.Format("Ui.History.Detail.RepairSummary", entry.RepairSummary ?? localizer.Get("Ui.History.Detail.None")))
            .AppendLine(localizer.Format("Ui.History.Detail.SkipReason", entry.SkipReason ?? localizer.Get("Ui.History.Detail.None")));
        foreach (var command in entry.Commands)
        {
            builder.AppendLine()
                .AppendLine(localizer.Format("Ui.History.Detail.Command", command.FileName, command.Arguments))
                .AppendLine(localizer.Format(
                    "Ui.History.Detail.ExitCode",
                    command.ExitCode,
                    DurationFormatter.Format(command.Duration, localizer)))
                .AppendLine(localizer.Format("Ui.History.Detail.StdOut", command.StdOutTail))
                .AppendLine(localizer.Format("Ui.History.Detail.StdErr", command.StdErrTail));
        }

        details.Text = builder.ToString();
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|*.*|*.*",
            FileName = "potion-history.csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine(string.Join(",", new[]
            {
                localizer.Get("Ui.History.Column.Timestamp"),
                localizer.Get("Ui.History.Column.Target"),
                localizer.Get("Ui.History.Column.Severity"),
                localizer.Get("Ui.History.Column.Result"),
                localizer.Get("Ui.History.Column.Summary"),
                localizer.Get("Ui.History.Column.RepairSummary"),
                localizer.Get("Ui.History.Column.SkipReason"),
                localizer.Get("Ui.History.Column.Commands")
            }.Select(EscapeCsv)));
            foreach (var entry in entries.Where(MatchesEntry))
            {
                builder.AppendLine(string.Join(",", new[]
                {
                    entry.TimestampUtc.ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
                    entry.Title,
                    entry.Status.ToString(),
                    entry.Outcome.ToString(),
                    entry.Detail,
                    entry.RepairSummary ?? string.Empty,
                    entry.SkipReason ?? string.Empty,
                    HistoryText.CommandSummary(entry.Commands)
                }.Select(EscapeCsv)));
            }

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            log.Warn("Unable to export history CSV.", ex);
            MessageBox.Show(
                this,
                localizer.Get("Ui.History.ExportFailed"),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string EscapeCsv(string value)
    {
        if (!string.IsNullOrEmpty(value) &&
            value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.Contains(',') ||
               value.Contains('"') ||
               value.Contains('\r') ||
               value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }

    private string StatusText(HealthStatus status) => status switch
    {
        HealthStatus.Critical => localizer.Get("Status.Critical"),
        HealthStatus.Warning => localizer.Get("Status.Warning"),
        _ => localizer.Get("Status.Healthy")
    };

    private string OutcomeText(HistoryOutcome outcome) => outcome switch
    {
        HistoryOutcome.Repaired => localizer.Get("Outcome.Repaired"),
        HistoryOutcome.RepairFailed => localizer.Get("Outcome.RepairFailed"),
        HistoryOutcome.Skipped => localizer.Get("Outcome.Skipped"),
        HistoryOutcome.ManualActionRequired => localizer.Get("Outcome.ManualActionRequired"),
        _ => localizer.Get("Outcome.Detected")
    };
}

internal sealed class SettingsForm : Form
{
    private readonly ISettingsStore store;
    private readonly ITrayLog log;
    private readonly ILocalizer localizer;
    private readonly ISystemInfoProvider system;
    private readonly TraySettings settings;
    private readonly CheckBox autoRepair = new() { AutoSize = true };
    private readonly ComboBox notifications = new();
    private readonly NumericUpDown notificationCooldown = Numeric(0, 1440);
    private readonly NumericUpDown duplicateSuppression = Numeric(0, 1440);
    private readonly NumericUpDown scanInterval = Numeric(1, 1440);
    private readonly CheckBox startup = new() { AutoSize = true };
    private readonly CheckBox dryRun = new() { AutoSize = true };
    private readonly NumericUpDown diskWarn = Numeric(1, 90);
    private readonly NumericUpDown diskCritical = Numeric(1, 90);
    private readonly NumericUpDown diskWarnFreeGb = Numeric(1, 4096);
    private readonly NumericUpDown diskCriticalFreeGb = Numeric(1, 4096);
    private readonly NumericUpDown memoryWarn = Numeric(1, 90);
    private readonly NumericUpDown attempts = Numeric(1, 50);
    private readonly NumericUpDown historyMax = Numeric(50, 20000);
    private readonly NumericUpDown retention = Numeric(1, 3650);
    private readonly CheckBox cleanup = new() { AutoSize = true };
    private readonly TextBox dnsProbeHost = new();
    private readonly TextBox services = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 70 };
    private readonly CheckedListBox enabledChecks = new() { Height = 115, CheckOnClick = true };
    private readonly ComboBox language = new();
    private readonly string[] checkIds = { "disk-space", "critical-services", "component-store", "memory-pressure", "pending-reboot", "network" };

    public SettingsForm(ISettingsStore store, ITrayLog log, ILocalizer localizer, ISystemInfoProvider system)
    {
        this.store = store;
        this.log = log;
        this.localizer = localizer;
        this.system = system;
        settings = store.Load().Clone();
        Text = localizer.Get("Ui.Settings.Title");
        Width = 620;
        Height = 650;
        StartPosition = FormStartPosition.CenterScreen;

        notifications.DropDownStyle = ComboBoxStyle.DropDownList;
        notifications.Items.AddRange(new object[]
        {
            localizer.Get("Ui.Menu.AllNotifications"),
            localizer.Get("Ui.Menu.RepairsOnly"),
            localizer.Get("Ui.Menu.FailuresOnly"),
            localizer.Get("Ui.Menu.NoNotifications")
        });
        language.DropDownStyle = ComboBoxStyle.DropDownList;
        language.Items.Add(localizer.Get("Ui.Settings.AutomaticLanguage"));
        foreach (var culture in SupportedCultures)
        {
            language.Items.Add(localizer.Get($"Ui.Settings.LanguageName.{culture}"));
        }
        autoRepair.Text = localizer.Get("Ui.Settings.AutoRepair");
        startup.Text = localizer.Get("Ui.Settings.Startup");
        dryRun.Text = localizer.Get("Ui.Settings.DryRun");
        cleanup.Text = localizer.Get("Ui.Settings.ComponentCleanup");
        InitializeItems();
        ApplyValues();

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(12)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        Add(table, localizer.Get("Ui.Settings.AutoRepair"), autoRepair);
        Add(table, localizer.Get("Ui.Settings.NotificationMode"), notifications);
        Add(table, localizer.Get("Ui.Settings.NotificationCooldown"), notificationCooldown);
        Add(table, localizer.Get("Ui.Settings.DuplicateSuppression"), duplicateSuppression);
        Add(table, localizer.Get("Ui.Settings.ScanInterval"), scanInterval);
        Add(table, localizer.Get("Ui.Settings.Startup"), startup);
        Add(table, string.Empty, dryRun);
        Add(table, localizer.Get("Ui.Settings.DiskWarn"), diskWarn);
        Add(table, localizer.Get("Ui.Settings.DiskCritical"), diskCritical);
        Add(table, localizer.Get("Ui.Settings.DiskWarnFreeGb"), diskWarnFreeGb);
        Add(table, localizer.Get("Ui.Settings.DiskCriticalFreeGb"), diskCriticalFreeGb);
        Add(table, localizer.Get("Ui.Settings.MemoryWarn"), memoryWarn);
        Add(table, localizer.Get("Ui.Settings.Attempts"), attempts);
        Add(table, localizer.Get("Ui.Settings.HistoryMax"), historyMax);
        Add(table, localizer.Get("Ui.Settings.Retention"), retention);
        Add(table, string.Empty, cleanup);
        Add(table, localizer.Get("Ui.Settings.DnsProbeHost"), dnsProbeHost);
        Add(table, localizer.Get("Ui.Settings.Services"), services);
        Add(table, localizer.Get("Ui.Settings.EnabledChecks"), enabledChecks);
        Add(table, localizer.Get("Ui.Settings.Language"), language);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = localizer.Get("Ui.Settings.Save"), DialogResult = DialogResult.OK, AutoSize = true };
        save.Click += (_, _) => SaveValues();
        var cancel = new Button { Text = localizer.Get("Ui.Settings.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(table);
        Controls.Add(buttons);
    }

    private void InitializeItems()
    {
        foreach (var id in checkIds)
        {
            enabledChecks.Items.Add(CheckTitle(id));
        }
    }

    private void ApplyValues()
    {
        autoRepair.Checked = settings.AutoRepairEnabled;
        notifications.SelectedIndex = (int)settings.Notifications;
        notificationCooldown.Value = settings.NotificationCooldownMinutes;
        duplicateSuppression.Value = settings.DuplicateSuppressionMinutes;
        scanInterval.Value = settings.ScanIntervalMinutes;
        startup.Checked = settings.RunAtWindowsStartup;
        dryRun.Checked = settings.DryRun;
        diskWarn.Value = settings.DiskWarnPercent;
        diskCritical.Value = settings.DiskCriticalPercent;
        diskWarnFreeGb.Value = settings.DiskWarnFreeGb;
        diskCriticalFreeGb.Value = settings.DiskCriticalFreeGb;
        memoryWarn.Value = settings.MemoryWarnPercent;
        attempts.Value = settings.MaxRepairAttemptsPerDay;
        historyMax.Value = settings.HistoryMaxEntries;
        retention.Value = settings.HistoryRetentionDays;
        cleanup.Checked = settings.AllowComponentCleanup;
        dnsProbeHost.Text = settings.DnsProbeHost;
        language.SelectedIndex = Array.IndexOf(SupportedCultures, settings.UiCulture) + 1;
        if (language.SelectedIndex < 0)
        {
            language.SelectedIndex = 0;
        }
        services.Text = string.Join(Environment.NewLine, settings.MonitoredServices);
        foreach (var id in checkIds)
        {
            var index = Array.IndexOf(checkIds, id);
            enabledChecks.SetItemChecked(index, settings.IsCheckEnabled(id));
        }
    }

    private void SaveValues()
    {
        settings.AutoRepairEnabled = autoRepair.Checked;
        settings.Notifications = (NotificationMode)Math.Max(0, notifications.SelectedIndex);
        settings.NotificationCooldownMinutes = (int)notificationCooldown.Value;
        settings.DuplicateSuppressionMinutes = (int)duplicateSuppression.Value;
        settings.ScanIntervalMinutes = (int)scanInterval.Value;
        settings.RunAtWindowsStartup = startup.Checked;
        settings.DryRun = dryRun.Checked;
        settings.DiskWarnPercent = (int)diskWarn.Value;
        settings.DiskCriticalPercent = (int)diskCritical.Value;
        settings.DiskWarnFreeGb = (int)diskWarnFreeGb.Value;
        settings.DiskCriticalFreeGb = (int)diskCriticalFreeGb.Value;
        settings.MemoryWarnPercent = (int)memoryWarn.Value;
        settings.MaxRepairAttemptsPerDay = (int)attempts.Value;
        settings.HistoryMaxEntries = (int)historyMax.Value;
        settings.HistoryRetentionDays = (int)retention.Value;
        settings.AllowComponentCleanup = cleanup.Checked;
        settings.DnsProbeHost = dnsProbeHost.Text;
        settings.UiCulture = language.SelectedIndex <= 0 ? string.Empty : SupportedCultures[language.SelectedIndex - 1];
        settings.MonitoredServices = services.Lines.ToList();
        settings.ChecksEnabled = checkIds
            .Select((id, index) => new { id, enabled = enabledChecks.GetItemChecked(index) })
            .ToDictionary(x => x.id, x => x.enabled, StringComparer.OrdinalIgnoreCase);
        var adjusted = settings.NormalizeWithChanges();
        if (adjusted.Count > 0)
        {
            ApplyValues();
            var names = string.Join(
                localizer.Get("Format.ListSeparator"),
                adjusted.Select(localizer.Get));
            MessageBox.Show(
                this,
                localizer.Format("Ui.Settings.Adjusted", names),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        if (settings.ChecksEnabled.Values.All(enabled => !enabled))
        {
            var result = MessageBox.Show(
                this,
                localizer.Get("Ui.Settings.NoChecksWarning"),
                Text,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (result == DialogResult.Cancel)
            {
                DialogResult = DialogResult.None;
                return;
            }
        }

        try
        {
            var unknownServices = system.GetServices(settings.MonitoredServices)
                .Where(service => !service.Exists)
                .Select(service => service.Name)
                .ToList();
            if (unknownServices.Count > 0)
            {
                MessageBox.Show(
                    this,
                    localizer.Format(
                        "Ui.Settings.UnknownServices",
                        string.Join(localizer.Get("Format.ListSeparator"), unknownServices)),
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            log.Warn("Unable to validate monitored services.", ex);
        }

        store.Save(settings);
        if (store.LastSaveFailed)
        {
            MessageBox.Show(
                this,
                localizer.Get("Ui.Settings.SaveFailed"),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            DialogResult = DialogResult.None;
            return;
        }

        if (!StartupRegistration.Apply(settings.RunAtWindowsStartup, log))
        {
            MessageBox.Show(
                this,
                localizer.Get("Notify.ActionFailed.Message"),
                localizer.Get("Notify.ActionFailed.Title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void Add(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 5, 4, 5) }, 0, row);
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 1, row);
    }

    private static NumericUpDown Numeric(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        DecimalPlaces = 0
    };

    private string CheckTitle(string checkId) => checkId switch
    {
        "disk-space" => localizer.Get("Check.DiskSpace.Title"),
        "critical-services" => localizer.Get("Check.CriticalServices.Title"),
        "component-store" => localizer.Get("Check.ComponentStore.Title"),
        "memory-pressure" => localizer.Get("Check.MemoryPressure.Title"),
        "pending-reboot" => localizer.Get("Check.PendingReboot.Title"),
        "network" => localizer.Get("Check.Network.Title"),
        _ => checkId
    };

    private static readonly string[] SupportedCultures =
        { "en", "ja", "zh-Hans", "ko", "es", "fr", "de", "pt-BR", "ru" };
}
