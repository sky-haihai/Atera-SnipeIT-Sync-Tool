namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Loads the one bundled product icon for NotifyIcon ownership without depending on loose runtime files.
/// </summary>
internal static class TrayIconLoader
{
    internal const string ResourceName =
        "AteraSnipeSync.TrayApp.Assets.tray-icon.ico";

    /// <summary>
    /// Returns a caller-owned clone of the embedded multi-resolution icon and fails explicitly when the build resource is missing or invalid.
    /// </summary>
    public static Icon Load()
    {
        try
        {
            using var resourceStream = typeof(TrayIconLoader).Assembly
                .GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    $"The bundled TrayApp icon resource '{ResourceName}' is missing.");
            using var sourceIcon = new Icon(resourceStream);
            return (Icon)sourceIcon.Clone();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"The bundled TrayApp icon resource '{ResourceName}' is invalid.",
                exception);
        }
    }
}
