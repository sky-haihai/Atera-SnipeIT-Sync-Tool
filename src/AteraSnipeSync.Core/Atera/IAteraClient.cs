namespace AteraSnipeSync.Core.Atera;

public interface IAteraClient
{
    Task<AteraPullResult> PullInventoryAsync(
        AteraPullRequest request,
        CancellationToken cancellationToken);
}
