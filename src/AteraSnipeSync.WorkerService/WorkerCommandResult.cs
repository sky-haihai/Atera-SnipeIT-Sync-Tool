using AteraSnipeSync.Core.Runtime.Ipc;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.Notifications;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Carries one command's terminal event type and its command-specific sanitized payload back to the IPC server.
/// </summary>
public sealed class WorkerCommandResult
{
    public required string EventType { get; init; }
    public required string Message { get; init; }
    public WorkerSyncResultSummary? SyncResult { get; init; }
    public ConnectionTestResult? ConnectionTest { get; init; }
    public NotificationTestResult? NotificationTest { get; init; }
    public WorkerStatusSnapshot? WorkerStatus { get; init; }
    public ScheduleReloadResult? ScheduleReload { get; init; }
    public string? ActiveOperation { get; init; }
    public string? PreflightDirectory { get; init; }
    public string? ReportPath { get; init; }
}
