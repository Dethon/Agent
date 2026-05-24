namespace Domain.DTOs.FileSystem;

public sealed record FsMoveResult
{
    public required string Status { get; init; }
    public required string Message { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
}