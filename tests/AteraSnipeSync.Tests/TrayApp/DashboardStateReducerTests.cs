using AteraSnipeSync.TrayApp;

namespace AteraSnipeSync.Tests.TrayApp;

/// <summary>
/// Verifies Dashboard control availability across Worker, SCM, reload, run, and maintenance states without starting WinForms.
/// </summary>
public sealed class DashboardStateReducerTests
{
    [Fact]
    public void Reduce_OnlineIdle_EnablesRunAndServiceActions()
    {
        var controls = DashboardStateReducer.Reduce(State());

        Assert.True(controls.ConfigEnabled);
        Assert.True(controls.RunButtonsEnabled);
        Assert.True(controls.RestartEnabled);
        Assert.False(controls.CancelEnabled);
    }

    [Theory]
    [InlineData((int)DashboardLocalActivity.ReloadingSchedule)]
    [InlineData((int)DashboardLocalActivity.ServiceMaintenance)]
    public void Reduce_LocalExclusiveActivity_DisablesRunAndServiceActions(
        int activityValue)
    {
        var activity = (DashboardLocalActivity)activityValue;
        var controls = DashboardStateReducer.Reduce(State(localActivity: activity));

        Assert.False(controls.RunButtonsEnabled);
        Assert.False(controls.RestartEnabled);
    }

    [Theory]
    [InlineData("scheduled")]
    [InlineData("connection-test")]
    [InlineData("preview")]
    [InlineData("sync-now")]
    public void Reduce_AnyWorkerRun_DisablesRunAndServiceActions(string operation)
    {
        var controls = DashboardStateReducer.Reduce(State(workerBusy: true, activeOperation: operation));

        Assert.True(controls.ConfigEnabled);
        Assert.False(controls.RunButtonsEnabled);
        Assert.False(controls.RestartEnabled);
    }

    [Fact]
    public void Reduce_NotInstalled_OnlyEnablesConfig()
    {
        var controls = DashboardStateReducer.Reduce(State(
            serviceState: WorkerWindowsServiceState.NotInstalled,
            workerOnline: false));

        Assert.True(controls.ConfigEnabled);
        Assert.False(controls.RunButtonsEnabled);
        Assert.False(controls.RestartEnabled);
    }

    [Fact]
    public void Reduce_Stopped_EnablesRestartButNotRuns()
    {
        var controls = DashboardStateReducer.Reduce(State(
            serviceState: WorkerWindowsServiceState.Stopped,
            workerOnline: false));

        Assert.False(controls.RunButtonsEnabled);
        Assert.True(controls.RestartEnabled);
    }

    [Fact]
    public void Reduce_RunningButIpcOffline_EnablesRestartOnly()
    {
        var controls = DashboardStateReducer.Reduce(State(workerOnline: false));

        Assert.False(controls.RunButtonsEnabled);
        Assert.True(controls.RestartEnabled);
    }

    [Fact]
    public void Reduce_TrayCommand_EnablesCancelOnlyForCancellableRequest()
    {
        var cancellable = DashboardStateReducer.Reduce(State(
            localActivity: DashboardLocalActivity.RunningTrayCommand,
            activeCommandCanCancel: true));
        var scheduled = DashboardStateReducer.Reduce(State(
            workerBusy: true,
            activeOperation: "scheduled",
            activeCommandCanCancel: false));

        Assert.True(cancellable.CancelEnabled);
        Assert.False(scheduled.CancelEnabled);
    }

    private static DashboardState State(
        WorkerWindowsServiceState serviceState = WorkerWindowsServiceState.Running,
        bool workerOnline = true,
        bool workerBusy = false,
        string? activeOperation = null,
        DashboardLocalActivity localActivity = DashboardLocalActivity.Idle,
        bool activeCommandCanCancel = false)
    {
        return new DashboardState(
            serviceState,
            workerOnline,
            ProtocolCompatible: true,
            workerBusy,
            activeOperation,
            localActivity,
            activeCommandCanCancel);
    }
}
