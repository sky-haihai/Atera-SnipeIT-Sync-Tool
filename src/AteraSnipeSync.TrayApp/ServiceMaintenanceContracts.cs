namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Lists the only elevated Worker maintenance operations accepted from the normal Tray process.
/// </summary>
internal enum ServiceMaintenanceOperation
{
    Restart,
    ReinstallAndRestart
}

/// <summary>
/// Carries the sanitized parent-facing outcome of one UAC helper execution.
/// </summary>
internal sealed class ServiceMaintenanceResult
{
    public required bool Succeeded { get; init; }
    public required bool Cancelled { get; init; }
    public required string Stage { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Defines stable helper process exit codes so no service identity or path is passed back through arguments.
/// </summary>
internal static class ServiceMaintenanceExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 10;
    public const int NotElevated = 11;
    public const int WorkerExecutableMissing = 12;
    public const int ServiceNotInstalled = 13;
    public const int StopFailed = 14;
    public const int DeleteFailed = 15;
    public const int CreateFailed = 16;
    public const int StartFailed = 17;
    public const int Timeout = 18;
    public const int UnexpectedFailure = 19;
}

/// <summary>
/// Parses the two exact helper argument sequences before the normal Tray single-instance guard.
/// </summary>
internal static class ServiceMaintenanceArguments
{
    public static bool HasMaintenanceSwitch(IReadOnlyList<string> args)
        => args.Count > 0
            && string.Equals(args[0], "--service-maintenance", StringComparison.Ordinal);

    public static bool TryParse(IReadOnlyList<string> args, out ServiceMaintenanceOperation operation)
    {
        operation = default;
        if (args.Count != 2
            || !string.Equals(args[0], "--service-maintenance", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(args[1], "restart", StringComparison.Ordinal))
        {
            operation = ServiceMaintenanceOperation.Restart;
            return true;
        }

        if (string.Equals(args[1], "reinstall-and-restart", StringComparison.Ordinal))
        {
            operation = ServiceMaintenanceOperation.ReinstallAndRestart;
            return true;
        }

        return false;
    }

    public static string ToArgument(ServiceMaintenanceOperation operation)
    {
        return operation switch
        {
            ServiceMaintenanceOperation.Restart => "restart",
            ServiceMaintenanceOperation.ReinstallAndRestart => "reinstall-and-restart",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }
}
