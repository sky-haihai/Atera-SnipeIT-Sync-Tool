namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Represents a Snipe-IT API failure that can be converted into a structured import failure.
/// </summary>
internal sealed class SnipeApiException : Exception
{
    public SnipeApiException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public SnipeApiException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
