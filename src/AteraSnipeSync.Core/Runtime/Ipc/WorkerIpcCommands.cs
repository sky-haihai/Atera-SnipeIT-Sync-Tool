namespace AteraSnipeSync.Core.Runtime.Ipc;

/// <summary>
/// Lists the only commands accepted by the version-one Worker IPC server.
/// </summary>
public static class WorkerIpcCommands
{
    public const string Ping = "Ping";
    public const string GetStatus = "GetStatus";
    public const string ReloadSchedule = "ReloadSchedule";
    public const string TestConnections = "TestConnections";
    public const string TestNotifications = "TestNotifications";
    public const string PreviewChanges = "PreviewChanges";
    public const string SyncNow = "SyncNow";
    public const string Cancel = "Cancel";

    public static bool IsKnown(string? command)
    {
        return command is Ping
            or GetStatus
            or ReloadSchedule
            or TestConnections
            or TestNotifications
            or PreviewChanges
            or SyncNow
            or Cancel;
    }

    public static bool IsLongRunning(string command)
    {
        return command is TestConnections or TestNotifications or PreviewChanges or SyncNow;
    }
}
