namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Classifies Atera pull failures so callers can distinguish auth, retry, response, and pagination issues.
/// </summary>
public enum AteraPullFailureKind
{
    InvalidRequest,
    AuthenticationFailed,
    RetryExhausted,
    NonRetryableHttpFailure,
    MalformedResponse,
    PaginationStateUnknown,
    Cancelled
}
