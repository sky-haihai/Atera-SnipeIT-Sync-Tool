using System.ComponentModel;
using System.Diagnostics;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Starts the current Tray executable in one restricted UAC helper mode and maps its fixed exit code to a UI result.
/// </summary>
internal sealed class WorkerServiceMaintenanceLauncher
{
    private readonly Func<string> _workerPath;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _runElevated;

    public WorkerServiceMaintenanceLauncher()
        : this(WorkerExecutableLocator.GetExpectedPath, RunElevatedProcessAsync)
    {
    }

    internal WorkerServiceMaintenanceLauncher(
        Func<string> workerPath,
        Func<ProcessStartInfo, CancellationToken, Task<int>> runElevated)
    {
        _workerPath = workerPath;
        _runElevated = runElevated;
    }

    /// <summary>
    /// Validates reinstall prerequisites before UAC, launches asynchronously, and treats UAC cancellation as a normal cancellation.
    /// </summary>
    public async Task<ServiceMaintenanceResult> ExecuteElevatedAsync(
        ServiceMaintenanceOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation == ServiceMaintenanceOperation.ReinstallAndRestart
            && !File.Exists(_workerPath()))
        {
            return Result(false, false, "WorkerExecutableMissing",
                "Worker executable is missing from the Tray application directory.");
        }

        var trayExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        var startInfo = new ProcessStartInfo(trayExecutable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--service-maintenance");
        startInfo.ArgumentList.Add(ServiceMaintenanceArguments.ToArgument(operation));

        try
        {
            return MapExitCode(await _runElevated(startInfo, cancellationToken).ConfigureAwait(false));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return Result(false, true, "Cancelled", "Service maintenance was cancelled.");
        }
    }

    private static async Task<int> RunElevatedProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the elevated service helper.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static ServiceMaintenanceResult MapExitCode(int exitCode)
    {
        return exitCode switch
        {
            ServiceMaintenanceExitCodes.Success => Result(true, false, "Completed", "Service maintenance completed."),
            ServiceMaintenanceExitCodes.WorkerExecutableMissing => Result(false, false, "WorkerExecutableMissing", "Worker executable is missing."),
            ServiceMaintenanceExitCodes.ServiceNotInstalled => Result(false, false, "ServiceNotInstalled", "Service is not installed; run the deployment or installer before restarting it."),
            ServiceMaintenanceExitCodes.StopFailed => Result(false, false, "StopFailed", "Service could not be stopped."),
            ServiceMaintenanceExitCodes.DeleteFailed => Result(false, false, "DeleteFailed", "Service registration could not be removed."),
            ServiceMaintenanceExitCodes.CreateFailed => Result(false, false, "CreateFailed", "Service could not be registered."),
            ServiceMaintenanceExitCodes.StartFailed => Result(false, false, "StartFailed", "Service could not be started."),
            ServiceMaintenanceExitCodes.Timeout => Result(false, false, "Timeout", "Service maintenance timed out."),
            _ => Result(false, false, "Failed", "Service maintenance failed; see the maintenance log.")
        };
    }

    private static ServiceMaintenanceResult Result(
        bool succeeded,
        bool cancelled,
        string stage,
        string message)
    {
        return new ServiceMaintenanceResult
        {
            Succeeded = succeeded,
            Cancelled = cancelled,
            Stage = stage,
            Message = message
        };
    }
}
