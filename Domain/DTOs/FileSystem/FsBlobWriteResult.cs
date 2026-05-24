namespace Domain.DTOs.FileSystem;

public sealed record FsBlobWriteResult
{
    public required string Path { get; init; }
    public required int BytesWritten { get; init; }
    public required long TotalBytes { get; init; }
}