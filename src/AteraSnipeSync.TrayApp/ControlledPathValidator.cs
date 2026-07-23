namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Restricts Explorer launches to known ProgramData output roots and the fixed colocated Worker executable.
/// </summary>
internal static class ControlledPathValidator
{
    public static string ProgramDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AteraSnipeSync");

    public static string PreflightRoot => Path.Combine(ProgramDataRoot, "Preflight");
    public static string HistoryRoot => Path.Combine(ProgramDataRoot, "History");
    public static string LogsRoot => Path.Combine(ProgramDataRoot, "Logs");

    public static bool IsUnderRoot(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar),
                fullRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }
}
