using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Runtime.Ipc;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.SnipeIt;
using AntButton = AntdUI.Button;
using AntInput = AntdUI.Input;
using AntLabel = AntdUI.Label;
using AntSelect = AntdUI.Select;
using AntSwitch = AntdUI.Switch;
using AntTabPage = AntdUI.TabPage;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Edits and tests the complete shared configuration inside the reusable Dashboard window.
/// </summary>
internal sealed class SyncConfigurationPage : UserControl
{
    private const string TeamsWebhookFormatText = "Teams Workflow (Adaptive Card)";
    private const string GenericWebhookFormatText = "Generic JSON";
    private static readonly string[] CompletedNotificationEventTypes =
    [
        NotificationEventTypes.ScheduledSyncCompleted,
        NotificationEventTypes.ManualSyncCompleted,
        NotificationEventTypes.ManualPreviewCompleted,
        NotificationEventTypes.SyncCompleted
    ];
    private static readonly string[] FailedNotificationEventTypes =
    [
        NotificationEventTypes.ScheduledSyncFailed,
        NotificationEventTypes.ManualSyncFailed,
        NotificationEventTypes.ManualPreviewFailed,
        NotificationEventTypes.SyncFailed
    ];

    private readonly LocalAppSettingsStore _settingsStore;
    private readonly WorkerIpcClient _ipcClient;
    private readonly DailyLogWriter _manualLogWriter;
    private readonly Dictionary<string, AntInput> _text = new(StringComparer.Ordinal);
    private readonly AntSwitch _createCompanies = Toggle("CreateMissingCompanies");
    private readonly AntSwitch _createModels = Toggle("CreateMissingModels");
    private readonly AntSwitch _scheduleEnabled = Toggle("ScheduleEnabled");
    private readonly AntSwitch _lastDay = Toggle("RunOnLastDayOfMonth");
    private readonly AntSwitch _notificationsEnabled = Toggle("NotificationsEnabled");
    private readonly AntSwitch _notifySyncCompleted = Toggle("NotifySyncCompleted");
    private readonly AntSwitch _notifySyncFailed = Toggle("NotifySyncFailed");
    private readonly AntSwitch _smtpUseSsl = Toggle("SmtpUseSsl");
    private readonly AntSelect _frequency = new() { Dock = DockStyle.Top, Height = 44, Name = "ScheduleFrequency", Radius = TrayUiTheme.ControlRadius };
    private readonly AntSelect _webhookPayloadFormat = new() { Dock = DockStyle.Top, Height = 44, Radius = TrayUiTheme.ControlRadius };
    private readonly AntButton _save = new()
    {
        Height = 46,
        IconSvg = "SaveOutlined",
        MinimumSize = new Size(156, 46),
        Padding = new Padding(14, 0, 14, 0),
        Radius = TrayUiTheme.ControlRadius,
        Text = "Save changes",
        Type = AntdUI.TTypeMini.Primary,
        Width = 156
    };
    private readonly AntButton _back = new()
    {
        Height = 46,
        IconSvg = "ArrowLeftOutlined",
        MinimumSize = new Size(176, 46),
        Padding = new Padding(14, 0, 14, 0),
        Radius = TrayUiTheme.ControlRadius,
        Text = "Cancel",
        Width = 176
    };
    private readonly AntButton _testConnections = new()
    {
        Height = 44,
        IconSvg = "ApiOutlined",
        MinimumSize = new Size(176, 44),
        Radius = TrayUiTheme.ControlRadius,
        Text = "Test Connections",
        Width = 176
    };
    private readonly AntButton _testNotifications = new()
    {
        Height = 44,
        IconSvg = "NotificationOutlined",
        MinimumSize = new Size(190, 44),
        Radius = TrayUiTheme.ControlRadius,
        Text = "Test Notifications",
        Width = 190
    };
    private readonly AntLabel _connectionTestStatus = new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Top,
        ForeColor = TrayUiTheme.MutedText,
        Height = 44,
        Text = "Current form settings will be saved before the Worker tests both connections.",
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly AntLabel _notificationTestStatus = new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Top,
        ForeColor = TrayUiTheme.MutedText,
        Height = 44,
        Text = "Sends one test through each completely configured notification channel.",
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly AntLabel _status = new() { AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = Color.DarkRed, TextAlign = ContentAlignment.MiddleLeft };

    internal event EventHandler? DashboardRequested;
    internal event EventHandler? SettingsSaved;

