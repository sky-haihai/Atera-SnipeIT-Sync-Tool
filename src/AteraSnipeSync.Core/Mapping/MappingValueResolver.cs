using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Mapping;

internal static class MappingValueResolver
{
    public static string ResolveCompanyName(
        AteraAgentDto agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings)
    {
        var companyName = InventoryMapper.Normalize(agent.CustomerName);

        if (companyName is not null)
        {
            return companyName;
        }

        warnings.Add(MappingWarningFactory.MissingCompany(agent));
        return options.DefaultCompanyName;
    }

    public static string ResolveManufacturerName(
        AteraAgentDto agent,
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
        AteraAgentDto agent,
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
        AteraAgentDto agent,
        MappingOptions options)
    {
        return options.DefaultCategoryName;
    }
}
