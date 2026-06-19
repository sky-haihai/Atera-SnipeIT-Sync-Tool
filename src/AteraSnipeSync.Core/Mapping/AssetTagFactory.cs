using AteraSnipeSync.Core.Atera;

namespace AteraSnipeSync.Core.Mapping;

internal static class AssetTagFactory
{
    public static string Create(AgentInfo agent)
    {
        var serial = InventoryMapper.Normalize(agent.SerialNumber);

        if (serial is not null)
        {
            return serial;
        }

        return $"ATERA-{agent.AgentId.Trim()}";
    }
}
