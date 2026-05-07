namespace McpChannelTelegram.Services;

public interface ISubAgentCancelNotifier
{
    Task EmitCancelSubAgentNotificationAsync(
        string conversationId,
        string handle,
        CancellationToken cancellationToken = default);
}
