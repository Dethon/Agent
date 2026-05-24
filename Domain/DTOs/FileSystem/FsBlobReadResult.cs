namespace Domain.DTOs.FileSystem;

public sealed record FsBlobReadResult
{
    public required string ContentBase64 { get; init; }
    public required bool Eof { get; init; }
    public required long TotalBytes { get; init; }
}