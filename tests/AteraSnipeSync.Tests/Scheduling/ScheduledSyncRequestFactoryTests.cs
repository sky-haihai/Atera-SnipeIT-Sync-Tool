using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Tests.Scheduling;

/// <summary>
/// Verifies that scheduler-triggered runs cannot accidentally use manual preflight CSV behavior.
/// </summary>
public sealed class ScheduledSyncRequestFactoryTests
{
    [Fact]
    public void CreateScheduledRequest_ForcesScheduledTriggerAndDisablesManualPreflightCsv()
    {
        var request = ScheduledSyncRequestFactory.CreateScheduledRequest(CreateBaseRequest());

        Assert.Equal("scheduled", request.Sync.TriggeredBy);
        Assert.False(request.SnipeIt.ManualPreflightCsvEnabled);
        Assert.Null(request.SnipeIt.ManualPreflightCsvDirectory);
    }

    private static SyncRunRequest CreateBaseRequest()
    {
        return new SyncRunRequest
        {
            Atera = new AteraPullRequest { ApiKey = "atera-key" },
            Mapping = new MappingOptions
            {
                DefaultCompanyName = "Acme",
                DefaultManufacturerName = "Dell",
                DefaultModelName = "Latitude",
                DefaultCategoryName = "Laptop",
                DefaultStatusId = 2
            },
            SnipeIt = new SnipeImportOptions
            {
                BaseUrl = "https://snipe.example.com/api/v1",
                ApiToken = "snipe-token",
                DryRun = false,
                CreateMissingCompanies = true,
                CreateMissingModels = true,
                ManualPreflightCsvEnabled = true,
                ManualPreflightCsvDirectory = @"C:\ProgramData\AteraSnipeSync\Preflight\run-1"
            },
            Sync = new SyncRunOptions
            {
                DryRun = false,
                TriggeredBy = "manual"
            }
        };
    }
}
