using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Mapping;

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
