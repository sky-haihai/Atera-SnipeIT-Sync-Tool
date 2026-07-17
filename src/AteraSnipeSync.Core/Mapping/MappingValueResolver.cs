using System.Text;
using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Mapping;

/// <summary>
/// Resolves mapped asset field values from Atera agent data, mapping defaults, and operator-provided aliases.
/// </summary>
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
            return companyName;
        }

        warnings.Add(MappingWarningFactory.MissingCompany(agent));
        return InventoryMapper.Normalize(options.DefaultCompanyName) ?? options.DefaultCompanyName;
    }

    /// <summary>
    /// Returns the configured company alias candidate while preserving the source name for target-aware precedence in Snipe Import.
    /// </summary>
    public static string? ResolveCompanyAliasName(string companyName, MappingOptions options)
    {
        var normalizedCompanyName = InventoryMapper.Normalize(companyName);
        if (normalizedCompanyName is null)
        {
            return null;
        }

        var aliasName = ResolveAlias(normalizedCompanyName, options.CompanyAliases);
        return string.Equals(aliasName, normalizedCompanyName, StringComparison.Ordinal)
            ? null
            : aliasName;
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
        return InventoryMapper.Normalize(options.DefaultManufacturerName) ?? options.DefaultManufacturerName;
    }

    /// <summary>
    /// Returns the configured manufacturer alias candidate while preserving the source name for target-aware precedence in Snipe Import.
    /// </summary>
    public static string? ResolveManufacturerAliasName(string manufacturerName, MappingOptions options)
    {
        var normalizedManufacturerName = InventoryMapper.Normalize(manufacturerName);
        if (normalizedManufacturerName is null)
        {
            return null;
        }

        var aliasName = ResolveAlias(normalizedManufacturerName, options.ManufacturerAliases);
        return string.Equals(aliasName, normalizedManufacturerName, StringComparison.Ordinal)
            ? null
            : aliasName;
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

    /// <summary>
    /// Applies an operator-configured one-way alias without fuzzy matching or changing an unmatched source value.
    /// </summary>
    private static string ResolveAlias(
        string sourceValue,
        IReadOnlyDictionary<string, string> aliases)
    {
        var normalizedSourceValue = InventoryMapper.Normalize(sourceValue);
        if (normalizedSourceValue is null)
        {
            return sourceValue;
        }

        var comparableSourceValue = NormalizeAliasComparable(normalizedSourceValue);
        foreach (var alias in aliases)
        {
            var aliasSource = InventoryMapper.Normalize(alias.Key);
            var aliasTarget = InventoryMapper.Normalize(alias.Value);
            if (aliasSource is not null
                && aliasTarget is not null
                && string.Equals(
                    NormalizeAliasComparable(aliasSource),
                    comparableSourceValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                return aliasTarget;
            }
        }

        return normalizedSourceValue;
    }

    /// <summary>
    /// Normalizes alias keys for comparison without changing the configured canonical output name.
    /// </summary>
    private static string? NormalizeAliasComparable(string? value)
    {
        var normalizedValue = InventoryMapper.Normalize(value);
        if (normalizedValue is null)
        {
            return null;
        }

        var builder = new StringBuilder(normalizedValue.Length);
        var previousWasWhitespace = false;
        foreach (var character in normalizedValue)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(IsDashLike(character) ? '-' : character);
            previousWasWhitespace = false;
        }

        return builder.ToString().TrimEnd();
    }

    private static bool IsDashLike(char character)
    {
        return character is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212';
    }
}
