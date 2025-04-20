namespace Domain.DTOs;

public record SearchResult
{
    public required string Title { get; init; }
    public string? Category { get; init; }
    public required int Id { get; init; }
    public long? Size { get; init; }
    public long? Seeders { get; init; }
    public long? Peers { get; init; }
    public required string Link { get; init; }
}