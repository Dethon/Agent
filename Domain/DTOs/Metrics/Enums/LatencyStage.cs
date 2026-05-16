namespace Domain.DTOs.Metrics.Enums;

public enum LatencyStage
{
    SessionWarmup,
    MemoryRecall,
    LlmFirstToken,
    LlmTotal,
    ToolExec,
    HistoryStore
}