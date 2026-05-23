namespace Domain.DTOs.FileSystem;

public sealed record FsInfoResult
{
    public required bool Exists { get; init; }
    public required string Path { get; init; }
    public bool? IsDirectory { get; init; }
    public long? Size { get; init; }
    public string? LastModified { get; init; }
}