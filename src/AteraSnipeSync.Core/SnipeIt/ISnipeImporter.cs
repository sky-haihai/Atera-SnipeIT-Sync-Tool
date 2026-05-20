namespace AteraSnipeSync.Core.SnipeIt;

public interface ISnipeImporter
{
    Task<SnipeImportResult> ImportAsync(
        SnipeImportBatch batch,
        SnipeImportOptions options,
        CancellationToken cancellationToken);
}
