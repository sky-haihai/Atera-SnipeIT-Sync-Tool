using AteraSnipeSync.Core.Runtime.Windows;

namespace AteraSnipeSync.Tests.WorkerService;

/// <summary>
/// Locks the service registration, display, and colocated executable identifiers shared by Worker and future Tray maintenance code.
/// </summary>
public sealed class WorkerServiceIdentityTests
{
    [Fact]
    public void Constants_MatchFixedWindowsServiceContract()
    {
        Assert.Equal("AteraSnipeItAutoSync", WorkerServiceIdentity.ServiceName);
        Assert.Equal("Atera Snipe-IT Auto Sync", WorkerServiceIdentity.DisplayName);
        Assert.Equal("AteraSnipeSync.WorkerService.exe", WorkerServiceIdentity.ExecutableFileName);
    }
}
