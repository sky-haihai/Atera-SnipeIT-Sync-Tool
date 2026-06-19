using AteraSnipeSync.Core.Atera;

namespace AteraSnipeSync.Core.Mapping;

internal static class NotesBuilder
{
    public static string Build(AgentInfo agent)
    {
        return string.Join(
            Environment.NewLine,
            "Imported from Atera.",
            $"Atera Agent ID: {agent.AgentId}",
            $"Atera Device Name: {agent.Name}",
            $"Atera Customer ID: {agent.CustomerId}",
            $"Atera Customer Name: {agent.CustomerName}");
    }
}
