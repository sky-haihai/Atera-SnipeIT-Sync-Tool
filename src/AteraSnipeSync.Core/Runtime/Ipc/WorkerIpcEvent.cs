using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Scheduling;

namespace AteraSnipeSync.Core.Runtime.Ipc;

/// <summary>
/// Carries one sanitized Worker acknowledgement, progress update, or terminal response to a Tray request.
/// </summary>
public sealed class WorkerIpcEvent
{
    public required int ProtocolVersion { get; init; }
    public required string RequestId { get; init; }
    public required string EventType { get; init; }
    public string? Message { get; init; }
    public SyncProgressUpdate? Progress { get; init; }
    public WorkerSyncResultSummary? SyncResult { get; init; }
    public ConnectionTestResult? ConnectionTest { get; init; }
    public NotificationTestResult? NotificationTest { get; init; }
    public WorkerStatusSnapshot? WorkerStatus { get; init; }
    public ScheduleReloadResult? ScheduleReload { get; init; }
    public string? ActiveOperation { get; init; }
    public string? PreflightDirectory { get; init; }
    public string? ReportPath { get; init; }
}
