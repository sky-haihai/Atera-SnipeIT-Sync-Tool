using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Mapping;

namespace AteraSnipeSync.Tests.Mapping;

public sealed class InventoryMapperTests
{
    [Fact]
    public void Map_UsesSerialNumberAsAssetTag_WhenSerialExists()
    {
        var source = CreatePullResult(CreateAgent(serialNumber: " SN-001 "));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        var asset = Assert.Single(result.Assets);
        Assert.Equal("SN-001", asset.AssetTag);
        Assert.Equal("SN-001", asset.Serial);
        Assert.Equal("Device 1", asset.Name);
        Assert.Equal(["00-11-22-33-44-55"], asset.MacAddresses);
        Assert.Equal("Atera", asset.SourceSystem);
        Assert.Equal("1001", asset.SourceId);
        Assert.Contains("Atera Agent ID: 1001", asset.Notes);
    }

    [Fact]
    public void Map_UsesAteraAgentIdFallbackAssetTagAndAddsWarning_WhenSerialMissing()
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
        Assert.Equal("Moore Equine Veterinary Centre", asset.CompanyName);
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
    public void Map_SkipsAgentAndAddsWarning_WhenSerialAndAgentIdMissing()
    {
        var source = CreatePullResult(CreateAgent(agentId: " ", serialNumber: null));
        var mapper = new InventoryMapper();

        var result = mapper.Map(source, CreateDefaultOptions());

        Assert.Empty(result.Assets);
        Assert.Contains(result.Warnings, warning => warning.Code == "MissingAgentIdentity");
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
    public void Map_ThrowsArgumentNullException_WhenOptionsIsNull()
    {
        var mapper = new InventoryMapper();

        Assert.Throws<ArgumentNullException>(() => mapper.Map(CreatePullResult(CreateAgent()), null!));
    }

    private static MappingOptions CreateDefaultOptions(IReadOnlyDictionary<string, string>? companyAliases = null)
    {
        return new MappingOptions
        {
            DefaultCompanyName = "Default Company",
            DefaultManufacturerName = "Default Manufacturer",
            DefaultModelName = "Default Model",
            DefaultCategoryName = "Default Category",
            DefaultStatusId = 2,
            CompanyAliases = companyAliases ?? new Dictionary<string, string>()
        };
    }

    private static AgentInfo CreateAgent(
        string agentId = "1001",
        string name = "Device 1",
        string? serialNumber = "SN-001",
        string? customerId = "C-001",
        string? customerName = "Customer 1",
        string? manufacturer = "Dell",
        string? model = "Latitude")
    {
        return new AgentInfo
        {
            AgentId = agentId,
            Name = name,
            RawJson = "{}",
            MacAddresses = ["00-11-22-33-44-55"],
            VendorSerialNumber = serialNumber,
            CustomerId = customerId,
            CustomerName = customerName,
            Vendor = manufacturer,
            VendorBrandModel = model
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
