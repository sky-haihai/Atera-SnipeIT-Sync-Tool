using System.Globalization;
using System.Text.Json;
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Loads complete, internally consistent Snipe-IT reference snapshots before the importer plans any writes.
/// It fails closed when pagination cannot prove that all rows from the initial snapshot were received.
/// </summary>
internal sealed class SnipeSnapshotLoader
{
    private const int CompanyPageSize = 500;
    private const int MaximumSnapshotPages = 10000;
    private readonly SnipeApiClient _apiClient;
    private readonly Action<JsonElement, HttpMethod, string, string> _ensureBusinessSuccess;

    public SnipeSnapshotLoader(
        SnipeApiClient apiClient,
        Action<JsonElement, HttpMethod, string, string> ensureBusinessSuccess)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(ensureBusinessSuccess);

        _apiClient = apiClient;
        _ensureBusinessSuccess = ensureBusinessSuccess;
    }

    /// <summary>
    /// Reads `/companies` in 500-row pages and rejects changing totals, premature empty pages,
    /// malformed rows, over-full results, and snapshots that exceed the safety page limit.
    /// </summary>
    public async Task<IReadOnlyList<SnipeCompanySnapshotRow>> LoadCompaniesAsync(
        SnipeImportOptions options,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ReportProgress(progress, "Loading Snipe-IT company snapshot.", current: 0, total: null);
        var companies = new List<SnipeCompanySnapshotRow>();
        int? expectedTotal = null;
        var offset = 0;

        for (var page = 1; page <= MaximumSnapshotPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = $"companies?limit={CompanyPageSize.ToString(CultureInfo.InvariantCulture)}&offset={offset.ToString(CultureInfo.InvariantCulture)}";
            var operation = $"Load company snapshot page {page}";
            ReportProgress(progress, operation + ".", companies.Count, expectedTotal);

            using var document = await _apiClient.SendJsonAsync(
                HttpMethod.Get,
                relativePath,
                payload: null,
                operation,
                options,
                cancellationToken).ConfigureAwait(false);
            _ensureBusinessSuccess(document.RootElement, HttpMethod.Get, relativePath, operation);

            var reportedTotal = ReadInt(document.RootElement, "total")
                ?? throw Incomplete($"{operation} returned JSON without the required total count.");
            expectedTotal ??= reportedTotal;
            if (reportedTotal != expectedTotal.Value)
            {
                throw Incomplete(
                    $"{operation} reported total {reportedTotal}, which changed from {expectedTotal.Value} during pagination.");
            }

            var rows = ReadRows(document.RootElement, operation, "SnipeImport.IncompleteCompanySnapshot");
            if (rows.Count == 0 && companies.Count < expectedTotal.Value)
            {
                throw Incomplete(
                    $"{operation} returned no rows before the reported total {expectedTotal.Value} was loaded.");
            }

            foreach (var row in rows)
            {
                var id = ReadInt(row, "id");
                var name = ReadString(row, "name");
                if (id is null || name is null)
                {
                    throw Incomplete($"{operation} returned a company row without a valid id or name.");
                }

                companies.Add(new SnipeCompanySnapshotRow(id.Value, name));
            }

            if (companies.Count > expectedTotal.Value)
            {
                throw Incomplete(
                    $"Company snapshot loaded {companies.Count} row(s), exceeding reported total {expectedTotal.Value}.");
            }

            if (companies.Count == expectedTotal.Value)
            {
                ReportProgress(
                    progress,
                    $"Loaded Snipe-IT company snapshot with {companies.Count} company(s).",
                    companies.Count,
                    expectedTotal);
                return companies;
            }

            offset += rows.Count;
        }

        throw Incomplete("Company snapshot exceeded the maximum safe page count.");
    }

    /// <summary>
    /// Loads a standard Snipe-IT `total`/`rows` endpoint into detached JSON rows for one-run parsing and indexing.
    /// Empty pages end the snapshot for compatibility with existing hardware/model behavior; the safety limit prevents runaway paging.
    /// </summary>
    public async Task<SnipePagedSnapshot> LoadPagedRowsAsync(
        string endpoint,
        int pageSize,
        string entityLabel,
        SnipeImportOptions options,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Snapshot endpoint is required.", nameof(endpoint));
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var rows = new List<JsonElement>();
        int? total = null;
        var offset = 0;
        for (var page = 1; page <= MaximumSnapshotPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                $"Loading Snipe-IT {entityLabel} snapshot page {page}.",
                rows.Count,
                total);
            var relativePath = $"{endpoint.Trim('/')}?limit={pageSize.ToString(CultureInfo.InvariantCulture)}&offset={offset.ToString(CultureInfo.InvariantCulture)}";
            var operation = $"Load {entityLabel} snapshot page {page}";
            using var document = await _apiClient.SendJsonAsync(
                HttpMethod.Get,
                relativePath,
                payload: null,
                operation,
                options,
                cancellationToken).ConfigureAwait(false);
            _ensureBusinessSuccess(document.RootElement, HttpMethod.Get, relativePath, operation);

            total ??= ReadInt(document.RootElement, "total");
            var pageRows = ReadRows(document.RootElement, operation, "SnipeImport.MalformedResponse");
            rows.AddRange(pageRows.Select(row => row.Clone()));
            if (pageRows.Count == 0)
            {
                return new SnipePagedSnapshot(rows, total);
            }

            offset += pageRows.Count;
            if (total is { } expectedTotal && offset >= expectedTotal)
            {
                return new SnipePagedSnapshot(rows, total);
            }
        }

        throw new SnipeApiException(
            "SnipeImport.MalformedResponse",
            $"{entityLabel} snapshot exceeded the maximum safe page count.");
    }

    /// <summary>
    /// Loads a single-page reference endpoint and rejects a reported total that differs from the returned row count.
    /// </summary>
    public async Task<SnipePagedSnapshot> LoadSinglePageRowsAsync(
        string relativePath,
        string operation,
        string incompleteFailureCode,
        SnipeImportOptions options,
        CancellationToken cancellationToken)
    {
        using var document = await _apiClient.SendJsonAsync(
            HttpMethod.Get,
            relativePath,
            payload: null,
            operation,
            options,
            cancellationToken).ConfigureAwait(false);
        _ensureBusinessSuccess(document.RootElement, HttpMethod.Get, relativePath, operation);

        var rows = ReadRows(document.RootElement, operation, "SnipeImport.MalformedResponse");
        var total = ReadInt(document.RootElement, "total");
        if (total is not null && total.Value != rows.Count)
        {
            throw new SnipeApiException(
                incompleteFailureCode,
                $"{operation} returned {rows.Count} row(s) but reported total {total.Value}.");
        }

        return new SnipePagedSnapshot(rows.Select(row => row.Clone()).ToList(), total);
    }

    private static IReadOnlyList<JsonElement> ReadRows(
        JsonElement root,
        string operation,
        string failureCode)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("rows", out var rows)
            && rows.ValueKind == JsonValueKind.Array)
        {
            return rows.EnumerateArray().ToList();
        }

        throw new SnipeApiException(
            failureCode,
            $"{operation} returned JSON without the required rows array.");
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = System.Net.WebUtility.HtmlDecode(property.GetString())?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static SnipeApiException Incomplete(string message)
    {
        return new SnipeApiException("SnipeImport.IncompleteCompanySnapshot", message);
    }

    private static void ReportProgress(
        IProgress<SyncProgressUpdate>? progress,
        string message,
        int? current,
        int? total)
    {
        progress?.Report(new SyncProgressUpdate
        {
            Stage = "SnipeImport",
            Message = message,
            Current = current,
            Total = total
        });
    }
}

/// <summary>
/// Carries the validated company identity fields exported by the complete company snapshot loader.
/// </summary>
internal sealed record SnipeCompanySnapshotRow(int Id, string Name);

/// <summary>
/// Carries detached rows and the optional total reported by a standard paged Snipe-IT snapshot endpoint.
/// </summary>
internal sealed record SnipePagedSnapshot(IReadOnlyList<JsonElement> Rows, int? Total);
