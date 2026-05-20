using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Mapping;

namespace AteraSnipeSync.Tests.Mapping;

public sealed class ReconstructionBoundaryTests
{
    [Fact]
    public void Map_ProducesSnipeImportBatchFromRepresentativeAteraPullResult()
    {
        var source = new AteraPullResult
        {
            Agents =
            [
                new AteraAgentDto
                {
                    AgentId = "12345",
                    Name = "LAPTOP-001",
                    SerialNumber = "ABC123",
                    CustomerId = "500",
                    CustomerName = "Acme Support",
                    Manufacturer = "Lenovo",
                    Model = "ThinkPad T14"
                },
                new AteraAgentDto
                {
                    AgentId = "67890",
                    Name = "DESKTOP-002",
                    SerialNumber = null,
                    CustomerId = "501",
                    CustomerName = "Branch Office",
                    Manufacturer = null,
                    Model = "OptiPlex 7000"
                }
            ],
            Customers = [],
            Summary = new PullSummary
            {
                AgentCount = 2,
                CustomerCount = 0,
                PulledAt = DateTimeOffset.UtcNow
            },
            Warnings = []
        };
        var options = new MappingOptions
        {
            DefaultCompanyName = "Unknown Company",
            DefaultManufacturerName = "Unknown Manufacturer",
            DefaultModelName = "Unknown Model",
            DefaultCategoryName = "Computer",
            DefaultStatusId = 2
        };
        var mapper = new InventoryMapper();

        var batch = mapper.Map(source, options);

        Assert.Equal(2, batch.Summary.SourceAgentCount);
        Assert.Equal(2, batch.Summary.MappedAssetCount);
        Assert.Equal(2, batch.Assets.Count);

        var serialBackedAsset = batch.Assets[0];
        Assert.Equal("ABC123", serialBackedAsset.AssetTag);
        Assert.Equal("ABC123", serialBackedAsset.Serial);
        Assert.Equal("LAPTOP-001", serialBackedAsset.Name);
        Assert.Equal("Acme Support", serialBackedAsset.CompanyName);
        Assert.Equal("Lenovo", serialBackedAsset.ManufacturerName);
        Assert.Equal("ThinkPad T14", serialBackedAsset.ModelName);
        Assert.Equal("Computer", serialBackedAsset.CategoryName);
        Assert.Equal(2, serialBackedAsset.StatusId);
        Assert.Equal("Atera", serialBackedAsset.SourceSystem);
        Assert.Equal("12345", serialBackedAsset.SourceId);
        Assert.Contains("Atera Agent ID: 12345", serialBackedAsset.Notes);
        Assert.Contains("Atera Customer Name: Acme Support", serialBackedAsset.Notes);

        var fallbackAsset = batch.Assets[1];
        Assert.Equal("ATERA-67890", fallbackAsset.AssetTag);
        Assert.Null(fallbackAsset.Serial);
        Assert.Equal("DESKTOP-002", fallbackAsset.Name);
        Assert.Equal("Branch Office", fallbackAsset.CompanyName);
        Assert.Equal("Unknown Manufacturer", fallbackAsset.ManufacturerName);
        Assert.Equal("OptiPlex 7000", fallbackAsset.ModelName);
        Assert.Equal("Computer", fallbackAsset.CategoryName);
        Assert.Equal(2, fallbackAsset.StatusId);
        Assert.Equal("Atera", fallbackAsset.SourceSystem);
        Assert.Equal("67890", fallbackAsset.SourceId);

        Assert.Contains(batch.Warnings, warning => warning.Code == "MissingSerialNumber");
        Assert.Contains(batch.Warnings, warning => warning.Code == "MissingManufacturer");
    }
}
