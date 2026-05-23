namespace Domain.DTOs.FileSystem;

public sealed record FsRemoveResult
{
    public required string Status { get; init; }
    public required string Message { get; init; }
    public required string OriginalPath { get; init; }
    public required string TrashPath { get; init; }
}