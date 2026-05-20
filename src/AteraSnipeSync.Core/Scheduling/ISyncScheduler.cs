namespace AteraSnipeSync.Core.Scheduling;

public interface ISyncScheduler
{
    Task StartAsync(CancellationToken cancellationToken);
}
