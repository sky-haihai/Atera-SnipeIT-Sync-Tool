namespace AteraSnipeSync.Core.Atera;

public sealed class AteraAgentDto
{
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public string? SerialNumber { get; init; }
    public string? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
}
