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
        var ignoredDeviceTypes = CreateIgnoredDeviceTypeSet(options.IgnoredDeviceTypes);

        foreach (var agent in source.Agents)
        {
            if (IsIgnoredDeviceType(agent, ignoredDeviceTypes))
            {
                warnings.Add(MappingWarningFactory.IgnoredDeviceType(agent));
                continue;
            }

            var serial = HardwareIdentityNormalizer.NormalizeSerial(agent.SerialNumber);
            var agentId = Normalize(agent.AgentId);

            if (serial is null && agentId is null)
            {
                warnings.Add(MappingWarningFactory.MissingAgentIdentity(agent));
                continue;
            }

            if (serial is null)
            {
                warnings.Add(HardwareIdentityNormalizer.IsPlaceholderSerial(agent.SerialNumber)
                    ? MappingWarningFactory.PlaceholderSerialNumber(agent)
                    : MappingWarningFactory.MissingSerialNumber(agent));
            }

            var sourceId = agentId ?? serial!;
            var assetTag = AssetTagFactory.Create(sourceId);
            var name = Normalize(agent.Name) ?? assetTag;
            var companyName = MappingValueResolver.ResolveCompanyName(agent, options, warnings);
            var manufacturerName = MappingValueResolver.ResolveManufacturerName(agent, options, warnings);

            assets.Add(new SnipeAssetImportRecord
            {
                AssetTag = assetTag,
                Name = name,
                Serial = serial,
                MacAddresses = agent.MacAddresses,
                CompanyName = companyName,
                CompanyAliasName = MappingValueResolver.ResolveCompanyAliasName(companyName, options),
                ManufacturerName = manufacturerName,
                ManufacturerAliasName = MappingValueResolver.ResolveManufacturerAliasName(manufacturerName, options),
                ModelName = MappingValueResolver.ResolveModelName(agent, options, warnings),
                CategoryName = MappingValueResolver.ResolveCategoryName(agent, options),
                DeviceType = Normalize(agent.DeviceType),
                StatusId = options.DefaultStatusId,
                Notes = NotesBuilder.Build(agent),
                SourceSystem = "Atera",
                SourceId = sourceId
            });
        }

        ApplySharedVirtualMachineSerialFallback(assets, warnings);

        AddDuplicateIdentityWarnings(
            assets,
            CreateIgnoredMacAddressSet(options.IgnoredMacAddresses),
            warnings);

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

    private static HashSet<string> CreateIgnoredDeviceTypeSet(IReadOnlyList<string> ignoredDeviceTypes)
    {
        var normalizedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deviceType in ignoredDeviceTypes)
        {
            var normalizedDeviceType = Normalize(deviceType);
            if (normalizedDeviceType is not null)
            {
                normalizedValues.Add(normalizedDeviceType);
            }
        }

        return normalizedValues;
    }

    private static bool IsIgnoredDeviceType(
        AgentInfo agent,
        IReadOnlySet<string> ignoredDeviceTypes)
    {
        var deviceType = Normalize(agent.DeviceType);
        return deviceType is not null && ignoredDeviceTypes.Contains(deviceType);
    }

    private static HashSet<string> CreateIgnoredMacAddressSet(IReadOnlyList<string> ignoredMacAddresses)
    {
        return ignoredMacAddresses
            .Select(MacAddressNormalizer.NormalizeComparable)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts duplicate serials on Virtual Machine models into audit-only values while preserving stable Atera identities.
    /// </summary>
    private static void ApplySharedVirtualMachineSerialFallback(
        IList<SnipeAssetImportRecord> assets,
        ICollection<ModuleWarning> warnings)
    {
        var duplicateSerialGroups = assets
            .Select((asset, index) => (Serial: HardwareIdentityNormalizer.NormalizeSerial(asset.Serial), Asset: asset, Index: index))
            .Where(item => item.Serial is not null)
            .GroupBy(item => item.Serial!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in duplicateSerialGroups)
        {
            var virtualMachines = group
                .Where(item => string.Equals(
                    Normalize(item.Asset.ModelName),
                    "Virtual Machine",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (virtualMachines.Count == 0)
            {
                continue;
            }

            foreach (var item in virtualMachines)
            {
                var asset = item.Asset;
                assets[item.Index] = new SnipeAssetImportRecord
                {
                    AssetTag = asset.AssetTag,
                    Name = asset.Name,
                    Serial = asset.Serial,
                    SerialIsReliableIdentity = false,
                    MacAddresses = asset.MacAddresses,
                    CompanyName = asset.CompanyName,
                    CompanyAliasName = asset.CompanyAliasName,
                    ManufacturerName = asset.ManufacturerName,
                    ManufacturerAliasName = asset.ManufacturerAliasName,
                    ModelName = asset.ModelName,
                    CategoryName = asset.CategoryName,
                    DeviceType = asset.DeviceType,
                    StatusId = asset.StatusId,
                    Notes = asset.Notes,
                    SourceSystem = asset.SourceSystem,
                    SourceId = asset.SourceId
                };
            }

            warnings.Add(MappingWarningFactory.SharedVirtualMachineSerial(
                virtualMachines.Select(item => item.Asset.SourceId)));
        }
    }

    /// <summary>
    /// Reports every batch-level identity collision so operators can correct source data before an import blocks it.
    /// </summary>
    private static void AddDuplicateIdentityWarnings(
        IReadOnlyList<SnipeAssetImportRecord> assets,
        IReadOnlySet<string> ignoredMacAddresses,
        ICollection<ModuleWarning> warnings)
    {
        AddDuplicateWarnings(assets, asset => Normalize(asset.SourceId), "DuplicateSourceId", "source id", warnings);
        AddDuplicateWarnings(assets, asset => Normalize(asset.AssetTag), "DuplicateAssetTag", "asset tag", warnings);
        AddDuplicateWarnings(
            assets,
            asset => asset.SerialIsReliableIdentity ? HardwareIdentityNormalizer.NormalizeSerial(asset.Serial) : null,
            "DuplicateSerial",
            "serial",
            warnings);

        var macOwners = assets
            .SelectMany(asset => asset.MacAddresses
                .Select(MacAddressNormalizer.NormalizeComparable)
                .Where(value => value is not null)
                .Where(value => !ignoredMacAddresses.Contains(value!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => (Value: value!, Asset: asset)))
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Asset.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        foreach (var group in macOwners)
        {
            warnings.Add(MappingWarningFactory.DuplicateIdentity(
                "DuplicateMacAddress",
                "MAC address",
                group.Select(item => item.Asset.SourceId)));
        }
    }

    private static void AddDuplicateWarnings(
        IReadOnlyList<SnipeAssetImportRecord> assets,
        Func<SnipeAssetImportRecord, string?> selector,
        string code,
        string identityName,
        ICollection<ModuleWarning> warnings)
    {
        foreach (var group in assets
            .Select(asset => (Value: selector(asset), Asset: asset))
            .Where(item => item.Value is not null)
            .GroupBy(item => item.Value!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            warnings.Add(MappingWarningFactory.DuplicateIdentity(
                code,
                identityName,
                group.Select(item => item.Asset.SourceId)));
        }
    }
}
