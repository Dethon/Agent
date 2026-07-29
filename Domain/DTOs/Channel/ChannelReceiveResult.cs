using Domain.Channels;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public sealed record ChannelReceiveResult
{
    public IReadOnlyList<ChannelInboxItem> Items { get; init; } = [];
}