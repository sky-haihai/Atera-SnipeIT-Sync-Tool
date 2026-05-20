namespace AteraSnipeSync.Core.Mapping;

public sealed class MappingOptions
{
    public required string DefaultCompanyName { get; init; }
    public required string DefaultManufacturerName { get; init; }
    public required string DefaultModelName { get; init; }
    public required string DefaultCategoryName { get; init; }
    public required int DefaultStatusId { get; init; }
}
