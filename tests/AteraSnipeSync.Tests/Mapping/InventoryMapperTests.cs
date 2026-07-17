using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Mapping;

namespace AteraSnipeSync.Tests.Mapping;

public sealed class InventoryMapperTests
{
    [Fact]
    public void Map_UsesAteraSourceIdAssetTag_WhenSerialExists()
    {
        var source = CreatePullResult(CreateAgent(serialNumber: " SN-001 "));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        var asset = Assert.Single(result.Assets);
        Assert.Equal("ATERA-1001", asset.AssetTag);
        Assert.Equal("SN-001", asset.Serial);
        Assert.Equal("Device 1", asset.Name);
        Assert.Equal(["00-11-22-33-44-55"], asset.MacAddresses);
        Assert.Equal("Atera", asset.SourceSystem);
        Assert.Equal("1001", asset.SourceId);
        Assert.Contains("Atera Agent ID: 1001", asset.Notes);
    }

    [Fact]
    public void Map_UsesAteraSourceIdAssetTagAndAddsWarning_WhenSerialMissing()
    {
        var source = CreatePullResult(CreateAgent(agentId: " 1001 ", serialNumber: null));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        var asset = Assert.Single(result.Assets);
        Assert.Equal("ATERA-1001", asset.AssetTag);
        Assert.Null(asset.Serial);
        Assert.Equal("1001", asset.SourceId);
        Assert.Contains(result.Warnings, warning => warning.Code == "MissingSerialNumber");
    }

    [Fact]
    public void Map_UsesSerialBackedSourceIdInAteraAssetTag_WhenAgentIdMissing()
    {
        var source = CreatePullResult(CreateAgent(agentId: " ", serialNumber: " SN-001 "));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        var asset = Assert.Single(result.Assets);
        Assert.Equal("SN-001", asset.SourceId);
        Assert.Equal("ATERA-SN-001", asset.AssetTag);
        Assert.Equal("SN-001", asset.Serial);
    }

    [Fact]
    public void Map_UsesDefaultCompanyAndAddsWarning_WhenCustomerNameMissing()
    {
        var options = CreateDefaultOptions();
        var source = CreatePullResult(CreateAgent(customerName: " "));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, options);

