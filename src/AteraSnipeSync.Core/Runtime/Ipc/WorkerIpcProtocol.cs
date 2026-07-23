namespace AteraSnipeSync.Core.Runtime.Ipc;

/// <summary>
/// Defines the compatible local pipe name, protocol version, and bounded message size shared by Worker and Tray.
/// </summary>
public static class WorkerIpcProtocol
{
    public const int Version = 1;
    public const string DefaultPipeName = "AteraSnipeSync.Worker.v1";
    public const int MaxMessageCharacters = 32 * 1024 * 1024;
}
