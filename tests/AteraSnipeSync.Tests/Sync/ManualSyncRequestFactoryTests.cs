using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Tests.Sync;

/// <summary>
/// Verifies the two manual TrayApp actions produce distinct sync request shapes.
/// </summary>
public sealed class ManualSyncRequestFactoryTests
{
    [Fact]
    public void CreateSyncNowRequest_DisablesPreflightCsvAndRunsRealManualSync()
    {
        var request = ManualSyncRequestFactory.CreateSyncNowRequest(CreateBaseRequest());

        Assert.Equal("manual", request.Sync.TriggeredBy);
        Assert.False(request.Sync.DryRun);
        Assert.False(request.SnipeIt.DryRun);
        Assert.False(request.SnipeIt.ManualPreflightCsvEnabled);
        Assert.Null(request.SnipeIt.ManualPreflightCsvDirectory);
        Assert.Equal("https://snipe.example.com/api/v1", request.SnipeIt.BaseUrl);
        Assert.Equal("_snipeit_mac_address_5", request.SnipeIt.MacAddressCustomFieldDbColumnName);
    }

    [Fact]
    public void CreatePreviewChangesRequest_EnablesDryRunAndPreflightCsv()
    {
        var request = ManualSyncRequestFactory.CreatePreviewChangesRequest(
            CreateBaseRequest(),
            @" C:\ProgramData\AteraSnipeSync\Preflight\run-1 ");

        Assert.Equal("manual-preview", request.Sync.TriggeredBy);
        Assert.True(request.Sync.DryRun);
        Assert.True(request.SnipeIt.DryRun);
        Assert.True(request.SnipeIt.ManualPreflightCsvEnabled);
        Assert.Equal(@"C:\ProgramData\AteraSnipeSync\Preflight\run-1", request.SnipeIt.ManualPreflightCsvDirectory);
    }

    [Fact]
    public void CreatePreviewChangesRequest_ThrowsArgumentException_WhenPreflightDirectoryBlank()
    {
        Assert.Throws<ArgumentException>(
            () => ManualSyncRequestFactory.CreatePreviewChangesRequest(CreateBaseRequest(), " "));
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
                DryRun = true,
                CreateMissingCompanies = true,
                CreateMissingModels = true,
                MacAddressCustomFieldDbColumnName = "_snipeit_mac_address_5",
                NameMatchThreshold = 0.93,
                ManualPreflightCsvEnabled = true,
                ManualPreflightCsvDirectory = @"C:\ProgramData\AteraSnipeSync\Preflight\old-run"
            },
            Sync = new SyncRunOptions
            {
                DryRun = true,
                TriggeredBy = "old"
            }
        };
    }
}