    internal SyncConfigurationPage(
        LocalAppSettingsStore settingsStore,
        WorkerIpcClient ipcClient,
        DailyLogWriter manualLogWriter)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _ipcClient = ipcClient ?? throw new ArgumentNullException(nameof(ipcClient));
        _manualLogWriter = manualLogWriter ?? throw new ArgumentNullException(nameof(manualLogWriter));
        InitializeComponent();
    }

    /// <summary>
    /// Reloads local JSON every time the reusable page is entered; load failures remain visible and never trigger an external request.
    /// </summary>
    internal async Task ActivateAsync()
    {
        SetActionButtonsEnabled(false);
        _status.ForeColor = TrayUiTheme.MutedText;
        _status.Text = "Loading configuration...";
        try
        {
            Apply(await _settingsStore.LoadSyncAppSettingsAsync(CancellationToken.None).ConfigureAwait(true));
            _status.Text = string.Empty;
        }
        catch (Exception exception)
        {
            _status.ForeColor = TrayUiTheme.Danger;
            _status.Text = $"Could not load configuration: {exception.Message}";
        }
        finally
        {
            SetActionButtonsEnabled(true);
        }
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        BackColor = TrayUiTheme.Canvas;
        Dock = DockStyle.Fill;
        _smtpUseSsl.Checked = true;

        var root = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Canvas,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        root.Controls.Add(new AntdUI.PageHeader
        {
            BackColor = TrayUiTheme.Surface,
            DividerShow = true,
            Dock = DockStyle.Fill,
            MaximizeBox = true,
            MinimizeBox = true,
            ShowButton = true,
            Text = "Configuration",
            SubText = "API, mapping, schedule and notifications",
            UseTextBold = true
        }, 0, 0);

        var tabs = new AntdUI.Tabs
        {
            BackColor = TrayUiTheme.Canvas,
            Dock = DockStyle.Fill,
            Margin = new Padding(18, 12, 18, 4),
            Type = AntdUI.TabType.Card,
            Style = new AntdUI.Tabs.StyleCard { Gap = 6, Radius = 7 }
        };
        tabs.Pages.Add(CreateApiTab());
        tabs.Pages.Add(CreateMappingTab());
        tabs.Pages.Add(CreateScheduleTab());
        tabs.Pages.Add(CreateNotificationTab());
        root.Controls.Add(tabs, 0, 1);

        var footer = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Surface,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 14, 18, 14),
            RowCount = 1
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _back.Dock = DockStyle.Fill;
        _back.Click += (_, _) => DashboardRequested?.Invoke(this, EventArgs.Empty);
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        _testConnections.Click += async (_, _) => await TestConnectionsAsync().ConfigureAwait(true);
        _testNotifications.Click += async (_, _) => await TestNotificationsAsync().ConfigureAwait(true);
        _save.Dock = DockStyle.Fill;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_back, 1, 0);
        footer.Controls.Add(_save, 2, 0);
        root.Controls.Add(footer, 0, 2);

        Controls.Add(root);
        ResumeLayout(performLayout: true);
    }

    private AntTabPage CreateApiTab()
    {
        var page = Page("API & Credentials");
        var grid = Grid();
        AddText(grid, "AteraBaseUrl", "Atera API base URL", "https://app.atera.com/api/v3/");
        AddText(grid, "AteraApiKey", "Atera API key", password: true);
        AddText(grid, "SnipeItBaseUrl", "Snipe-IT API base URL");
        AddText(grid, "SnipeItApiToken", "Snipe-IT API token", password: true);
        AddControl(grid, string.Empty, _testConnections);
        AddControl(grid, "Connection status", _connectionTestStatus);
        page.Controls.Add(grid);
        return page;
    }

    private AntTabPage CreateMappingTab()
    {
        var page = Page("Mapping & Import");
        var grid = Grid();
        AddText(grid, "CompanyAliases", "Company aliases (one key=value per line)", multiline: true);
        AddText(grid, "ManufacturerAliases", "Manufacturer aliases (one key=value per line)", multiline: true);
        AddText(grid, "DefaultCategory", "Default category", "Computer");
        AddText(grid, "NormalizeCategories", "Normalize from categories (;)", "Server; Laptop; Desktop");
        AddText(grid, "IgnoredDeviceTypes", "Ignored device types (;)");
        AddText(grid, "DefaultStatusId", "Default status ID", "2");
        AddText(grid, "MacColumn", "MAC custom field DB column");
        AddText(grid, "MacFieldset", "MAC fieldset name");
        AddText(grid, "IgnoredMacs", "Ignored MAC addresses (;)");
        AddText(grid, "NameThreshold", "Name match threshold", "0.92");
        AddControl(grid, "Create missing companies", _createCompanies);
        AddControl(grid, "Create missing models", _createModels);
        page.Controls.Add(grid);
        return page;
    }

    private AntTabPage CreateScheduleTab()
    {
        var page = Page("Schedule");
        var grid = Grid();
        foreach (var value in Enum.GetNames<ScheduleFrequency>())
        {
            _frequency.Items.Add(value);
        }

        _frequency.SelectedValue = ScheduleFrequency.Daily.ToString();
        AddControl(grid, "Frequency", _frequency);
        AddControl(grid, "Schedule enabled", _scheduleEnabled);
        AddText(grid, "TimeZone", "Windows time zone ID", TimeZoneInfo.Local.Id);
        AddText(grid, "RunTimes", "Run times (;, HH:mm)", "02:00");
        AddText(grid, "WeekDays", "Weekly days (;)", "Monday");
        AddText(grid, "MonthDays", "Monthly days (;)", "1");
        AddControl(grid, "Also run on the last day of month", _lastDay);
        _frequency.SelectedValueChanged += (_, _) => UpdateScheduleFieldVisibility(grid);
        UpdateScheduleFieldVisibility(grid);
        page.Controls.Add(grid);
        return page;
    }

    /// <summary>
    /// Shows only the day-selection rows applicable to the selected schedule frequency without clearing their values.
    /// </summary>
    private void UpdateScheduleFieldVisibility(TableLayoutPanel grid)
    {
        var frequency = Enum.TryParse<ScheduleFrequency>(_frequency.Text, ignoreCase: true, out var selected)
            ? selected
            : ScheduleFrequency.Daily;
        SetSettingRowVisible(grid, _text["WeekDays"], frequency == ScheduleFrequency.Weekly);
        SetSettingRowVisible(grid, _text["MonthDays"], frequency == ScheduleFrequency.Monthly);
        SetSettingRowVisible(grid, _lastDay, frequency == ScheduleFrequency.Monthly);
        grid.PerformLayout();
    }

    /// <summary>
    /// Applies one visibility state to both controls in an AutoSize settings row so hidden rows collapse completely.
    /// </summary>
    private static void SetSettingRowVisible(TableLayoutPanel grid, Control valueControl, bool visible)
    {
        var row = grid.GetRow(valueControl);
        for (var column = 0; column < grid.ColumnCount; column++)
        {
            if (grid.GetControlFromPosition(column, row) is { } rowControl)
            {
                rowControl.Visible = visible;
            }
        }
    }

    private AntTabPage CreateNotificationTab()
    {
        var page = Page("Notifications");
        var grid = Grid();
        _webhookPayloadFormat.Items.Add(TeamsWebhookFormatText);
        _webhookPayloadFormat.Items.Add(GenericWebhookFormatText);
        _webhookPayloadFormat.SelectedValue = TeamsWebhookFormatText;
        AddControl(grid, "Notifications enabled", _notificationsEnabled);
        AddControl(grid, "Sync completed", _notifySyncCompleted);
        AddControl(grid, "Sync failed", _notifySyncFailed);

        AddText(grid, "SmtpHost", "SMTP host");
        AddText(grid, "SmtpPort", "SMTP port", "587");
        AddControl(grid, "Use TLS/SSL for SMTP", _smtpUseSsl);
        AddText(grid, "SmtpUsername", "SMTP username");
        AddText(grid, "SmtpPassword", "SMTP password", password: true);
        AddText(grid, "EmailFrom", "Email from");
        AddText(grid, "EmailTo", "Email to");
        AddControl(grid, "Webhook format", _webhookPayloadFormat);
        AddText(grid, "WebhookUrl", "Webhook URL");
        AddControl(grid, string.Empty, _testNotifications);
        AddControl(grid, "Test status", _notificationTestStatus);
        page.Controls.Add(grid);
        return page;
    }

    private async Task SaveAsync()
    {
        SetActionButtonsEnabled(false);
        _status.ForeColor = Color.DarkRed;
        _status.Text = "Saving...";
        try
        {
            var settings = BuildSettings();
            await _settingsStore.SaveSyncAppSettingsAsync(settings, CancellationToken.None).ConfigureAwait(true);
            _status.ForeColor = TrayUiTheme.Success;
            _status.Text = "Configuration saved.";
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _status.Text = $"Save failed: {exception.Message}";
        }
        finally
        {
            SetActionButtonsEnabled(true);
        }
    }

    /// <summary>
    /// Saves and reloads the complete current form, then asks the Worker to test both APIs without putting credentials in IPC.
    /// </summary>
    private async Task TestConnectionsAsync()
    {
        if (!_testConnections.Enabled)
        {
            return;
        }

        var settingsSaved = false;
        SetActionButtonsEnabled(false);
        _status.ForeColor = TrayUiTheme.MutedText;
        _status.Text = "Saving current settings and starting connection test...";
        _connectionTestStatus.ForeColor = TrayUiTheme.MutedText;
        _connectionTestStatus.Text = "Testing Atera and Snipe-IT through the Worker...";
        WriteManualLog("Command", "Saving current configuration before connection test.");

        try
        {
            var settings = BuildSettings();
            await _settingsStore.SaveSyncAppSettingsAsync(settings, CancellationToken.None).ConfigureAwait(true);
            settingsSaved = true;

            var reloadEvent = await _ipcClient
                .ExecuteAsync(WorkerIpcCommands.ReloadSchedule, CancellationToken.None)
                .ConfigureAwait(true);
            if (reloadEvent.ScheduleReload is not { Applied: true })
            {
                throw new WorkerCommandException("Worker did not apply the saved configuration for testing.");
            }

            var progress = new Progress<SyncProgressUpdate>(update =>
                WriteManualLog("Progress", $"{update.Stage}: {update.Message}"));
            WriteManualLog("Command", $"Starting {WorkerIpcCommands.TestConnections}.");
            var operation = _ipcClient.Start(
                WorkerIpcCommands.TestConnections,
                progress,
                CancellationToken.None);
            var terminal = await operation.Completion.ConfigureAwait(true);
            var result = terminal.ConnectionTest
                ?? throw new WorkerProtocolException("Worker did not return connection-test results.");
            var succeeded = result.Atera.Succeeded && result.SnipeIt.Succeeded;

            _connectionTestStatus.ForeColor = succeeded ? TrayUiTheme.Success : TrayUiTheme.Danger;
            _connectionTestStatus.Text =
                $"Atera: {Outcome(result.Atera.Succeeded)}  •  Snipe-IT: {Outcome(result.SnipeIt.Succeeded)}";
            _status.ForeColor = succeeded ? TrayUiTheme.Success : TrayUiTheme.Danger;
            _status.Text = succeeded
                ? "Settings saved. Both connections succeeded."
                : "Settings saved. One or more connections need attention; see the daily log.";
            WriteManualLog(
                "Connection",
                $"Atera={Outcome(result.Atera.Succeeded)}; {result.Atera.Message}");
            WriteManualLog(
                "Connection",
                $"Snipe-IT={Outcome(result.SnipeIt.Succeeded)}; {result.SnipeIt.Message}");
        }
        catch (WorkerBusyException exception)
        {
            _connectionTestStatus.ForeColor = TrayUiTheme.Danger;
            _connectionTestStatus.Text = "Connection test not started: Worker is busy.";
            _status.ForeColor = TrayUiTheme.Danger;
            _status.Text = $"{SavedPrefix(settingsSaved)}Active operation: {TrayStatusFormatter.FormatOperation(exception.ActiveOperation)}";
            WriteManualLog("Busy", _status.Text);
        }
        catch (Exception exception) when (exception is WorkerUnavailableException
            or WorkerProtocolException
            or WorkerCommandException)
        {
            _connectionTestStatus.ForeColor = TrayUiTheme.Danger;
            _connectionTestStatus.Text = "Connection test could not be completed.";
            _status.ForeColor = TrayUiTheme.Danger;
            _status.Text = SavedPrefix(settingsSaved) + exception.Message;
            WriteManualLog("Error", _status.Text);
        }
        catch (Exception exception) when (exception is ArgumentException
            or FormatException
            or IOException
            or UnauthorizedAccessException)
        {
            _connectionTestStatus.ForeColor = TrayUiTheme.Danger;
            _connectionTestStatus.Text = "Connection test could not be started.";
            _status.ForeColor = TrayUiTheme.Danger;
            _status.Text = SavedPrefix(settingsSaved) + exception.Message;
            WriteManualLog("Error", _status.Text);
        }
        catch
        {
            _connectionTestStatus.ForeColor = TrayUiTheme.Danger;
            _connectionTestStatus.Text = "Connection test could not be completed.";
            _status.ForeColor = TrayUiTheme.Danger;
            _status.Text = SavedPrefix(settingsSaved) + "Worker communication failed; check Worker status before retrying.";
            WriteManualLog("Error", _status.Text);
        }
        finally
        {
            SetActionButtonsEnabled(true);
        }
    }

    /// <summary>
    /// Saves current settings and asks the Worker to send one real Email and Webhook test without placing channel settings in IPC.
    /// </summary>
    private async Task TestNotificationsAsync()
    {
        if (!_testNotifications.Enabled)
        {
            return;
        }

        var settingsSaved = false;
        SetActionButtonsEnabled(false);
        _status.ForeColor = TrayUiTheme.MutedText;
        _status.Text = "Saving current settings and starting notification tests...";
        _notificationTestStatus.ForeColor = TrayUiTheme.MutedText;
        _notificationTestStatus.Text = "Testing Email and Webhook through the Worker...";
        WriteManualLog("Command", "Saving current configuration before notification tests.");

        try
        {
            var settings = BuildSettings();
            await _settingsStore.SaveSyncAppSettingsAsync(settings, CancellationToken.None).ConfigureAwait(true);
            settingsSaved = true;

            var reloadEvent = await _ipcClient
                .ExecuteAsync(WorkerIpcCommands.ReloadSchedule, CancellationToken.None)
                .ConfigureAwait(true);
            if (reloadEvent.ScheduleReload is not { Applied: true })
            {
                throw new WorkerCommandException("Worker did not apply the saved configuration for testing.");
            }

            WriteManualLog("Command", $"Starting {WorkerIpcCommands.TestNotifications}.");
            var operation = _ipcClient.Start(
                WorkerIpcCommands.TestNotifications,
                progress: null,
                CancellationToken.None);
            var terminal = await operation.Completion.ConfigureAwait(true);
            var result = terminal.NotificationTest
                ?? throw new WorkerProtocolException("Worker did not return notification-test results.");
            var anyConfigured = result.Email.Configured || result.Webhook.Configured;
            var allConfiguredSucceeded = (!result.Email.Configured || result.Email.Succeeded)
                && (!result.Webhook.Configured || result.Webhook.Succeeded);
            var succeeded = anyConfigured && allConfiguredSucceeded;

            _notificationTestStatus.ForeColor = !anyConfigured
                ? TrayUiTheme.MutedText
                : succeeded ? TrayUiTheme.Success : TrayUiTheme.Danger;
            _notificationTestStatus.Text =
                $"Email: {NotificationOutcome(result.Email.Configured, result.Email.Succeeded)}  •  "
                + $"Webhook: {NotificationOutcome(result.Webhook.Configured, result.Webhook.Succeeded)}";
            _status.ForeColor = succeeded ? TrayUiTheme.Success : TrayUiTheme.Danger;
            _status.Text = !anyConfigured
                ? "Settings saved. No notification channel is fully configured."
                : succeeded
                    ? "Settings saved. All configured endpoints accepted the tests; downstream delivery is not confirmed."
                    : "Settings saved. One or more notification tests failed; see Worker service logs.";
            WriteManualLog("Notification", $"Email={NotificationOutcome(result.Email.Configured, result.Email.Succeeded)}.");
            WriteManualLog("Notification", $"Webhook={NotificationOutcome(result.Webhook.Configured, result.Webhook.Succeeded)}.");
        }
        catch (WorkerBusyException exception)
        {
            SetNotificationTestFailure(
                $"{SavedPrefix(settingsSaved)}Active operation: {TrayStatusFormatter.FormatOperation(exception.ActiveOperation)}");
        }
        catch (Exception exception) when (exception is WorkerUnavailableException
            or WorkerProtocolException
            or WorkerCommandException
            or ArgumentException
            or FormatException
            or IOException
            or UnauthorizedAccessException)
        {
            SetNotificationTestFailure(SavedPrefix(settingsSaved) + exception.Message);
        }
        catch
        {
            SetNotificationTestFailure(
                SavedPrefix(settingsSaved) + "Worker communication failed; check Worker status before retrying.");
        }
        finally
        {
            SetActionButtonsEnabled(true);
        }
    }

    private void SetNotificationTestFailure(string message)
    {
        _notificationTestStatus.ForeColor = TrayUiTheme.Danger;
        _notificationTestStatus.Text = "Notification tests could not be completed.";
        _status.ForeColor = TrayUiTheme.Danger;
        _status.Text = message;
        WriteManualLog("Error", message);
    }

    private SyncAppSettings BuildSettings()
    {
        var ateraBase = ApiEndpointValidator.ValidateAteraBaseUri(Value("AteraBaseUrl")).AbsoluteUri;
        var snipeBase = ApiEndpointValidator.ValidateSnipeBaseUri(Value("SnipeItBaseUrl")).AbsoluteUri;
        var schedule = new SyncScheduleOptions
        {
            Enabled = _scheduleEnabled.Checked,
            Frequency = Enum.Parse<ScheduleFrequency>(_frequency.Text, ignoreCase: true),
            TimeZoneId = Value("TimeZone"),
            RunTimes = Split("RunTimes").Select(value => TimeOnly.ParseExact(value, "HH:mm")).ToArray(),
            DaysOfWeek = Split("WeekDays").Select(value => Enum.Parse<DayOfWeek>(value, true)).ToArray(),
            DaysOfMonth = Split("MonthDays").Select(int.Parse).ToArray(),
            RunOnLastDayOfMonth = _lastDay.Checked,
            PreventOverlappingRuns = true
        };
        ScheduleCalculator.Validate(schedule);

        var statusId = int.Parse(Value("DefaultStatusId"));
        var threshold = double.Parse(Value("NameThreshold"), System.Globalization.CultureInfo.InvariantCulture);
        if (statusId <= 0 || threshold is <= 0 or > 1)
        {
            throw new ArgumentException("Status ID must be positive and name match threshold must be in (0, 1].");
        }

        var macColumn = Optional("MacColumn");
        var macFieldset = Optional("MacFieldset");
        if (string.IsNullOrWhiteSpace(macColumn) != string.IsNullOrWhiteSpace(macFieldset))
        {
            throw new ArgumentException("MAC DB column and MAC fieldset must be configured together.");
        }

        var smtpPort = int.Parse(Value("SmtpPort"));
        if (smtpPort is < 1 or > 65535)
        {
            throw new ArgumentException("SMTP port must be between 1 and 65535.");
        }

        var smtpUsername = Optional("SmtpUsername");
        var smtpPassword = Optional("SmtpPassword");
        if (string.IsNullOrWhiteSpace(smtpUsername) != string.IsNullOrWhiteSpace(smtpPassword))
        {
            throw new ArgumentException("SMTP username and password must be configured together.");
        }

        var smtpHost = Optional("SmtpHost");
        var emailFrom = Optional("EmailFrom");
        var emailTo = Optional("EmailTo");
        var emailConfigured = smtpHost is not null
            || smtpUsername is not null
            || smtpPassword is not null
            || emailFrom is not null
            || emailTo is not null;
        if (emailConfigured && (smtpHost is null || emailFrom is null || emailTo is null))
        {
            throw new ArgumentException("SMTP host, Email from, and Email to are all required when email is configured.");
        }

        var webhookUrl = Optional("WebhookUrl");
        if (webhookUrl is not null
            && (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri)
                || webhookUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Webhook URL must be an absolute HTTPS URL.");
        }

        return new SyncAppSettings
        {
            AteraBaseUrl = ateraBase,
            AteraApiKey = Value("AteraApiKey"),
            SnipeItBaseUrl = snipeBase,
            SnipeItApiToken = Value("SnipeItApiToken"),
            DefaultCompanyName = SyncApplicationDefaults.CompanyName,
            CompanyAliases = ParseAliases(_text["CompanyAliases"].Text),
            DefaultManufacturerName = SyncApplicationDefaults.ManufacturerName,
            ManufacturerAliases = ParseAliases(_text["ManufacturerAliases"].Text),
            DefaultModelName = SyncApplicationDefaults.ModelName,
            DefaultCategoryName = Value("DefaultCategory"),
            ModelCategoriesToNormalize = Split("NormalizeCategories"),
            IgnoredDeviceTypes = Split("IgnoredDeviceTypes"),
            DefaultStatusId = statusId,
            MacAddressCustomFieldDbColumnName = macColumn,
            MacAddressFieldsetName = macFieldset,
            IgnoredMacAddresses = Split("IgnoredMacs"),
            NameMatchThreshold = threshold,
            CreateMissingCompanies = _createCompanies.Checked,
            CreateMissingModels = _createModels.Checked,
            Schedule = schedule,
            Notifications = new NotificationConfig
            {
                Enabled = _notificationsEnabled.Checked,
                OnEvents = ReadSelectedNotificationEvents(),
                SmtpHost = smtpHost,
                SmtpPort = smtpPort,
                SmtpUseSsl = _smtpUseSsl.Checked,
                SmtpUsername = smtpUsername,
                SmtpPassword = smtpPassword,
                EmailFrom = emailFrom,
                EmailTo = emailTo,
                WebhookPayloadFormat = ParseWebhookPayloadFormat(_webhookPayloadFormat.Text),
                WebhookUrl = webhookUrl
            }
        };
    }

    private void Apply(SyncAppSettings? settings)
    {
        ResetToDefaults();
        if (settings is null)
        {
            return;
        }

        Set("AteraBaseUrl", settings.AteraBaseUrl);
        Set("AteraApiKey", settings.AteraApiKey);
        Set("SnipeItBaseUrl", settings.SnipeItBaseUrl);
        Set("SnipeItApiToken", settings.SnipeItApiToken);
        Set("CompanyAliases", FormatAliases(settings.CompanyAliases));
        Set("ManufacturerAliases", FormatAliases(settings.ManufacturerAliases));
        Set("DefaultCategory", settings.DefaultCategoryName);
        Set("NormalizeCategories", string.Join("; ", settings.ModelCategoriesToNormalize));
        Set("IgnoredDeviceTypes", string.Join("; ", settings.IgnoredDeviceTypes));
        Set("DefaultStatusId", settings.DefaultStatusId?.ToString());
        Set("MacColumn", settings.MacAddressCustomFieldDbColumnName);
        Set("MacFieldset", settings.MacAddressFieldsetName);
        Set("IgnoredMacs", string.Join("; ", settings.IgnoredMacAddresses));
        Set("NameThreshold", settings.NameMatchThreshold?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _createCompanies.Checked = settings.CreateMissingCompanies ?? false;
        _createModels.Checked = settings.CreateMissingModels ?? false;

        if (settings.Schedule is { } schedule)
        {
            _scheduleEnabled.Checked = schedule.Enabled;
            _frequency.SelectedValue = schedule.Frequency.ToString();
            Set("TimeZone", schedule.TimeZoneId);
            Set("RunTimes", string.Join("; ", schedule.RunTimes.Select(value => value.ToString("HH:mm"))));
            Set("WeekDays", string.Join("; ", schedule.DaysOfWeek));
            Set("MonthDays", string.Join("; ", schedule.DaysOfMonth));
            _lastDay.Checked = schedule.RunOnLastDayOfMonth;
        }

        _notificationsEnabled.Checked = settings.Notifications.Enabled;
        ApplyNotificationEvents(settings.Notifications.OnEvents);
        Set("SmtpHost", settings.Notifications.SmtpHost);
        Set("SmtpPort", settings.Notifications.SmtpPort.ToString());
        _smtpUseSsl.Checked = settings.Notifications.SmtpUseSsl;
        Set("SmtpUsername", settings.Notifications.SmtpUsername);
        Set("SmtpPassword", settings.Notifications.SmtpPassword);
        Set("EmailFrom", settings.Notifications.EmailFrom);
        Set("EmailTo", settings.Notifications.EmailTo);
        _webhookPayloadFormat.SelectedValue = FormatWebhookPayloadFormat(settings.Notifications.WebhookPayloadFormat);
        Set("WebhookUrl", settings.Notifications.WebhookUrl);
    }

    /// <summary>
    /// Clears every reused input to its declared default before local settings are applied, preventing stale values across page visits.
    /// </summary>
    private void ResetToDefaults()
    {
        foreach (var input in _text.Values)
        {
            input.Text = input.PlaceholderText ?? string.Empty;
        }

        _createCompanies.Checked = true;
        _createModels.Checked = false;
        _scheduleEnabled.Checked = false;
        _lastDay.Checked = false;
        _notificationsEnabled.Checked = false;
        ApplyNotificationEvents([]);
        _smtpUseSsl.Checked = true;
        _frequency.SelectedValue = ScheduleFrequency.Daily.ToString();
        _webhookPayloadFormat.SelectedValue = TeamsWebhookFormatText;
        _connectionTestStatus.ForeColor = TrayUiTheme.MutedText;
        _connectionTestStatus.Text = "Current form settings will be saved before the Worker tests both connections.";
        _notificationTestStatus.ForeColor = TrayUiTheme.MutedText;
        _notificationTestStatus.Text = "Sends one test through each completely configured notification channel.";
    }

    private string Value(string key)
    {
        var value = _text[key].Text.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{key} is required.")
            : value;
    }

    private string? Optional(string key)
        => string.IsNullOrWhiteSpace(_text[key].Text) ? null : _text[key].Text.Trim();

    private IReadOnlyList<string> Split(string key)
        => _text[key].Text.Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Expands the selected completed/failed outcome groups into the canonical event names consumed by the Worker filter.
    /// </summary>
    internal IReadOnlyList<string> ReadSelectedNotificationEvents()
    {
        var selected = new List<string>(CompletedNotificationEventTypes.Length + FailedNotificationEventTypes.Length);
        if (_notifySyncCompleted.Checked)
        {
            selected.AddRange(CompletedNotificationEventTypes);
        }

        if (_notifySyncFailed.Checked)
        {
            selected.AddRange(FailedNotificationEventTypes);
        }

        return selected;
    }

    /// <summary>
    /// Groups known persisted event names by outcome so any legacy event in a group enables its single UI switch.
    /// </summary>
    internal void ApplyNotificationEvents(IEnumerable<string> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        var selected = new HashSet<string>(
            eventTypes
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()),
            StringComparer.OrdinalIgnoreCase);

        _notifySyncCompleted.Checked = CompletedNotificationEventTypes.Any(selected.Contains);
        _notifySyncFailed.Checked = FailedNotificationEventTypes.Any(selected.Contains);
    }

    private static IReadOnlyDictionary<string, string> ParseAliases(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("=>", StringComparison.Ordinal) || line.Count(value => value == '=') != 1)
            {
                throw new ArgumentException("Each alias line must contain exactly one '=' and cannot use '=>'.");
            }

            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Alias keys and values cannot be blank.");
            }

            result.Add(parts[0], parts[1]);
        }

        return result;
    }

    private static string FormatAliases(IReadOnlyDictionary<string, string> values)
        => string.Join(Environment.NewLine, values.Select(value => $"{value.Key}={value.Value}"));

    private void Set(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _text[key].Text = value;
        }
    }

    private static AntTabPage Page(string text) => new()
    {
        AutoScroll = true,
        BackColor = TrayUiTheme.Surface,
        Padding = new Padding(8),
        Text = text
    };

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Surface,
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(20)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private void AddText(
        TableLayoutPanel grid,
        string key,
        string label,
        string defaultValue = "",
        bool password = false,
        bool multiline = false)
    {
        var box = new AntInput
        {
            Dock = DockStyle.Top,
            Height = multiline ? 96 : 44,
            Multiline = multiline,
            Name = key,
            PlaceholderText = defaultValue,
            Radius = TrayUiTheme.ControlRadius,
            Text = defaultValue,
            UseSystemPasswordChar = password
        };
        _text.Add(key, box);
        AddControl(grid, label, box);
    }

    private static void AddControl(TableLayoutPanel grid, string label, Control control)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new AntLabel
        {
            Dock = DockStyle.Fill,
            ForeColor = TrayUiTheme.Text,
            Padding = new Padding(0, 10, 8, 10),
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        control.Margin = new Padding(3, 9, 3, 9);
        grid.Controls.Add(control, 1, row);
    }

    /// <summary>
    /// Creates a compact AntdUI switch; its descriptive label is rendered in the settings grid's first column.
    /// </summary>
    private static AntSwitch Toggle(string name) => new()
    {
        Anchor = AnchorStyles.Left,
        Fill = TrayUiTheme.Primary,
        MinimumSize = new Size(56, 32),
        Name = name,
        Size = new Size(56, 32)
    };

    private void SetActionButtonsEnabled(bool enabled)
    {
        _back.Enabled = enabled;
        _save.Enabled = enabled;
        _testConnections.Enabled = enabled;
        _testNotifications.Enabled = enabled;
    }

    private void WriteManualLog(string category, string message)
    {
        var timestamp = DateTimeOffset.Now;
        var safeMessage = (message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        _manualLogWriter.TryWrite(
            timestamp,
            $"{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{category}] {safeMessage}{Environment.NewLine}");
    }

    private static string Outcome(bool succeeded) => succeeded ? "Success" : "Failed";

    private static string NotificationOutcome(bool configured, bool succeeded)
        => !configured ? "Not configured" : succeeded ? "Accepted" : "Failed";

    /// <summary>
    /// Maps the bounded UI label to the wire format saved for the Worker webhook sender.
    /// </summary>
    private static WebhookPayloadFormat ParseWebhookPayloadFormat(string value) => value switch
    {
        TeamsWebhookFormatText => WebhookPayloadFormat.TeamsAdaptiveCard,
        GenericWebhookFormatText => WebhookPayloadFormat.GenericJson,
        _ => throw new ArgumentException("Select a supported webhook format.")
    };

    private static string FormatWebhookPayloadFormat(WebhookPayloadFormat value) => value switch
    {
        WebhookPayloadFormat.TeamsAdaptiveCard => TeamsWebhookFormatText,
        WebhookPayloadFormat.GenericJson => GenericWebhookFormatText,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported webhook payload format.")
    };

    private static string SavedPrefix(bool settingsSaved)
        => settingsSaved ? "Settings saved, but " : string.Empty;

}
