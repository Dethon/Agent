namespace Domain.DTOs.FileSystem;

public sealed record FsCreateResult
{
    public required string Status { get; init; }
    public required string FilePath { get; init; }
    public required string Size { get; init; }
    public required int Lines { get; init; }
    public string? Note { get; init; }
}