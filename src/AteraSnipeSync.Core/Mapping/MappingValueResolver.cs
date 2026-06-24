using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Mapping;

internal static class MappingValueResolver
{
    public static string ResolveCompanyName(
        AgentInfo agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings)
    {
        var companyName = InventoryMapper.Normalize(agent.CustomerName);

        if (companyName is not null)
        {
            return ResolveCompanyAlias(companyName, options);
        }

        warnings.Add(MappingWarningFactory.MissingCompany(agent));
        return ResolveCompanyAlias(options.DefaultCompanyName, options);
    }

    public static string ResolveManufacturerName(
        AgentInfo agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings)
    {
        var manufacturer = InventoryMapper.Normalize(agent.Manufacturer);

        if (manufacturer is not null)
        {
            return manufacturer;
        }

        warnings.Add(MappingWarningFactory.MissingManufacturer(agent));
        return options.DefaultManufacturerName;
    }

    public static string ResolveModelName(
        AgentInfo agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings)
    {
        var model = InventoryMapper.Normalize(agent.Model);

        if (model is not null)
        {
            return model;
        }

        warnings.Add(MappingWarningFactory.MissingModel(agent));
        return options.DefaultModelName;
    }

    public static string ResolveCategoryName(
        AgentInfo agent,
        MappingOptions options)
    {
        return options.DefaultCategoryName;
    }

    private static string ResolveCompanyAlias(string companyName, MappingOptions options)
    {
        var normalizedCompanyName = InventoryMapper.Normalize(companyName);
        if (normalizedCompanyName is null)
        {
            return companyName;
        }

        foreach (var alias in options.CompanyAliases)
        {
            var aliasSource = InventoryMapper.Normalize(alias.Key);
            var aliasTarget = InventoryMapper.Normalize(alias.Value);
            if (aliasSource is not null
                && aliasTarget is not null
                && string.Equals(aliasSource, normalizedCompanyName, StringComparison.OrdinalIgnoreCase))
            {
                return aliasTarget;
            }
        }

        return normalizedCompanyName;
    }
}
