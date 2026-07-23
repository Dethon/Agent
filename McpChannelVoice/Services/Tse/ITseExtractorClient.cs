namespace McpChannelVoice.Services.Tse;

// Tri-state sidecar reply: Wav carries the extracted 16 kHz mono WAV on success. A null Wav
// with Rejected marks a request-level 4xx (the sidecar is reachable but refused this request,
// e.g. an unenrolled speaker) as opposed to plain unavailability (service down, deadline
// expired, 5xx) - distinct so the trial dashboard can tell enrollment mismatch from downtime.
public sealed record TseExtractReply(byte[]? Wav, bool Rejected);

public interface ITseExtractorClient
{
    // Fail-open by contract: only the caller's own cancellation surfaces as an exception.
    Task<TseExtractReply> ExtractAsync(byte[] mixtureWav, string speaker, CancellationToken ct);
}