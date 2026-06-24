namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Starts the Windows Forms manual sync test application.
/// </summary>
static class Program
{
    /// <summary>
    /// The main entry point for manual local sync validation without requiring WorkerService.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new ManualSyncForm());
    }    
}
