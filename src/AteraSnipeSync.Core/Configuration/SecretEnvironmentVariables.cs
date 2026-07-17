namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Defines the process/service environment variables used to inject test-stage API credentials outside JSON config.
/// </summary>
public static class SecretEnvironmentVariables
{
    public const string AteraApiKey = "ATERA_API_KEY";
    public const string SnipeItApiToken = "SNIPEIT_API_TOKEN";
}
