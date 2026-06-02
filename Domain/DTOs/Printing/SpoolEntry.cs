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

    // When the coordinator first saw this submitted job absent from the printer's active set. Pruning
    // waits until it has stayed absent past the reconcile grace window, so a just-submitted job the
    // printer hasn't registered yet — or a transient empty Get-Jobs response — isn't pruned early.
    public DateTimeOffset? MissingSince { get; init; }

    public bool IsSubmitted => JobId is not null;
}