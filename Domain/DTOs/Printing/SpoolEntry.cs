namespace Domain.DTOs.Printing;

// One queued document. JobId is null until the coordinator submits it to the printer.
public sealed record SpoolEntry
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteAt { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public int? JobId { get; init; }

    public bool IsSubmitted => JobId is not null;
}