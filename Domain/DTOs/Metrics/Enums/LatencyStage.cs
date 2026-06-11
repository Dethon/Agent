namespace Domain.DTOs.Metrics.Enums;

// Persisted as integers in metric events (Redis): values are pinned explicitly — never
// renumber or reuse one; append new members with the next free number.
public enum LatencyStage
{
    SessionWarmup = 0,
    MemoryRecall = 1,
    LlmFirstToken = 2,
    LlmTotal = 3,
    ToolExec = 4,
    HistoryStore = 5,
    FirstReply = 6
}