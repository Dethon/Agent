namespace Domain.DTOs.FileSystem;

// What a copy or a move answers. Deliberately not FsCopyResult or FsMoveResult: those are the wire
// contract for a backend's native primitive — one mount, one call, paths in that backend's
// coordinates — while a transfer spans two mounts, recurses directories and reports per-entry
// outcomes no backend result can express, in the virtual paths it was asked in. See
// docs/adr/0016-a-tool-answers-in-the-coordinates-it-was-asked-in.md.
//
// One type serves both branches, so a file transfer and a directory transfer read the same way.
// It is not in FileSystemOperations.All: that list is keyed by backend operation and would need a
// fake entry to hold this.
public sealed record FsTransferResult
{
    public required string Status { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }

    // Absent when a native move ran: FsMoveResult carries no byte count, and the response used to
    // say so with minus one. The contract omits nulls, so the model sees no field rather than a
    // sentinel to interpret.
    public long? Bytes { get; init; }

    public FsTransferSummary? Summary { get; init; }

    public IReadOnlyList<FsTransferEntry>? Entries { get; init; }

    public const string Ok = "ok";
    public const string Partial = "partial";
    public const string Failed = "failed";
}

public sealed record FsTransferSummary
{
    public required int Transferred { get; init; }
    public required int Failed { get; init; }
    public required int Skipped { get; init; }
    public required long TotalBytes { get; init; }
}

public sealed record FsTransferEntry
{
    public required string Status { get; init; }

    // Absent for the one entry with no virtual path: a glob entry outside the requested source
    // directory is outside the coordinate frame, so its raw string goes in Error as diagnostics
    // rather than here as a path to retry.
    public string? Source { get; init; }

    public string? Destination { get; init; }
    public long? Bytes { get; init; }
    public string? Error { get; init; }
}