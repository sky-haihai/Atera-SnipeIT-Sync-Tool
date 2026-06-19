namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Carries caller-supplied credentials for one Atera inventory pull.
/// </summary>
public sealed class AteraPullRequest
{
    public required string ApiKey { get; init; }
}
