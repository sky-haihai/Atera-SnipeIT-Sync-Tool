namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Applies one deterministic availability policy across SCM state, Worker state, and local asynchronous activity.
/// </summary>
internal static class DashboardStateReducer
{
    /// <summary>
    /// Computes control availability without side effects so every UI path and unit test shares identical rules.
    /// </summary>
    public static DashboardControlState Reduce(DashboardState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var maintenance = state.LocalActivity == DashboardLocalActivity.ServiceMaintenance;
        var locallyIdle = state.LocalActivity == DashboardLocalActivity.Idle;
        var canMaintain = locallyIdle && !state.WorkerBusy;
        var workerReady = state.ServiceState == WorkerWindowsServiceState.Running
            && state.WorkerOnline
            && state.ProtocolCompatible
            && !state.WorkerBusy
            && locallyIdle;

        return new DashboardControlState(
            ConfigEnabled: !maintenance
                && state.LocalActivity is not DashboardLocalActivity.ReloadingSchedule,
            RunButtonsEnabled: workerReady,
            RestartEnabled: canMaintain
                && state.ServiceState is WorkerWindowsServiceState.Running
                    or WorkerWindowsServiceState.Stopped
                    or WorkerWindowsServiceState.StartPending
                    or WorkerWindowsServiceState.StopPending,
            CancelEnabled: state.LocalActivity == DashboardLocalActivity.RunningTrayCommand
                && state.ActiveCommandCanCancel);
    }
}
