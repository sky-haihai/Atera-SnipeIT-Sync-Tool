using AteraSnipeSync.Core.Runtime.Windows;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Resolves and validates the one permitted Worker executable directly beside TrayApp.
/// </summary>
internal static class WorkerExecutableLocator
{
    public static string GetExpectedPath()
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var path = Path.GetFullPath(Path.Combine(baseDirectory, WorkerServiceIdentity.ExecutableFileName));
        if (!string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar),
                baseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(path),
                WorkerServiceIdentity.ExecutableFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Worker executable path is outside the Tray application directory.");
        }

        return path;
    }
}
