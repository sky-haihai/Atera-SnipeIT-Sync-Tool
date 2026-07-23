using AteraSnipeSync.Core.Scheduling;

namespace AteraSnipeSync.Tests.Scheduling;

/// <summary>
/// Verifies the process-wide run lease rejects overlap and releases only the matching active operation.
/// </summary>
public sealed class SyncRunCoordinatorTests
{
    [Fact]
    public void TryAcquire_RejectsSecondOperation_AndReleasesIdempotently()
    {
        var coordinator = new SyncRunCoordinator();
        var started = DateTimeOffset.Parse("2026-07-17T12:00:00Z");

        var acquired = coordinator.TryAcquire(
            "request-1",
            WorkerOperationNames.Preview,
            started,
            out var lease);
        var second = coordinator.TryAcquire(
            "request-2",
            WorkerOperationNames.SyncNow,
            started.AddSeconds(1),
            out var secondLease);

        Assert.True(acquired);
        Assert.False(second);
        Assert.Null(secondLease);
        Assert.True(coordinator.IsRunning);
        Assert.Equal("request-1", coordinator.ActiveOperationId);
        Assert.Equal(WorkerOperationNames.Preview, coordinator.ActiveOperation);
        Assert.Equal(started, coordinator.ActiveStartedUtc);

        lease!.Dispose();
        lease.Dispose();

        Assert.False(coordinator.IsRunning);
        Assert.True(coordinator.TryAcquire(
            "request-3",
            WorkerOperationNames.Scheduled,
            started.AddMinutes(1),
            out var thirdLease));
        thirdLease!.Dispose();
    }
}
