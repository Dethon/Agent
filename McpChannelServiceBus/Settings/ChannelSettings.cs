namespace McpChannelServiceBus.Settings;

public record ChannelSettings
{
    public required string ServiceBusConnectionString { get; init; }
    public required string PromptQueueName { get; init; }
    public required string ResponseQueueName { get; init; }
}
