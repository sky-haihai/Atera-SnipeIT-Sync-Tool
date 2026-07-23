namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Normalizes the Windows Service states that influence Dashboard availability.
/// </summary>
internal enum WorkerWindowsServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    StartPending,
    Running,
    StopPending
}

/// <summary>
/// Identifies local Tray work that must disable conflicting Dashboard actions before the first await.
/// </summary>
internal enum DashboardLocalActivity
{
    Idle,
    ReloadingSchedule,
    RunningTrayCommand,
    ServiceMaintenance
}

/// <summary>
/// Combines fresh SCM/Worker observations with the current local Tray activity.
/// </summary>
internal sealed record DashboardState(
    WorkerWindowsServiceState ServiceState,
    bool WorkerOnline,
    bool ProtocolCompatible,
    bool WorkerBusy,
    string? ActiveOperation,
    DashboardLocalActivity LocalActivity,
    bool ActiveCommandCanCancel);

/// <summary>
/// Carries the complete enabled-state decision for Dashboard action controls.
/// </summary>
internal sealed record DashboardControlState(
    bool ConfigEnabled,
    bool RunButtonsEnabled,
    bool RestartEnabled,
    bool CancelEnabled);
