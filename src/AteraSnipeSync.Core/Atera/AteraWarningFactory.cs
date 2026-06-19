using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Creates sanitized warnings for non-fatal Atera pull record issues.
/// </summary>
public static class AteraWarningFactory
{
    public static ModuleWarning MalformedAgentRecord(string reason)
    {
        return new ModuleWarning
        {
            Source = "AteraPull",
            Code = "AteraPull.MalformedAgentRecord",
            Message = $"Atera agent record could not be converted: {reason}"
        };
    }

    public static ModuleWarning MissingAgentIdentity(string sourceDescription)
    {
        return new ModuleWarning
        {
            Source = "AteraPull",
            Code = "AteraPull.MissingAgentIdentity",
            Message = $"Atera agent record is missing required identity fields: {sourceDescription}"
        };
    }
}
