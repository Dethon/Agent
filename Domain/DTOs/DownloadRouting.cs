using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record DownloadRouting
{
    public required int DownloadId { get; init; }
    public required string Title { get; init; }
    public required ConversationContext Context { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
}