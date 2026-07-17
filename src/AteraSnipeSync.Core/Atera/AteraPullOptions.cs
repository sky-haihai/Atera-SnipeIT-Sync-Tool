namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Configures Atera HTTP access and retry behavior without carrying credentials.
/// </summary>
public sealed class AteraPullOptions
{
    public required Uri BaseUri { get; init; }
    public required int MaxRetryAttempts { get; init; }
    public required TimeSpan RetryDelay { get; init; }
    public int ItemsPerPage { get; init; } = 500;
    public int? MaxPages { get; init; }
}
