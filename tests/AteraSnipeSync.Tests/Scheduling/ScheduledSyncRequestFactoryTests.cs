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
    public void CreateScheduledRequest_ForcesRealScheduledTriggerAndDisablesManualPreflightCsv()
    {
        var request = ScheduledSyncRequestFactory.CreateScheduledRequest(CreateBaseRequest());

        Assert.Equal("scheduled", request.Sync.TriggeredBy);
        Assert.False(request.Sync.DryRun);
        Assert.False(request.SnipeIt.DryRun);
        Assert.False(request.SnipeIt.ManualPreflightCsvEnabled);
        Assert.Null(request.SnipeIt.ManualPreflightCsvDirectory);
        Assert.Equal("Assets with MAC Address", request.SnipeIt.MacAddressFieldsetName);
        Assert.Equal("Computer", request.SnipeIt.ModelCategoryNormalizationTargetName);
        Assert.Equal(["Server", "Laptop", "Desktop"], request.SnipeIt.ModelCategoriesToNormalize);
        Assert.Equal(["00:09:0F:AA:00:01"], request.SnipeIt.IgnoredMacAddresses);
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
                DefaultCategoryName = "Computer",
                DefaultStatusId = 2
            },
            SnipeIt = new SnipeImportOptions
            {
                BaseUrl = "https://snipe.example.com/api/v1",
                ApiToken = "snipe-token",
                DryRun = true,
                CreateMissingCompanies = true,
                CreateMissingModels = true,
                MacAddressFieldsetName = "Assets with MAC Address",
                ModelCategoryNormalizationTargetName = "Computer",
                ModelCategoriesToNormalize = ["Server", "Laptop", "Desktop"],
                IgnoredMacAddresses = ["00:09:0F:AA:00:01"],
                ManualPreflightCsvEnabled = true,
                ManualPreflightCsvDirectory = @"C:\ProgramData\AteraSnipeSync\Preflight\run-1"
            },
            Sync = new SyncRunOptions
            {
                DryRun = true,
                TriggeredBy = "manual"
            }
        };
    }
}
