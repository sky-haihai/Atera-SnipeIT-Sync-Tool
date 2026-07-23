using System.Security.Principal;
using AteraSnipeSync.Core.Runtime.Windows;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Performs the fixed elevated restart/re-registration sequences and stops immediately at the first failed stage.
/// </summary>
internal sealed class ElevatedServiceMaintenanceRunner
{
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(30);
    private readonly IServiceCommandRunner _commands;
    private readonly IWorkerServiceStatusReader _statusReader;
    private readonly Func<bool> _isAdministrator;
    private readonly Func<string> _workerPath;
    private readonly Func<string, Task> _log;

    public ElevatedServiceMaintenanceRunner()
        : this(
            new ServiceCommandRunner(),
            new WorkerServiceStatusReader(),
            IsCurrentProcessAdministrator,
            WorkerExecutableLocator.GetExpectedPath,
            ServiceMaintenanceLog.WriteAsync)
    {
    }

    internal ElevatedServiceMaintenanceRunner(
        IServiceCommandRunner commands,
        IWorkerServiceStatusReader statusReader,
        Func<bool> isAdministrator,
        Func<string> workerPath,
        Func<string, Task> log)
    {
        _commands = commands;
        _statusReader = statusReader;
        _isAdministrator = isAdministrator;
        _workerPath = workerPath;
        _log = log;
    }

    /// <summary>
    /// Validates elevation and fixed paths, then runs Restart or ReinstallAndRestart with stage-specific exit codes.
    /// </summary>
    public async Task<int> RunAsync(
        ServiceMaintenanceOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_isAdministrator())
            {
                return await FinishAsync("NotElevated", ServiceMaintenanceExitCodes.NotElevated).ConfigureAwait(false);
            }

            var workerPath = _workerPath();
            if (operation == ServiceMaintenanceOperation.ReinstallAndRestart && !File.Exists(workerPath))
            {
                return await FinishAsync("WorkerExecutableMissing", ServiceMaintenanceExitCodes.WorkerExecutableMissing)
                    .ConfigureAwait(false);
            }

            var status = await _statusReader.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status == WorkerWindowsServiceState.StartPending)
            {
                if (!await WaitForStateAsync(WorkerWindowsServiceState.Running, cancellationToken).ConfigureAwait(false))
                {
                    return await FinishAsync("StartPendingTimeout", ServiceMaintenanceExitCodes.Timeout).ConfigureAwait(false);
                }

                status = WorkerWindowsServiceState.Running;
            }
            else if (status == WorkerWindowsServiceState.StopPending)
            {
                if (!await WaitForStateAsync(WorkerWindowsServiceState.Stopped, cancellationToken).ConfigureAwait(false))
                {
                    return await FinishAsync("StopPendingTimeout", ServiceMaintenanceExitCodes.Timeout).ConfigureAwait(false);
                }

                status = WorkerWindowsServiceState.Stopped;
            }

            if (operation == ServiceMaintenanceOperation.Restart
                && status == WorkerWindowsServiceState.NotInstalled)
            {
                return await FinishAsync("ServiceNotInstalled", ServiceMaintenanceExitCodes.ServiceNotInstalled)
                    .ConfigureAwait(false);
            }

            if (status == WorkerWindowsServiceState.Running)
            {
                if (!await StopAsync(cancellationToken).ConfigureAwait(false))
                {
                    return await FinishAsync("StopFailed", ServiceMaintenanceExitCodes.StopFailed).ConfigureAwait(false);
                }
            }

            if (operation == ServiceMaintenanceOperation.ReinstallAndRestart)
            {
                status = await _statusReader.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status != WorkerWindowsServiceState.NotInstalled)
                {
                    var delete = await _commands.ExecuteAsync(
                        "delete",
                        [WorkerServiceIdentity.ServiceName],
                        StageTimeout,
                        cancellationToken).ConfigureAwait(false);
                    if (!delete.Succeeded
                        || !await WaitForStateAsync(WorkerWindowsServiceState.NotInstalled, cancellationToken).ConfigureAwait(false))
                    {
                        return await FinishAsync("DeleteFailed", delete.TimedOut
                            ? ServiceMaintenanceExitCodes.Timeout
                            : ServiceMaintenanceExitCodes.DeleteFailed).ConfigureAwait(false);
                    }
                }

                var create = await _commands.ExecuteAsync(
                    "create",
                    [
                        WorkerServiceIdentity.ServiceName,
                        "binPath=",
                        $"\"{workerPath}\"",
                        "start=",
                        "auto",
                        "obj=",
                        "LocalSystem",
                        "DisplayName=",
                        WorkerServiceIdentity.DisplayName
                    ],
                    StageTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (!create.Succeeded)
                {
                    return await FinishAsync("CreateFailed", create.TimedOut
                        ? ServiceMaintenanceExitCodes.Timeout
                        : ServiceMaintenanceExitCodes.CreateFailed).ConfigureAwait(false);
                }
            }

            var start = await _commands.ExecuteAsync(
                "start",
                [WorkerServiceIdentity.ServiceName],
                StageTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!start.Succeeded
                || !await WaitForStateAsync(WorkerWindowsServiceState.Running, cancellationToken).ConfigureAwait(false))
            {
                return await FinishAsync("StartFailed", start.TimedOut
                    ? ServiceMaintenanceExitCodes.Timeout
                    : ServiceMaintenanceExitCodes.StartFailed).ConfigureAwait(false);
            }

            return await FinishAsync("Completed", ServiceMaintenanceExitCodes.Success).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FinishAsync("Cancelled", ServiceMaintenanceExitCodes.UnexpectedFailure).ConfigureAwait(false);
        }
        catch
        {
            return await FinishAsync("UnexpectedFailure", ServiceMaintenanceExitCodes.UnexpectedFailure).ConfigureAwait(false);
        }
    }

    private async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        var stop = await _commands.ExecuteAsync(
            "stop",
            [WorkerServiceIdentity.ServiceName],
            StageTimeout,
            cancellationToken).ConfigureAwait(false);
        return stop.Succeeded
            && await WaitForStateAsync(WorkerWindowsServiceState.Stopped, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WaitForStateAsync(
        WorkerWindowsServiceState expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + StageTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await _statusReader.GetStatusAsync(cancellationToken).ConfigureAwait(false) == expected)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<int> FinishAsync(string stage, int exitCode)
    {
        await _log($"{DateTimeOffset.UtcNow:O} Stage={stage} ExitCode={exitCode}").ConfigureAwait(false);
        return exitCode;
    }

    private static bool IsCurrentProcessAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>
/// Appends sanitized stage-only maintenance records under the controlled ProgramData log directory.
/// </summary>
internal static class ServiceMaintenanceLog
{
    public static async Task WriteAsync(string message)
    {
        Directory.CreateDirectory(ControlledPathValidator.LogsRoot);
        var path = Path.Combine(
            ControlledPathValidator.LogsRoot,
            $"ServiceMaintenance_{DateTime.Now:yyyyMMdd}.log");
        await File.AppendAllTextAsync(path, message + Environment.NewLine).ConfigureAwait(false);
    }
}
