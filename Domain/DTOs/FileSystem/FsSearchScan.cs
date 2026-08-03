namespace Domain.DTOs.FileSystem;

// The caller-supplied half of a text search: the pattern, the limits, and the values echoed back
// in FsSearchResult. Backends hand one of these to the shared search template along with the nodes
// to scan, so every filesystem applies the same limits and reports the same shape.
public sealed record FsSearchScan
{
    public required string Query { get; init; }
    public required bool Regex { get; init; }
    public required string Path { get; init; }
    public required int MaxResults { get; init; }
    public required int ContextLines { get; init; }
    public required VfsTextSearchOutputMode OutputMode { get; init; }
}