using System.Net;
using System.Text;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.SnipeIt;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.SnipeIt;

public sealed class SnipeImporterTests
{
    [Fact]
    public async Task ImportAsync_UpdatesAssetByMacBeforeSerial_WhenMacMatches()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, Rows(Asset(500, "Wrong Serial Asset", "TAG-500", "OTHER-SERIAL")));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(500));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedAssets);
        Assert.Empty(result.Failures);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.PathAndQuery.Contains("filter=", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request => request.PathAndQuery.Contains("byserial", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, request => request.Method.Method == "PATCH" && request.PathAndQuery == "/api/v1/hardware/500");
    }

    [Fact]
    public async Task ImportAsync_UpdatesAssetBySerial_WhenMacDoesNotMatch()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Asset(501, "Serial Asset", "TAG-501", "SN-001"));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(501));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedAssets);
        Assert.Empty(result.Failures);
        Assert.Contains(handler.Requests, request => request.PathAndQuery == "/api/v1/hardware/byserial/SN-001");
        Assert.Contains(handler.Requests, request => request.Method.Method == "PATCH" && request.PathAndQuery == "/api/v1/hardware/501");
    }

    [Fact]
    public async Task ImportAsync_UpdatesAssetByHighSimilarityName_WhenStrongKeysDoNotMatch()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Asset(502, "Device 1", "TAG-502", "DIFFERENT")));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(502));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedAssets);
        Assert.Empty(result.Failures);
        Assert.Contains(handler.Requests, request => request.Method.Method == "PATCH" && request.PathAndQuery == "/api/v1/hardware/502");
    }

    [Fact]
    public async Task ImportAsync_CreatesAsset_WhenNoMatchExists()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(700));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedAssets);
        Assert.Empty(result.Failures);
        var createRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware");
        Assert.Contains("\"asset_tag\":\"TAG-001\"", createRequest.Content);
        Assert.Contains("\"_snipeit_mac_address_5\":\"00:11:22:33:44:55\"", createRequest.Content);
    }

    [Fact]
    public async Task ImportAsync_DoesNotWrite_WhenDryRun()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        var importer = CreateImporter(handler);

        var options = CreateOptions(dryRun: true);
        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            options,
            CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.CreatedCompanies);
        Assert.Equal(1, result.CreatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.All(result.Actions, action => Assert.False(action.WasExecuted));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_ReportsProgressDuringPlanning()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        var importer = CreateImporter(handler);
        var updates = new List<SyncProgressUpdate>();

        await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(dryRun: true),
            CancellationToken.None,
            new CapturingProgress(updates));

        Assert.Contains(updates, update => update.Stage == "SnipeImport" && update.Message.Contains("Planning Snipe-IT company references", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Stage == "SnipeImport" && update.Message.Contains("Matching Snipe-IT asset", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Stage == "SnipeImport" && update.Message.Contains("Completed Snipe-IT dry-run planning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportAsync_CreatesMissingCompany_WhenAllowed()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(40, "Latitude")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(10));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(701));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedCompanies);
        Assert.Equal(1, result.CreatedAssets);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/companies");
    }

    [Fact]
    public async Task ImportAsync_CreatesMissingModel_WhenAllowed()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(40));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(702));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/models");
    }

    [Fact]
    public async Task ImportAsync_CreatesMissingCategory_WhenCategoryMissing()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(20));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(40));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(703));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedCategories);
        Assert.Equal(1, result.CreatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.Empty(result.Failures);
        var categoryCreate = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/categories");
        Assert.Contains("\"name\":\"Laptop\"", categoryCreate.Content);
        Assert.Contains("\"category_type\":\"asset\"", categoryCreate.Content);
    }

    [Fact]
    public async Task ImportAsync_CreatesAllMissingReferencesBeforeHardwareWrites()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(10));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(20));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(40));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(704));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedCompanies);
        Assert.Equal(1, result.CreatedCategories);
        Assert.Equal(1, result.CreatedModels);
        Assert.Equal(1, result.CreatedAssets);
        var companyPostIndex = FindRequestIndex(handler, HttpMethod.Post, "/api/v1/companies");
        var categoryPostIndex = FindRequestIndex(handler, HttpMethod.Post, "/api/v1/categories");
        var modelPostIndex = FindRequestIndex(handler, HttpMethod.Post, "/api/v1/models");
        var hardwarePostIndex = FindRequestIndex(handler, HttpMethod.Post, "/api/v1/hardware");
        Assert.True(companyPostIndex < categoryPostIndex);
        Assert.True(categoryPostIndex < modelPostIndex);
        Assert.True(modelPostIndex < hardwarePostIndex);
    }

    [Fact]
    public async Task ImportAsync_FailsRecord_WhenMacMatchIsAmbiguous()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(
            HttpStatusCode.OK,
            Rows(
                Asset(500, "Device 1", "TAG-500", "SN-A"),
                Asset(501, "Device 1 Clone", "TAG-501", "SN-B")));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.FailedAssets);
        Assert.Contains(result.Failures, failure => failure.Code == "SnipeImport.AmbiguousMacMatch");
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_TreatsStatusErrorBodyAsFailure_WhenHttpStatusIsOk()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, """{ "status": "error", "messages": "Validation failed." }""");
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.FailedAssets);
        Assert.Equal(0, result.CreatedAssets);
        Assert.Contains(result.Failures, failure => failure.Code == "SnipeImport.BusinessError");
    }

    [Fact]
    public async Task ImportAsync_AddsAutoSyncedNoteToCreateAndUpdatePayloads()
    {
        var syncedAt = new DateTimeOffset(2026, 6, 17, 21, 30, 45, TimeSpan.Zero);
        var createHandler = new StubHttpMessageHandler();
        QueueDependencyLookups(createHandler);
        createHandler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        createHandler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        createHandler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        createHandler.QueueResponse(HttpStatusCode.OK, SuccessPayload(800));
        var createImporter = CreateImporter(createHandler, syncedAt);

        await createImporter.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        var createRequest = Assert.Single(createHandler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware");
        Assert.Contains("Imported from Atera.", createRequest.Content);
        Assert.Contains("Auto Synced from Atera at 2026-06-17T21:30:45Z", createRequest.Content);

        var updateHandler = new StubHttpMessageHandler();
        QueueDependencyLookups(updateHandler);
        updateHandler.QueueResponse(HttpStatusCode.OK, Rows(Asset(801, "Device 1", "TAG-801", "SN-001")));
        updateHandler.QueueResponse(HttpStatusCode.OK, SuccessPayload(801));
        var updateImporter = CreateImporter(updateHandler, syncedAt);

        await updateImporter.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        var updateRequest = Assert.Single(updateHandler.Requests, request => request.Method.Method == "PATCH" && request.PathAndQuery == "/api/v1/hardware/801");
        Assert.Contains("Imported from Atera.", updateRequest.Content);
        Assert.Contains("Auto Synced from Atera at 2026-06-17T21:30:45Z", updateRequest.Content);
    }

    [Fact]
    public async Task ImportAsync_WritesManualPreflightCsvBeforeAnyPostOrPatch_WhenEnabled()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Post || request.Method.Method == "PATCH")
            {
                Assert.True(File.Exists(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv")));
                Assert.True(File.Exists(Path.Combine(preflightDirectory, "snipeit-companies-plan.csv")));
                Assert.True(File.Exists(Path.Combine(preflightDirectory, "snipeit-categories-plan.csv")));
                Assert.True(File.Exists(Path.Combine(preflightDirectory, "snipeit-models-plan.csv")));
            }
        };
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(10));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(40));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(900));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(preflightDirectory: preflightDirectory),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedCompanies);
        Assert.Equal(1, result.CreatedModels);
        Assert.Equal(1, result.CreatedAssets);
        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        var companyCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-companies-plan.csv"));
        var categoryCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-categories-plan.csv"));
        var modelCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-models-plan.csv"));
        Assert.Contains("Operation", assetCsv);
        Assert.Contains("Add,TAG-001,Device 1,SN-001,Acme,Latitude,Laptop,Dell", assetCsv);
        Assert.Contains("Add,Acme", companyCsv);
        Assert.Contains("Operation,Name,CategoryType", categoryCsv);
        Assert.Contains("Add,Latitude,Laptop,20,Dell,30", modelCsv);
    }

    [Fact]
    public async Task ImportAsync_WritesMissingCategoryPreflightCsv_WhenCategoryWillBeCreated()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(dryRun: true, preflightDirectory: preflightDirectory),
            CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.CreatedCategories);
        Assert.Equal(1, result.CreatedModels);
        Assert.Equal(1, result.CreatedAssets);
        var categoryCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-categories-plan.csv"));
        var modelCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-models-plan.csv"));
        Assert.Contains("Add,Laptop,asset", categoryCsv);
        Assert.Contains("Add,Latitude,Laptop,,Dell,30", modelCsv);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_DoesNotWrite_WhenManualPreflightCsvWriteFails()
    {
        var blockedPath = Path.Combine(Path.GetTempPath(), "AteraSnipeSync.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath)!);
        await File.WriteAllTextAsync(blockedPath, "not a directory");
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(preflightDirectory: blockedPath),
            CancellationToken.None);

        Assert.Contains(result.Failures, failure => failure.Code == "SnipeImport.PreflightCsvWriteFailed");
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_WritesManualPreflightCsvAndDoesNotMutate_WhenDryRun()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(dryRun: true, preflightDirectory: preflightDirectory),
            CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.CreatedAssets);
        Assert.True(File.Exists(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv")));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_WritesBlockedAssetRowsToPreflightCsv_WhenPlanningFails()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(
                dryRun: true,
                preflightDirectory: preflightDirectory,
                createMissingCompanies: false),
            CancellationToken.None);

        Assert.Equal(1, result.FailedAssets);
        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        Assert.Contains("FailureCode", assetCsv);
        Assert.Contains("Blocked,TAG-001,Device 1,SN-001,Acme,Latitude,Laptop,Dell,,,SnipeImport.CompanyMissing", assetCsv);
        Assert.Contains("Company 'Acme' does not exist and creation is disabled.", assetCsv);
        Assert.DoesNotContain(handler.Requests, request => request.PathAndQuery.StartsWith("/api/v1/hardware", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_ReusesReferenceLookups_ForRepeatedReferenceNames()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, BusinessNotFound());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(assetTag: "TAG-001", name: "Device 1", serial: "SN-001", macAddresses: ["00-11-22-33-44-55"]),
                CreateRecord(assetTag: "TAG-002", name: "Device 2", serial: "SN-002", macAddresses: ["66-77-88-99-AA-BB"])),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Equal(2, result.CreatedAssets);
        Assert.Equal(1, CountRequests(handler, "/api/v1/companies"));
        Assert.Equal(1, CountRequests(handler, "/api/v1/categories"));
        Assert.Equal(1, CountRequests(handler, "/api/v1/manufacturers"));
        Assert.Equal(1, CountRequests(handler, "/api/v1/models"));
    }

    private static void QueueDependencyLookups(StubHttpMessageHandler handler)
    {
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(40, "Latitude")));
    }

    private static SnipeImporter CreateImporter(StubHttpMessageHandler handler)
    {
        return new SnipeImporter(new HttpClient(handler), NullLogger<SnipeImporter>.Instance);
    }

    private static SnipeImporter CreateImporter(StubHttpMessageHandler handler, DateTimeOffset utcNow)
    {
        return new SnipeImporter(
            new HttpClient(handler),
            NullLogger<SnipeImporter>.Instance,
            new FixedTimeProvider(utcNow));
    }

    private static int CountRequests(StubHttpMessageHandler handler, string pathPrefix)
    {
        return handler.Requests.Count(request => request.PathAndQuery.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static int FindRequestIndex(
        StubHttpMessageHandler handler,
        HttpMethod method,
        string pathAndQuery)
    {
        var index = handler.Requests.FindIndex(request =>
            request.Method == method
            && string.Equals(request.PathAndQuery, pathAndQuery, StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, $"Expected {method} {pathAndQuery} request.");
        return index;
    }

    private static SnipeImportOptions CreateOptions(
        bool dryRun = false,
        string? preflightDirectory = null,
        bool createMissingCompanies = true,
        bool createMissingModels = true)
    {
        return new SnipeImportOptions
        {
            BaseUrl = "https://snipe.example.com/api/v1",
            ApiToken = "secret-token",
            DryRun = dryRun,
            CreateMissingCompanies = createMissingCompanies,
            CreateMissingModels = createMissingModels,
            MacAddressCustomFieldDbColumnName = "_snipeit_mac_address_5",
            NameMatchThreshold = 0.92,
            ManualPreflightCsvEnabled = preflightDirectory is not null,
            ManualPreflightCsvDirectory = preflightDirectory
        };
    }

    private static SnipeImportBatch CreateBatch(params SnipeAssetImportRecord[] records)
    {
        return new SnipeImportBatch
        {
            Assets = records,
            Summary = new AteraSnipeSync.Core.Mapping.MappingSummary
            {
                SourceAgentCount = records.Length,
                MappedAssetCount = records.Length
            },
            Warnings = []
        };
    }

    private static SnipeAssetImportRecord CreateRecord(
        string assetTag = "TAG-001",
        string name = "Device 1",
        string? serial = "SN-001",
        IReadOnlyList<string>? macAddresses = null)
    {
        return new SnipeAssetImportRecord
        {
            AssetTag = assetTag,
            Name = name,
            Serial = serial,
            MacAddresses = macAddresses ?? ["00-11-22-33-44-55"],
            CompanyName = "Acme",
            ManufacturerName = "Dell",
            ModelName = "Latitude",
            CategoryName = "Laptop",
            StatusId = 2,
            Notes = "Imported from Atera.",
            SourceSystem = "Atera",
            SourceId = "1001"
        };
    }

    private static string EmptyRows()
    {
        return """{ "total": 0, "rows": [] }""";
    }

    private static string Rows(params string[] rows)
    {
        return $$"""{ "total": {{rows.Length}}, "rows": [{{string.Join(",", rows)}}] }""";
    }

    private static string Entity(int id, string name)
    {
        return $$"""{ "id": {{id}}, "name": "{{name}}" }""";
    }

    private static string Asset(int id, string name, string assetTag, string serial)
    {
        return $$"""{ "id": {{id}}, "name": "{{name}}", "asset_tag": "{{assetTag}}", "serial": "{{serial}}" }""";
    }

    private static string SuccessPayload(int id)
    {
        return $$"""{ "status": "success", "payload": { "id": {{id}} } }""";
    }

    private static string BusinessNotFound()
    {
        return """{ "status": "error", "messages": "Asset does not exist." }""";
    }

    private static string CreateTempDirectoryPath()
    {
        return Path.Combine(Path.GetTempPath(), "AteraSnipeSync.Tests", Guid.NewGuid().ToString("N"));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = [];

        public List<CapturedRequest> Requests { get; } = [];
        public Action<CapturedRequest>? OnRequest { get; set; }

        public void QueueResponse(HttpStatusCode statusCode, string content)
        {
            _responses.Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                content));
            OnRequest?.Invoke(Requests[^1]);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP response is available.");
            }

            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string PathAndQuery,
        string Content);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class CapturingProgress(List<SyncProgressUpdate> updates) : IProgress<SyncProgressUpdate>
    {
        public void Report(SyncProgressUpdate value)
        {
            updates.Add(value);
        }
    }
}
