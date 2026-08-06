namespace Domain.DTOs.FileSystem;

// The answer to "may this path leave the mount". There is nothing to report beyond the path that
// was asked about: this payload existing at all is the allowance, and a refusal is the error
// envelope every other operation refuses with.
public sealed record FsMoveOutCheckResult
{
    public required string Path { get; init; }

    // Said in one place because four say it: the backend base's default, the proxy's answer for a
    // mount that advertised no check, and the one mount whose rule found nothing to refuse.
    public static FsResult<FsMoveOutCheckResult> Allow(string path) =>
        new FsResult<FsMoveOutCheckResult>.Ok(new FsMoveOutCheckResult { Path = path });
}