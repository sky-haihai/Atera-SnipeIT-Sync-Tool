namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Provides time to Atera pull code so tests can assert deterministic completion timestamps.
/// </summary>
public interface IAteraClock
{
    DateTimeOffset UtcNow { get; }
}
