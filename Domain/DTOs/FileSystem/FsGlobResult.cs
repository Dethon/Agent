namespace Domain.DTOs.FileSystem;

public sealed record FsGlobResult
{
    public required IReadOnlyList<string> Entries { get; init; }
    public required bool Truncated { get; init; }
    public required int Total { get; init; }
}