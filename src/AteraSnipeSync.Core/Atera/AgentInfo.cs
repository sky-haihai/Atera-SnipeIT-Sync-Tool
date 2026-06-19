namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Captures one Atera agent/device record from the pull module, preserving the broad
/// AgentQueryDTO shape while exposing stable convenience fields for later modules.
/// </summary>
public sealed class AgentInfo
{
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public required string RawJson { get; init; }
    public string? MachineId { get; init; }
    public string? DeviceGuid { get; init; }
    public string? FolderId { get; init; }
    public string? FolderName { get; init; }
    public string? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? AgentName { get; init; }
    public string? SystemName { get; init; }
    public string? MachineName { get; init; }
    public string? DomainName { get; init; }
    public string? CurrentLoggedUsers { get; init; }
    public string? ComputerDescription { get; init; }
    public bool? Monitored { get; init; }
    public string? AgentVersion { get; init; }
    public bool? Favorite { get; init; }
    public string? ThresholdId { get; init; }
    public string? MonitoredAgentId { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public bool? Online { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public string? ReportedFromIp { get; init; }
    public string? AppViewUrl { get; init; }
    public string? Motherboard { get; init; }
    public string? Processor { get; init; }
    public int? Memory { get; init; }
    public string? Display { get; init; }
    public string? Sound { get; init; }
    public int? ProcessorCoresCount { get; init; }
    public string? SystemDrive { get; init; }
    public string? ProcessorClock { get; init; }
    public string? Vendor { get; init; }
    public string? VendorSerialNumber { get; init; }
    public string? VendorBrandModel { get; init; }
    public string? ProductName { get; init; }
    public string? BiosManufacturer { get; init; }
    public string? BiosVersion { get; init; }
    public DateTimeOffset? BiosReleaseDate { get; init; }
    public IReadOnlyList<string> MacAddresses { get; init; } = [];
    public IReadOnlyList<string> IpAddresses { get; init; } = [];
    public string? HardwareDisksJson { get; init; }
    public string? BatteryInfoJson { get; init; }
    public string? OS { get; init; }
    public string? OSType { get; init; }
    public string? WindowsSerialNumber { get; init; }
    public string? Office { get; init; }
    public string? OfficeSP { get; init; }
    public bool? OfficeOEM { get; init; }
    public string? OfficeSerialNumber { get; init; }
    public double? OSNum { get; init; }
    public DateTimeOffset? LastRebootTime { get; init; }
    public string? OSVersion { get; init; }
    public string? OSBuild { get; init; }
    public string? OfficeFullVersion { get; init; }
    public string? DeviceType { get; init; }
    public string? LastLoginUser { get; init; }

    public string? SerialNumber => VendorSerialNumber;

    public string? Manufacturer => Vendor;

    public string? Model => VendorBrandModel ?? ProductName;
}
