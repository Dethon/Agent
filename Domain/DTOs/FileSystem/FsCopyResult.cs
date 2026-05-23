namespace Domain.DTOs.FileSystem;

public sealed record FsCopyResult
{
    public required string Status { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public required long Bytes { get; init; }
}