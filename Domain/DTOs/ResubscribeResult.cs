using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public enum ResubscribeStatus
{
    Resubscribed,
    NotFound,
    AlreadyCompleted,
    AlreadyTracked
}

[PublicAPI]
public record ResubscribeResult(int DownloadId, ResubscribeStatus Status, string Message);