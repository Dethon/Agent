namespace Domain.Exceptions;

// The voice hub could not be reached at all (connection failure or request timeout) — distinct from
// the hub answering with an error status. The HTTP adapters throw this so TimerFileSystem can fail
// closed with a typed, retryable tool error instead of leaking a raw HTTP exception envelope.
public sealed class VoiceHubUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);