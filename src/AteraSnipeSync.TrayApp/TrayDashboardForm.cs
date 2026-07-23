using System.Diagnostics;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Runtime.Ipc;
using AntButton = AntdUI.Button;
using AntLabel = AntdUI.Label;
using AntPanel = AntdUI.Panel;
using AntProgress = AntdUI.Progress;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Presents the one reusable operator Dashboard and delegates every run to Worker IPC while keeping SCM and Worker health separate.
/// </summary>
public sealed class TrayDashboardForm : AntdUI.BorderlessForm
{
    private readonly LocalAppSettingsStore _settingsStore;
    private readonly WorkerIpcClient _ipcClient;
    private readonly IWorkerServiceStatusReader _serviceStatusReader;
    private readonly WorkerServiceMaintenanceLauncher _maintenanceLauncher;
    private readonly DailyLogWriter _manualLogWriter;
    private readonly bool _ownsManualLogWriter;
    private readonly Panel _pageHost = new() { BackColor = TrayUiTheme.Canvas, Dock = DockStyle.Fill };
    private readonly SyncConfigurationPage _configurationPage;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };
    private readonly AntLabel _serviceStatus = ValueLabel();
    private readonly AntLabel _workerStatus = ValueLabel();
    private readonly AntLabel _scheduleStatus = ValueLabel();
    private readonly AntLabel _nextRun = ValueLabel();
    private readonly AntLabel _phase = ValueLabel();
    private readonly AntLabel _message = ValueLabel();
    private readonly AntProgress _progress = new() { Dock = DockStyle.Top, Height = 12, Radius = TrayUiTheme.ControlRadius };
    private readonly AntLabel _lastRunTime = SecondaryValueLabel();
    private readonly AntLabel _createdCount = MetricValueLabel();
    private readonly AntLabel _updatedCount = MetricValueLabel();
    private readonly AntLabel _noChangeCount = MetricValueLabel();
    private readonly AntLabel _deletedCount = MetricValueLabel();
    private readonly AntButton _config = Button("Config");
    private readonly AntButton _preview = Button("Preview");
    private readonly AntButton _sync = Button("Sync Now");
    private readonly AntButton _cancel = Button("Cancel");
    private readonly AntButton _restart = Button("Restart Service");
    private readonly AntButton _openLogFolder = Button("Open Log Folder");

    private DashboardState _state = new(
        WorkerWindowsServiceState.Unknown,
        WorkerOnline: false,
        ProtocolCompatible: true,
        WorkerBusy: false,
        ActiveOperation: null,
        DashboardLocalActivity.Idle,
        ActiveCommandCanCancel: false);
    private WorkerStatusSnapshot? _latestWorkerStatus;
    private string? _activeRequestId;
    private LatestSyncHistorySummary? _offlineHistorySummary;
    private long _refreshGeneration;
    private int _refreshInProgress;
    private bool _allowClose;
    private Control? _dashboardPage;

    internal bool IsConfigurationPageActive { get; private set; }

    public TrayDashboardForm()
        : this(
            new LocalAppSettingsStore(LocalAppSettingsStore.GetDefaultFilePath()),
            new WorkerIpcClient(),
            new WorkerServiceStatusReader(),
            new WorkerServiceMaintenanceLauncher(),
            new DailyLogWriter(ControlledPathValidator.LogsRoot),
            ownsManualLogWriter: true)
    {
    }

    internal TrayDashboardForm(
        LocalAppSettingsStore settingsStore,
        WorkerIpcClient ipcClient,
        IWorkerServiceStatusReader serviceStatusReader,
        WorkerServiceMaintenanceLauncher maintenanceLauncher,
        DailyLogWriter manualLogWriter)
        : this(
            settingsStore,
            ipcClient,
            serviceStatusReader,
            maintenanceLauncher,
            manualLogWriter,
            ownsManualLogWriter: false)
    {
    }

    private TrayDashboardForm(
        LocalAppSettingsStore settingsStore,
        WorkerIpcClient ipcClient,
        IWorkerServiceStatusReader serviceStatusReader,
        WorkerServiceMaintenanceLauncher maintenanceLauncher,
        DailyLogWriter manualLogWriter,
        bool ownsManualLogWriter)
    {
        _settingsStore = settingsStore;
        _ipcClient = ipcClient;
        _serviceStatusReader = serviceStatusReader;
        _maintenanceLauncher = maintenanceLauncher;
        _manualLogWriter = manualLogWriter;
        _ownsManualLogWriter = ownsManualLogWriter;
        _configurationPage = new SyncConfigurationPage(_settingsStore, _ipcClient, _manualLogWriter);
        InitializeComponent();
    }

    /// <summary>
    /// Allows TrayApplicationContext to perform the one real form close during explicit Tray exit.
    /// </summary>
    internal void PrepareForExit() => _allowClose = true;

    /// <summary>
    /// Queries SCM and Worker independently; only the newest refresh generation may update controls.
    /// </summary>
    public async Task RefreshSystemStateAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var serviceTask = _serviceStatusReader.GetStatusAsync(cancellationToken);
        WorkerStatusSnapshot? workerStatus = null;
        var workerOnline = false;
        var protocolCompatible = true;
        try
        {
            var response = await _ipcClient
                .ExecuteAsync(WorkerIpcCommands.GetStatus, cancellationToken)
                .ConfigureAwait(true);
            workerStatus = response.WorkerStatus;
            workerOnline = true;
        }
        catch (WorkerProtocolException)
        {
            workerOnline = true;
            protocolCompatible = false;
        }
        catch (WorkerUnavailableException)
        {
            // SCM state remains independently useful while Worker health is offline.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // A refresh failure is represented as Worker Offline and does not terminate TrayApp.
        }

        var offlineSummary = workerOnline
            ? null
            : await LatestSyncHistoryReader
                .ReadSummaryAsync(ControlledPathValidator.HistoryRoot, cancellationToken)
                .ConfigureAwait(true);
        var serviceState = await serviceTask.ConfigureAwait(true);
        if (generation != Volatile.Read(ref _refreshGeneration))
        {
            return;
        }

        _latestWorkerStatus = workerStatus;
        _offlineHistorySummary = offlineSummary;
        _state = _state with
        {
            ServiceState = serviceState,
            WorkerOnline = workerOnline,
            ProtocolCompatible = protocolCompatible,
            WorkerBusy = workerStatus?.IsRunning ?? false,
            ActiveOperation = workerStatus?.ActiveOperation
        };
        RenderSystemState();
        ApplyControlState();
    }

    /// <summary>
    /// Prevents the normal close button from disposing the reusable Dashboard; TrayApplicationContext sets AllowClose during real exit.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            ShowDashboardPage();
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _refreshTimer.Start();
            _ = RefreshSystemStateAsync();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        TrayUiTheme.ApplyWindow(this);
        Text = "Atera Snipe-IT Auto Sync";
        MinimumSize = new Size(920, 650);
        Size = new Size(1040, 700);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        TrayUiTheme.StyleButton(_config);
        _config.AutoSize = false;
        _config.Dock = DockStyle.Fill;
        _config.Ghost = true;
        _config.IconSvg = "SettingOutlined";
        _config.Margin = Padding.Empty;
        _config.MinimumSize = new Size(176, 40);
        _config.Radius = TrayUiTheme.ControlRadius;
        _config.Text = "Configuration";
        var headerActions = new Panel
        {
            BackColor = TrayUiTheme.Surface,
            Dock = DockStyle.Right,
            Padding = new Padding(6, 6, 8, 6),
            Width = 196
        };
        headerActions.Controls.Add(_config);

        var header = new AntdUI.PageHeader
        {
            BackColor = TrayUiTheme.Surface,
            DividerShow = true,
            Dock = DockStyle.Fill,
            MaximizeBox = true,
            MinimizeBox = true,
            ShowButton = true,
            SubText = "Dashboard",
            Text = "Auto Sync",
            UseTextBold = true
        };
        header.Controls.Add(headerActions);
        shell.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Canvas,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 10),
            RowCount = 3
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var statusGrid = new TableLayoutPanel { BackColor = TrayUiTheme.Surface, ColumnCount = 2, Dock = DockStyle.Fill };
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddStatus(statusGrid, "Windows Service", _serviceStatus);
        AddStatus(statusGrid, "Worker IPC", _workerStatus);

        var activity = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Surface,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 3
        };
        activity.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        activity.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        activity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _phase.Dock = DockStyle.Fill;
        _phase.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point);
        _phase.ForeColor = TrayUiTheme.Text;
        _progress.Margin = new Padding(0, 8, 0, 8);
        _message.AutoEllipsis = true;
        _message.Dock = DockStyle.Fill;
        _message.ForeColor = TrayUiTheme.MutedText;
        _message.TextAlign = ContentAlignment.TopLeft;
        activity.Controls.Add(_phase, 0, 0);
        activity.Controls.Add(_progress, 0, 1);
        activity.Controls.Add(_message, 0, 2);

        var leftColumn = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Canvas,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 2
        };
        leftColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        leftColumn.Controls.Add(TrayUiTheme.CreateCard("System status", statusGrid), 0, 0);
        leftColumn.Controls.Add(TrayUiTheme.CreateCard("Current activity", activity), 0, 1);
        body.Controls.Add(leftColumn, 0, 0);

        var scheduleGrid = new TableLayoutPanel { BackColor = TrayUiTheme.Surface, ColumnCount = 2, Dock = DockStyle.Fill };
        scheduleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        scheduleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddStatus(scheduleGrid, "Schedule", _scheduleStatus);
        AddStatus(scheduleGrid, "Next run", _nextRun);

        _sync.IconSvg = "SyncOutlined";
        _preview.IconSvg = "EyeOutlined";
        _cancel.IconSvg = "StopOutlined";
        _restart.IconSvg = "ReloadOutlined";
        _openLogFolder.IconSvg = "FolderOpenOutlined";
        TrayUiTheme.StyleDashboardActionButton(_sync, TrayUiTheme.ButtonKind.Primary);
        TrayUiTheme.StyleDashboardActionButton(_preview);
        TrayUiTheme.StyleDashboardActionButton(_cancel, TrayUiTheme.ButtonKind.Danger);
        TrayUiTheme.StyleDashboardActionButton(_restart);
        TrayUiTheme.StyleButton(_openLogFolder);
        _openLogFolder.AutoSize = false;
        _openLogFolder.MinimumSize = new Size(176, 38);
        _openLogFolder.Size = new Size(176, 38);

        var actionButtons = new[] { _sync, _preview, _cancel, _restart };
        var actions = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Surface,
            ColumnCount = actionButtons.Length,
            Dock = DockStyle.Fill,
            Padding = Padding.Empty,
            RowCount = 1
        };
        for (var column = 0; column < actionButtons.Length; column++)
        {
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            actions.Controls.Add(actionButtons[column], column, 0);
        }

        _config.Click += async (_, _) => await ShowConfigurationPageAsync().ConfigureAwait(true);
        _preview.Click += async (_, _) => await RunSyncCommandAsync(previewOnly: true).ConfigureAwait(true);
        _sync.Click += async (_, _) => await RunSyncCommandAsync(previewOnly: false).ConfigureAwait(true);
        _cancel.Click += async (_, _) => await CancelActiveCommandAsync().ConfigureAwait(true);
        _restart.Click += async (_, _) => await RestartServiceAsync().ConfigureAwait(true);
        _openLogFolder.Click += (_, _) => OpenControlledDirectory(
            ControlledPathValidator.ProgramDataRoot,
            ControlledPathValidator.ProgramDataRoot);
        var actionCard = TrayUiTheme.CreateSurface(actions, new Padding(14, 18, 14, 18));
        body.Controls.Add(actionCard, 0, 1);
        body.SetColumnSpan(actionCard, 2);

        var latest = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Surface,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        latest.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        latest.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        latest.Controls.Add(_lastRunTime, 0, 0);
        latest.Controls.Add(CreateMetricsGrid(), 0, 1);

        var rightColumn = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Canvas,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 2
        };
        rightColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        rightColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        rightColumn.Controls.Add(TrayUiTheme.CreateCard("Schedule", scheduleGrid), 0, 0);
        rightColumn.Controls.Add(TrayUiTheme.CreateCard("Latest run", latest), 0, 1);
        body.Controls.Add(rightColumn, 1, 0);

        var fileBar = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Canvas,
            ColumnCount = 2,
            Dock = DockStyle.Fill
        };
        fileBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileBar.Controls.Add(new AntLabel
        {
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            ForeColor = TrayUiTheme.MutedText,
            Text = "Detailed activity is available in the log folder."
        }, 0, 0);
        var fileActions = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = TrayUiTheme.Canvas,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        fileActions.Controls.Add(_openLogFolder);
        fileBar.Controls.Add(fileActions, 1, 0);
        body.Controls.Add(fileBar, 0, 2);
        body.SetColumnSpan(fileBar, 2);

        shell.Controls.Add(body, 0, 1);
        _dashboardPage = shell;
        _configurationPage.Visible = false;
        _configurationPage.DashboardRequested += (_, _) => ShowDashboardPage();
        _configurationPage.SettingsSaved += async (_, _) =>
        {
            ShowDashboardPage();
            await ReloadScheduleAsync().ConfigureAwait(true);
        };
        _pageHost.Controls.Add(shell);
        _pageHost.Controls.Add(_configurationPage);
        Controls.Add(_pageHost);

        _refreshTimer.Tick += async (_, _) => await RefreshFromTimerAsync().ConfigureAwait(true);
        _phase.Text = "Idle";
        _message.Text = "Ready for a preview or sync. Connection testing is available in Configuration.";
        SetLatestRun("Run a preview or sync to populate this summary.");
        ApplyControlState();
        ResumeLayout(performLayout: true);
    }

    /// <summary>
    /// Replaces Dashboard content with the embedded configuration page and reloads local values for this visit.
    /// </summary>
    internal async Task ShowConfigurationPageAsync()
    {
        if (!DashboardStateReducer.Reduce(_state).ConfigEnabled)
        {
            return;
        }

        IsConfigurationPageActive = true;
        Text = "Atera Snipe-IT Auto Sync — Configuration";
        _dashboardPage!.Visible = false;
        _configurationPage.Visible = true;
        _configurationPage.BringToFront();
        await _configurationPage.ActivateAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Restores the original Dashboard content without relying on a child-window close or DialogResult path.
    /// </summary>
    internal void ShowDashboardPage()
    {
        IsConfigurationPageActive = false;
        Text = "Atera Snipe-IT Auto Sync";
        _configurationPage.Visible = false;
        if (_dashboardPage is not null)
        {
            _dashboardPage.Visible = true;
            _dashboardPage.BringToFront();
        }

        ApplyControlState();
    }

    private async Task ReloadScheduleAsync()
    {
        _state = _state with { LocalActivity = DashboardLocalActivity.ReloadingSchedule };
        _message.Text = "Configuration saved. Reloading Worker schedule...";
        ApplyControlState();
        try
        {
            var response = await _ipcClient
                .ExecuteAsync(WorkerIpcCommands.ReloadSchedule, CancellationToken.None)
                .ConfigureAwait(true);
            var reload = response.ScheduleReload!;
            _message.Text = reload.Applied
                ? $"Schedule applied. Next run: {TrayStatusFormatter.FormatNextRun(reload.Snapshot.NextRunUtc, TimeZoneInfo.Local)}"
                : "Configuration saved, but Worker did not apply the schedule.";
        }
        catch (Exception exception) when (exception is WorkerUnavailableException
            or WorkerProtocolException
            or WorkerCommandException)
        {
            _message.Text = "Configuration saved, but Worker has not reloaded it: " + exception.Message;
        }
        catch
        {
            _message.Text = "Configuration saved, but Worker communication failed before reload was confirmed.";
        }
        finally
        {
            _state = _state with { LocalActivity = DashboardLocalActivity.Idle };
            ApplyControlState();
            await RefreshSystemStateAsync().ConfigureAwait(true);
        }
    }

    private async Task RunSyncCommandAsync(bool previewOnly)
    {
        if (!previewOnly
            && MessageBox.Show(
                this,
                "Sync Now may create or update Snipe-IT records. Continue?",
                "Confirm Sync Now",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        await RunTrayCommandAsync(
            previewOnly ? WorkerIpcCommands.PreviewChanges : WorkerIpcCommands.SyncNow,
            previewOnly).ConfigureAwait(true);
    }

    private async Task RunTrayCommandAsync(string command, bool previewOnly)
    {
        if (!DashboardStateReducer.Reduce(_state).RunButtonsEnabled)
        {
            return;
        }

        var progressCalculator = new SyncProgressCalculator(previewOnly);
        var stageTracker = new SyncUiStageTracker();
        _phase.Text = stageTracker.Start();
        _progress.Value = 0;
        _message.Text = "Worker command is starting. Detailed activity is being written to the daily log.";
        WriteManualLog("Command", $"Starting {command}.");
        var progress = new Progress<SyncProgressUpdate>(update =>
        {
            _progress.Value = progressCalculator.Calculate(update) / 100F;
            WriteManualLog("Progress", $"{update.Stage}: {update.Message}");
            var milestone = stageTracker.Observe(update);
            if (milestone is not null)
            {
                _phase.Text = milestone;
                _message.Text = "Sync is in progress. Open Log Folder for per-record details.";
            }
        });

        _state = _state with
        {
            LocalActivity = DashboardLocalActivity.RunningTrayCommand,
            ActiveCommandCanCancel = true
        };
        ApplyControlState();
        var operation = _ipcClient.Start(command, progress, CancellationToken.None);
        _activeRequestId = operation.RequestId;
        ApplyControlState();

        try
        {
            var terminal = await operation.Completion.ConfigureAwait(true);
            if (terminal.EventType == WorkerIpcEventTypes.Cancelled)
            {
                _phase.Text = "Sync canceled.";
                _message.Text = terminal.Message ?? "Worker operation was cancelled.";
                WriteManualLog("Cancelled", _message.Text);
            }
            else if (terminal.SyncResult is { } syncResult)
            {
                _progress.Value = 1F;
                if (syncResult.Cancelled)
                {
                    _phase.Text = "Sync canceled.";
                }
                else if (!syncResult.Success)
                {
                    _phase.Text = "Sync failed.";
                }
                else
                {
                    foreach (var milestone in stageTracker.Complete())
                    {
                        _phase.Text = milestone;
                    }
                }

                RenderSyncResult(syncResult);
            }
        }
        catch (WorkerBusyException exception)
        {
            _phase.Text = "Worker busy.";
            _message.Text = $"Active operation: {TrayStatusFormatter.FormatOperation(exception.ActiveOperation)}";
            WriteManualLog("Busy", _message.Text);
        }
        catch (Exception exception) when (exception is WorkerUnavailableException
            or WorkerProtocolException
            or WorkerCommandException)
        {
            _phase.Text = "Sync failed.";
            _message.Text = exception.Message;
            WriteManualLog("Error", exception.Message);
        }
        catch
        {
            _phase.Text = "Sync failed.";
            _message.Text = "Worker communication failed; check Worker status before retrying.";
            WriteManualLog("Error", _message.Text);
        }
        finally
        {
            _activeRequestId = null;
            _state = _state with
            {
                LocalActivity = DashboardLocalActivity.Idle,
                ActiveCommandCanCancel = false
            };
            ApplyControlState();
            await RefreshSystemStateAsync().ConfigureAwait(true);
        }
    }

    private async Task CancelActiveCommandAsync()
    {
        var requestId = _activeRequestId;
        if (requestId is null || !DashboardStateReducer.Reduce(_state).CancelEnabled)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            _message.Text = await _ipcClient.CancelAsync(requestId, timeout.Token).ConfigureAwait(true)
                ? "Cancellation requested; waiting for Worker terminal status."
                : "Could not confirm cancellation.";
        }
        catch
        {
            _message.Text = "Could not confirm cancellation; Worker may still be running.";
        }
    }

    private async Task RestartServiceAsync()
    {
        var controls = DashboardStateReducer.Reduce(_state);
        if (!controls.RestartEnabled)
        {
            return;
        }

        _state = _state with { LocalActivity = DashboardLocalActivity.ServiceMaintenance };
        _message.Text = "Waiting for elevated service maintenance...";
        ApplyControlState();
        try
        {
            var result = await _maintenanceLauncher
                .ExecuteElevatedAsync(ServiceMaintenanceOperation.Restart, CancellationToken.None)
                .ConfigureAwait(true);
            _message.Text = result.Message;
            if (result.Succeeded)
            {
                await WaitForWorkerHealthAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _message.Text = "Service maintenance failed: " + exception.Message;
        }
        finally
        {
            _state = _state with { LocalActivity = DashboardLocalActivity.Idle };
            ApplyControlState();
            await RefreshSystemStateAsync().ConfigureAwait(true);
        }
    }

    private async Task WaitForWorkerHealthAsync()
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                await _ipcClient.ExecuteAsync(WorkerIpcCommands.Ping, CancellationToken.None).ConfigureAwait(true);
                _message.Text = "Service restarted and Worker health is online.";
                return;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            }
        }

        _message.Text = "Service started, but Worker health is not available yet.";
    }

    private async Task RefreshFromTimerAsync()
    {
        if (Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            await RefreshSystemStateAsync().ConfigureAwait(true);
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    private void RenderSystemState()
    {
        _serviceStatus.Text = _state.ServiceState.ToString();
        _workerStatus.Text = !_state.ProtocolCompatible
            ? "Protocol mismatch"
            : TrayStatusFormatter.FormatWorkerStatus(_latestWorkerStatus, _state.WorkerOnline);
        _scheduleStatus.Text = _latestWorkerStatus switch
        {
            null => "Unknown",
            { ScheduleConfigurationValid: false } => "Invalid — " + _latestWorkerStatus.ScheduleError,
            { ScheduleEnabled: false } => "Disabled",
            _ => "Enabled"
        };
        _nextRun.Text = TrayStatusFormatter.FormatNextRun(_latestWorkerStatus?.NextRunUtc, TimeZoneInfo.Local);
        ApplyStatusColors();

        if (_latestWorkerStatus?.LatestSync is { } latest)
        {
            SetLatestRun(
                latest.LastRunFinishedAt is null
                    ? "Finish time unavailable"
                    : $"Finished {latest.LastRunFinishedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                latest.Created,
                latest.Updated,
                latest.Skipped,
                latest.Deleted);
        }
        else if (_offlineHistorySummary is not null)
        {
            SetLatestRun(
                _offlineHistorySummary.FinishedAtUtc is null
                    ? "Loaded from local history while Worker is offline"
                    : $"Finished {_offlineHistorySummary.FinishedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} · local history",
                _offlineHistorySummary.Created,
                _offlineHistorySummary.Updated,
                _offlineHistorySummary.NoChange,
                _offlineHistorySummary.Deleted);
        }
    }

    private void RenderSyncResult(WorkerSyncResultSummary result)
    {
        var outcome = result.Cancelled
            ? "Cancelled"
            : result.Success
                ? "Completed"
                : "Failed";
        SetLatestRun(
            $"Finished {result.FinishedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            result.Created,
            result.Updated,
            result.Skipped,
            result.Deleted);
        _message.Text = $"{outcome}. {result.WarningCount} warning(s); details are available in Logs.";

        WriteManualLog(
            "Result",
            $"Outcome={outcome}; Pulled={result.Pulled}; Mapped={result.Mapped}; Created={result.Created}; "
            + $"Updated={result.Updated}; NoChange={result.Skipped}; Deleted={result.Deleted}; "
            + $"Failed={result.Failed}; Warnings={result.WarningCount}.");
        foreach (var failure in result.Failures)
        {
            WriteManualLog("Failure", $"{failure.Stage}: {failure.Message}");
        }
    }

    private void ApplyStatusColors()
    {
        _serviceStatus.ForeColor = _state.ServiceState == WorkerWindowsServiceState.Running
            ? TrayUiTheme.Success
            : _state.ServiceState is WorkerWindowsServiceState.Stopped or WorkerWindowsServiceState.NotInstalled
                ? TrayUiTheme.Danger
                : TrayUiTheme.Warning;
        _workerStatus.ForeColor = _state.WorkerOnline && _state.ProtocolCompatible
            ? TrayUiTheme.Success
            : TrayUiTheme.Danger;
        _scheduleStatus.ForeColor = _latestWorkerStatus is { ScheduleConfigurationValid: true, ScheduleEnabled: true }
            ? TrayUiTheme.Success
            : _latestWorkerStatus is { ScheduleConfigurationValid: false }
                ? TrayUiTheme.Danger
                : TrayUiTheme.MutedText;
    }

    private TableLayoutPanel CreateMetricsGrid()
    {
        var grid = new TableLayoutPanel
        {
            BackColor = TrayUiTheme.Surface,
            ColumnCount = 4,
            Dock = DockStyle.Fill,
            RowCount = 1,
            Margin = new Padding(0, 8, 0, 0)
        };
        for (var column = 0; column < 4; column++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(CreateMetric("Created", _createdCount), 0, 0);
        grid.Controls.Add(CreateMetric("Updated", _updatedCount), 1, 0);
        grid.Controls.Add(CreateMetric("No change", _noChangeCount), 2, 0);
        grid.Controls.Add(CreateMetric("Deleted", _deletedCount), 3, 0);
        return grid;
    }

    private static AntPanel CreateMetric(string caption, AntLabel value)
    {
        var panel = new AntPanel
        {
            Back = Color.FromArgb(247, 249, 252),
            BorderColor = TrayUiTheme.Border,
            BorderWidth = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(2),
            Padding = new Padding(8, 5, 8, 5),
            Radius = TrayUiTheme.NestedSurfaceRadius
        };
        var layout = new TableLayoutPanel
        {
            BackColor = Color.FromArgb(247, 249, 252),
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        layout.Controls.Add(new AntLabel
        {
            Dock = DockStyle.Fill,
            ForeColor = TrayUiTheme.MutedText,
            Text = caption,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        value.Dock = DockStyle.Fill;
        layout.Controls.Add(value, 0, 1);
        panel.Controls.Add(layout);
        return panel;
    }

    private void SetLatestRun(
        string timestamp,
        int? created = null,
        int? updated = null,
        int? noChange = null,
        int? deleted = null)
    {
        _lastRunTime.Text = timestamp;
        _createdCount.Text = MetricText(created);
        _updatedCount.Text = MetricText(updated);
        _noChangeCount.Text = MetricText(noChange);
        _deletedCount.Text = MetricText(deleted);
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

    private void ApplyControlState()
    {
        var controls = DashboardStateReducer.Reduce(_state);
        _config.Enabled = controls.ConfigEnabled;
        _preview.Enabled = controls.RunButtonsEnabled;
        _sync.Enabled = controls.RunButtonsEnabled;
        _restart.Enabled = controls.RestartEnabled;
        _cancel.Enabled = controls.CancelEnabled;
    }

    private static void OpenControlledDirectory(string? path, string root)
    {
        var target = string.IsNullOrWhiteSpace(path) ? root : path;
        if (!ControlledPathValidator.IsUnderRoot(target, root))
        {
            return;
        }

        Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static void AddStatus(TableLayoutPanel grid, string caption, Control value)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
        grid.Controls.Add(new AntLabel
        {
            Dock = DockStyle.Fill,
            ForeColor = TrayUiTheme.MutedText,
            Padding = new Padding(0, 4, 8, 4),
            Text = caption,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        grid.Controls.Add(value, 1, row);
    }

    private static string MetricText(int? value) => value?.ToString("N0") ?? "—";

    private static AntButton Button(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(100, 32) };

    private static AntLabel ValueLabel() => new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        Padding = new Padding(0, 4, 0, 4),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static AntLabel SecondaryValueLabel() => new()
    {
        Dock = DockStyle.Top,
        ForeColor = TrayUiTheme.MutedText,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static AntLabel MetricValueLabel() => new()
    {
        Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point),
        ForeColor = TrayUiTheme.Text,
        Text = "—",
        TextAlign = ContentAlignment.TopLeft
    };

    /// <summary>
    /// Disposes the timer and flushes the private daily log writer used only by the standalone constructor.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            if (_ownsManualLogWriter)
            {
                _manualLogWriter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }
}
