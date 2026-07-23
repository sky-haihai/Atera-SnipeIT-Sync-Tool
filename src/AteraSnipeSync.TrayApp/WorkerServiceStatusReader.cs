using System.ComponentModel;
using System.ServiceProcess;
using AteraSnipeSync.Core.Runtime.Windows;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Defines the read-only SCM query boundary used by the Dashboard and elevated maintenance orchestration.
/// </summary>
internal interface IWorkerServiceStatusReader
{
    Task<WorkerWindowsServiceState> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Queries only the fixed Worker service and maps missing/transient SCM states without mutating Windows Services.
/// </summary>
internal sealed class WorkerServiceStatusReader : IWorkerServiceStatusReader
{
    /// <summary>
    /// Performs the blocking ServiceController status read on a pool thread and maps service-not-found to NotInstalled.
    /// </summary>
    public async Task<WorkerWindowsServiceState> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var controller = new ServiceController(WorkerServiceIdentity.ServiceName);
                return controller.Status switch
                {
                    ServiceControllerStatus.Stopped => WorkerWindowsServiceState.Stopped,
                    ServiceControllerStatus.StartPending => WorkerWindowsServiceState.StartPending,
                    ServiceControllerStatus.Running => WorkerWindowsServiceState.Running,
                    ServiceControllerStatus.StopPending => WorkerWindowsServiceState.StopPending,
                    _ => WorkerWindowsServiceState.Unknown
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            return WorkerWindowsServiceState.NotInstalled;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1060)
        {
            return WorkerWindowsServiceState.NotInstalled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return WorkerWindowsServiceState.Unknown;
        }
    }
}
