using JetBrains.Annotations;

namespace Agent.Settings;

public record ServiceBusSettings
{
    public required string ConnectionString { get; [UsedImplicitly] init; }
    public required string PromptQueueName { get; [UsedImplicitly] init; }
    public required string ResponseQueueName { get; [UsedImplicitly] init; }
    public int MaxConcurrentCalls { get; [UsedImplicitly] init; } = 10;
}
