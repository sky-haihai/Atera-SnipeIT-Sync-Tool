using AteraSnipeSync.Core.Atera;

namespace AteraSnipeSync.Tests.Atera;

public sealed class AteraPullContractTests
{
    [Fact]
    public void AteraPullResult_DoesNotExposeCustomersCollection()
    {
        var property = typeof(AteraPullResult).GetProperty("Customers");

        Assert.Null(property);
    }

    [Fact]
    public void PullSummary_DoesNotExposeCustomerCount()
    {
        var property = typeof(PullSummary).GetProperty("CustomerCount");

        Assert.Null(property);
    }

    [Fact]
    public void AteraPullException_ExposesFailureKind()
    {
        var exception = new AteraPullException(
            AteraPullFailureKind.RetryExhausted,
            "Atera pull failed after retry attempts were exhausted.");

        Assert.Equal(AteraPullFailureKind.RetryExhausted, exception.FailureKind);
    }
}
