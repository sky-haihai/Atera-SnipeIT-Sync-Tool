namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Holds Snipe-IT connection defaults and import matching settings loaded from local configuration.
/// </summary>
public sealed class SnipeItConfig
{
    public required string BaseUrl { get; init; }
    public required string ApiToken { get; init; }
    public required int DefaultStatusId { get; init; }
    public string? MacAddressCustomFieldDbColumnName { get; init; }
    public string? MacAddressFieldsetName { get; init; }
    public IReadOnlyList<string> ModelCategoriesToNormalize { get; init; } = [];
    public IReadOnlyList<string> IgnoredMacAddresses { get; init; } = [];
    public double? NameMatchThreshold { get; init; }
}
