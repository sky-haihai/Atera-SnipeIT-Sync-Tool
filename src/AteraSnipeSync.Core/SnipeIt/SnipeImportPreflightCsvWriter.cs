using System.Text;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Writes manual sync preflight CSV files atomically before the importer performs Snipe-IT mutations.
/// </summary>
internal static class SnipeImportPreflightCsvWriter
{
    public const string AssetsFileName = "snipeit-assets-plan.csv";
    public const string CompaniesFileName = "snipeit-companies-plan.csv";
    public const string ModelsFileName = "snipeit-models-plan.csv";

    /// <summary>
    /// Creates the output directory and writes the asset, company, and model plan CSV files.
    /// </summary>
    public static async Task WriteAsync(
        SnipeImportPreflightPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Manual preflight CSV directory is required.", nameof(outputDirectory));
        }

        Directory.CreateDirectory(outputDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, AssetsFileName),
            BuildAssetsCsv(plan.Assets),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, CompaniesFileName),
            BuildCompaniesCsv(plan.Companies),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, ModelsFileName),
            BuildModelsCsv(plan.Models),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildAssetsCsv(IReadOnlyList<SnipeAssetPreflightRow> rows)
    {
        var builder = new StringBuilder();
        AppendRow(builder, "Operation", "AssetTag", "Name", "Serial", "CompanyName", "ModelName", "CategoryName", "ManufacturerName", "ExistingAssetId", "ExistingAssetTag");

        foreach (var row in rows)
        {
            AppendRow(
                builder,
                row.Operation,
                row.AssetTag,
                row.Name,
                row.Serial,
                row.CompanyName,
                row.ModelName,
                row.CategoryName,
                row.ManufacturerName,
                row.ExistingAssetId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.ExistingAssetTag);
        }

        return builder.ToString();
    }

    private static string BuildCompaniesCsv(IReadOnlyList<SnipeCompanyPreflightRow> rows)
    {
        var builder = new StringBuilder();
        AppendRow(builder, "Operation", "Name");

        foreach (var row in rows)
        {
            AppendRow(builder, row.Operation, row.Name);
        }

        return builder.ToString();
    }

    private static string BuildModelsCsv(IReadOnlyList<SnipeModelPreflightRow> rows)
    {
        var builder = new StringBuilder();
        AppendRow(builder, "Operation", "Name", "CategoryName", "CategoryId", "ManufacturerName", "ManufacturerId");

        foreach (var row in rows)
        {
            AppendRow(
                builder,
                row.Operation,
                row.Name,
                row.CategoryName,
                row.CategoryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.ManufacturerName,
                row.ManufacturerId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, params string?[] columns)
    {
        builder.AppendLine(string.Join(",", columns.Select(Escape)));
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
