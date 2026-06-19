namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Represents an expected Atera pull failure with a category that orchestration can react to.
/// </summary>
public sealed class AteraPullException : Exception
{
    public AteraPullException(
        AteraPullFailureKind failureKind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    public AteraPullFailureKind FailureKind { get; }
}
