namespace Domain.DTOs.FileSystem;

public sealed record FsReadResult
{
    public required string FilePath { get; init; }
    public required string Content { get; init; }
    public required int TotalLines { get; init; }
    public required bool Truncated { get; init; }
    public string? Suggestion { get; init; }
}