        var asset = Assert.Single(result.Assets);
        Assert.Equal(options.DefaultCompanyName, asset.CompanyName);
        Assert.Contains(result.Warnings, warning => warning.Code == "MissingCompany");
    }

    [Fact]
    public void Map_UsesCompanyAlias_WhenCustomerNameMatchesAlias()
    {
        var options = CreateDefaultOptions(
            new Dictionary<string, string>
            {
                [" moore equine veterinary centre - ar "] = "Moore Equine Veterinary Centre"
            });
        var source = CreatePullResult(CreateAgent(customerName: "Moore Equine Veterinary Centre - AR"));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, options);

        var asset = Assert.Single(result.Assets);
        Assert.Equal("Moore Equine Veterinary Centre - AR", asset.CompanyName);
        Assert.Equal("Moore Equine Veterinary Centre", asset.CompanyAliasName);
    }

    [Fact]
    public void Map_UsesCompanyAlias_WhenCustomerNameHasEquivalentWhitespaceAndDash()
    {
        var options = CreateDefaultOptions(
            new Dictionary<string, string>
            {
                ["Moore Equine Veterinary Centre - AR"] = "Moore Equine Veterinary Centre"
            });
        var source = CreatePullResult(CreateAgent(customerName: "Moore Equine Veterinary Centre\u00A0\u2013\u00A0AR"));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, options);

        var asset = Assert.Single(result.Assets);
        Assert.Equal("Moore Equine Veterinary Centre – AR", asset.CompanyName);
        Assert.Equal("Moore Equine Veterinary Centre", asset.CompanyAliasName);
    }

    [Fact]
    public void Map_UsesManufacturerAlias_WhenManufacturerMatchesAlias()
    {
        var options = CreateDefaultOptions(
            manufacturerAliases: new Dictionary<string, string>
            {
                ["Dell Inc."] = "Dell"
            });
        var source = CreatePullResult(CreateAgent(manufacturer: " Dell Inc. "));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, options);

        var asset = Assert.Single(result.Assets);
        Assert.Equal("Dell Inc.", asset.ManufacturerName);
        Assert.Equal("Dell", asset.ManufacturerAliasName);
    }

    [Fact]
    public void Map_UsesManufacturerAlias_WhenManufacturerHasEquivalentWhitespaceAndDash()
    {
        var options = CreateDefaultOptions(
            manufacturerAliases: new Dictionary<string, string>
            {
                ["Hewlett Packard - Enterprise"] = "HPE"
            });
        var source = CreatePullResult(CreateAgent(manufacturer: "Hewlett Packard – Enterprise"));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, options);

        var asset = Assert.Single(result.Assets);
        Assert.Equal("Hewlett\u00A0Packard \u2013 Enterprise", asset.ManufacturerName);
        Assert.Equal("HPE", asset.ManufacturerAliasName);
    }

    [Fact]
    public void Map_UsesDefaultManufacturerAndAddsWarning_WhenManufacturerMissing()
    {
        var options = CreateDefaultOptions();
        var source = CreatePullResult(CreateAgent(manufacturer: null));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, options);

        var asset = Assert.Single(result.Assets);
        Assert.Equal(options.DefaultManufacturerName, asset.ManufacturerName);
        Assert.Contains(result.Warnings, warning => warning.Code == "MissingManufacturer");
    }

    [Fact]
    public void Map_UsesDefaultModelAndAddsWarning_WhenModelMissing()
    {
        var options = CreateDefaultOptions();
        var source = CreatePullResult(CreateAgent(model: null));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, options);

        var asset = Assert.Single(result.Assets);
        Assert.Equal(options.DefaultModelName, asset.ModelName);
        Assert.Contains(result.Warnings, warning => warning.Code == "MissingModel");
    }

    [Fact]
    public void Map_UsesDefaultCategoryAndPreservesDeviceType_WhenDeviceTypeIsServer()
    {
        var source = CreatePullResult(CreateAgent(deviceType: " sErVeR "));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        var asset = Assert.Single(result.Assets);
        Assert.Equal("Default Category", asset.CategoryName);
        Assert.Equal("sErVeR", asset.DeviceType);
    }

    [Fact]
    public void Map_UsesDefaultCategory_ForNonServerAndMissingDeviceTypes()
    {
        var source = CreatePullResult(
            CreateAgent(agentId: "1001", deviceType: " Workstation "),
            CreateAgent(agentId: "1002", name: "Device 2", serialNumber: "SN-002", deviceType: null));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        Assert.All(result.Assets, asset => Assert.Equal("Default Category", asset.CategoryName));
        Assert.Equal("Workstation", result.Assets[0].DeviceType);
        Assert.Null(result.Assets[1].DeviceType);
    }

    [Fact]
    public void Map_SkipsAgentAndAddsWarning_WhenSerialAndAgentIdMissing()
    {
        var source = CreatePullResult(CreateAgent(agentId: " ", serialNumber: null));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        Assert.Empty(result.Assets);
        Assert.Contains(result.Warnings, warning => warning.Code == "MissingAgentIdentity");
    }

    [Fact]
    public void Map_SkipsAgentAndAddsWarning_WhenDeviceTypeIgnored()
    {
        var source = CreatePullResult(
            CreateAgent(agentId: "1001", deviceType: " Server "),
            CreateAgent(agentId: "1002", name: "Device 2", serialNumber: "SN-002", deviceType: "PC"));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions(ignoredDeviceTypes: ["server"]));

        var asset = Assert.Single(result.Assets);
        Assert.Equal("ATERA-1002", asset.AssetTag);
        Assert.Equal(2, result.Summary.SourceAgentCount);
        Assert.Equal(1, result.Summary.MappedAssetCount);
        Assert.Contains(
            result.Warnings,
            warning => warning.Code == "IgnoredDeviceType"
                && warning.Message.Contains("Server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Map_PopulatesSummaryCounts()
    {
        var source = CreatePullResult(
            CreateAgent(agentId: "1001"),
            CreateAgent(agentId: "1002"),
            CreateAgent(agentId: " ", serialNumber: null));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        Assert.Equal(3, result.Summary.SourceAgentCount);
        Assert.Equal(2, result.Summary.MappedAssetCount);
    }

    [Fact]
    public void Map_ThrowsArgumentNullException_WhenSourceIsNull()
    {
        var mapper = new InventoryMapper();

        Assert.Throws<ArgumentNullException>(() => mapper.Map(null!, CreateDefaultOptions()));
    }

    [Fact]
    public void Map_KeepsIgnoredMacsButDoesNotReportDuplicateMacWarning()
    {
        const string ignoredMac = "00:09:0F:AA:00:01";
        var source = CreatePullResult(
            CreateAgent(
                agentId: "1001",
                serialNumber: "SN-001",
                macAddresses: [ignoredMac, "00:11:22:33:44:55"]),
            CreateAgent(
                agentId: "1002",
                serialNumber: "SN-002",
                macAddresses: [ignoredMac, "66:77:88:99:AA:BB"]));
        var mapper = new InventoryMapper();

        var result = mapper.Map(
            source,
            CreateDefaultOptions(ignoredMacAddresses: [ignoredMac]));

        Assert.Equal(2, result.Assets.Count);
        Assert.All(result.Assets, asset => Assert.Contains(ignoredMac, asset.MacAddresses));
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "DuplicateMacAddress");
    }

    [Fact]
    public void Map_UsesAteraIdentityAndKeepsSerialForAudit_WhenVirtualMachinesShareSerial()
    {
        const string sharedSerial = "4429-2131-5910-6266-0689-5985-27";
        var source = CreatePullResult(
            CreateAgent(
                agentId: "69",
                name: "SERVER-RDS01",
                serialNumber: sharedSerial,
                model: "Virtual Machine",
                deviceType: "Server",
                macAddresses: ["00:15:5D:C3:A8:01"]),
            CreateAgent(
                agentId: "70",
                name: "SERVER-VEEAM",
                serialNumber: sharedSerial,
                model: " virtual machine ",
                macAddresses: ["00:15:5D:C3:A8:03"]));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        Assert.Equal(["ATERA-69", "ATERA-70"], result.Assets.Select(asset => asset.AssetTag));
        Assert.All(result.Assets, asset =>
        {
            Assert.Equal(sharedSerial, asset.Serial);
            Assert.False(asset.SerialIsReliableIdentity);
        });
        Assert.Equal("Server", result.Assets[0].DeviceType);
        Assert.Contains(result.Warnings, warning => warning.Code == "SharedVirtualMachineSerial");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code is "DuplicateSerial" or "DuplicateAssetTag");
    }

    [Fact]
    public void Map_KeepsDuplicateSerialProtection_WhenPhysicalMachinesShareSerial()
    {
        var source = CreatePullResult(
            CreateAgent(agentId: "1001", serialNumber: "SN-SHARED", macAddresses: ["00:11:22:33:44:55"]),
            CreateAgent(agentId: "1002", serialNumber: "SN-SHARED", macAddresses: ["66:77:88:99:AA:BB"]));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        Assert.All(result.Assets, asset => Assert.True(asset.SerialIsReliableIdentity));
        Assert.Equal(["ATERA-1001", "ATERA-1002"], result.Assets.Select(asset => asset.AssetTag));
        Assert.Contains(result.Warnings, warning => warning.Code == "DuplicateSerial");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "DuplicateAssetTag");
    }

    [Fact]
    public void Map_OnlyAppliesSharedSerialFallbackToVirtualMachine_InMixedModelGroup()
    {
        var source = CreatePullResult(
            CreateAgent(
                agentId: "1001",
                name: "VM-01",
                serialNumber: "SN-SHARED",
                model: "Virtual Machine",
                macAddresses: ["00:11:22:33:44:55"]),
            CreateAgent(
                agentId: "1002",
                name: "PHYSICAL-01",
                serialNumber: "SN-SHARED",
                model: "PowerEdge R740",
                macAddresses: ["66:77:88:99:AA:BB"]));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        var virtualMachine = Assert.Single(result.Assets, asset => asset.SourceId == "1001");
        Assert.Equal("ATERA-1001", virtualMachine.AssetTag);
        Assert.False(virtualMachine.SerialIsReliableIdentity);
        var physicalMachine = Assert.Single(result.Assets, asset => asset.SourceId == "1002");
        Assert.Equal("ATERA-1002", physicalMachine.AssetTag);
        Assert.True(physicalMachine.SerialIsReliableIdentity);
    }

    [Fact]
    public void Map_ThrowsArgumentNullException_WhenOptionsIsNull()
    {
        var mapper = new InventoryMapper();

        Assert.Throws<ArgumentNullException>(() => mapper.Map(CreatePullResult(CreateAgent()), null!));
    }

    private static MappingOptions CreateDefaultOptions(
        IReadOnlyDictionary<string, string>? companyAliases = null,
        IReadOnlyDictionary<string, string>? manufacturerAliases = null,
        IReadOnlyList<string>? ignoredDeviceTypes = null,
        IReadOnlyList<string>? ignoredMacAddresses = null)
    {
        return new MappingOptions
        {
            DefaultCompanyName = "Default Company",
            DefaultManufacturerName = "Default Manufacturer",
            DefaultModelName = "Default Model",
            DefaultCategoryName = "Default Category",
            DefaultStatusId = 2,
            CompanyAliases = companyAliases ?? new Dictionary<string, string>(),
            ManufacturerAliases = manufacturerAliases ?? new Dictionary<string, string>(),
            IgnoredDeviceTypes = ignoredDeviceTypes ?? [],
            IgnoredMacAddresses = ignoredMacAddresses ?? []
        };
    }

    private static AgentInfo CreateAgent(
        string agentId = "1001",
        string name = "Device 1",
        string? serialNumber = "SN-001",
        string? customerId = "C-001",
        string? customerName = "Customer 1",
        string? manufacturer = "Dell",
        string? model = "Latitude",
        string? deviceType = null,
        IReadOnlyList<string>? macAddresses = null)
    {
        return new AgentInfo
        {
            AgentId = agentId,
            Name = name,
            RawJson = "{}",
            MacAddresses = macAddresses ?? ["00-11-22-33-44-55"],
            VendorSerialNumber = serialNumber,
            CustomerId = customerId,
            CustomerName = customerName,
            Vendor = manufacturer,
            VendorBrandModel = model,
            DeviceType = deviceType
        };
    }

    private static AteraPullResult CreatePullResult(params AgentInfo[] agents)
    {
        return new AteraPullResult
        {
            Agents = agents,
            Summary = new PullSummary
            {
                AgentCount = agents.Length,
                PulledAt = DateTimeOffset.UtcNow
            },
            Warnings = []
        };
    }
}
