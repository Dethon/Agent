using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.Channel;

public enum ChannelInboxItemKind
{
    Message,
    Cancel
}

[PublicAPI]
public sealed record ChannelInboxItem
{
    public required ChannelInboxItemKind Kind { get; init; }
    public ChannelMessageNotification? Message { get; init; }
    public ChannelCancelNotification? Cancel { get; init; }

    public static ChannelInboxItem ForMessage(ChannelMessageNotification message) =>
        new() { Kind = ChannelInboxItemKind.Message, Message = message };

    public static ChannelInboxItem ForCancel(ChannelCancelNotification cancel) =>
        new() { Kind = ChannelInboxItemKind.Cancel, Cancel = cancel };
}