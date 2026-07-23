using AteraSnipeSync.Core.Configuration;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Routes restricted elevated helper mode before starting the single-instance Windows Tray application.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs one exact helper operation or the normal NotifyIcon message loop; normal exit never stops WorkerService.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (ServiceMaintenanceArguments.HasMaintenanceSwitch(args))
        {
            if (!ServiceMaintenanceArguments.TryParse(args, out var operation))
            {
                return ServiceMaintenanceExitCodes.InvalidArguments;
            }

            return new ElevatedServiceMaintenanceRunner()
                .RunAsync(operation, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        if (!TraySingleInstanceGuard.TryAcquire(out var guard))
        {
            MessageBox.Show(
                "Atera Snipe-IT Auto Sync TrayApp is already running.",
                "Atera Snipe-IT Auto Sync",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        using (guard)
        {
            ApplicationConfiguration.Initialize();
            AntdUI.Config.Mode = AntdUI.TMode.Light;
            AntdUI.Config.Animation = true;
            Application.SetDefaultFont(new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point));
            var settingsStore = new LocalAppSettingsStore(LocalAppSettingsStore.GetDefaultFilePath());
            Application.Run(new TrayApplicationContext(
                new WorkerIpcClient(),
                settingsStore,
                new WorkerServiceStatusReader(),
                new WorkerServiceMaintenanceLauncher()));
        }

        return 0;
    }
}
