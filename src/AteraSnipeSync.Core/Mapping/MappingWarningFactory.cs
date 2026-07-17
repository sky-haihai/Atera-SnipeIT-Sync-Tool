using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Mapping;

/// <summary>
/// Creates consistent Reconstruction warnings for skipped or fallback-mapped Atera records.
/// </summary>
internal static class MappingWarningFactory
{
    public static ModuleWarning MissingAgentIdentity(AgentInfo agent)
    {
        return Create(
            "MissingAgentIdentity",
            $"Atera agent '{Describe(agent)}' is missing both serial number and agent id.");
    }

    public static ModuleWarning MissingSerialNumber(AgentInfo agent)
    {
        return Create(
            "MissingSerialNumber",
            $"Atera agent '{Describe(agent)}' is missing serial number; using Atera agent id fallback.");
    }

    public static ModuleWarning PlaceholderSerialNumber(AgentInfo agent)
    {
        return Create(
            "PlaceholderSerialNumber",
            $"Atera agent '{Describe(agent)}' has a non-unique firmware serial placeholder; using Atera agent id fallback.");
    }

    public static ModuleWarning DuplicateIdentity(
        string code,
        string identityName,
        IEnumerable<string> sourceIds)
    {
        var affectedSources = string.Join(", ", sourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(5));
        return Create(code, $"Mapped assets share the same {identityName}; affected source ids: {affectedSources}.");
    }

    public static ModuleWarning SharedVirtualMachineSerial(IEnumerable<string> sourceIds)
    {
        var affectedSources = string.Join(", ", sourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(5));
        return Create(
            "SharedVirtualMachineSerial",
            $"Virtual Machine records share a source serial; using ATERA source ids as asset tags and retaining the serial for audit only. Affected source ids: {affectedSources}.");
    }

    public static ModuleWarning MissingCompany(AgentInfo agent)
    {
        return Create(
            "MissingCompany",
            $"Atera agent '{Describe(agent)}' is missing company; using default company.");
    }

    public static ModuleWarning MissingManufacturer(AgentInfo agent)
    {
        return Create(
            "MissingManufacturer",
            $"Atera agent '{Describe(agent)}' is missing manufacturer; using default manufacturer.");
    }

    public static ModuleWarning MissingModel(AgentInfo agent)
    {
        return Create(
            "MissingModel",
            $"Atera agent '{Describe(agent)}' is missing model; using default model.");
    }

    public static ModuleWarning IgnoredDeviceType(AgentInfo agent)
    {
        return Create(
            "IgnoredDeviceType",
            $"Atera agent '{Describe(agent)}' has ignored device type '{InventoryMapper.Normalize(agent.DeviceType)}'; skipping Snipe-IT sync.");
    }

    private static ModuleWarning Create(string code, string message)
    {
        return new ModuleWarning
        {
            Source = "Reconstruction",
            Code = code,
            Message = message
        };
    }

    private static string Describe(AgentInfo agent)
    {
        return InventoryMapper.Normalize(agent.AgentId)
            ?? InventoryMapper.Normalize(agent.Name)
            ?? "<unknown>";
    }
}
