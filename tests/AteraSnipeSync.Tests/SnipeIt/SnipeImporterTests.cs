using System.Net;
using System.Text;
using System.Text.Json;
using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.SnipeIt;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.SnipeIt;

public sealed class SnipeImporterTests
{
    [Fact]
    public async Task ImportAsync_BlocksAsset_WhenMacAndSerialConflict()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler, Asset(500, "Wrong Serial Asset", "TAG-500", "OTHER-SERIAL", "00:11:22:33:44:55"));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(500));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(0, result.UpdatedAssets);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.ConflictingStrongIdentityMatch", failure.Code);
        Assert.DoesNotContain(handler.Requests, request => request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_UpdatesAssetBySerial_WhenMacDoesNotMatch()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler, Asset(501, "Serial Asset", "TAG-501", "SN-001"));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(501));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedAssets);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(handler.Requests, request => request.PathAndQuery.Contains("byserial", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, request => request.Method.Method == "PATCH" && request.PathAndQuery == "/api/v1/hardware/501");
    }

    [Fact]
    public async Task ImportAsync_MigratesLegacySerialAssetTagToAteraSourceIdTag()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler, Asset(501, "Legacy Asset", "SN-001", "SN-001"));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(501));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(assetTag: "ATERA-1001", serial: "SN-001", sourceId: "1001")),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedAssets);
        Assert.Empty(result.Failures);
        var updateRequest = Assert.Single(
            handler.Requests,
            request => request.Method.Method == "PATCH" && request.PathAndQuery == "/api/v1/hardware/501");
        Assert.Contains("\"asset_tag\":\"ATERA-1001\"", updateRequest.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_CreatesSharedSerialVirtualMachines_WithoutWritingSerial()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(701));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(702));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(
                    assetTag: "ATERA-69",
                    name: "SERVER-RDS01",
                    serial: "SN-SHARED",
                    macAddresses: ["00:15:5D:C3:A8:01"],
                    sourceId: "69",
                    serialIsReliableIdentity: false),
                CreateRecord(
                    assetTag: "ATERA-70",
                    name: "SERVER-VEEAM",
                    serial: "SN-SHARED",
                    macAddresses: ["00:15:5D:C3:A8:03"],
                    sourceId: "70",
                    serialIsReliableIdentity: false)),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(2, result.CreatedAssets);
        Assert.Empty(result.Failures);
        var createRequests = handler.Requests
            .Where(request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware")
            .ToList();
        Assert.Equal(2, createRequests.Count);
        Assert.All(createRequests, request => Assert.DoesNotContain("\"serial\"", request.Content, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportAsync_UpdatesSharedSerialVirtualMachine_ByExactAssetTag()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler, Asset(500, "Old VM Name", "ATERA-69", "OTHER-SHARED-SERIAL"));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(500));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                assetTag: "ATERA-69",
                name: "SERVER-RDS01",
                serial: "SN-SHARED",
                sourceId: "69",
                serialIsReliableIdentity: false)),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedAssets);
        Assert.Empty(result.Failures);
        var updateRequest = Assert.Single(
            handler.Requests,
            request => request.Method.Method == "PATCH" && request.PathAndQuery == "/api/v1/hardware/500");
        Assert.DoesNotContain("\"serial\"", updateRequest.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_BlocksSharedSerialVirtualMachine_WhenAssetTagAndMacMatchDifferentTargets()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(
            handler,
            Asset(500, "Asset Tag Target", "ATERA-69", "", "00:15:5D:C3:A8:99"),
            Asset(501, "MAC Target", "ATERA-501", "", "00:15:5D:C3:A8:01"));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                assetTag: "ATERA-69",
                name: "SERVER-RDS01",
                serial: "SN-SHARED",
                macAddresses: ["00:15:5D:C3:A8:01"],
                sourceId: "69",
                serialIsReliableIdentity: false)),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.FailedAssets);
        Assert.Contains(result.Failures, failure => failure.Code == "SnipeImport.ConflictingStrongIdentityMatch");
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_LoadsHardwareSnapshotPagesBeforeMatching()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, RowsWithTotal(2, Asset(500, "Other Asset", "TAG-500", "SN-000")));
        handler.QueueResponse(HttpStatusCode.OK, RowsWithTotal(2, Asset(501, "Serial Asset", "TAG-501", "SN-002")));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-002")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedAssets);
        Assert.Equal(2, CountRequests(handler, "/api/v1/hardware?"));
        Assert.Contains(handler.Requests, request => request.PathAndQuery.Contains("offset=0", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.PathAndQuery.Contains("offset=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportAsync_LoadsModelSnapshotPagesBeforePlanning()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, RowsWithTotal(2, Model(41, "Other Model", 20)));
        handler.QueueResponse(HttpStatusCode.OK, RowsWithTotal(2, Model(40, "Latitude", 20)));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Equal(0, result.CreatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.Equal(2, CountRequests(handler, "/api/v1/models?"));
        Assert.Contains(handler.Requests, request => request.PathAndQuery.Contains("limit=500", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.PathAndQuery.Contains("offset=0", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.PathAndQuery.Contains("offset=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportAsync_SharesModelSnapshotAcrossDifferentModelReferences()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(
            HttpStatusCode.OK,
            Rows(Model(40, "Latitude", 20), Model(41, "OptiPlex", 20)));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(assetTag: "TAG-001", name: "Device 1", modelName: "Latitude"),
                CreateRecord(assetTag: "TAG-002", name: "Device 2", serial: "SN-002", modelName: "OptiPlex")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Equal(0, result.CreatedModels);
        Assert.Equal(2, result.CreatedAssets);
        Assert.Equal(1, CountRequests(handler, "/api/v1/models?"));
    }

    [Fact]
    public async Task ImportAsync_BlocksOnlyAssetsReferencingAmbiguousModelKey()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(
            HttpStatusCode.OK,
            Rows(
                Model(40, "Latitude", 20),
                Model(41, "Dell Pro 14", 20),
                Model(42, "Dell Pro 14", 20)));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(assetTag: "TAG-001", name: "Device 1", modelName: "Latitude"),
                CreateRecord(assetTag: "TAG-002", name: "Device 2", serial: "SN-002", modelName: "Dell Pro 14")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedAssets);
        Assert.Equal(1, result.FailedAssets);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("TAG-002", failure.TargetName);
        Assert.Equal("SnipeImport.AmbiguousModelMatch", failure.Code);
        Assert.Contains("Dell Pro 14", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, CountRequests(handler, "/api/v1/hardware?"));
    }

    [Fact]
    public async Task ImportAsync_BlocksExistingCategoryModels_WhenModelSnapshotFails()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, """{ "status": "error", "messages": "Model lookup failed." }""");
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(assetTag: "TAG-001", name: "Device 1", modelName: "Latitude"),
                CreateRecord(assetTag: "TAG-002", name: "Device 2", serial: "SN-002", modelName: "OptiPlex")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Equal(2, result.FailedAssets);
        Assert.All(result.Failures, failure => Assert.Equal("SnipeImport.BusinessError", failure.Code));
        Assert.Equal(1, CountRequests(handler, "/api/v1/models?"));
        Assert.DoesNotContain(handler.Requests, request => request.PathAndQuery.StartsWith("/api/v1/hardware", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportAsync_UpdatesAssetByHighSimilarityName_WhenStrongKeysDoNotMatch()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler, Asset(502, "Device 1", "TAG-502", "DIFFERENT"));
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
    public async Task ImportAsync_CreatesAsset_WhenNameMatchesButReferencesDiffer()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(
            handler,
            Asset(
                502,
                "Device 1",
                "VUE-00502",
                "DIFFERENT",
                companyName: "Acme",
                categoryName: "Phone",
                modelName: "Desk Phone"));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(700));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedAssets);
        Assert.Equal(0, result.UpdatedAssets);
        Assert.Empty(result.Failures);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware");
        Assert.DoesNotContain(handler.Requests, request => request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_CreatesAsset_WhenNoMatchExists()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
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
    public async Task ImportAsync_IgnoresConfiguredMacForMatchingAndPayloadSelection()
    {
        const string ignoredMac = "00:09:0F:AA:00:01";
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler, Asset(500, "Other Device", "TAG-500", "OTHER-SERIAL", ignoredMac));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(700));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                serial: "SN-001",
                macAddresses: [ignoredMac, "00-11-22-33-44-55"])),
            CreateOptions(ignoredMacAddresses: [ignoredMac]),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedAssets);
        Assert.Equal(0, result.UpdatedAssets);
        Assert.Empty(result.Failures);
        var request = Assert.Single(
            handler.Requests,
            item => item.Method == HttpMethod.Post && item.PathAndQuery == "/api/v1/hardware");
        Assert.Contains("\"_snipeit_mac_address_5\":\"00:11:22:33:44:55\"", request.Content);
        Assert.DoesNotContain(ignoredMac, request.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportAsync_DoesNotBlockRecordsThatOnlyShareIgnoredMac()
    {
        const string ignoredMac = "00:09:0F:AA:00:01";
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(
                    assetTag: "TAG-001",
                    serial: "SN-001",
                    macAddresses: [ignoredMac, "00-11-22-33-44-55"]),
                CreateRecord(
                    assetTag: "TAG-002",
                    name: "Device 2",
                    serial: "SN-002",
                    macAddresses: [ignoredMac, "66-77-88-99-AA-BB"])),
            CreateOptions(
                dryRun: true,
                preflightDirectory: preflightDirectory,
                ignoredMacAddresses: [ignoredMac]),
            CancellationToken.None);

        Assert.Equal(2, result.CreatedAssets);
        Assert.Empty(result.Failures);
        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        Assert.Contains(ignoredMac, assetCsv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SnipeImport.DuplicateBatchIdentity", assetCsv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_RejectsInvalidIgnoredMacBeforeAnyRequest()
    {
        var handler = new StubHttpMessageHandler();
        var importer = CreateImporter(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(ignoredMacAddresses: ["not-a-mac"]),
            CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ImportAsync_DoesNotWrite_WhenDryRun()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        QueueHardwareSnapshot(handler);
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
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);
        var updates = new List<SyncProgressUpdate>();

        await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001", macAddresses: ["00-11-22-33-44-55"])),
            CreateOptions(dryRun: true),
            CancellationToken.None,
            new CapturingProgress(updates));

        Assert.Contains(updates, update => update.Stage == "SnipeImport" && update.Message.Contains("Planning Snipe-IT company references", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Stage == "SnipeImport" && update.Message.Contains("Loading Snipe-IT company snapshot", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Stage == "SnipeImport" && update.Message.Contains("Loading Snipe-IT model snapshot", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Stage == "SnipeImport" && update.Message.Contains("Loading Snipe-IT hardware snapshot", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Stage == "SnipeImport" && update.Message.Contains("Matching Snipe-IT asset", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Stage == "SnipeImport" && update.Message.Contains("Completed Snipe-IT dry-run planning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportAsync_ReportsNoOp_WhenNoReferenceCreatesAreNeeded()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(701));
        var importer = CreateImporter(handler);
        var updates = new List<SyncProgressUpdate>();

        await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None,
            new CapturingProgress(updates));

        Assert.Contains(updates, update =>
            update.Message == "No missing Snipe-IT reference records need to be created."
            && update.Current == 0
            && update.Total == 0);
        Assert.DoesNotContain(updates, update =>
            update.Message.StartsWith("Creating missing Snipe-IT reference records", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportAsync_CreatesMissingCompany_WhenAllowed()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 20)));
        QueueHardwareSnapshot(handler);
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
    public async Task ImportAsync_ReportsReferenceTarget_WhenReferenceCreationFails()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 20)));
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(
            HttpStatusCode.OK,
            """{ "status": "error", "messages": { "name": ["The name has already been taken."] } }""");
        var importer = CreateImporter(handler);
        var updates = new List<SyncProgressUpdate>();

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(),
                CreateRecord(assetTag: "TAG-002", name: "Device 2", serial: "SN-002")),
            CreateOptions(),
            CancellationToken.None,
            new CapturingProgress(updates));

        Assert.Equal(2, result.FailedAssets);
        Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, failure =>
        {
            Assert.Equal("SnipeImport.ValidationError", failure.Code);
            Assert.Contains("Reference creation failed before asset import", failure.Message);
            Assert.Contains("Create Company 'Acme' via POST /companies failed", failure.Message);
            Assert.Contains("name: The name has already been taken.", failure.Message);
        });
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware");
        Assert.Contains(updates, update =>
            update.Message.StartsWith("Failed Snipe-IT Company reference 'Acme' after ", StringComparison.Ordinal)
            && update.Message.Contains("(SnipeImport.ValidationError).", StringComparison.Ordinal)
            && update.Current == 1
            && update.Total == 1);
    }

    [Fact]
    public async Task ImportAsync_CreatesMissingModel_WhenAllowed()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        QueueHardwareSnapshot(handler);
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
    public async Task ImportAsync_PreviewBlocksServerDeviceWhenModelUsesServerCategory_AndWritesDeviceType()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Computer")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "PowerEdge R740", 30, "Server")));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                modelName: "PowerEdge R740",
                categoryName: "Computer",
                deviceType: "Server")),
            CreateOptions(dryRun: true, preflightDirectory: preflightDirectory),
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.ModelCategoryMismatch", failure.Code);
        Assert.Contains("category 'Server' (id 30)", failure.Message);
        Assert.Contains("device type 'Server'", failure.Message);
        Assert.Contains("category 'Computer' (id 20)", failure.Message);
        Assert.Equal(1, result.FailedAssets);
        Assert.Equal(0, result.CreatedModels);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Equal(0, CountRequests(handler, "/api/v1/hardware"));

        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        var lines = assetCsv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",DeviceType", lines[0]);
        Assert.Contains("Blocked,TAG-001,Device 1,SN-001,,Acme,PowerEdge R740,Computer,Dell", lines[1]);
        Assert.EndsWith(",Server", lines[1]);
    }

    [Fact]
    public async Task ImportAsync_PreviewBlocksComputerModelInServerCategory()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Computer")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "PowerEdge R740", 30, "Server")));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                modelName: "PowerEdge R740",
                categoryName: "Computer",
                deviceType: "Workstation")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.ModelCategoryMismatch", failure.Code);
        Assert.Contains("category 'Server' (id 30)", failure.Message);
        Assert.Contains("category 'Computer' (id 20)", failure.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ImportAsync_PreviewReusesModel_WhenNameAndCategoryMatch()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Server")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "PowerEdge R740", 30, "Server")));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                modelName: "PowerEdge R740",
                categoryName: "Server",
                deviceType: "Server")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.Equal(0, result.CreatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ImportAsync_ReusesUniqueModelNumber_WhenManufacturerAndCategoryMatch()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Server")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(
            40,
            "Dell Fourteen Generation Server",
            30,
            "Server",
            modelNumber: "PowerEdge R740",
            manufacturerId: 50,
            manufacturerName: "Dell")));
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(702));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                modelName: "PowerEdge R740",
                categoryName: "Server",
                deviceType: "Server")),
            CreateOptions(),
            CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.Equal(0, result.CreatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/models");
        var hardwareCreate = Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware");
        Assert.Contains("\"model_id\":40", hardwareCreate.Content);
    }

    [Fact]
    public async Task ImportAsync_ReusesUniqueModelNumber_WhenCategoryWillBeNormalizedToPlannedCategory()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(
            40,
            "Dell Latitude 5430",
            20,
            "Laptop",
            modelNumber: "Latitude 5430",
            manufacturerId: 50,
            manufacturerName: "Dell")));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                modelName: "Latitude 5430",
                categoryName: "Computer",
                deviceType: "PC")),
            CreateOptions(
                dryRun: true,
                normalizationTargetName: "Computer",
                modelCategoriesToNormalize: ["Laptop"]),
            CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.CreatedCategories);
        Assert.Equal(0, result.CreatedModels);
        Assert.Equal(1, result.UpdatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/models");
    }

    [Fact]
    public async Task ImportAsync_BlocksUniqueModelNumber_WhenManufacturerDoesNotMatch()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Server")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(
            40,
            "HPE Fourteen Generation Server",
            30,
            "Server",
            modelNumber: "PowerEdge R740",
            manufacturerId: 51,
            manufacturerName: "HPE")));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                modelName: "PowerEdge R740",
                categoryName: "Server",
                deviceType: "Server")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.ModelNameConflict", failure.Code);
        Assert.Contains("manufacturer 'Dell' (id 50)", failure.Message);
        Assert.Contains("manufacturer 'HPE' (id 51)", failure.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ImportAsync_BlocksUniqueModelNumber_WhenCategoryDoesNotMatch()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Server")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(
            40,
            "Dell Fourteen Generation Server",
            20,
            "Computer",
            modelNumber: "PowerEdge R740",
            manufacturerId: 50,
            manufacturerName: "Dell")));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                modelName: "PowerEdge R740",
                categoryName: "Server",
                deviceType: "Server")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.ModelNameConflict", failure.Code);
        Assert.Contains("category 'Server' (id 30)", failure.Message);
        Assert.Contains("category 'Computer' (id 20)", failure.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ImportAsync_BlocksModelNumber_WhenMultipleModelsMatch()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Server")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(
            Model(
                40,
                "Dell Fourteen Generation Server",
                30,
                "Server",
                modelNumber: "PowerEdge R740",
                manufacturerId: 50,
                manufacturerName: "Dell"),
            Model(
                41,
                "Legacy PowerEdge R740",
                30,
                "Server",
                modelNumber: "PowerEdge R740",
                manufacturerId: 51,
                manufacturerName: "Other")));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                modelName: "PowerEdge R740",
                categoryName: "Server",
                deviceType: "Server")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.ModelNameConflict", failure.Code);
        Assert.Contains("multiple Snipe-IT models", failure.Message);
        Assert.Contains("1 additional model(s)", failure.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ImportAsync_BlocksBeforeCreatingMissingCategory_WhenSameNameExistsElsewhere()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "PowerEdge R740", 20, "Computer")));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(
                modelName: "PowerEdge R740",
                categoryName: "Server",
                deviceType: "Server")),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal("SnipeImport.ModelCategoryMismatch", Assert.Single(result.Failures).Code);
        Assert.Equal(0, result.CreatedCategories);
        Assert.Equal(0, result.CreatedModels);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ImportAsync_PreviewBlocksConflictingSameBatchModelCreates()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Server")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Computer")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(50, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(
                    assetTag: "TAG-SERVER",
                    modelName: "Shared Model",
                    categoryName: "Server",
                    deviceType: "Server"),
                CreateRecord(
                    assetTag: "TAG-COMPUTER",
                    name: "Device 2",
                    serial: "SN-002",
                    modelName: "Shared Model",
                    categoryName: "Computer",
                    deviceType: "Workstation")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Equal(2, result.FailedAssets);
        Assert.All(result.Failures, failure => Assert.Equal("SnipeImport.ModelBatchConflict", failure.Code));
        Assert.Equal(0, result.CreatedModels);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ImportAsync_CreatesMissingCategory_WhenCategoryMissing()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        QueueHardwareSnapshot(handler);
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
        Assert.Single(handler.Requests, request =>
            request.Method == HttpMethod.Get
            && request.PathAndQuery.StartsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase));
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
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(10));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(20));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(40));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(704));
        var importer = CreateImporter(handler);
        var updates = new List<SyncProgressUpdate>();

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None,
            new CapturingProgress(updates));

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
        Assert.Contains(updates, update =>
            update.Message == "Preparing Snipe-IT reference records before asset writes: total=3; companies=1; categories=1; models added=1; models updated=0."
            && update.Current == 0
            && update.Total == 3);
        Assert.Contains(updates, update =>
            update.Message == "Creating Snipe-IT Company reference 'Acme'."
            && update.Current == 1
            && update.Total == 3);
        Assert.Contains(updates, update =>
            update.Message.StartsWith("Created Snipe-IT Company reference 'Acme' in ", StringComparison.Ordinal)
            && update.Message.EndsWith("s.", StringComparison.Ordinal)
            && update.Current == 1
            && update.Total == 3);
        Assert.Contains(updates, update =>
            update.Message == "Creating Snipe-IT Category reference 'Laptop'."
            && update.Current == 2
            && update.Total == 3);
        Assert.Contains(updates, update =>
            update.Message.StartsWith("Created Snipe-IT Category reference 'Laptop' in ", StringComparison.Ordinal)
            && update.Current == 2
            && update.Total == 3);
        Assert.Contains(updates, update =>
            update.Message == "Creating Snipe-IT Model reference 'Latitude'."
            && update.Current == 3
            && update.Total == 3);
        Assert.Contains(updates, update =>
            update.Message.StartsWith("Created Snipe-IT Model reference 'Latitude' in ", StringComparison.Ordinal)
            && update.Current == 3
            && update.Total == 3);
        Assert.Contains(updates, update =>
            update.Message.StartsWith("Prepared all Snipe-IT reference records: total=3; elapsed=", StringComparison.Ordinal)
            && update.Current == 3
            && update.Total == 3);
    }

    [Fact]
    public async Task ImportAsync_AddsFirstAddedNoteToEveryCreatedObject()
    {
        var createdAt = new DateTimeOffset(2026, 7, 17, 18, 0, 0, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(10));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(20));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(40));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(704));
        var importer = CreateImporter(handler, createdAt);

        await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        const string expectedNote = "First added by Atera-SnipeIT Sync Tool at 2026-07-17T18:00:00Z";
        foreach (var endpoint in new[] { "/api/v1/companies", "/api/v1/categories", "/api/v1/models", "/api/v1/hardware" })
        {
            var request = Assert.Single(
                handler.Requests,
                candidate => candidate.Method == HttpMethod.Post && candidate.PathAndQuery == endpoint);
            Assert.Contains(expectedNote, request.Content);
        }
    }

    [Fact]
    public async Task ImportAsync_FailsRecord_WhenMacMatchIsAmbiguous()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(
            handler,
            Asset(500, "Device 1", "TAG-500", "SN-A", "00:11:22:33:44:55"),
            Asset(501, "Device 1 Clone", "TAG-501", "SN-B", "00:11:22:33:44:55"));
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
    public async Task ImportAsync_FailsRecord_WhenSerialMatchIsAmbiguous()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(
            handler,
            Asset(500, "Other Asset 1", "TAG-500", "SN-001"),
            Asset(501, "Other Asset 2", "TAG-501", "SN-001"));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(serial: "SN-001")),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(1, result.FailedAssets);
        Assert.Contains(result.Failures, failure => failure.Code == "SnipeImport.AmbiguousSerialMatch");
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_TreatsStatusErrorBodyAsFailure_WhenHttpStatusIsOk()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
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
    public async Task ImportAsync_ReportsFieldValidationDetails_WhenMessagesIsObject()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(
            HttpStatusCode.OK,
            """
            {
              "status": "error",
              "messages": {
                "asset_tag": ["The asset tag has already been taken."],
                "status_id": ["The selected status id is invalid."]
              }
            }
            """);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.ValidationError", failure.Code);
        Assert.Contains("Create Asset 'TAG-001' via POST /hardware failed", failure.Message);
        Assert.Contains("asset_tag: The asset tag has already been taken.", failure.Message);
        Assert.Contains("status_id: The selected status id is invalid.", failure.Message);
        Assert.DoesNotContain("\"messages\"", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_ClassifiesAuthenticationFailure_WithRequestContext()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(
            HttpStatusCode.Unauthorized,
            """{ "status": "error", "messages": "An Error has occured! Unauthenticated." }""");
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.AuthenticationFailed", failure.Code);
        Assert.Contains("Load company snapshot via GET /companies failed", failure.Message);
        Assert.Contains("HTTP 401", failure.Message);
        Assert.Contains("Unauthenticated", failure.Message);
    }

    [Fact]
    public async Task ImportAsync_ClassifiesServerFailure_WithResponseDetail()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(
            HttpStatusCode.ServiceUnavailable,
            """{ "status": "error", "messages": "Maintenance in progress." }""");
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.ServerError", failure.Code);
        Assert.Contains("Load hardware snapshot page 1 via GET /hardware?", failure.Message);
        Assert.Contains("HTTP 503", failure.Message);
        Assert.Contains("Detail: Maintenance in progress.", failure.Message);
    }

    [Fact]
    public async Task ImportAsync_AddsAutoSyncedNoteToCreateAndUpdatePayloads()
    {
        var syncedAt = new DateTimeOffset(2026, 6, 17, 21, 30, 45, TimeSpan.Zero);
        var createHandler = new StubHttpMessageHandler();
        QueueDependencyLookups(createHandler);
        QueueHardwareSnapshot(createHandler);
        createHandler.QueueResponse(HttpStatusCode.OK, SuccessPayload(800));
        var createImporter = CreateImporter(createHandler, syncedAt);

        await createImporter.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        var createRequest = Assert.Single(createHandler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware");
        Assert.Contains("Imported from Atera.", createRequest.Content);
        Assert.Contains("First added by Atera-SnipeIT Sync Tool at 2026-06-17T21:30:45Z", createRequest.Content);
        Assert.Contains("Auto Synced from Atera at 2026-06-17T21:30:45Z", createRequest.Content);

        const string originalFirstAddedNote = "First added by Atera-SnipeIT Sync Tool at 2026-05-01T10:20:30Z";
        var updateHandler = new StubHttpMessageHandler();
        QueueDependencyLookups(updateHandler);
        QueueHardwareSnapshot(
            updateHandler,
            Asset(
                801,
                "Device 1",
                "TAG-801",
                "SN-001",
                notes: $"Operator note.\n{originalFirstAddedNote}\nAuto Synced from Atera at 2026-05-02T10:20:30Z"));
        updateHandler.QueueResponse(HttpStatusCode.OK, SuccessPayload(801));
        var updateImporter = CreateImporter(updateHandler, syncedAt);

        await updateImporter.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        var updateRequest = Assert.Single(updateHandler.Requests, request => request.Method.Method == "PATCH" && request.PathAndQuery == "/api/v1/hardware/801");
        Assert.Contains("Imported from Atera.", updateRequest.Content);
        Assert.Contains(originalFirstAddedNote, updateRequest.Content);
        Assert.DoesNotContain("First added by Atera-SnipeIT Sync Tool at 2026-06-17T21:30:45Z", updateRequest.Content);
        Assert.Contains("Auto Synced from Atera at 2026-06-17T21:30:45Z", updateRequest.Content);
    }

    [Fact]
    public async Task ImportAsync_DoesNotBackfillFirstAddedNote_WhenUpdatingExistingAssetWithoutMarker()
    {
        var syncedAt = new DateTimeOffset(2026, 6, 17, 21, 30, 45, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(
            handler,
            Asset(801, "Device 1", "TAG-801", "SN-001", notes: "Existing operator-managed asset."));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(801));
        var importer = CreateImporter(handler, syncedAt);

        await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(),
            CancellationToken.None);

        var updateRequest = Assert.Single(
            handler.Requests,
            request => request.Method.Method == "PATCH" && request.PathAndQuery == "/api/v1/hardware/801");
        Assert.DoesNotContain("First added by Atera-SnipeIT Sync Tool at", updateRequest.Content);
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
        QueueHardwareSnapshot(handler);
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
        Assert.Contains("Add,TAG-001,Device 1,SN-001,,Acme,Latitude,Laptop,Dell", assetCsv);
        Assert.Contains("Add,Acme", companyCsv);
        Assert.Contains("Operation,Name,CategoryType", categoryCsv);
        Assert.Contains(
            "Add,Latitude,,,,Laptop,20,Dell,30,,,,,Create",
            modelCsv);
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
        QueueHardwareSnapshot(handler);
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
        Assert.Contains(
            "Add,Latitude,,,,Laptop,,Dell,30,,,,,Create",
            modelCsv);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_PreviewPlansFieldsetUpdatesForAllDefaultCategoryModelsOnly()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Computer")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(
            HttpStatusCode.OK,
            Rows(
                Model(40, "Latitude", 20, "Computer"),
                Model(41, "OptiPlex", 20, "Computer"),
                Model(90, "Office Printer", 70, "Printer")));
        handler.QueueResponse(
            HttpStatusCode.OK,
            Rows(Fieldset(7, "Assets with MAC Address", "_snipeit_mac_address_5")));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(categoryName: "Computer")),
            CreateOptions(
                dryRun: true,
                preflightDirectory: preflightDirectory,
                macAddressFieldsetName: "Assets with MAC Address",
                normalizationTargetName: "Computer",
                modelCategoriesToNormalize: ["Server", "Laptop", "Desktop"]),
            CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(2, result.UpdatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.Empty(result.Failures);
        Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Get
                && request.PathAndQuery.StartsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase));
        Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Get
                && request.PathAndQuery.Equals("/api/v1/fieldsets", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");

        var modelCsv = await File.ReadAllTextAsync(
            Path.Combine(preflightDirectory, "snipeit-models-plan.csv"));
        Assert.Contains("Modify,Latitude,40,Computer,20,Computer,20", modelCsv);
        Assert.Contains("Modify,OptiPlex,41,Computer,20,Computer,20", modelCsv);
        Assert.Contains("Assets with MAC Address,7,Fieldset", modelCsv);
        Assert.DoesNotContain("Office Printer", modelCsv);
    }

    [Fact]
    public async Task ImportAsync_CombinesCategoryNormalizationAndFieldsetUpdateBeforeAssetWrite()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Computer")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 65, "Server")));
        handler.QueueResponse(
            HttpStatusCode.OK,
            Rows(Fieldset(7, "Assets with MAC Address", "_snipeit_mac_address_5")));
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(40));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(901));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(categoryName: "Computer")),
            CreateOptions(
                macAddressFieldsetName: "Assets with MAC Address",
                normalizationTargetName: "Computer",
                modelCategoriesToNormalize: ["Server", "Laptop", "Desktop"]),
            CancellationToken.None);

        Assert.Equal(1, result.UpdatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.Empty(result.Failures);
        var modelPatch = Assert.Single(
            handler.Requests,
            request => request.Method.Method == "PATCH"
                && request.PathAndQuery == "/api/v1/models/40");
        Assert.Contains("\"category_id\":20", modelPatch.Content);
        Assert.Contains("\"fieldset_id\":7", modelPatch.Content);
        Assert.Equal(
            1,
            handler.Requests.Count(request =>
                request.Method.Method == "PATCH"
                && request.PathAndQuery.StartsWith("/api/v1/models/", StringComparison.OrdinalIgnoreCase)));
        Assert.True(
            FindRequestIndex(handler, HttpMethod.Patch, "/api/v1/models/40")
            < FindRequestIndex(handler, HttpMethod.Post, "/api/v1/hardware"));
    }

    [Fact]
    public async Task ImportAsync_AssignsFieldsetWhenCreatingModelInDefaultCategory()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Computer")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(
            HttpStatusCode.OK,
            Rows(Fieldset(7, "Assets with MAC Address", "_snipeit_mac_address_5")));
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(40));
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(902));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(categoryName: "Computer")),
            CreateOptions(
                macAddressFieldsetName: "Assets with MAC Address",
                normalizationTargetName: "Computer",
                modelCategoriesToNormalize: ["Server", "Laptop", "Desktop"]),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedModels);
        Assert.Equal(0, result.UpdatedModels);
        Assert.Equal(1, result.CreatedAssets);
        Assert.Empty(result.Failures);
        var modelPost = Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Post
                && request.PathAndQuery == "/api/v1/models");
        Assert.Contains("\"category_id\":20", modelPost.Content);
        Assert.Contains("\"fieldset_id\":7", modelPost.Content);
        Assert.True(
            FindRequestIndex(handler, HttpMethod.Post, "/api/v1/models")
            < FindRequestIndex(handler, HttpMethod.Post, "/api/v1/hardware"));
    }

    [Fact]
    public async Task ImportAsync_BlocksBeforeMutation_WhenConfiguredFieldsetDoesNotExist()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Computer")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 20, "Computer")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(categoryName: "Computer")),
            CreateOptions(
                macAddressFieldsetName: "Assets with MAC Address",
                normalizationTargetName: "Computer",
                modelCategoriesToNormalize: ["Server", "Laptop", "Desktop"]),
            CancellationToken.None);

        Assert.Equal(1, result.FailedAssets);
        Assert.Contains(result.Failures, failure => failure.Code == "SnipeImport.MacFieldsetMissing");
        Assert.DoesNotContain(
            handler.Requests,
            request => request.PathAndQuery.StartsWith("/api/v1/hardware", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_DoesNotResolveMacFieldset_WhenOnlyOtherCategoryModelsExist()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(70, "Printer")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(90, "Office Printer", 70, "Printer")));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(modelName: "Office Printer", categoryName: "Printer")),
            CreateOptions(
                dryRun: true,
                macAddressFieldsetName: "A Fieldset That Does Not Exist",
                normalizationTargetName: "Computer",
                modelCategoriesToNormalize: ["Server", "Laptop", "Desktop"]),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedAssets);
        Assert.Equal(0, result.UpdatedModels);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.PathAndQuery.Equals("/api/v1/fieldsets", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_DoesNotWrite_WhenManualPreflightCsvWriteFails()
    {
        var blockedPath = Path.Combine(Path.GetTempPath(), "AteraSnipeSync.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath)!);
        await File.WriteAllTextAsync(blockedPath, "not a directory");
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
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
        QueueHardwareSnapshot(handler);
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
    public async Task ImportAsync_PreviewUsesCompanyAliasMappedFromAteraCompanyName()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Moore Equine Veterinary Centre")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 20)));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);
        var mapper = new InventoryMapper();
        var batch = mapper.Map(
            CreateAteraPullResult(CreateAteraAgent(customerName: "Moore Equine Veterinary Centre\u00A0\u2013\u00A0AR")),
            CreateMappingOptions(new Dictionary<string, string>
            {
                ["Moore Equine Veterinary Centre - AR"] = "Moore Equine Veterinary Centre"
            }));

        var result = await importer.ImportAsync(
            batch,
            CreateOptions(dryRun: true, preflightDirectory: preflightDirectory),
            CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(0, result.CreatedCompanies);
        var companyCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-companies-plan.csv"));
        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        Assert.DoesNotContain("Add,", companyCsv, StringComparison.Ordinal);
        Assert.Contains("Add,ATERA-1001,Device 1,SN-001,00:11:22:33:44:55,Moore Equine Veterinary Centre,Latitude,Laptop,Dell", assetCsv);
        var companyRequest = Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Get
                && request.PathAndQuery.StartsWith("/api/v1/companies", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/api/v1/companies", companyRequest.PathAndQuery);
    }

    [Fact]
    public async Task ImportAsync_PreviewPrefersDirectCompanyMatchOverConfiguredAlias()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Existing Company")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 20)));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);
        var mapper = new InventoryMapper();
        var batch = mapper.Map(
            CreateAteraPullResult(CreateAteraAgent(customerName: "Existing Company")),
            CreateMappingOptions(new Dictionary<string, string>
            {
                ["Existing Company"] = "Canonical Alias"
            }));

        var result = await importer.ImportAsync(
            batch,
            CreateOptions(dryRun: true, preflightDirectory: preflightDirectory),
            CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.Equal(0, result.CreatedCompanies);
        var companyCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-companies-plan.csv"));
        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        Assert.DoesNotContain("Add,", companyCsv, StringComparison.Ordinal);
        Assert.Contains(",Existing Company,Latitude,Laptop,Dell,", assetCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("Canonical Alias", assetCsv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_PreviewCreatesAliasTarget_WhenSourceAndAliasAreMissing()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 20)));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);
        var mapper = new InventoryMapper();
        var batch = mapper.Map(
            CreateAteraPullResult(CreateAteraAgent(customerName: "Missing Source Company")),
            CreateMappingOptions(new Dictionary<string, string>
            {
                ["Missing Source Company"] = "Canonical Alias"
            }));

        var result = await importer.ImportAsync(
            batch,
            CreateOptions(dryRun: true, preflightDirectory: preflightDirectory),
            CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.CreatedCompanies);
        var companyCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-companies-plan.csv"));
        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        Assert.Contains("Add,Canonical Alias", companyCsv, StringComparison.Ordinal);
        Assert.Contains(",Canonical Alias,Latitude,Laptop,Dell,", assetCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("Missing Source Company", assetCsv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_PreviewUsesManufacturerAliasForUniqueModelNumberReuse()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(
            40,
            "Dell Latitude 5430",
            20,
            "Laptop",
            modelNumber: "Latitude 5430",
            manufacturerId: 30,
            manufacturerName: "Dell")));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);
        var mapper = new InventoryMapper();
        var batch = mapper.Map(
            CreateAteraPullResult(CreateAteraAgent(
                customerName: "Acme",
                manufacturer: "Dell Inc.",
                model: "Latitude 5430")),
            CreateMappingOptions(
                manufacturerAliases: new Dictionary<string, string>
                {
                    ["Dell Inc."] = "Dell"
                }));

        var result = await importer.ImportAsync(
            batch,
            CreateOptions(dryRun: true, preflightDirectory: preflightDirectory),
            CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.CreatedModels);
        var modelCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-models-plan.csv"));
        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        Assert.DoesNotContain("Add,", modelCsv, StringComparison.Ordinal);
        Assert.Contains(",Latitude 5430,Laptop,Dell,", assetCsv, StringComparison.Ordinal);
        Assert.DoesNotContain("Dell Inc.", assetCsv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_PreviewPrefersDirectManufacturerMatchOverConfiguredAlias()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(31, "Dell Inc.")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(
            40,
            "Latitude",
            20,
            "Laptop",
            manufacturerId: 31,
            manufacturerName: "Dell Inc.")));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);
        var mapper = new InventoryMapper();
        var batch = mapper.Map(
            CreateAteraPullResult(CreateAteraAgent(
                customerName: "Acme",
                manufacturer: "Dell Inc.")),
            CreateMappingOptions(
                manufacturerAliases: new Dictionary<string, string>
                {
                    ["Dell Inc."] = "Dell"
                }));

        var result = await importer.ImportAsync(
            batch,
            CreateOptions(dryRun: true, preflightDirectory: preflightDirectory),
            CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.Equal(0, result.CreatedModels);
        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        Assert.Contains(",Latitude,Laptop,Dell Inc.,", assetCsv, StringComparison.Ordinal);
        Assert.DoesNotContain(",Latitude,Laptop,Dell,", assetCsv, StringComparison.Ordinal);
        var manufacturerRequest = Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Get
                && request.PathAndQuery.StartsWith("/api/v1/manufacturers", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("name=Dell%20Inc.", manufacturerRequest.PathAndQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportAsync_PreviewWarnsOnceForAliasTarget_WhenSourceAndAliasManufacturersAreMissing()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);
        var mapper = new InventoryMapper();
        var batch = mapper.Map(
            CreateAteraPullResult(CreateAteraAgent(
                customerName: "Acme",
                manufacturer: "Dell Inc.")),
            CreateMappingOptions(
                manufacturerAliases: new Dictionary<string, string>
                {
                    ["Dell Inc."] = "Dell"
                }));

        var result = await importer.ImportAsync(
            batch,
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Empty(result.Failures);
        var warning = Assert.Single(result.Warnings, item => item.Code == "SnipeImport.ManufacturerMissing");
        Assert.Contains("Manufacturer 'Dell' does not exist", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Dell Inc.", warning.Message, StringComparison.Ordinal);
        var manufacturerRequests = handler.Requests
            .Where(request => request.Method == HttpMethod.Get
                && request.PathAndQuery.StartsWith("/api/v1/manufacturers", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Collection(
            manufacturerRequests,
            request => Assert.Contains("name=Dell%20Inc.", request.PathAndQuery, StringComparison.OrdinalIgnoreCase),
            request => Assert.Contains("name=Dell", request.PathAndQuery, StringComparison.OrdinalIgnoreCase));
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
        Assert.Contains("Blocked,TAG-001,Device 1,SN-001,,Acme,Latitude,Laptop,Dell,,,,,,SnipeImport.CompanyMissing", assetCsv);
        Assert.Contains("Company 'Acme' does not exist and creation is disabled.", assetCsv);
        Assert.DoesNotContain(handler.Requests, request => request.PathAndQuery.StartsWith("/api/v1/hardware", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_WritesMacAndConflictDetailsToPreflightCsv_WhenBatchIdentityCollides()
    {
        var preflightDirectory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(
                    assetTag: "TAG-SHARED",
                    name: "Device A",
                    serial: "SN-SHARED",
                    macAddresses: ["00-11-22-33-44-55", "AA-BB-CC-DD-EE-FF"],
                    sourceId: "agent-1"),
                CreateRecord(
                    assetTag: "TAG-SHARED",
                    name: "Device B",
                    serial: "SN-SHARED",
                    macAddresses: ["00:11:22:33:44:55"],
                    sourceId: "agent-2"),
                CreateRecord(
                    assetTag: "TAG-UNIQUE",
                    name: "Device C",
                    serial: "SN-UNIQUE",
                    macAddresses: ["0011.2233.4455"],
                    sourceId: "agent-3")),
            CreateOptions(dryRun: true, preflightDirectory: preflightDirectory),
            CancellationToken.None);

        Assert.Equal(3, result.FailedAssets);
        Assert.All(result.Failures, failure => Assert.Equal("SnipeImport.DuplicateBatchIdentity", failure.Code));
        var assetCsv = await File.ReadAllTextAsync(Path.Combine(preflightDirectory, "snipeit-assets-plan.csv"));
        Assert.Contains(
            "Operation,AssetTag,Name,Serial,MacAddresses,CompanyName,ModelName,CategoryName,ManufacturerName,ExistingAssetId,ExistingAssetTag,ConflictingFields,ConflictingValue,ConflictingAssets,FailureCode,FailureMessage,DeviceType",
            assetCsv,
            StringComparison.Ordinal);
        Assert.Contains("00:11:22:33:44:55; AA:BB:CC:DD:EE:FF", assetCsv, StringComparison.Ordinal);
        Assert.Contains("asset tag; MAC address; serial", assetCsv, StringComparison.Ordinal);
        Assert.Contains(
            "asset tag=TAG-SHARED; MAC address=00:11:22:33:44:55; serial=SN-SHARED",
            assetCsv,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssetTag=TAG-SHARED | Name=Device B | SourceId=agent-2; AssetTag=TAG-UNIQUE | Name=Device C | SourceId=agent-3",
            assetCsv,
            StringComparison.Ordinal);
        Assert.Contains("Blocked,TAG-UNIQUE,Device C,SN-UNIQUE,00:11:22:33:44:55", assetCsv, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post || request.Method.Method == "PATCH");
    }

    [Fact]
    public async Task ImportAsync_ReusesReferenceLookups_ForRepeatedReferenceNames()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
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
        Assert.Equal(1, CountRequests(handler, "/api/v1/hardware?"));
    }

    [Fact]
    public async Task ImportAsync_LoadsCompaniesOnce_ForDifferentCompanyNames()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(
            HttpStatusCode.OK,
            Rows(Entity(10, "Acme"), Entity(11, "Contoso")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 20)));
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(assetTag: "TAG-001", serial: "SN-001", companyName: "Acme"),
                CreateRecord(assetTag: "TAG-002", serial: "SN-002", companyName: "Contoso")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Equal(2, result.CreatedAssets);
        var companyRequest = Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Get
                && request.PathAndQuery.StartsWith("/api/v1/companies", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/api/v1/companies", companyRequest.PathAndQuery);
    }

    [Fact]
    public async Task ImportAsync_MatchesHtmlEscapedCompanyName_FromCompanySnapshot()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(
            HttpStatusCode.OK,
            Rows(Entity(10, "Ktunaxa Kinbasket Child &amp; Family Services")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 20)));
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(701));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord(companyName: "Ktunaxa Kinbasket Child & Family Services")),
            CreateOptions(),
            CancellationToken.None);

        Assert.Equal(0, result.CreatedCompanies);
        Assert.Equal(1, result.CreatedAssets);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/companies");
        var hardwareCreate = Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware");
        Assert.Contains("\"company_id\":10", hardwareCreate.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_BlocksCompanyPlanning_WhenCompanySnapshotCountIsIncomplete()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, RowsWithTotal(2, Entity(10, "Acme")));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.IncompleteCompanySnapshot", failure.Code);
        Assert.Contains("returned 1 company row(s) but reported total 2", failure.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ImportAsync_BlocksWrites_WhenHardwareEnvelopeHasNoRows()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, "{}");
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(CreateBatch(CreateRecord()), CreateOptions(), CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport.MalformedResponse", failure.Code);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task ImportAsync_BlocksWrites_WhenHardwareRowHasNoId()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        handler.QueueResponse(HttpStatusCode.OK, Rows("""{ "name": "malformed" }"""));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(CreateBatch(CreateRecord()), CreateOptions(), CancellationToken.None);

        Assert.Equal("SnipeImport.MalformedResponse", Assert.Single(result.Failures).Code);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task ImportAsync_DoesNotRecordExecutedAction_WhenCreateFails()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.InternalServerError, """{ "messages": "temporary" }""");
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(maxReadRetryAttempts: 3),
            CancellationToken.None);

        Assert.Empty(result.Actions);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware"));
        Assert.Equal("SnipeImport.ServerError", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public async Task ImportAsync_FailsCreate_WhenSuccessPayloadHasNoId()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, """{ "status": "success" }""");
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(CreateBatch(CreateRecord()), CreateOptions(), CancellationToken.None);

        Assert.Empty(result.Actions);
        Assert.Equal("SnipeImport.MissingResponseId", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public async Task ImportAsync_RetriesReadOnlyLookup_ButNeverReplaysMutation()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable, """{ "messages": "retry" }""");
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable, """{ "messages": "do not replay" }""");
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(CreateRecord()),
            CreateOptions(maxReadRetryAttempts: 2),
            CancellationToken.None);

        Assert.Equal(2, CountRequests(handler, "/api/v1/companies"));
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware"));
        Assert.Equal("SnipeImport.ServerError", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public async Task ImportAsync_ReturnsPartialAudit_WhenCancelledAfterSuccessfulWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload(901));
        handler.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Post && request.PathAndQuery == "/api/v1/hardware")
            {
                cancellation.Cancel();
            }
        };
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(assetTag: "TAG-001", name: "Device 1", serial: "SN-001"),
                CreateRecord(assetTag: "TAG-002", name: "Device 2", serial: "SN-002")),
            CreateOptions(),
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.CreatedAssets);
        Assert.True(Assert.Single(result.Actions).WasExecuted);
        Assert.Contains(result.Failures, failure => failure.Code == "SnipeImport.CancelledAfterPartialExecution");
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task ImportAsync_BlocksAllRecords_WhenTheyReserveSameTargetAsset()
    {
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler, Asset(500, "Device 1", "TARGET-500", serial: ""));
        var importer = CreateImporter(handler);

        var result = await importer.ImportAsync(
            CreateBatch(
                CreateRecord(assetTag: "TAG-001", name: "Device 1", serial: "SN-001"),
                CreateRecord(assetTag: "TAG-002", name: "Device 1", serial: "SN-002")),
            CreateOptions(dryRun: true),
            CancellationToken.None);

        Assert.Equal(2, result.FailedAssets);
        Assert.All(result.Failures, failure => Assert.Equal("SnipeImport.DuplicateTargetReservation", failure.Code));
        Assert.Empty(result.Actions);
    }

    [Fact]
    public async Task ImportAsync_NeutralizesSpreadsheetFormula_InPreflightCsv()
    {
        var directory = CreateTempDirectoryPath();
        var handler = new StubHttpMessageHandler();
        QueueDependencyLookups(handler);
        QueueHardwareSnapshot(handler);
        var importer = CreateImporter(handler);

        await importer.ImportAsync(
            CreateBatch(CreateRecord(name: "=cmd|' /C calc'!A0")),
            CreateOptions(dryRun: true, preflightDirectory: directory),
            CancellationToken.None);

        var csv = await File.ReadAllTextAsync(Path.Combine(directory, "snipeit-assets-plan.csv"));
        Assert.Contains("'=cmd|' /C calc'!A0", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_RejectsStandardPayloadField_AsMacCustomField()
    {
        var options = new SnipeImportOptions
        {
            BaseUrl = "https://snipe.example.com/api/v1",
            ApiToken = "secret-token",
            DryRun = true,
            CreateMissingCompanies = true,
            CreateMissingModels = true,
            MacAddressCustomFieldDbColumnName = "asset_tag"
        };
        var importer = CreateImporter(new StubHttpMessageHandler());

        await Assert.ThrowsAsync<ArgumentException>(
            () => importer.ImportAsync(CreateBatch(CreateRecord()), options, CancellationToken.None));
    }

    private static void QueueDependencyLookups(StubHttpMessageHandler handler)
    {
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(10, "Acme")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(20, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Entity(30, "Dell")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "Latitude", 20)));
    }

    private static void QueueHardwareSnapshot(StubHttpMessageHandler handler, params string[] assets)
    {
        handler.QueueResponse(HttpStatusCode.OK, Rows(assets));
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
        bool createMissingModels = true,
        int maxReadRetryAttempts = 0,
        IReadOnlyList<string>? ignoredMacAddresses = null,
        string? macAddressFieldsetName = null,
        string? normalizationTargetName = null,
        IReadOnlyList<string>? modelCategoriesToNormalize = null)
    {
        return new SnipeImportOptions
        {
            BaseUrl = "https://snipe.example.com/api/v1",
            ApiToken = "secret-token",
            DryRun = dryRun,
            CreateMissingCompanies = createMissingCompanies,
            CreateMissingModels = createMissingModels,
            MacAddressCustomFieldDbColumnName = "_snipeit_mac_address_5",
            MacAddressFieldsetName = macAddressFieldsetName,
            ModelCategoryNormalizationTargetName = normalizationTargetName,
            ModelCategoriesToNormalize = modelCategoriesToNormalize ?? [],
            IgnoredMacAddresses = ignoredMacAddresses ?? [],
            NameMatchThreshold = 0.92,
            ManualPreflightCsvEnabled = preflightDirectory is not null,
            ManualPreflightCsvDirectory = preflightDirectory,
            MaxReadRetryAttempts = maxReadRetryAttempts,
            RetryBaseDelay = TimeSpan.Zero
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

    private static MappingOptions CreateMappingOptions(
        IReadOnlyDictionary<string, string>? companyAliases = null,
        IReadOnlyDictionary<string, string>? manufacturerAliases = null)
    {
        return new MappingOptions
        {
            DefaultCompanyName = "Default Company",
            DefaultManufacturerName = "Dell",
            DefaultModelName = "Latitude",
            DefaultCategoryName = "Laptop",
            DefaultStatusId = 2,
            CompanyAliases = companyAliases ?? new Dictionary<string, string>(),
            ManufacturerAliases = manufacturerAliases ?? new Dictionary<string, string>(),
            IgnoredDeviceTypes = []
        };
    }

    private static AteraPullResult CreateAteraPullResult(params AgentInfo[] agents)
    {
        return new AteraPullResult
        {
            Agents = agents,
            Summary = new AteraSnipeSync.Core.Atera.PullSummary
            {
                AgentCount = agents.Length,
                PulledAt = DateTimeOffset.UtcNow
            },
            Warnings = []
        };
    }

    private static AgentInfo CreateAteraAgent(
        string customerName,
        string manufacturer = "Dell",
        string model = "Latitude")
    {
        return new AgentInfo
        {
            AgentId = "1001",
            Name = "Device 1",
            RawJson = "{}",
            MacAddresses = ["00-11-22-33-44-55"],
            VendorSerialNumber = "SN-001",
            CustomerId = "C-001",
            CustomerName = customerName,
            Vendor = manufacturer,
            VendorBrandModel = model
        };
    }

    private static SnipeAssetImportRecord CreateRecord(
        string assetTag = "TAG-001",
        string name = "Device 1",
        string? serial = "SN-001",
        IReadOnlyList<string>? macAddresses = null,
        string modelName = "Latitude",
        string companyName = "Acme",
        string? sourceId = null,
        bool serialIsReliableIdentity = true,
        string categoryName = "Laptop",
        string? deviceType = "Workstation",
        string manufacturerName = "Dell")
    {
        return new SnipeAssetImportRecord
        {
            AssetTag = assetTag,
            Name = name,
            Serial = serial,
            SerialIsReliableIdentity = serialIsReliableIdentity,
            MacAddresses = macAddresses ?? [],
            CompanyName = companyName,
            ManufacturerName = manufacturerName,
            ModelName = modelName,
            CategoryName = categoryName,
            DeviceType = deviceType,
            StatusId = 2,
            Notes = "Imported from Atera.",
            SourceSystem = "Atera",
            SourceId = sourceId ?? assetTag
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

    private static string RowsWithTotal(int total, params string[] rows)
    {
        return $$"""{ "total": {{total}}, "rows": [{{string.Join(",", rows)}}] }""";
    }

    private static string Entity(int id, string name)
    {
        return $$"""{ "id": {{id}}, "name": "{{name}}" }""";
    }

    private static string Model(
        int id,
        string name,
        int categoryId,
        string categoryName = "Laptop",
        string? modelNumber = null,
        int? fieldsetId = null,
        string? fieldsetName = null,
        int? manufacturerId = null,
        string? manufacturerName = null)
    {
        var modelNumberProperty = modelNumber is null ? string.Empty : $", \"model_number\": \"{modelNumber}\"";
        var fieldsetProperty = fieldsetId is null
            ? string.Empty
            : $", \"fieldset\": {{ \"id\": {fieldsetId.Value}, \"name\": \"{fieldsetName ?? $"Fieldset {fieldsetId.Value}"}\" }}";
        var manufacturerProperty = manufacturerId is null
            ? string.Empty
            : $", \"manufacturer\": {{ \"id\": {manufacturerId.Value}, \"name\": \"{manufacturerName ?? $"Manufacturer {manufacturerId.Value}"}\" }}";
        return $$"""{ "id": {{id}}, "name": "{{name}}"{{modelNumberProperty}}, "category": { "id": {{categoryId}}, "name": "{{categoryName}}" }{{manufacturerProperty}}{{fieldsetProperty}} }""";
    }

    private static string Fieldset(int id, string name, params string[] dbColumns)
    {
        var fields = string.Join(
            ",",
            dbColumns.Select(dbColumn => $$"""{ "db_column_name": "{{dbColumn}}" }"""));
        return $$"""{ "id": {{id}}, "name": "{{name}}", "fields": { "rows": [{{fields}}] } }""";
    }

    private static string Asset(
        int id,
        string name,
        string assetTag,
        string serial,
        string? macAddress = null,
        string companyName = "Acme",
        string categoryName = "Laptop",
        string modelName = "Latitude",
        string? notes = null)
    {
        var customFields = macAddress is null
            ? string.Empty
            : $$""", "custom_fields": { "MAC Address": { "field": "_snipeit_mac_address_5", "value": "{{macAddress}}" } }""";
        var notesProperty = notes is null
            ? string.Empty
            : $""", "notes": {JsonSerializer.Serialize(notes)}""";
        return $$"""{ "id": {{id}}, "name": "{{name}}", "asset_tag": "{{assetTag}}", "serial": "{{serial}}", "company": { "id": 10, "name": "{{companyName}}" }, "category": { "id": 20, "name": "{{categoryName}}" }, "model": { "id": 40, "name": "{{modelName}}" }{{notesProperty}}{{customFields}} }""";
    }

    private static string SuccessPayload(int id)
    {
        return $$"""{ "status": "success", "payload": { "id": {{id}} } }""";
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
