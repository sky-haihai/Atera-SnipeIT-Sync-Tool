using System.Diagnostics;
using AteraSnipeSync.Core.Configuration;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Owns the single NotifyIcon and reusable Dashboard lifetime without coupling Tray exit to Worker Service lifetime.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly WorkerIpcClient _ipcClient;
    private readonly LocalAppSettingsStore _settingsStore;
    private readonly IWorkerServiceStatusReader _serviceStatusReader;
    private readonly WorkerServiceMaintenanceLauncher _maintenanceLauncher;
    private readonly DailyLogWriter _manualLogWriter;
    private readonly Icon _trayIcon;
    private readonly NotifyIcon _notifyIcon;
    private TrayDashboardForm? _dashboard;
    private bool _isExiting;

    public TrayApplicationContext(
        WorkerIpcClient ipcClient,
        LocalAppSettingsStore settingsStore,
        IWorkerServiceStatusReader serviceStatusReader,
        WorkerServiceMaintenanceLauncher maintenanceLauncher)
    {
        _ipcClient = ipcClient;
        _settingsStore = settingsStore;
        _serviceStatusReader = serviceStatusReader;
        _maintenanceLauncher = maintenanceLauncher;
        _manualLogWriter = new DailyLogWriter(ControlledPathValidator.LogsRoot);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("View Last Sync Status", null, (_, _) => ShowDashboard());
        menu.Items.Add("Open Log Folder", null, (_, _) => OpenDirectory(ControlledPathValidator.ProgramDataRoot));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Tray App", null, (_, _) => ExitTray());

        _trayIcon = TrayIconLoader.Load();
        _notifyIcon = new NotifyIcon
        {
            Text = "Atera Snipe-IT Auto Sync",
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                ShowDashboard();
            }
        };
        _notifyIcon.MouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                ShowDashboard();
            }
        };
    }

    /// <summary>
    /// Shows and activates the one Dashboard instance, recreating it only after disposal and refreshing current state immediately.
    /// </summary>
    internal void ShowDashboard()
    {
        if (_dashboard is null || _dashboard.IsDisposed)
        {
            _dashboard = new TrayDashboardForm(
                _settingsStore,
                _ipcClient,
                _serviceStatusReader,
                _maintenanceLauncher,
                _manualLogWriter);
        }

        _dashboard.Show();
        if (_dashboard.WindowState == FormWindowState.Minimized)
        {
            _dashboard.WindowState = FormWindowState.Normal;
        }

        _dashboard.Activate();
        _ = _dashboard.RefreshSystemStateAsync();
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
        _manualLogWriter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.ExitThreadCore();
    }

    private void ExitTray()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        if (_dashboard is not null && !_dashboard.IsDisposed)
        {
            _dashboard.PrepareForExit();
            _dashboard.Close();
            _dashboard.Dispose();
        }

        ExitThread();
    }

    private static void OpenDirectory(string path)
    {
        if (!ControlledPathValidator.IsUnderRoot(path, path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
