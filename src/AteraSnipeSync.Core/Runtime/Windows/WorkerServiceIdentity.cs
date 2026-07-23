namespace AteraSnipeSync.Core.Runtime.Windows;

/// <summary>
/// Defines the immutable Windows Service identity shared by the Worker host and the Tray service controller.
/// </summary>
public static class WorkerServiceIdentity
{
    public const string ServiceName = "AteraSnipeItAutoSync";
    public const string DisplayName = "Atera Snipe-IT Auto Sync";
    public const string ExecutableFileName = "AteraSnipeSync.WorkerService.exe";
}
