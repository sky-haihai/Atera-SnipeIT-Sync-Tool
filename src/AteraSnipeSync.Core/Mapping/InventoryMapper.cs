using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.SnipeIt;

namespace AteraSnipeSync.Core.Mapping;

/// <summary>
/// Converts pulled Atera agents into Snipe-IT import records without calling external systems.
/// </summary>
public sealed class InventoryMapper : IInventoryMapper
{
    public SnipeImportBatch Map(
        AteraPullResult source,
        MappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var assets = new List<SnipeAssetImportRecord>();
        var warnings = new List<ModuleWarning>();

        foreach (var agent in source.Agents)
        {
            var serial = Normalize(agent.SerialNumber);
            var agentId = Normalize(agent.AgentId);

            if (serial is null && agentId is null)
            {
                warnings.Add(MappingWarningFactory.MissingAgentIdentity(agent));
                continue;
            }

            if (serial is null)
            {
                warnings.Add(MappingWarningFactory.MissingSerialNumber(agent));
            }

            var assetTag = AssetTagFactory.Create(agent);
            var name = Normalize(agent.Name) ?? assetTag;

            assets.Add(new SnipeAssetImportRecord
            {
                AssetTag = assetTag,
                Name = name,
                Serial = serial,
                MacAddresses = agent.MacAddresses,
                CompanyName = MappingValueResolver.ResolveCompanyName(agent, options, warnings),
                ManufacturerName = MappingValueResolver.ResolveManufacturerName(agent, options, warnings),
                ModelName = MappingValueResolver.ResolveModelName(agent, options, warnings),
                CategoryName = MappingValueResolver.ResolveCategoryName(agent, options),
                StatusId = options.DefaultStatusId,
                Notes = NotesBuilder.Build(agent),
                SourceSystem = "Atera",
                SourceId = agentId ?? serial!
            });
        }

        return new SnipeImportBatch
        {
            Assets = assets,
            Summary = new MappingSummary
            {
                SourceAgentCount = source.Agents.Count,
                MappedAssetCount = assets.Count
            },
            Warnings = warnings
        };
    }

    internal static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
