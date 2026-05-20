namespace AteraSnipeSync.Core.Configuration;

public sealed class SnipeItConfig
{
    public required string BaseUrl { get; init; }
    public required string ApiToken { get; init; }
    public required int DefaultStatusId { get; init; }
}
