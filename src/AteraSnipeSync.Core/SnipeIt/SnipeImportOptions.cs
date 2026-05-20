namespace AteraSnipeSync.Core.SnipeIt;

public sealed class SnipeImportOptions
{
    public required string BaseUrl { get; init; }
    public required string ApiToken { get; init; }
    public required bool DryRun { get; init; }
    public required bool CreateMissingCompanies { get; init; }
    public required bool CreateMissingModels { get; init; }
}
