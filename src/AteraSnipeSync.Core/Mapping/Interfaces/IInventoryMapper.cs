using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.SnipeIt;

namespace AteraSnipeSync.Core.Mapping;

public interface IInventoryMapper
{
    SnipeImportBatch Map(
        AteraPullResult source,
        MappingOptions options);
}
