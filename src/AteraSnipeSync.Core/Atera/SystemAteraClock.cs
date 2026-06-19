namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Supplies wall-clock UTC time for production Atera pull summaries.
/// </summary>
public sealed class SystemAteraClock : IAteraClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
