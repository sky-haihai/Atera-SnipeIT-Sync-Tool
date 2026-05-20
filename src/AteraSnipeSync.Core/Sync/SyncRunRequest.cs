using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.SnipeIt;

namespace AteraSnipeSync.Core.Sync;

public sealed class SyncRunRequest
{
    public required AteraPullRequest Atera { get; init; }
    public required MappingOptions Mapping { get; init; }
    public required SnipeImportOptions SnipeIt { get; init; }
    public required SyncRunOptions Sync { get; init; }
}
